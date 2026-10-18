using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Geo;

/// <summary>A place the collection shows often — a candidate for a zone.</summary>
/// <param name="Center">Centre of the shots taken there.</param>
/// <param name="PhotoCount">How many shots were taken there.</param>
/// <param name="Visits">On how many separate occasions.</param>
/// <param name="PlaceName">Resolved place name, where one could be found.</param>
public sealed record PlaceSuggestion(GeoPoint Center, int PhotoCount, int Visits, string? PlaceName)
{
    /// <summary>A place with many separate visits is an everyday place — the
    /// most likely candidate for a home zone.</summary>
    public ZoneKind LikelyKind => Visits >= 3 ? ZoneKind.Home : ZoneKind.Vacation;

    public string DisplayName => PlaceName ?? $"{Center.Latitude:F4}, {Center.Longitude:F4}";
}

/// <summary>Finds the places where most photographs were taken.
///
/// <para>Meant to replace placing a zone on a map: a suggestion drawn from the
/// user's own shots hits the centre exactly, whereas a point set by hand can
/// land two kilometres off — and that is precisely what makes the home
/// detection worthless.</para></summary>
public static class PlaceSuggestions
{
    /// <summary>Greedy clustering: the densest spot forms a cluster, its shots
    /// drop out, then the next.
    ///
    /// <para>The seed is the busiest cell of a roughly one-kilometre grid, the
    /// same trick <see cref="PlaceResolver"/> uses. It used to be the point
    /// with the most neighbours within the radius, which meant measuring every
    /// point against every other one: on a collection of 16 000 shots — 10 900
    /// of them with GPS — that is 119 million distance calculations per round,
    /// on the interface thread, and the window simply stopped responding. The
    /// grid gets the same answer in one pass.</para>
    ///
    /// <para>Cluster membership is still decided by real distance from the
    /// centre, so the result is exact; only the choice of where to start is an
    /// approximation. A place lying across a cell boundary can have its count
    /// split, which at worst reorders two suggestions of similar size.</para></summary>
    public static IReadOnlyList<PlaceSuggestion> Find(
        IReadOnlyList<MediaItem> items,
        double radiusKm,
        TimeSpan maxGap,
        int maxSuggestions = 8)
    {
        var points = items
            .Where(i => i.Location is not null)
            .Select(i => (Location: i.Location!.Value, i.TakenLocal))
            .OrderBy(p => p.TakenLocal)
            .ToList();

        var result = new List<PlaceSuggestion>();
        while (points.Count > 0 && result.Count < maxSuggestions)
        {
            var densest = points
                .GroupBy(p => (Math.Round(p.Location.Latitude, 2), Math.Round(p.Location.Longitude, 2)))
                .OrderByDescending(g => g.Count())
                .First();
            var seed = new GeoPoint(
                densest.Average(p => p.Location.Latitude),
                densest.Average(p => p.Location.Longitude));

            var cluster = points
                .Where(p => p.Location.DistanceKm(seed) <= radiusKm)
                .ToList();

            // A single photo is not a place.
            if (cluster.Count < 2)
            {
                break;
            }

            var center = new GeoPoint(
                cluster.Average(p => p.Location.Latitude),
                cluster.Average(p => p.Location.Longitude));

            string? name = null;
            try
            {
                name = OfflineGeocoder.Instance.FindNearest(center)?.DisplayName;
            }
            catch
            {
                // Resolving the name is a convenience — coordinates will do.
            }

            result.Add(new PlaceSuggestion(center, cluster.Count, CountVisits(cluster, maxGap), name));

            // By distance, not by Except: two shots taken at the same second in
            // the same spot are equal as values, and Except would drop both
            // copies from everything that follows.
            points = [.. points.Where(p => p.Location.DistanceKm(seed) > radiusKm)];
        }

        return result;
    }

    private static int CountVisits(
        List<(GeoPoint Location, DateTime TakenLocal)> cluster, TimeSpan maxGap)
    {
        var visits = 0;
        DateTime? previous = null;
        foreach (var (_, taken) in cluster.OrderBy(p => p.TakenLocal))
        {
            if (previous is null || taken - previous.Value > maxGap)
            {
                visits++;
            }

            previous = taken;
        }

        return visits;
    }
}
