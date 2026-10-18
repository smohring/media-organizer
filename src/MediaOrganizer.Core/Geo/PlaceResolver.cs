using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Geo;

/// <summary>Resolves the places a group actually visited.
///
/// <para>This matters on a trip with several stops: the geometric centroid
/// would sit somewhere in between and geocode to a random hamlet. Instead the
/// largest stay clusters (on a roughly 1 km grid) are resolved individually,
/// ordered by how many shots each holds.</para></summary>
public static class PlaceResolver
{
    public static List<GeocodeResult> ResolveVisitedPlaces(
        IEnumerable<MediaItem> items, int maxPlaces = 3, double maxDistanceKm = 50)
    {
        var clusters = items
            .Where(i => i.Location is not null)
            .Select(i => i.Location!.Value)
            .GroupBy(p => (Math.Round(p.Latitude, 2), Math.Round(p.Longitude, 2)))
            .OrderByDescending(g => g.Count())
            .ToList();

        var places = new List<GeocodeResult>();
        foreach (var cluster in clusters)
        {
            if (places.Count >= maxPlaces)
            {
                break;
            }

            var centroid = new GeoPoint(
                cluster.Average(p => p.Latitude),
                cluster.Average(p => p.Longitude));

            GeocodeResult? place;
            try
            {
                place = OfflineGeocoder.Instance.FindNearest(centroid, maxDistanceKm);
            }
            catch
            {
                // Resolving the name is a convenience.
                continue;
            }

            if (place is not null
                && !places.Any(p => p.Name.Equals(place.Name, StringComparison.OrdinalIgnoreCase)))
            {
                places.Add(place);
            }
        }

        return places;
    }
}
