namespace MediaOrganizer.Core.Models;

/// <summary>A line the core wants shown, as a resource key and its arguments.
///
/// <para>Same reason as <see cref="PipelineProgress"/>: the core has no
/// interface language, so it never produces a finished sentence. Used where a
/// status line is not part of a run — installing ffmpeg, fetching the
/// recognition models, updating the application.</para></summary>
public readonly record struct StatusReport(string MessageKey, params object[] MessageArgs);

/// <summary>Every key of a <see cref="StatusReport"/>. Both interface
/// dictionaries have to define each of them; <c>UiStringsTests</c> checks
/// it.</summary>
public static class SetupKeys
{
    public const string DownloadingFile = "S.Setup.DownloadingFile";
    public const string DownloadingUpdate = "S.Setup.DownloadingUpdate";
    public const string VerifyingDownload = "S.Setup.VerifyingDownload";
    public const string ArchiveAlreadyThere = "S.Setup.ArchiveAlreadyThere";
    public const string Unpacking = "S.Setup.Unpacking";
    public const string UpdateReady = "S.Setup.UpdateReady";

    /// <summary>Why this copy cannot replace itself. Reported before the
    /// download rather than after it.</summary>
    public const string RunsFromDriveRoot = "S.Setup.RunsFromDriveRoot";

    public const string NotAnApplicationFolder = "S.Setup.NotAnApplicationFolder";

    public const string FolderNotWritable = "S.Setup.FolderNotWritable";
}
