using MediaOrganizer.Core.Geo;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Naming;

public enum GroupContextKind
{
    /// <summary>No GPS, or no home zones defined.</summary>
    Unknown,

    /// <summary>Inside a home zone — everyday life, and the place is
    /// irrelevant to the title.</summary>
    Home,

    /// <summary>Away from home, within a single day.</summary>
    DayTrip,

    /// <summary>Away from home across several days — very likely a
    /// holiday.</summary>
    Vacation,
}

/// <summary>The context of a group: its kind, the zone name where there is
/// one, and the resolved place.</summary>
public sealed record GroupContextInfo(GroupContextKind Kind, string? ZoneName, GeocodeResult? Place);

/// <summary>Classifies a group's context deterministically from its GPS
/// centre, the home zones and its time span. Costs no model call and steers
/// the naming — "Urlaub Hamburg" against "Mia spielt im Wohnzimmer".</summary>
public static class GroupContextClassifier
{
    /// <summary>Beyond this distance from the nearest home zone a group counts
    /// as travel; below it as the surrounding area, which is everyday
    /// life.</summary>
    internal const double TripDistanceKm = 30.0;

    /// <summary>Beyond this distance a single-day stay counts as an
    /// outing.</summary>
    private const double DayTripDistanceKm = 5.0;

    public static GroupContextInfo Classify(MediaGroup group, IReadOnlyList<HomeZone> allZones)
    {
        if (group.Centroid is not { } centroid)
        {
            return new GroupContextInfo(GroupContextKind.Unknown, null, null);
        }

        // The primary place is the largest stay cluster, not the centroid — on
        // a round trip the centroid lands in the middle of nowhere.
        var place = PlaceResolver.ResolveVisitedPlaces(group.Items, maxPlaces: 1).FirstOrDefault();

        // Erst die Zonen, und zwar ALLE Arten. Wer einen Ort benannt hat, weiß
        // besser als jede Ortsdatenbank, wie er heißt — "Ferienhaus Sylt" statt
        // "Westerland". Vorher zählten nur Heimatzonen, und Urlaubs- oder
        // sonstige Zonen wirkten nirgends: Man konnte sie anlegen, sie standen
        // in den Einstellungen, und kein Ordnername hat sie je erwähnt.
        var covering = allZones.FirstOrDefault(z =>
            z.Covers(centroid));

        // Nur eine echte Heimatzone bedeutet Alltag. Eine Ferienwohnung liegt
        // auch auf der Karte, ist aber genau das Gegenteil.
        if (covering is { Kind: ZoneKind.Home })
        {
            return new GroupContextInfo(GroupContextKind.Home, covering.Name, place);
        }

        if (covering is not null)
        {
            // Eine Urlaubszone ist erklärtermaßen ein Urlaub; eine sonstige Zone
            // richtet sich danach, ob mehr als ein Tag darin liegt.
            var away = covering.Kind == ZoneKind.Vacation
                       || group.StartLocal.Date != group.EndLocal.Date
                ? GroupContextKind.Vacation
                : GroupContextKind.DayTrip;
            return new GroupContextInfo(away, covering.Name, place);
        }

        var zones = allZones.Where(z => z.Kind == ZoneKind.Home).ToList();
        if (zones.Count == 0)
        {
            // Without home zones there is no way to decide home against travel.
            return new GroupContextInfo(GroupContextKind.Unknown, null, place);
        }

        var distanceFromHome = zones.Min(z => z.Center.DistanceKm(centroid));
        var multiDay = group.StartLocal.Date != group.EndLocal.Date;

        if (distanceFromHome >= TripDistanceKm && multiDay)
        {
            return new GroupContextInfo(GroupContextKind.Vacation, null, place);
        }

        if (distanceFromHome >= DayTripDistanceKm)
        {
            return new GroupContextInfo(GroupContextKind.DayTrip, null, place);
        }

        return new GroupContextInfo(GroupContextKind.Unknown, null, place);
    }
}
