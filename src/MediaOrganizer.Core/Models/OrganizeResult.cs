namespace MediaOrganizer.Core.Models;

public enum FileOpStatus
{
    Moved,
    Copied,

    /// <summary>The target already held a file with identical content; the
    /// source was left untouched.</summary>
    SkippedDuplicate,

    Failed,
}

/// <summary>The outcome of one file operation.</summary>
public sealed class FileOperationResult
{
    public required MediaItem Item { get; init; }

    /// <summary>Final path in the target; null when skipped or failed.</summary>
    public string? FinalPath { get; set; }

    public FileOpStatus Status { get; set; }

    public string? Error { get; set; }
}

/// <summary>A target folder with files in it after a run.</summary>
public sealed class OrganizedFolder
{
    public required string FolderPath { get; init; }

    public required string Theme { get; init; }

    public required DateTime GroupStart { get; init; }

    public required List<FileOperationResult> Operations { get; init; }
}

/// <summary>The overall outcome of a processing run.</summary>
public sealed class OrganizeRunResult
{
    public List<OrganizedFolder> Folders { get; init; } = [];

    public List<RunWarning> Warnings { get; init; } = [];

    public int TotalFiles => Folders.Sum(f => f.Operations.Count);
}
