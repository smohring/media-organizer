using System.Diagnostics;
using System.Globalization;

namespace MediaOrganizer.Core.Video;

/// <summary>Frame-Extraktion aus Videos über eine lokale ffmpeg-Installation.
/// Ist ffmpeg nicht vorhanden, degradiert die App sauber: Videos werden dann
/// nur über Container-Metadaten eingeordnet.</summary>
public sealed class FfmpegService(string? explicitPath = null)
{
    private readonly Lazy<string?> _ffmpegPath = new(() => Locate(explicitPath));

    public bool IsAvailable => _ffmpegPath.Value is not null;

    public string? ExecutablePath => _ffmpegPath.Value;

    /// <summary>Sucht ffmpeg.exe: expliziter Pfad, PATH des Prozesses, dann
    /// der (ggf. seit App-Start aktualisierte) PATH aus Benutzer-/System-
    /// Umgebung und zuletzt bekannte winget-Installationsorte. So wird eine
    /// frische Installation auch ohne Neustart der App gefunden.</summary>
    public static string? Locate(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var searchPaths = new List<string>();
        foreach (var target in new[]
                 {
                     EnvironmentVariableTarget.Process,
                     EnvironmentVariableTarget.User,
                     EnvironmentVariableTarget.Machine,
                 })
        {
            try
            {
                searchPaths.AddRange((Environment.GetEnvironmentVariable("PATH", target) ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
            }
            catch
            {
                // Registry-Zugriff kann eingeschränkt sein.
            }
        }

        // winget legt Pakete unter %LocalAppData%\Microsoft\WinGet\Packages ab;
        // der Ordnername enthält die Version, daher Wildcard-Suche.
        try
        {
            var wingetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetRoot))
            {
                searchPaths.AddRange(Directory.EnumerateDirectories(wingetRoot, "*FFmpeg*")
                    .SelectMany(d => Directory.EnumerateDirectories(d, "bin", SearchOption.AllDirectories)));
            }
        }
        catch
        {
            // Suche im winget-Verzeichnis ist optional.
        }

        foreach (var dir in searchPaths)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ungültige PATH-Einträge ignorieren.
            }
        }

        return null;
    }

    /// <summary>Extrahiert bis zu <paramref name="maxFrames"/> gleichmäßig über
    /// die Laufzeit verteilte Frames als JPEG-Bytes. Deckt damit auch Videos
    /// über 60 s ab (Gemma-4-Limit), da immer nur eine kleine Standbild-Sequenz
    /// übergeben wird.</summary>
    public async Task<List<byte[]>> ExtractFramesAsync(
        string videoPath,
        TimeSpan? knownDuration,
        int maxFrames = 6,
        int maxDimension = 896,
        CancellationToken ct = default)
    {
        var ffmpeg = _ffmpegPath.Value;
        if (ffmpeg is null)
        {
            return [];
        }

        var duration = knownDuration ?? TimeSpan.FromSeconds(10);
        var frameCount = duration.TotalSeconds < 15
            ? Math.Min(3, maxFrames)
            : maxFrames;

        var frames = new List<byte[]>(frameCount);
        var tempDir = Path.Combine(Path.GetTempPath(), "MediaOrganizer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            for (var i = 0; i < frameCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                // Gleichmäßige Stützstellen, Ränder gemieden (5 %..95 %).
                var fraction = frameCount == 1 ? 0.25 : 0.05 + 0.90 * i / (frameCount - 1);
                var position = TimeSpan.FromSeconds(duration.TotalSeconds * fraction);
                var outFile = Path.Combine(tempDir, $"frame_{i:D2}.jpg");

                // WICHTIG: Zahlen invariant formatieren — mit deutschem
                // Dezimalkomma ("-ss 2,50") bricht ffmpeg sofort ab.
                var seconds = position.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);
                var ok = await RunFfmpegAsync(ffmpeg,
                    $"-ss {seconds} -i \"{videoPath}\" -frames:v 1 " +
                    $"-vf \"scale='min({maxDimension},iw)':-2\" -q:v 4 -y \"{outFile}\"",
                    ct).ConfigureAwait(false);

                if (ok && File.Exists(outFile))
                {
                    frames.Add(await File.ReadAllBytesAsync(outFile, ct).ConfigureAwait(false));
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Temp-Reste sind unkritisch.
            }
        }

        return frames;
    }

    /// <summary>Erstellt ein einzelnes Vorschaubild (JPEG) für die Ergebnisansicht.</summary>
    public async Task<byte[]?> ExtractThumbnailAsync(
        string videoPath,
        int maxDimension = 320,
        CancellationToken ct = default)
    {
        var frames = await ExtractFramesAsync(videoPath, knownDuration: null, maxFrames: 1, maxDimension, ct)
            .ConfigureAwait(false);
        return frames.Count > 0 ? frames[0] : null;
    }

    private static async Task<bool> RunFfmpegAsync(string ffmpegPath, string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        try
        {
            if (!process.Start())
            {
                return false;
            }

            // Puffer leeren, damit ffmpeg nicht am vollen stderr-Pipe hängt.
            _ = process.StandardError.ReadToEndAsync(ct);
            _ = process.StandardOutput.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Prozess ist ggf. schon beendet.
            }

            throw;
        }
        catch
        {
            return false;
        }
    }
}
