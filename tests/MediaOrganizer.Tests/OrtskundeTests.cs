using MediaOrganizer.Core;
using MediaOrganizer.Core.Geo;
using MediaOrganizer.Core.Grouping;
using MediaOrganizer.Core.Models;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Die Unterscheidung Alltagsort/Reise und was daran hängt.
///
/// <para>Alle Fälle bilden gemeldete Fehlbilder nach. Der gemeinsame Auslöser
/// war, dass die Regeln an einer handgesetzten Heimatzone hingen, die rund
/// zwei Kilometer neben den Aufnahmeorten lag — dadurch galt die eigene
/// Wohnung als Ausflug. Die Testsammlung hat deshalb bewusst keine Zone.</para></summary>
public class OrtskundeTests
{
    private static readonly PlaceIndex Orte = new(Aufnahmen.Sammlung(), Aufnahmen.MaxLuecke);

    private static List<MediaGroup> Zusammenfassen(List<MediaGroup> gruppen, double radiusKm) =>
        OrganizePipeline.MergeSameRegion(gruppen, radiusKm, Aufnahmen.MaxLuecke, Orte);

    // --- Ortskunde selbst ---

    [Fact]
    public void Zuhause_gilt_als_Alltagsort()
    {
        Assert.True(Orte.IsRoutine(new GeoPoint(Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon), 10));
    }

    [Fact]
    public void Ein_einmalig_besuchter_Ort_gilt_als_Reise()
    {
        Assert.False(Orte.IsRoutine(new GeoPoint(Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon), 10));
    }

    // --- Zone schlägt Datenlage ---

    /// <summary>Wer eine Zone gesetzt hat, hat die Frage beantwortet: Ein
    /// Urlaubsort bleibt eine Reise, auch wenn die Sammlung ihn mehrfach zeigt.
    /// Vorher entschied allein die Besuchszählung, und ab dem zweiten Aufenthalt
    /// zerfiel dieselbe Ferienwohnung in Einzelordner.</summary>
    [Fact]
    public void Eine_Urlaubszone_bleibt_Reise_trotz_mehrerer_Besuche()
    {
        var urlaub = new HomeZone
        {
            Name = "Ferienwohnung",
            Latitude = Aufnahmen.ZuhauseLat,
            Longitude = Aufnahmen.ZuhauseLon,
            Kind = ZoneKind.Vacation,
            RadiusKm = 2,
        };

        List<MediaGroup> Gruppen() =>
        [
            Aufnahmen.Gruppe("A", 5, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon,
                new DateTime(2026, 5, 1, 10, 0, 0)),
            Aufnahmen.Gruppe("B", 5, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon,
                new DateTime(2026, 5, 1, 20, 0, 0)),
        ];

        // Ohne Zone: mehrfach besuchter Ort — Alltag, also getrennt lassen.
        Assert.Equal(2, OrganizePipeline.MergeSameRegion(
            Gruppen(), 10, Aufnahmen.MaxLuecke, Orte).Count);

        // Mit Urlaubszone: eine Reise, die Etappen gehören zusammen.
        Assert.Single(OrganizePipeline.MergeSameRegion(
            Gruppen(), 10, Aufnahmen.MaxLuecke, Orte, [urlaub]));
    }

    /// <summary>Und die Gegenrichtung: Eine Heimatzone macht die Gegend zum
    /// Alltag, auch wo die Daten nur einen Besuch zeigen.</summary>
    [Fact]
    public void Eine_Heimatzone_bleibt_Alltag_trotz_eines_einzigen_Besuchs()
    {
        var zuhause = new HomeZone
        {
            Name = "Zuhause",
            Latitude = Aufnahmen.ReisezielLat,
            Longitude = Aufnahmen.ReisezielLon,
            Kind = ZoneKind.Home,
            RadiusKm = 2,
        };

        List<MediaGroup> Gruppen() =>
        [
            Aufnahmen.Gruppe("A", 5, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon,
                new DateTime(2026, 5, 23, 10, 0, 0)),
            Aufnahmen.Gruppe("B", 5, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon,
                new DateTime(2026, 5, 23, 20, 0, 0)),
        ];

        Assert.Single(OrganizePipeline.MergeSameRegion(
            Gruppen(), 10, Aufnahmen.MaxLuecke, Orte));
        Assert.Equal(2, OrganizePipeline.MergeSameRegion(
            Gruppen(), 10, Aufnahmen.MaxLuecke, Orte, [zuhause]).Count);
    }

    /// <summary>Die eine Zeitgrenze wirkt auch hier — und in diese Richtung ist
    /// sie empfindlich: Je GRÖSSER sie steht, desto mehr Besuche verschmelzen zu
    /// einem, bis ein Alltagsort wie eine einmalige Reise aussieht. Das ist der
    /// Grund für den kleinen Standardwert; dieser Test hält die Schwelle
    /// fest, an der es kippt.</summary>
    [Fact]
    public void Die_Besuchsluecke_entscheidet_ueber_Alltag_und_Reise()
    {
        var zuhause = new GeoPoint(Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon);
        var alleDreiTage = Enumerable.Range(0, 10)
            .SelectMany(i => Aufnahmen.Serie(
                new DateTime(2026, 5, 1, 10, 0, 0).AddDays(i * 3), 3,
                Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon))
            .ToList();

        // Zwei Tage: jeder Besuch zählt einzeln — Alltag.
        Assert.True(new PlaceIndex(alleDreiTage, TimeSpan.FromHours(48)).IsRoutine(zuhause, 10));

        // Sieben Tage: alles verschmilzt zu einem Besuch — sähe aus wie eine Reise.
        Assert.False(new PlaceIndex(alleDreiTage, TimeSpan.FromDays(7)).IsRoutine(zuhause, 10));
    }

    /// <summary>Ein Ausflugsziel dicht bei zuhause erbt dessen Alltagscharakter,
    /// sobald der Radius beide umfasst — richtig so: Es ist die Gegend, in der
    /// ohnehin ständig fotografiert wird.</summary>
    [Fact]
    public void Ausflug_dicht_bei_zuhause_gilt_im_grossen_Radius_als_Alltag()
    {
        Assert.True(Orte.IsRoutine(new GeoPoint(Aufnahmen.NahesZielLat, Aufnahmen.NahesZielLon), 10));
    }

    // --- Meldung: Wohnung und Biergarten 4,3 km entfernt in einem Ordner ---

    [Fact]
    public void Wohnung_und_nahes_Ausflugsziel_bleiben_getrennt()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("Mia beim Essen", 6, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 2, 10, 0, 0)),
            Aufnahmen.Gruppe("Ausflug NahesZiel", 13, Aufnahmen.NahesZielLat, Aufnahmen.NahesZielLon, new DateTime(2026, 5, 2, 15, 0, 0)),
        ], radiusKm: 10);

        Assert.Equal(2, ergebnis.Count);
    }

    // --- Meldung: drei Tätigkeiten zuhause wurden zu 37 Dateien ---

    [Fact]
    public void Verschiedene_Taetigkeiten_zuhause_bleiben_getrennte_Ereignisse()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("Mia spielt Sofa", 12, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 6, 10, 0, 0)),
            Aufnahmen.Gruppe("Eis essen", 25, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 6, 15, 0, 0)),
            Aufnahmen.Gruppe("Holzbauprojekt", 3, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 6, 18, 0, 0)),
        ], radiusKm: 10);

        Assert.Equal(3, ergebnis.Count);
    }

    // --- Meldung: mehrere Reiseziel-Ordner für einen Ausflug ---

    [Fact]
    public void Etappen_desselben_Ausflugs_werden_zusammengefuehrt()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("In der Kirche", 9, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 14, 0, 0)),
            Aufnahmen.Gruppe("Im Restaurant", 2, Aufnahmen.ReisezielLat + 0.002, Aufnahmen.ReisezielLon + 0.003, new DateTime(2026, 5, 23, 16, 0, 0)),
            Aufnahmen.Gruppe("Stadtansicht", 2, Aufnahmen.ReisezielLat + 0.004, Aufnahmen.ReisezielLon - 0.002, new DateTime(2026, 5, 23, 17, 0, 0)),
        ], radiusKm: 10);

        Assert.Single(ergebnis);
        Assert.Equal(13, ergebnis[0].Items.Count);
    }

    /// <summary>Zwischen zwei Etappen kann eine Gruppe an anderem Ort liegen.
    /// Ein Vergleich nur mit dem direkten Vorgänger ließe die Etappen für immer
    /// getrennt — genau daran scheiterte es zuvor.</summary>
    [Fact]
    public void Eine_Gruppe_dazwischen_blockiert_die_Zusammenfuehrung_nicht()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("Reiseziel Vormittag", 38, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 24, 9, 0, 0)),
            Aufnahmen.Gruppe("Campingplatz", 26, Aufnahmen.NachbarortLat, Aufnahmen.NachbarortLon, new DateTime(2026, 5, 24, 13, 0, 0)),
            Aufnahmen.Gruppe("Reiseziel Umzug", 11, Aufnahmen.ReisezielLat + 0.003, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 24, 16, 0, 0)),
        ], radiusKm: 10);

        Assert.Equal(2, ergebnis.Count);
        Assert.Equal(49, ergebnis.Single(g => g.Theme.StartsWith("Reiseziel")).Items.Count);
        Assert.Equal(26, ergebnis.Single(g => g.Theme == "Campingplatz").Items.Count);
    }

    [Theory]
    [InlineData(10, 2)]   // Campingplatz 25 km weit weg: eigener Ordner
    [InlineData(30, 1)]   // größerer Radius: Teil derselben Unternehmung
    public void Der_Radius_entscheidet_ueber_entfernte_Etappen(double radiusKm, int erwarteteGruppen)
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("Reiseziel", 9, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 24, 9, 0, 0)),
            Aufnahmen.Gruppe("Camping", 26, Aufnahmen.NachbarortLat, Aufnahmen.NachbarortLon, new DateTime(2026, 5, 24, 19, 0, 0)),
        ], radiusKm);

        Assert.Equal(erwarteteGruppen, ergebnis.Count);
    }

    [Fact]
    public void Radius_null_schaltet_die_Zusammenfuehrung_ab()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("A", 9, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 14, 0, 0)),
            Aufnahmen.Gruppe("B", 4, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 17, 0, 0)),
        ], radiusKm: 0);

        Assert.Equal(2, ergebnis.Count);
    }

    [Fact]
    public void Der_Sammelordner_wird_nie_eingesaugt()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("Ausflug", 9, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 14, 0, 0)),
            Aufnahmen.Sammelordner(5, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 16, 0, 0)),
        ], radiusKm: 10);

        Assert.Equal(2, ergebnis.Count);
    }

    /// <summary>Derselbe Ort kann Monate später erneut besucht worden sein.</summary>
    [Fact]
    public void Gleicher_Ort_mit_Monaten_Abstand_wird_nicht_zusammengefuehrt()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("Reiseziel Mai", 9, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 14, 0, 0)),
            Aufnahmen.Gruppe("Reiseziel August", 9, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 8, 23, 14, 0, 0)),
        ], radiusKm: 10);

        Assert.Equal(2, ergebnis.Count);
    }
}
