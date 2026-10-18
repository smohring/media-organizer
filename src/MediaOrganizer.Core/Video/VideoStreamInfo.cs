using System.Diagnostics;
using System.Globalization;

namespace MediaOrganizer.Core.Video;

/// <summary>Technische Eckdaten der Videospur. <paramref name="Width"/> und
/// <paramref name="Height"/> sind die KODIERTEN Maße; Handy-Aufnahmen tragen
/// zusätzlich einen Drehwinkel.</summary>
public sealed record VideoStreamInfo(
    string Codec, int Width, int Height, double FrameRate, int Rotation = 0,
    bool HasAudio = false)
{
    /// <summary>Bei ±90°/270° vertauschen sich Breite und Höhe. ffmpeg dreht
    /// beim Dekodieren automatisch, die Zielgröße muss also von den
    /// ANZEIGE-Maßen ausgehen — sonst wird das Bild verzerrt.</summary>
    public bool IsQuarterTurned => Math.Abs(Rotation % 180) == 90;

    public int DisplayWidth => IsQuarterTurned ? Height : Width;

    public int DisplayHeight => IsQuarterTurned ? Width : Height;
}

/// <summary>Liest Videoeigenschaften über ffprobe (liegt neben ffmpeg).</summary>
public static class VideoProbe
{
    public static VideoStreamInfo? Read(string ffmpegPath, string videoPath)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
        if (!File.Exists(ffprobe))
        {
            return null;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = "-v error -select_streams v:0 " +
                            "-show_entries stream=codec_name,width,height,avg_frame_rate:" +
                            "stream_side_data=rotation " +
                            $"-of default=nw=1 \"{videoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            string? codec = null;
            int width = 0, height = 0, rotation = 0;
            double fps = 30;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('=', 2);
                if (parts.Length != 2)
                {
                    continue;
                }

                switch (parts[0])
                {
                    case "codec_name":
                        codec = parts[1].Trim();
                        break;
                    case "width":
                        int.TryParse(parts[1], out width);
                        break;
                    case "height":
                        int.TryParse(parts[1], out height);
                        break;
                    case "avg_frame_rate":
                        fps = ParseRate(parts[1]) ?? 30;
                        break;
                    case "rotation":
                        // Handy-Aufnahmen liefern z.B. -90.
                        if (double.TryParse(parts[1], NumberStyles.Any,
                                CultureInfo.InvariantCulture, out var deg))
                        {
                            rotation = ((int)Math.Round(deg) % 360 + 360) % 360;
                        }

                        break;
                }
            }

            return codec is null || width <= 0 || height <= 0
                ? null
                : new VideoStreamInfo(codec, width, height, fps, rotation,
                    HasAudioStream(ffprobe, videoPath));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Hat die Datei überhaupt eine Tonspur? Eigener ffprobe-Aufruf,
    /// weil die Videoabfrage mit <c>-select_streams v:0</c> nur die Bildspur
    /// sieht und Tonspuren dort grundsätzlich nie auftauchen.</summary>
    private static bool HasAudioStream(string ffprobe, string videoPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = "-v error -select_streams a -show_entries stream=codec_type " +
                            $"-of csv=p=0 \"{videoPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Contains("audio", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>ffprobe liefert die Bildrate als Bruch ("30000/1001").</summary>
    private static double? ParseRate(string value)
    {
        var parts = value.Trim().Split('/');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var num)
            || !double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var den)
            || den == 0 || num <= 0)
        {
            return null;
        }

        return num / den;
    }
}
