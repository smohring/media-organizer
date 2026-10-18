using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Update;

/// <summary>Outcome of a check against the update feed.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The running build is the newest one.</summary>
    UpToDate,

    /// <summary>A newer release is available; see
    /// <see cref="UpdateCheckResult.Manifest"/>.</summary>
    UpdateAvailable,

    /// <summary>The check did not complete — no network, feed unreachable,
    /// unreadable document. Never an error the user must act on.</summary>
    Failed,
}

/// <param name="Status">What came of the check.</param>
/// <param name="Manifest">The advertised release, set when
/// <paramref name="Status"/> is <see cref="UpdateCheckStatus.UpdateAvailable"/>.</param>
/// <param name="Message">Human-readable detail, set when the check failed.</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateManifest? Manifest = null,
    string Message = "");

/// <summary>Checks an HTTPS feed for a newer release, fetches the release
/// archive and replaces the application directory with it.
///
/// <para><b>Why a self-replacing application and not an installer.</b>
/// Media Organizer ships portable: a folder that is unpacked wherever the user
/// wants and started from there. There is nothing registered with Windows that
/// could be upgraded, so updating means exchanging the contents of that folder.
/// A running process cannot overwrite its own files, so the exchange is done by
/// a small script that waits for this process to exit — see
/// <see cref="ApplyAndRestart"/>.</para>
///
/// <para><b>What guards the download.</b> Three things, in this order: the feed
/// is fetched over TLS, so the checksum it carries comes from the release owner
/// and not from the network; the archive's SHA-256 must match that checksum
/// before it is unpacked anywhere; and if
/// <see cref="AppSettings.UpdateExpectedSigner"/> is configured, the unpacked
/// executable must additionally carry a valid Authenticode signature from that
/// subject. The third guard stays optional because a build signed with a
/// self-created test certificate cannot pass it — see
/// <c>build\sign.ps1</c>.</para>
///
/// <para><b>What this class never does.</b> It does not update on its own. A
/// check reports and stops; downloading a hundred megabytes and restarting the
/// application are decisions this layer does not get to make.</para></summary>
public sealed class UpdateService
{
    /// <summary>The feed is small and static. A short timeout keeps a stalled
    /// server from holding up the start of the application, which is where the
    /// automatic check runs.</summary>
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppSettings _settings;
    private readonly Func<HttpClient> _httpFactory;

    public UpdateService(AppSettings settings, Func<HttpClient>? httpFactory = null)
    {
        _settings = settings;
        _httpFactory = httpFactory ?? (() => new HttpClient());
    }

    /// <summary>Where archives and unpacked staging directories are parked.
    /// Under the user's temp directory on purpose: if anything goes wrong
    /// midway, Windows cleans up after us.</summary>
    public static string WorkDirectory => Path.Combine(
        Path.GetTempPath(), "MediaOrganizer", "updates");

    /// <summary>The directory that will be replaced — the one the running
    /// executable sits in.</summary>
    public static string ApplicationDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath ?? "") is { Length: > 0 } dir
            ? dir
            : AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>Can this installation replace itself in place, and if not, why
    /// not? Checked before anything is downloaded, so the user is not told
    /// about a problem after waiting for 150 MB.</summary>
    public static bool CanUpdateInPlace(out StatusReport problem)
    {
        var directory = ApplicationDirectory;

        // A drive root as application directory means the archive was unpacked
        // straight onto a drive. Mirroring into it would delete everything else
        // on that drive.
        if (string.IsNullOrEmpty(directory)
            || string.Equals(Path.GetPathRoot(directory), directory, StringComparison.OrdinalIgnoreCase))
        {
            problem = new StatusReport(SetupKeys.RunsFromDriveRoot);
            return false;
        }

        // Sanity check before anything is ever mirrored over this directory.
        if (!File.Exists(Path.Combine(directory, "MediaOrganizer.exe")))
        {
            problem = new StatusReport(SetupKeys.NotAnApplicationFolder);
            return false;
        }

        try
        {
            // The only reliable test for "may I write here" on Windows is to
            // write. Permissions alone do not account for read-only media or a
            // folder controlled by another process.
            var probe = Path.Combine(directory, $".update-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception)
        {
            problem = new StatusReport(SetupKeys.FolderNotWritable, directory);
            return false;
        }

        problem = default;
        return true;
    }

    /// <summary>Asks the feed what the newest release is.
    ///
    /// <para>Never throws for an unreachable server. A failed update check is
    /// not a failure of the application, and the caller runs this at
    /// startup.</para></summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        if (!Uri.TryCreate(_settings.UpdateFeedUrl, UriKind.Absolute, out var feedUri)
            || feedUri.Scheme != Uri.UriSchemeHttps)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                Message: "No https address is configured for the update feed.");
        }

        try
        {
            using var http = _httpFactory();
            http.Timeout = FeedTimeout;

            // Some caching proxies happily serve a stale feed for hours, which
            // looks exactly like "no update available".
            http.DefaultRequestHeaders.CacheControl = new() { NoCache = true };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"MediaOrganizer/{AppVersion.CurrentDisplay}");

            var json = await http.GetStringAsync(feedUri, ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);

            if (manifest is null)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed, Message: "The update feed is empty.");
            }

            if (!manifest.IsUsable(out var problem))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Message: problem);
            }

            return AppVersion.IsNewerThanCurrent(manifest.ParsedVersion!)
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, manifest)
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, manifest);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Message: ex.Message);
        }
    }

    /// <summary>Downloads the release archive, verifies it and unpacks it into a
    /// staging directory. Returns the path of that directory; its contents are
    /// what <see cref="ApplyAndRestart"/> will move into place.</summary>
    /// <exception cref="InvalidOperationException">The download does not match
    /// the checksum from the feed, does not contain an application, or fails the
    /// signature requirement.</exception>
    public async Task<string> DownloadAsync(
        UpdateManifest manifest,
        IProgress<(StatusReport Message, double Fraction)> progress,
        CancellationToken ct = default)
    {
        if (!manifest.IsUsable(out var problem))
        {
            throw new InvalidOperationException(problem);
        }

        var version = manifest.ParsedVersion!.ToString(3);
        Directory.CreateDirectory(WorkDirectory);
        var archive = Path.Combine(WorkDirectory, $"MediaOrganizer-{version}-win-x64.zip");

        if (!(File.Exists(archive) && await MatchesChecksumAsync(archive, manifest.Sha256, ct).ConfigureAwait(false)))
        {
            // Download beside the target, not onto it: a half-written file that
            // happens to keep the final name is the one thing that must never
            // survive a crash here.
            var temp = archive + ".part";
            await DownloadToFileAsync(manifest, temp, progress, ct).ConfigureAwait(false);

            progress.Report((new StatusReport(SetupKeys.VerifyingDownload), 1));
            if (!await MatchesChecksumAsync(temp, manifest.Sha256, ct).ConfigureAwait(false))
            {
                TryDelete(temp);
                throw new InvalidOperationException(
                    "The downloaded archive does not match the checksum from the update feed. " +
                    "It was discarded.");
            }

            File.Move(temp, archive, overwrite: true);
        }
        else
        {
            progress.Report((new StatusReport(SetupKeys.ArchiveAlreadyThere), 1));
        }

        progress.Report((new StatusReport(SetupKeys.Unpacking), 1));
        var staging = Path.Combine(WorkDirectory, $"staging-{version}");
        TryDeleteDirectory(staging);
        ZipFile.ExtractToDirectory(archive, staging);

        // Release archives are built with the application directory as their
        // root, but an archive repacked by hand often gains a wrapping folder.
        // Following it here is cheaper than an update that mirrors a single
        // subdirectory over the installation.
        var root = FindApplicationRoot(staging)
                   ?? throw new InvalidOperationException(
                       "The downloaded archive does not contain MediaOrganizer.exe.");

        RequireTrustedSignature(Path.Combine(root, "MediaOrganizer.exe"));

        progress.Report((new StatusReport(SetupKeys.UpdateReady), 1));
        return root;
    }

    /// <summary>Hands the exchange over to a script and reports whether the
    /// application should now shut down.
    ///
    /// <para>A process cannot replace the files it is running from, so the work
    /// happens after this one is gone. The script waits for the process to
    /// exit, mirrors the staging directory over the application directory,
    /// starts the new build and removes the staging copy. If the mirror fails
    /// it starts the old build again rather than leaving the user with
    /// nothing.</para></summary>
    /// <returns>True when the script was started and the caller should exit.</returns>
    public static bool ApplyAndRestart(string stagingDirectory)
    {
        if (!CanUpdateInPlace(out _))
        {
            return false;
        }

        var applicationDirectory = ApplicationDirectory;
        var executable = Path.Combine(applicationDirectory, "MediaOrganizer.exe");
        var script = Path.Combine(WorkDirectory, "apply-update.cmd");

        try
        {
            Directory.CreateDirectory(WorkDirectory);
            File.WriteAllText(script, BuildSwapScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{script}\" {Environment.ProcessId} " +
                            $"\"{stagingDirectory}\" \"{applicationDirectory}\" \"{executable}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath(),
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The exchange itself, as a batch file.
    ///
    /// <para>Batch rather than PowerShell on purpose: an execution policy can
    /// block a .ps1, and the one moment this must not fail is after the
    /// application has already closed.</para>
    ///
    /// <para><c>ping</c> rather than <c>timeout</c> for the wait loop, because
    /// <c>timeout</c> aborts when it has no console to read from — which is
    /// exactly the case here, the script runs with no window.</para></summary>
    private static string BuildSwapScript() =>
        """
        @echo off
        setlocal
        set "PROCESSID=%~1"
        set "STAGE=%~2"
        set "APPDIR=%~3"
        set "EXECUTABLE=%~4"

        rem Wait for Media Organizer to exit, at most about a minute. Longer than
        rem that and something is holding it open that this script cannot fix.
        set /a attempts=0
        :wait
        tasklist /FI "PID eq %PROCESSID%" /NH 2>nul | find "%PROCESSID%" >nul
        if errorlevel 1 goto ready
        set /a attempts+=1
        if %attempts% GEQ 60 goto abandon
        ping -n 2 127.0.0.1 >nul
        goto wait

        :ready
        rem /MIR also removes files the new version no longer ships. Leaving a
        rem stale DLL from an older runtime behind is how an update turns into a
        rem crash on start.
        robocopy "%STAGE%" "%APPDIR%" /MIR /R:3 /W:2 /NFL /NDL /NJH /NJS /NP >"%TEMP%\MediaOrganizer-update.log" 2>&1
        if errorlevel 8 goto failed

        start "" "%EXECUTABLE%"
        rmdir /s /q "%STAGE%" 2>nul
        exit /b 0

        :failed
        rem The installation is possibly half-replaced, but starting what is
        rem there beats leaving the user staring at nothing. The robocopy log
        rem says what happened.
        start "" "%EXECUTABLE%"
        exit /b 1

        :abandon
        exit /b 2

        """;

    private async Task DownloadToFileAsync(
        UpdateManifest manifest,
        string target,
        IProgress<(StatusReport Message, double Fraction)> progress,
        CancellationToken ct)
    {
        using var http = _httpFactory();
        http.Timeout = TimeSpan.FromMinutes(30);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"MediaOrganizer/{AppVersion.CurrentDisplay}");

        using var response = await http
            .GetAsync(manifest.PackageUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? manifest.SizeBytes;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = File.Create(target);

        var buffer = new byte[81920];
        long read = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            read += count;
            progress.Report((
                new StatusReport(
                    SetupKeys.DownloadingUpdate,
                    read / 1024 / 1024,
                    Math.Max(total, read) / 1024 / 1024),
                total > 0 ? Math.Clamp(read / (double)total, 0, 1) : 0));
        }
    }

    /// <summary>Finds the directory inside the unpacked archive that holds the
    /// application. Looks one level deep only — anything more nested is not an
    /// archive this application produced.</summary>
    private static string? FindApplicationRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, "MediaOrganizer.exe")))
        {
            return staging;
        }

        foreach (var directory in Directory.EnumerateDirectories(staging))
        {
            if (File.Exists(Path.Combine(directory, "MediaOrganizer.exe")))
            {
                return directory;
            }
        }

        return null;
    }

    /// <summary>Enforces <see cref="AppSettings.UpdateExpectedSigner"/> when one
    /// is configured. Does nothing otherwise — an unsigned test build is a
    /// normal state before the first real certificate exists.</summary>
    private void RequireTrustedSignature(string path)
    {
        var expected = _settings.UpdateExpectedSigner;
        if (string.IsNullOrWhiteSpace(expected) || !OperatingSystem.IsWindows())
        {
            return;
        }

        var (trusted, subject) = Authenticode.Inspect(path);
        if (!trusted)
        {
            throw new InvalidOperationException(
                "The downloaded build carries no valid signature. It was discarded.");
        }

        if (subject?.Contains(expected, StringComparison.OrdinalIgnoreCase) != true)
        {
            throw new InvalidOperationException(
                $"The downloaded build is signed by \"{subject}\" instead of the expected " +
                $"\"{expected}\". It was discarded.");
        }
    }

    private static async Task<bool> MatchesChecksumAsync(string path, string expectedHex, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Left behind in the temp directory; Windows clears it eventually.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Same as above.
        }
    }
}
