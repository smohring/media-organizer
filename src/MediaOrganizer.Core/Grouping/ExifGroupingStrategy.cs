using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Grouping;

/// <summary>Grouping from metadata alone, without the model.
///
/// <para>A hard split at gaps above the configured maximum event gap: a holiday
/// photographed every few days stays together, a pause longer than that
/// separates two visits to the same place.</para>
///
/// <para><paramref name="splitOnPlaceChange"/> decides whether the place has a
/// say here as well:</para>
/// <list type="bullet">
/// <item><b>false — EXIF-only mode.</b> Time alone. The place is dealt with
/// afterwards by <c>OrganizePipeline.SplitByPlace</c>, which is both finer
/// (two kilometres instead of 25) and fairer (it looks at the whole group, not
/// at two neighbouring shots), and by then the shots without coordinates are
/// already out. A rule that also split at every 8 h pause and at every missing
/// coordinate would only fragment what the later steps have to glue back
/// together.</item>
/// <item><b>true — the combined mode</b>, which uses this as its pre-cut before
/// the model looks at the content, and where neither of those later steps runs.
/// There a gap over 8 h splits unless GPS shows things continue within 25 km,
/// and a missing coordinate splits.</item>
/// </list></summary>
public sealed class ExifGroupingStrategy(TimeSpan maxEventGap, bool splitOnPlaceChange = true)
    : IGroupingStrategy
{
    private static readonly TimeSpan SoftGap = TimeSpan.FromHours(8);
    private const double SamePlaceKm = 25.0;

    public ExifGroupingStrategy()
        : this(TimeSpan.FromDays(7))
    {
    }

    public Task<List<MediaGroup>> GroupAsync(
        IReadOnlyList<MediaItem> items,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new PipelineProgress(
            PipelinePhase.Group, 0, 1, ProgressKeys.GroupingByTimeAndGps));

        var sorted = items.OrderBy(i => i.TakenLocal).ToList();
        var groups = new List<MediaGroup>();
        List<MediaItem>? current = null;
        MediaItem? previous = null;

        foreach (var item in sorted)
        {
            ct.ThrowIfCancellationRequested();

            var startNew = current is null || previous is null || ShouldSplit(previous, item);
            if (startNew)
            {
                current = [];
                groups.Add(new MediaGroup { Items = current });
            }

            current!.Add(item);
            previous = item;
        }

        progress.Report(new PipelineProgress(
            PipelinePhase.Group, 1, 1, ProgressKeys.GroupsFormed, groups.Count));
        return Task.FromResult(groups);
    }

    private bool ShouldSplit(MediaItem previous, MediaItem next)
    {
        var gap = next.TakenLocal - previous.TakenLocal;
        if (gap > maxEventGap)
        {
            return true;
        }

        // Ohne Modell trennt allein die Zeit. Den Ort übernimmt danach
        // OrganizePipeline.SplitByPlace — und zwar besser: auf zwei Kilometer
        // genau statt auf 25, und für die ganze Gruppe statt nur für zwei
        // benachbarte Aufnahmen. Aufnahmen ohne GPS sind dort ohnehin schon
        // aussortiert, ein Schnitt an ihrer fehlenden Koordinate wäre also
        // sinnlos.
        if (!splitOnPlaceChange)
        {
            return false;
        }

        if (gap <= SoftGap)
        {
            return false;
        }

        // A long pause: only keep them together when GPS shows the same place.
        if (previous.Location is { } a && next.Location is { } b)
        {
            return a.DistanceKm(b) >= SamePlaceKm;
        }

        return true;
    }
}
