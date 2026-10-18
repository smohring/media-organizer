using MediaOrganizer.Core;
using MediaOrganizer.Core.Geo;
using MediaOrganizer.Core.Grouping;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Planning;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Wann eine als "unpassend" markierte Aufnahme zurück ins Ereignis
/// geholt wird, statt im Sammelordner zu landen.
///
/// <para>Das Modell markiert Aufnahmen als unpassend, wenn ihm das Motiv aus
/// der Reihe fällt. Auf einer Reise ist das der falsche Schluss: Was am
/// Ausflugsort entstand, gehört zum Ausflug. Zuhause ist es der richtige —
/// dort tragen alle Aufnahmen dieselben Koordinaten, der Ort sagt also
/// nichts aus.</para></summary>
public class SammelordnerTests
{
    // --- Themenordner IM Sammelordner ---

    private static readonly IReadOnlySet<string> Dateiarten =
        MediaOrganizer.Core.Llm.Prompts.German.FileKindCategories;

    private static MediaItem MitKategorie(DateTime zeitpunkt, string kategorie)
    {
        var item = Aufnahmen.OhneGps(zeitpunkt);
        item.Category = kategorie;
        return item;
    }

    /// <summary>Der Sammelordner wird groß — gemessen 5024 von 6039 Dateien.
    /// Eine DATEIART darin bekommt einen eigenen Ordner, statt lose im Haufen zu
    /// liegen: Screenshots gehören zu Screenshots.</summary>
    [Fact]
    public void EineDateiartBekommtEinenThemenordnerImSammelordner()
    {
        var sammel = new MediaGroup
        {
            Items =
            [
                .. Enumerable.Range(0, 4).Select(i =>
                    MitKategorie(new DateTime(2026, 5, 1, 10, 0, 0).AddMinutes(i), "Screenshot")),
                .. Enumerable.Range(0, 3).Select(i =>
                    MitKategorie(new DateTime(2026, 5, 2, 10, 0, 0).AddMinutes(i), "Beleg")),
                MitKategorie(new DateTime(2026, 5, 3, 10, 0, 0), "Rechnung"),
            ],
            IsCollection = true,
            Theme = "Vermischt",
        };

        var result = OrganizePipeline.SplitCollectionByCategory(
            [sammel], minItems: 2, fileKinds: Dateiarten);

        var screenshots = Assert.Single(result, g => g.Theme == "Screenshot");
        Assert.Equal(4, screenshots.Items.Count);
        Assert.Equal("Vermischt", screenshots.InsideFolder);
        Assert.True(screenshots.IsDateless);

        Assert.Single(result, g => g.Theme == "Beleg");

        // Einzelstück bleibt im Haufen: Ein Ordner für ein Bild ist keiner.
        var rest = Assert.Single(result, g => g.IsCollection);
        Assert.Single(rest.Items);
    }

    /// <summary>Ein MOTIV bekommt keinen. Gemeldet: ein Ordner "Fahrrad" mit
    /// fünf verschiedenen Rädern, "Kind" mit Aufnahmen aus völlig
    /// unterschiedlichen Zusammenhängen. Solche Ordner sagen nichts — dann
    /// lieber keiner.</summary>
    [Theory]
    [InlineData("Fahrrad")]
    [InlineData("Kind")]
    [InlineData("Familie")]
    [InlineData("Pflanze")]
    [InlineData("Natur")]
    public void EinMotivBekommtKeinenThemenordner(string kategorie)
    {
        var sammel = new MediaGroup
        {
            Items = [.. Enumerable.Range(0, 8).Select(i =>
                MitKategorie(new DateTime(2026, 5, 1, 10, 0, 0).AddMinutes(i), kategorie))],
            IsCollection = true,
            Theme = "Vermischt",
        };

        var result = OrganizePipeline.SplitCollectionByCategory(
            [sammel], minItems: 2, fileKinds: Dateiarten);

        Assert.Equal(8, Assert.Single(result).Items.Count);
    }

    /// <summary>Ohne Modell gibt es keine Kategorien — dann passiert hier
    /// nichts, und der Sammelordner bleibt, wie er ist.</summary>
    [Fact]
    public void OhneKategorienBleibtDerSammelordnerUnberuehrt()
    {
        var sammel = new MediaGroup
        {
            Items = [.. Enumerable.Range(0, 5)
                .Select(i => Aufnahmen.OhneGps(new DateTime(2026, 5, 1, 10, 0, 0).AddMinutes(i)))],
            IsCollection = true,
            Theme = "Vermischt",
        };

        var result = OrganizePipeline.SplitCollectionByCategory([sammel], minItems: 2, fileKinds: Dateiarten);

        Assert.Single(result);
        Assert.Equal(5, result[0].Items.Count);
    }

    /// <summary>Der Themenordner liegt IM Sammelordner, nicht daneben.</summary>
    [Fact]
    public void DerThemenordnerLiegtImSammelordner()
    {
        var path = PlanPaths.BuildTargetPath(
            @"C:\ziel", TargetHierarchy.YearMonth, "", "Screenshot",
            isDateless: true, insideFolder: "Vermischt");

        Assert.Equal(@"C:\ziel\Vermischt\Screenshot", path);
    }

    private static readonly PlaceIndex Orte = new(Aufnahmen.Sammlung(), Aufnahmen.MaxLuecke);

    private static MediaGroup? Zuordnen(List<MediaGroup> gruppen, MediaItem item, double radiusKm = 2.0) =>
        LlmGroupingStrategy.FindPlaceGroup(gruppen, item, radiusKm, Orte, Aufnahmen.MaxLuecke);

    private static MediaGroup Ausflug() => Aufnahmen.Gruppe(
        "Ausflug Reiseziel", 13,
        Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon,
        new DateTime(2026, 5, 23, 14, 0, 0));

    /// <summary>Die gemeldete Hüpfburg: mitten im Ausflug, fremdes Motiv.</summary>
    [Fact]
    public void Fremdes_Motiv_am_Ausflugsort_kommt_zurueck_ins_Ereignis()
    {
        var huepfburg = Aufnahmen.Am(
            new DateTime(2026, 5, 23, 14, 20, 0), Aufnahmen.ReisezielLat + 0.001, Aufnahmen.ReisezielLon);

        Assert.Equal("Ausflug Reiseziel", Zuordnen([Ausflug()], huepfburg)?.Theme);
    }

    /// <summary>Zuhause muss der Bauplan im Sammelordner bleiben — sonst
    /// landet alles, was das Modell nicht einordnen kann, im Alltagsordner.</summary>
    [Fact]
    public void Zuhause_bleibt_eine_unpassende_Aufnahme_liegen()
    {
        var alltag = Aufnahmen.Gruppe(
            "Mia spielt", 8, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 6, 15, 0, 0));
        var bauplan = Aufnahmen.Am(new DateTime(2026, 5, 6, 15, 20, 0), Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon);

        Assert.Null(Zuordnen([alltag], bauplan));
    }

    [Fact]
    public void Ohne_GPS_bleibt_es_bei_der_Einschaetzung_des_Modells()
    {
        Assert.Null(Zuordnen([Ausflug()], Aufnahmen.OhneGps(new DateTime(2026, 5, 23, 15, 0, 0))));
    }

    [Fact]
    public void Eine_weit_entfernte_Aufnahme_wird_nicht_hineingezogen()
    {
        var weitWeg = Aufnahmen.Am(
            new DateTime(2026, 5, 23, 14, 20, 0), Aufnahmen.NachbarortLat, Aufnahmen.NachbarortLon);

        Assert.Null(Zuordnen([Ausflug()], weitWeg));
    }

    [Fact]
    public void Radius_null_schaltet_die_Rueckholung_ab()
    {
        var huepfburg = Aufnahmen.Am(
            new DateTime(2026, 5, 23, 14, 20, 0), Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon);

        Assert.Null(Zuordnen([Ausflug()], huepfburg, radiusKm: 0));
    }

    /// <summary>Zeitlich weit weg heißt: anderer Besuch, nicht dieses Ereignis.</summary>
    [Fact]
    public void Monate_spaeter_am_selben_Ort_wird_nicht_zugeordnet()
    {
        var spaeter = Aufnahmen.Am(
            new DateTime(2026, 8, 23, 14, 20, 0), Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon);

        Assert.Null(Zuordnen([Ausflug()], spaeter));
    }
}
