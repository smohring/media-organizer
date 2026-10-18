using MediaOrganizer.Core.Grouping;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Geo;

/// <summary>Guesses the home zone when the user has configured none: the place
/// that keeps turning up across several clearly separated time windows (more
/// than 48 h apart) is very likely home.
///
/// <para>A single holiday, even a long one, deliberately does not meet that
/// criterion.</para></summary>
public static class HomeZoneDetector
{
    /// <summary>The name is passed in rather than fixed here: in EXIF-only mode
    /// it becomes the name of a folder on disk, and that has to follow the
    /// chosen output language.</summary>
    public static HomeZone? DetectImplicitHome(IReadOnlyList<MediaItem> items, string zoneName)
    {
        var located = items.Where(i => i.Location is not null).ToList();
        if (located.Count < 8)
        {
            return null;
        }

        var windows = TimeClustering.SplitAtGaps(
            items.OrderBy(i => i.TakenLocal).ToList(), TimeSpan.FromHours(48));
        if (windows.Count < 3)
        {
            return null;
        }

        // Dichtester Ort (~1-km-Raster).
        var top = located
            .GroupBy(i => (Math.Round(i.Location!.Value.Latitude, 2), Math.Round(i.Location!.Value.Longitude, 2)))
            .OrderByDescending(g => g.Count())
            .First();
        var centroid = new GeoPoint(
            top.Average(i => i.Location!.Value.Latitude),
            top.Average(i => i.Location!.Value.Longitude));

        var windowsWithCluster = windows.Count(w => w.Any(i =>
            i.Location is { } l && l.DistanceKm(centroid) <= 1.5));
        if (windowsWithCluster < 3)
        {
            return null;
        }

        return new HomeZone
        {
            Name = zoneName,
            Latitude = centroid.Latitude,
            Longitude = centroid.Longitude,
        };
    }
}
