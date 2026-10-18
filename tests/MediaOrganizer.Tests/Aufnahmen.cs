using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Tests;

/// <summary>Building blocks for test data: shots and groups at given places and
/// times.
///
/// <para>The coordinates are invented. What matters is not where they are but
/// how far apart they are: the grouping rules turn on distances — a few
/// kilometres means an outing, tens of kilometres mean a trip — so the spacing
/// below reproduces the reported failure case exactly, while the absolute
/// position is a round number in the middle of nowhere.</para>
///
/// <para>They used to be the author's actual home and the actual places
/// visited. That put a real address into a public repository, for no gain: the
/// tests never cared which point on the globe it was.</para></summary>
internal static class Aufnahmen
{
    /// <summary>The everyday place. In the reported case the configured home
    /// zone sat about two kilometres off, which is what the old detection
    /// failed on.</summary>
    public const double ZuhauseLat = 50.0000;
    public const double ZuhauseLon = 10.0000;

    /// <summary>4.4 km from home — close, but an outing.</summary>
    public const double NahesZielLat = 50.0090;
    public const double NahesZielLon = 10.0595;

    /// <summary>About 105 km from home: far enough to be a trip.</summary>
    public const double ReisezielLat = 50.7000;
    public const double ReisezielLon = 11.0000;

    /// <summary>26 km from the trip destination — a second place on the same
    /// trip, not a second trip.</summary>
    public const double NachbarortLat = 50.4920;
    public const double NachbarortLon = 11.1560;

    /// <summary>Dieselbe Grenze, die die Pipeline für Ereignisse verwendet.</summary>
    public static readonly TimeSpan MaxLuecke = TimeSpan.FromHours(48);

    public static MediaItem Am(DateTime zeitpunkt, double lat, double lon) => new()
    {
        SourcePath = $@"C:\test\{zeitpunkt:yyyyMMdd_HHmmss}_{lat:F4}_{lon:F4}.jpg",
        Kind = MediaKind.Image,
        TimestampSource = TimestampSource.ExifOriginal,
        TakenLocal = zeitpunkt,
        Location = new GeoPoint(lat, lon),
    };

    /// <summary>Aufnahme ohne GPS — etwa ein Bildschirmfoto.</summary>
    public static MediaItem OhneGps(DateTime zeitpunkt) => new()
    {
        SourcePath = $@"C:\test\{zeitpunkt:yyyyMMdd_HHmmss}_ohne_gps.jpg",
        Kind = MediaKind.Image,
        TimestampSource = TimestampSource.ExifOriginal,
        TakenLocal = zeitpunkt,
    };

    /// <summary>Serie im Abstand von fünf Minuten am selben Ort.</summary>
    public static List<MediaItem> Serie(DateTime start, int anzahl, double lat, double lon) =>
        [.. Enumerable.Range(0, anzahl).Select(i => Am(start.AddMinutes(i * 5), lat, lon))];

    public static List<MediaItem> Serie(DateTime start, int anzahl, TimeSpan schritt, double lat, double lon) =>
        [.. Enumerable.Range(0, anzahl).Select(i => Am(start + (schritt * i), lat, lon))];

    public static MediaGroup Gruppe(string thema, int anzahl, double lat, double lon, DateTime start) => new()
    {
        Items = Serie(start, anzahl, lat, lon),
        Theme = thema,
    };

    public static MediaGroup Sammelordner(int anzahl, double lat, double lon, DateTime start)
    {
        var gruppe = Gruppe("Vermischt", anzahl, lat, lon, start);
        gruppe.IsCollection = true;
        return gruppe;
    }

    /// <summary>Eine vollständige Sammlung, die den gemeldeten Fehlerfall
    /// nachbildet: Alltag über den ganzen Mai verteilt, ein Ausflug in der
    /// Nähe, dazu ein zweitägiger Ausflug an zwei entfernte Orte.
    ///
    /// <para>Bewusst OHNE konfigurierte Heimatzone: Die Unterscheidung
    /// Alltagsort/Reise muss allein aus den Daten kommen.</para></summary>
    public static List<MediaItem> Sammlung()
    {
        var items = new List<MediaItem>();

        foreach (var tag in new[] { 1, 2, 6, 7, 9, 13, 17, 27, 28 })
        {
            items.AddRange(Serie(new DateTime(2026, 5, tag, 10, 0, 0), 10, ZuhauseLat, ZuhauseLon));
        }

        items.AddRange(Serie(new DateTime(2026, 5, 2, 15, 0, 0), 13, NahesZielLat, NahesZielLon));

        items.AddRange(Serie(new DateTime(2026, 5, 23, 14, 0, 0), 13, ReisezielLat, ReisezielLon));
        items.AddRange(Serie(new DateTime(2026, 5, 23, 19, 0, 0), 26, NachbarortLat, NachbarortLon));
        items.AddRange(Serie(new DateTime(2026, 5, 24, 9, 0, 0), 40, ReisezielLat, ReisezielLon));
        items.AddRange(Serie(new DateTime(2026, 5, 24, 13, 0, 0), 26, NachbarortLat, NachbarortLon));
        items.AddRange(Serie(new DateTime(2026, 5, 24, 16, 0, 0), 34, ReisezielLat, ReisezielLon));

        return items;
    }
}
