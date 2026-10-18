using System.Diagnostics;

namespace MediaOrganizer.Core.Video;

/// <summary>Installs ffmpeg through winget (Gyan.FFmpeg) and reports progress
/// line by line — the same shape as the Ollama model download.</summary>
public static class FfmpegInstaller
{
    public static bool IsWingetAvailable => FindWinget() is not null;

    private static string? FindWinget()
    {
        foreach (var target in new[]
                 {
                     EnvironmentVariableTarget.Process,
                     EnvironmentVariableTarget.User,
                     EnvironmentVariableTarget.Machine,
                 })
        {
            try
            {
                var dirs = (Environment.GetEnvironmentVariable("PATH", target) ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
                foreach (var dir in dirs)
                {
                    var candidate = Path.Combine(dir.Trim(), "winget.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
                // Invalid entries, or restricted access.
            }
        }

        return null;
    }

    /// <summary>Runs the installation, feeding output lines to the interface
    /// and returning the ffmpeg path that was found, or null.</summary>
    public static async Task<string?> InstallAsync(
        IProgress<string> output,
        CancellationToken ct = default)
    {
        var winget = FindWinget();
        if (winget is null)
        {
            // No message from here: this channel carries winget's own output,
            // and the caller already has a translated sentence for the case.
            return null;
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = winget,
            Arguments = "install --id Gyan.FFmpeg --exact --silent " +
                        "--accept-source-agreements --accept-package-agreements",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                output.Report(e.Data.Trim());
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                output.Report(e.Data.Trim());
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may already have ended.
            }

            throw;
        }
        // Anything else goes up as it is: the caller turns it into a translated
        // sentence, which reporting it here would only overwrite.

        // Right after the installation the new PATH is not yet visible in this
        // process — which is why Locate() also searches the winget paths.
        return FfmpegService.Locate();
    }
}
