using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Grouping;

/// <summary>A strategy that turns a flat list of media into event groups.</summary>
public interface IGroupingStrategy
{
    Task<List<MediaGroup>> GroupAsync(
        IReadOnlyList<MediaItem> items,
        IProgress<PipelineProgress> progress,
        CancellationToken ct);
}
