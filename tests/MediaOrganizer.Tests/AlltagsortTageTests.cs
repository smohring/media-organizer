using MediaOrganizer.Core;
using MediaOrganizer.Core.Geo;
using MediaOrganizer.Core.Models;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>An einem Alltagsort ist der Tageswechsel eine Grenze, auf einer
/// Reise nicht.
///
/// <para>Gemeldet: Ein Ordner "20260615_Verkaufsartikel im Laden", dessen
/// letztes Bild am nächsten Vormittag entstand und das Kombiinstrument eines
/// Autos zeigt. Die Ereignis-Lücke sind 24 Stunden — der nächste Vormittag
/// liegt darunter, also blieb er drin.</para></summary>
public class AlltagsortTageTests
{
    private static readonly PlaceIndex Orte = new(Aufnahmen.Sammlung(), Aufnahmen.MaxLuecke);

    private static readonly HomeZone Zuhause = new()
    {
        Name = "Zuhause",
        Latitude = Aufnahmen.ZuhauseLat,
        Longitude = Aufnahmen.ZuhauseLon,
        Kind = ZoneKind.Home,
        RadiusKm = 0.7,
    };

    private static List<MediaGroup> Trennen(params MediaGroup[] gruppen) =>
        OrganizePipeline.SplitEverydayPlacesByDay(
            [.. gruppen], radiusKm: 10, Orte, [Zuhause]);

    private static MediaGroup UeberZweiTage(double lat, double lon)
    {
        var gruppe = Aufnahmen.Gruppe("Laden", 4, lat, lon, new DateTime(2026, 6, 15, 14, 0, 0));
        gruppe.Items.AddRange(
            Aufnahmen.Gruppe("x", 1, lat, lon, new DateTime(2026, 6, 16, 9, 0, 0)).Items);
        return gruppe;
    }

    /// <summary>Der gemeldete Fall: Laden am 15., Auto am 16. — zwei Ordner.</summary>
    [Fact]
    public void AmAlltagsortTrenntDerTageswechsel()
    {
        var ergebnis = Trennen(UeberZweiTage(Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon));

        Assert.Equal(2, ergebnis.Count);
        Assert.Equal([4, 1], ergebnis.Select(g => g.Items.Count));
    }

    /// <summary>Der Gegenfall, und er ist der wichtigere: Auf einer Reise bleibt
    /// ein Ereignis über Mitternacht ganz.</summary>
    [Fact]
    public void AufReisenBleibtDerTagUeberschrittenGanz()
    {
        var ergebnis = Trennen(UeberZweiTage(Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));

        Assert.Equal(5, Assert.Single(ergebnis).Items.Count);
    }

    /// <summary>Eine Gruppe an einem einzigen Tag wird nicht angefasst.</summary>
    [Fact]
    public void EinTagBleibtEinTag()
    {
        var gruppe = Aufnahmen.Gruppe(
            "Laden", 5, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 6, 15, 14, 0, 0));

        Assert.Same(gruppe, Assert.Single(Trennen(gruppe)));
    }

    /// <summary>Sammel- und Alltagsordner spannen die ganze Sammlung — dort ist
    /// ein Tageswechsel keine Grenze.</summary>
    [Fact]
    public void DieToepfeBleibenUnberuehrt()
    {
        var sammel = Aufnahmen.Sammelordner(
            4, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 6, 15, 14, 0, 0));
        sammel.Items.AddRange(Aufnahmen.Gruppe(
            "x", 2, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon,
            new DateTime(2026, 6, 17, 9, 0, 0)).Items);

        Assert.Same(sammel, Assert.Single(Trennen(sammel)));
    }
}
