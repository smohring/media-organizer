using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Faces;

/// <summary>Manages the ONNX models (OpenCV Zoo, Apache-2.0): YuNet finds
/// faces, SFace turns them into feature vectors, MobileNetV2 turns a whole
/// picture into one.
///
/// <para>All are downloaded only when needed — the same way the Ollama model
/// is — and live under <c>%AppData%\MediaOrganizer\models</c>.</para></summary>
public sealed class FaceModelProvider
{
    private const string BaseUrl =
        "https://media.githubusercontent.com/media/opencv/opencv_zoo/main/models";

    private static readonly (string FileName, string Url, long ApproxBytes)[] Models =
    [
        ("face_detection_yunet_2023mar.onnx",
            $"{BaseUrl}/face_detection_yunet/face_detection_yunet_2023mar.onnx", 232_589),
        ("face_recognition_sface_2021dec.onnx",
            $"{BaseUrl}/face_recognition_sface/face_recognition_sface_2021dec.onnx", 38_695_637),
        ("image_classification_mobilenetv2_2022apr.onnx",
            $"{BaseUrl}/image_classification_mobilenet/image_classification_mobilenetv2_2022apr.onnx",
            13_964_571),
    ];

    public static string ModelDirectory => Path.Combine(AppDataFolder.Root, "models");

    public string DetectorPath => Path.Combine(ModelDirectory, Models[0].FileName);

    public string RecognizerPath => Path.Combine(ModelDirectory, Models[1].FileName);

    /// <summary>Whole-image encoder: turns a picture into one vector, so that
    /// "do these belong together" becomes arithmetic instead of a question to
    /// the language model.</summary>
    public string SceneEncoderPath => Path.Combine(ModelDirectory, Models[2].FileName);

    /// <summary>Are both face models present and of a plausible size? The scene
    /// encoder is deliberately not part of this: it arrived later, and an
    /// installation missing it must not lose face recognition as well.</summary>
    public bool IsInstalled => Models.Take(2).All(Present);

    public bool IsSceneEncoderInstalled => Present(Models[2]);

    private static bool Present((string FileName, string Url, long ApproxBytes) model)
    {
        var path = Path.Combine(ModelDirectory, model.FileName);
        return File.Exists(path) && new FileInfo(path).Length > model.ApproxBytes / 2;
    }

    public static long TotalDownloadBytes => Models.Sum(m => m.ApproxBytes);

    /// <summary>Downloads whichever models are missing; reports progress as a
    /// fraction from 0 to 1. The line is a key and its arguments — the caller
    /// owns the language, see <see cref="StatusReport"/>.</summary>
    public async Task DownloadAsync(
        IProgress<(StatusReport Message, double Fraction)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelDirectory);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        long completedBefore = 0;
        foreach (var (fileName, url, approxBytes) in Models)
        {
            var target = Path.Combine(ModelDirectory, fileName);
            if (File.Exists(target) && new FileInfo(target).Length > approxBytes / 2)
            {
                completedBefore += approxBytes;
                continue;
            }

            var temp = target + ".part";
            using (var response = await http
                       .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? approxBytes;

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var destination = File.Create(temp);

                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    read += count;
                    var fraction = (completedBefore + read) / (double)TotalDownloadBytes;
                    progress.Report((
                        new StatusReport(SetupKeys.DownloadingFile, fileName),
                        Math.Clamp(fraction, 0, 1)));
                }
            }

            File.Move(temp, target, overwrite: true);
            completedBefore += approxBytes;
        }
    }
}
