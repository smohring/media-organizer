using MediaOrganizer.Core.Llm;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Grouping;

/// <summary>Combined mode: describes every image with the model and hands the
/// collection on as one single group.
///
/// <para>The cutting is the EXIF chain's job — the same one the EXIF-only mode
/// runs. What the model adds comes later in the pipeline: names, merging of
/// related groups, and events inside the everyday and collecting
/// folders.</para></summary>
public sealed class CombinedGroupingStrategy(
    MediaDescriber describer,
    int maxParallel,
    bool reuseBursts) : IGroupingStrategy
{
    public async Task<List<MediaGroup>> GroupAsync(
        IReadOnlyList<MediaItem> items,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        await LlmGroupingStrategy.DescribeAllAsync(
                [.. items.OrderBy(i => i.TakenLocal)], describer, maxParallel, reuseBursts, progress, ct)
            .ConfigureAwait(false);

        return await new ExifGroupingStrategy(TimeSpan.MaxValue, splitOnPlaceChange: false)
            .GroupAsync(items, progress, ct).ConfigureAwait(false);
    }
}
