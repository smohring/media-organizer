using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Planning;

/// <summary>A folder that already exists in the source.</summary>
public sealed class SourceFolderInfo
{
    public required string Path { get; init; }

    /// <summary>Path relative to the source folder. Files sitting directly in
    /// the source folder appear under a name of their own.</summary>
    public required string DisplayName { get; init; }

    public List<MediaItem> Items { get; init; } = [];

    public DateTime Start => Items.Count > 0 ? Items.Min(i => i.TakenLocal) : DateTime.MinValue;
}

/// <summary>Ist-Zustand des Quellordners.</summary>
public sealed class SourceSnapshot
{
    public required string Root { get; init; }

    public List<SourceFolderInfo> Folders { get; init; } = [];

    public List<RunWarning> Warnings { get; init; } = [];

    public int TotalFiles => Folders.Sum(f => f.Items.Count);
}

/// <summary>Reads the existing structure of the source folder — from the file
/// system and EXIF alone, with no model and no file operation. This is what
/// shows the user the current state before a run has even started.</summary>
public static class SourceSnapshotScanner
{
    public const string RootFolderLabel = "(direkt im Quellordner)";

    public static SourceSnapshot Scan(
        string sourceFolder,
        string? targetFolder,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(sourceFolder);
        var snapshot = new SourceSnapshot { Root = root };
        if (!Directory.Exists(root))
        {
            return snapshot;
        }

        var target = string.IsNullOrWhiteSpace(targetFolder) ? null : Path.GetFullPath(targetFolder);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var files = new List<(string Path, MediaKind Kind)>();
        var skipped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", enumeration))
        {
            ct.ThrowIfCancellationRequested();

            // When the target folder sits inside the source folder, it is not part
        // of the current state.
            if (target is not null
                && path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (MediaMetadataReader.Classify(path) is { } kind)
            {
                files.Add((path, kind));
                continue;
            }

            // Sidecars and marker files are not media and not news either —
            // the same silence as in the pipeline, or the two views would
            // disagree about the same folder.
            if (MediaMetadataReader.IsCompanionFile(path))
            {
                continue;
            }

            var extension = Path.GetExtension(path);
            extension = string.IsNullOrEmpty(extension) ? "(no extension)" : extension.ToLowerInvariant();
            skipped[extension] = skipped.GetValueOrDefault(extension) + 1;
        }

        if (skipped.Count > 0)
        {
            snapshot.Warnings.Add(RunWarnings.Skipped(
                skipped.Values.Sum(),
                string.Join(", ", skipped.OrderByDescending(e => e.Value).Take(6)
                    .Select(e => $"{e.Key} ({e.Value})"))));
        }

        var reader = new MediaMetadataReader();
        var byFolder = new Dictionary<string, SourceFolderInfo>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new PipelineProgress(
                PipelinePhase.Metadata, i, files.Count,
                ProgressKeys.ReadingSourceStructure, i + 1, files.Count));

            var (path, kind) = files[i];
            MediaItem item;
            try
            {
                item = reader.Read(path, kind);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(path) ?? root;
            if (!byFolder.TryGetValue(directory, out var folder))
            {
                var relative = Path.GetRelativePath(root, directory);
                folder = new SourceFolderInfo
                {
                    Path = directory,
                    DisplayName = relative is "." or "" ? RootFolderLabel : relative,
                };
                byFolder[directory] = folder;
            }

            folder.Items.Add(item);
        }

        snapshot.Folders.AddRange(byFolder.Values
            .OrderBy(f => f.DisplayName == RootFolderLabel ? 0 : 1)
            .ThenBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase));

        foreach (var folder in snapshot.Folders)
        {
            folder.Items.Sort((a, b) => a.TakenLocal.CompareTo(b.TakenLocal));
        }

        progress?.Report(new PipelineProgress(
            PipelinePhase.Done, files.Count, files.Count, ProgressKeys.SourceStructureRead));
        return snapshot;
    }
}
