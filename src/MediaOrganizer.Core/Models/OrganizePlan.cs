namespace MediaOrganizer.Core.Models;

/// <summary>What is meant to happen to a file — preview information only, with
/// no file operation behind it yet.</summary>
public enum PlannedStatus
{
    Move,

    Copy,

    /// <summary>A file of the same name is already in the target, or a second
    /// planned file carries the same name. On applying it gets a unique name,
    /// "name (2).ext" — nothing is ever overwritten.</summary>
    NameConflict,

    /// <summary>Same name AND same content, confirmed by SHA-256. On applying
    /// it is skipped and the source is left untouched.</summary>
    PossibleDuplicate,
}

/// <summary>A file in the plan: where it would go and what would happen
/// there.</summary>
public sealed class PlannedItem
{
    public required MediaItem Item { get; init; }

    public PlannedStatus Status { get; set; }

    /// <summary>Why it has that status — the colliding target path, for
    /// instance.</summary>
    public string? Reason { get; set; }

    public string FileName => Item.FileName;
}

/// <summary>A target folder in the plan. It does not exist on disk yet while
/// <see cref="IsNew"/> holds.</summary>
public sealed class PlannedFolder
{
    /// <summary>Full target path, including the hierarchy levels.</summary>
    public required string TargetPath { get; set; }

    /// <summary>The topic part of the folder name, without the date
    /// prefix.</summary>
    public required string Theme { get; set; }

    /// <summary>Date prefix "yyyyMMdd". Empty for a dateless folder that spans
    /// the whole collection; on a dateless folder split per period it holds
    /// that period and decides the hierarchy level alone, never the name (see
    /// <c>PlanPaths.BuildTargetPath</c>).</summary>
    public string DatePrefix { get; set; } = "";

    /// <summary>The collecting folder for unmatched shots. Carries no date
    /// prefix in its name.</summary>
    public bool IsCollection { get; set; }

    /// <summary>Name of the folder this one sits inside — set on the themed
    /// subfolders of the collecting folder ("Vermischt\Screenshot"), empty
    /// otherwise.</summary>
    public string InsideFolder { get; set; } = "";

    /// <summary>No date prefix in the name: the collecting folder and the
    /// everyday folder of the EXIF-only mode. Each spans a stretch of time —
    /// the whole collection, or one year or month once the split is switched
    /// on.</summary>
    public bool IsDateless { get; set; }

    /// <summary>The folder does not exist in the target yet.</summary>
    public bool IsNew { get; set; }

    /// <summary>Renamed by the user — the date prefix is then NOT recalculated
    /// when files are moved around.</summary>
    public bool HasManualName { get; set; }

    public List<PlannedItem> Items { get; init; } = [];

    public DateTime GroupStart => Items.Count > 0
        ? Items.Min(i => i.Item.TakenLocal)
        : DateTime.MinValue;
}

/// <summary>The result of a sorting run before any file operation: which
/// folders would appear and which file would go where.
///
/// <para>Freely editable and free to throw away — only
/// <c>PlanExecutor</c> writes to disk.</para></summary>
public sealed class OrganizePlan
{
    public required string BaseFolder { get; init; }

    public required TargetHierarchy Hierarchy { get; init; }

    public required TransferMode Mode { get; init; }

    public List<PlannedFolder> Folders { get; init; } = [];

    public List<RunWarning> Warnings { get; init; } = [];

    public int TotalFiles => Folders.Sum(f => f.Items.Count);

    public int NewFolderCount => Folders.Count(f => f.IsNew);

    public int ConflictCount => Folders.SelectMany(f => f.Items)
        .Count(i => i.Status == PlannedStatus.NameConflict);

    public int DuplicateCount => Folders.SelectMany(f => f.Items)
        .Count(i => i.Status == PlannedStatus.PossibleDuplicate);
}
