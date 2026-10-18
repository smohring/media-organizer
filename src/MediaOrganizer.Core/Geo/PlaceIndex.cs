using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Geo;

/// <summary>Answers one question about a place: does the collection show it on
/// ONE occasion, or on several separated in time?
///
/// <para>What follows from the answer:</para>
/// <list type="bullet">
/// <item><b>One visit</b> — a trip, an outing. Whatever was taken there
/// belongs together, however different the subjects: the church, the
/// restaurant and the bouncy castle of the same city trip are ONE event.</item>
/// <item><b>Several visits</b> — an everyday place: home, the grandparents,
/// work. There the place says nothing about belonging together; playing,
/// eating ice cream and rebuilding the kitchen are three events even though
/// they carry the same coordinates.</item>
/// </list>
///
/// <para>This deliberately replaces the earlier check against the configured
/// home zones. That one hung on a hand-placed coordinate with a 0.7 km radius —
/// if it was off, the living room counted as an outing and everyday shots were
/// merged into a single event. The number of visits, by contrast, is in the
/// data itself and needs no maintenance.</para></summary>
public sealed class PlaceIndex
{
    private readonly (GeoPoint Location, DateTime Taken)[] _points;
    private readonly TimeSpan _maxGap;

    /// <param name="maxGap">The gap at which two shots in the same place count
    /// as separate visits — the same boundary that separates events.</param>
    public PlaceIndex(IReadOnlyList<MediaItem> items, TimeSpan maxGap)
    {
        _maxGap = maxGap;
        _points =
        [
            .. items
                .Where(i => i.Location is not null)
                .Select(i => (Location: i.Location!.Value, i.TakenLocal))
                .OrderBy(p => p.TakenLocal),
        ];
    }

    /// <summary>How many separate visits fall within
    /// <paramref name="radiusKm"/> of the place.</summary>
    public int CountVisits(GeoPoint place, double radiusKm)
    {
        DateTime? previous = null;
        var visits = 0;
        foreach (var (location, taken) in _points)
        {
            if (location.DistanceKm(place) > radiusKm)
            {
                continue;
            }

            if (previous is null || taken - previous.Value > _maxGap)
            {
                visits++;
            }

            previous = taken;
        }

        return visits;
    }

    /// <summary>An everyday place: the collection shows it on more than one
    /// occasion. There, the place is no evidence that shots belong
    /// together.</summary>
    public bool IsRoutine(GeoPoint place, double radiusKm) => CountVisits(place, radiusKm) > 1;
}
