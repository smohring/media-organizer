using MediaOrganizer.Core.Grouping;
using MediaOrganizer.Core.Models;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Grouping by how a picture LOOKS instead of by what a one-line
/// description says about it.
///
/// <para>Reported case: "20250921_Anna im Wald" held a family gathering at a
/// table, an afternoon on the carpet at home, a sandpit and a cyclist. The
/// descriptions read alike ("Anna with wood…"), the pictures do not — measured
/// 0.59 between the two the model merged.</para>
///
/// <para>The rule the user set: rather several folders than one that mixes what
/// does not belong. Every case here is written to that direction.</para></summary>
public class BildVektorTests
{
    private static readonly DateTime Tag = new(2026, 5, 17, 10, 0, 0);

    private static readonly TimeSpan EinTag = TimeSpan.FromDays(1);

    /// <summary>Vectors of length 4 are enough to test the rules — what matters
    /// is the angle between them, not the dimension.</summary>
    private static float[] Vektor(params float[] werte)
    {
        var laenge = MathF.Sqrt(werte.Sum(w => w * w));
        return [.. werte.Select(w => w / laenge)];
    }

    private static MediaItem Aufnahme(int minute) =>
        Aufnahmen.OhneGps(Tag.AddMinutes(minute));

    private static (List<MediaItem> Items, Dictionary<MediaItem, float[]> Vektoren) Szene(
        params (int Minute, float[] Vektor)[] eingaben)
    {
        var items = new List<MediaItem>();
        var vektoren = new Dictionary<MediaItem, float[]>();
        foreach (var (minute, vektor) in eingaben)
        {
            var item = Aufnahme(minute);
            items.Add(item);
            vektoren[item] = vektor;
        }

        return (items, vektoren);
    }

    // --- Ähnlichkeit ---

    [Fact]
    public void GleicherVektorIstEins() =>
        Assert.Equal(1.0f, SceneEmbedder.Similarity(Vektor(1, 2, 3, 4), Vektor(1, 2, 3, 4)), 3);

    [Fact]
    public void SenkrechteVektorenSindNull() =>
        Assert.Equal(0.0f, SceneEmbedder.Similarity(Vektor(1, 0, 0, 0), Vektor(0, 1, 0, 0)), 3);

    /// <summary>Unterschiedliche Länge heißt: nicht vergleichbar. Lieber 0 als
    /// ein Zufallswert, denn 0 trennt — und im Zweifel wird getrennt.</summary>
    [Fact]
    public void UnterschiedlicheLaengeIstNull() =>
        Assert.Equal(0.0f, SceneEmbedder.Similarity(Vektor(1, 0), Vektor(1, 0, 0, 0)), 3);

    // --- Clusterung ---

    [Fact]
    public void AehnlicheAufnahmenWerdenEineGruppe()
    {
        var (items, vektoren) = Szene(
            (0, Vektor(1, 0.05f, 0, 0)),
            (5, Vektor(1, 0.10f, 0, 0)),
            (9, Vektor(1, 0.02f, 0, 0)));

        Assert.Equal(3, Assert.Single(SceneClustering.Cluster(items, vektoren, EinTag)).Count);
    }

    /// <summary>Der gemeldete Fall: Was verschieden aussieht, darf nicht
    /// zusammen in einen datierten Ordner — auch nicht am selben Tag.</summary>
    [Fact]
    public void VerschiedeneAnlaesseBleibenGetrennt()
    {
        var (items, vektoren) = Szene(
            (0, Vektor(1, 0, 0, 0)),
            (5, Vektor(1, 0.05f, 0, 0)),
            (10, Vektor(0, 1, 0, 0)),
            (15, Vektor(0, 0, 1, 0)));

        var cluster = SceneClustering.Cluster(items, vektoren, EinTag);

        Assert.Equal(3, cluster.Count);
        Assert.Equal(2, cluster.Max(c => c.Count));
    }

    /// <summary>Die Kehrseite, und sie ist ausdrücklich gewollt: Bei knapper
    /// Ähnlichkeit wird getrennt, nicht zusammengelegt.</summary>
    [Fact]
    public void ImZweifelWirdGetrennt()
    {
        // Knapp unter der Schwelle — 0,70 gegen 0,72.
        var knapp = MathF.Sqrt(1 - (0.70f * 0.70f));
        var (items, vektoren) = Szene(
            (0, Vektor(1, 0, 0, 0)),
            (5, Vektor(0.70f, knapp, 0, 0)));

        Assert.Equal(2, SceneClustering.Cluster(items, vektoren, EinTag).Count);
    }

    /// <summary>Zeit bleibt Wächter: Derselbe Garten sieht im Juni aus wie im
    /// August. Ohne die Zeitgrenze würden zwei Grillabende ein Ordner.</summary>
    [Fact]
    public void GleichesAussehenAnAnderemTagBleibtGetrennt()
    {
        var items = new List<MediaItem> { Aufnahme(0), Aufnahmen.OhneGps(Tag.AddDays(60)) };
        var vektoren = new Dictionary<MediaItem, float[]>
        {
            [items[0]] = Vektor(1, 0, 0, 0),
            [items[1]] = Vektor(1, 0, 0, 0),
        };

        Assert.Equal(2, SceneClustering.Cluster(items, vektoren, EinTag).Count);
    }

    /// <summary>Der gemeldete Fall: Das Zeitfenster gilt für die GANZE Gruppe,
    /// nicht bis zum nächsten Nachbarn. Sonst hangelt sich ein Cluster über
    /// Tage — jeder Schritt innerhalb eines Tages, jeder Schritt ähnlich genug,
    /// weil es dasselbe Kind in derselben Wohnung ist. Aus 11 Aufnahmen eines
    /// Tages wurden so 31.</summary>
    [Fact]
    public void EinClusterHangeltSichNichtUeberTage()
    {
        var gleich = Vektor(1, 0, 0, 0);
        var items = new List<MediaItem>
        {
            Aufnahmen.OhneGps(Tag),
            Aufnahmen.OhneGps(Tag.AddHours(20)),
            Aufnahmen.OhneGps(Tag.AddHours(40)),
            Aufnahmen.OhneGps(Tag.AddHours(60)),
        };
        var vektoren = items.ToDictionary(i => i, _ => gleich);

        var cluster = SceneClustering.Cluster(items, vektoren, EinTag);

        Assert.True(cluster.Count > 1, "Die Kette lief über die Zeitgrenze hinaus.");
        Assert.All(cluster, c => Assert.True(
            c.Max(i => i.TakenLocal) - c.Min(i => i.TakenLocal) <= EinTag,
            "Eine Gruppe spannt weiter als das Zeitfenster."));
    }

    /// <summary>Der gemeldete Fall, mit den gemessenen Zeiten: Schokolade um
    /// 09:02 und 09:24, Haarspangen um 10:02, ein Video um 10:19. Die 38 Minuten
    /// dazwischen sind die Grenze — für ein Aussehens-Modell ist alles dasselbe
    /// Kind in derselben Wohnung, die Pause muss trennen.</summary>
    [Fact]
    public void DiePauseTrenntDenAnlass()
    {
        var start = new DateTime(2026, 6, 14, 9, 2, 0);
        var zeiten = new[] { 0, 22, 60, 77 };   // 09:02, 09:24, 10:02, 10:19
        var items = zeiten.Select(m => Aufnahmen.OhneGps(start.AddMinutes(m))).ToList();

        var strecken = MediaOrganizer.Core.Grouping.TimeClustering
            .SplitAtGaps(items, SceneClustering.OccasionPause);

        Assert.Equal(2, strecken.Count);
        Assert.Equal(2, strecken[0].Count);
        Assert.Equal(2, strecken[1].Count);
    }

    /// <summary>Die Kehrseite: Innerhalb einer Strecke ohne Pause schneidet die
    /// Zeit nichts mehr. Eine Feier, die zwei Stunden lang durchfotografiert
    /// wird, darf nicht in gleich lange Stücke zerfallen.</summary>
    [Fact]
    public void EineDurchgehendeStreckeWirdNichtZerschnitten()
    {
        var gleich = Vektor(1, 0, 0, 0);
        var items = Enumerable.Range(0, 12)
            .Select(i => Aufnahmen.OhneGps(Tag.AddMinutes(i * 10)))
            .ToList();
        var vektoren = items.ToDictionary(i => i, _ => gleich);

        Assert.Single(SceneClustering.Cluster(items, vektoren, TimeSpan.MaxValue));
    }

    /// <summary>Eine Aufnahme wird am NÄCHSTEN Mitglied gemessen, nicht am
    /// Mittel: Ein Anlass wandert — Tisch, Garten, wieder Tisch —, und ein
    /// Mittelwert läge zwischen allem und passte zu nichts.</summary>
    [Fact]
    public void DieKetteZaehlt()
    {
        var (items, vektoren) = Szene(
            (0, Vektor(1, 0, 0, 0)),
            (5, Vektor(0.8f, 0.6f, 0, 0)),
            (10, Vektor(0.3f, 0.95f, 0, 0)));

        Assert.Single(SceneClustering.Cluster(items, vektoren, EinTag));
    }

    /// <summary>Ohne Vektor gibt es keinen Beleg — die Aufnahme bleibt für sich
    /// und fällt später unter die Mindestgröße zurück in den Sammelordner.</summary>
    [Fact]
    public void OhneVektorKeineGruppe()
    {
        var items = new List<MediaItem> { Aufnahme(0), Aufnahme(5) };
        var vektoren = new Dictionary<MediaItem, float[]> { [items[0]] = Vektor(1, 0, 0, 0) };

        Assert.Equal(2, SceneClustering.Cluster(items, vektoren, EinTag).Count);
    }

    // --- Rückholung aus dem Sammelordner ---

    /// <summary>Die Gruppen so, wie sie VOR der Rückholung aussahen.</summary>
    private static MediaGroup? Suche(
        MediaItem item,
        Dictionary<MediaItem, float[]> vektoren,
        params MediaGroup[] gruppen) =>
        SceneClustering.FindHome(
            item, vektoren[item], gruppen,
            gruppen.ToDictionary(g => g, g => (IReadOnlyList<MediaItem>)[.. g.Items]),
            vektoren, EinTag);

    private static (MediaGroup Gruppe, Dictionary<MediaItem, float[]> Vektoren) Ereignis(
        float[] vektor, int anzahl = 3, int startMinute = 0)
    {
        var gruppe = new MediaGroup { Items = [], Theme = "Grillabend" };
        var vektoren = new Dictionary<MediaItem, float[]>();
        for (var i = 0; i < anzahl; i++)
        {
            var item = Aufnahme(startMinute + (i * 5));
            gruppe.Items.Add(item);
            vektoren[item] = vektor;
        }

        return (gruppe, vektoren);
    }

    /// <summary>Der gewünschte Fall: Ein WhatsApp-Bild ohne GPS sieht aus wie
    /// die Aufnahmen der Feier und stammt vom selben Tag — es gehört dazu.</summary>
    [Fact]
    public void EinPassendesBildFindetSeineGruppe()
    {
        var (gruppe, vektoren) = Ereignis(Vektor(1, 0.05f, 0, 0));
        var ohneGps = Aufnahme(30);
        vektoren[ohneGps] = Vektor(1, 0.08f, 0, 0);

        Assert.Same(gruppe, Suche(ohneGps, vektoren, [gruppe]));
    }

    /// <summary>Sieht es anders aus, bleibt es liegen — auch am selben Tag.</summary>
    [Fact]
    public void EinFremdesBildBleibtImSammelordner()
    {
        var (gruppe, vektoren) = Ereignis(Vektor(1, 0, 0, 0));
        var fremd = Aufnahme(30);
        vektoren[fremd] = Vektor(0, 1, 0, 0);

        Assert.Null(Suche(fremd, vektoren, [gruppe]));
    }

    /// <summary>Und außerhalb des Zeitfensters ebenso, so ähnlich es auch
    /// aussieht: Derselbe Garten, aber ein anderer Anlass.</summary>
    [Fact]
    public void AusserhalbDesZeitfenstersBleibtEsLiegen()
    {
        var (gruppe, vektoren) = Ereignis(Vektor(1, 0, 0, 0));
        var spaeter = Aufnahmen.OhneGps(Tag.AddDays(30));
        vektoren[spaeter] = Vektor(1, 0, 0, 0);

        Assert.Null(Suche(spaeter, vektoren, [gruppe]));
    }

    /// <summary>Die Rückholung ist strenger als die Gruppierung im Topf: Eine
    /// falsche Zuordnung verdirbt einen Ordner, der richtig war. Was für eine
    /// gemeinsame Gruppe reichen würde (0,72), reicht hier nicht.</summary>
    [Fact]
    public void DieRueckholungIstStrengerAlsDieGruppierung()
    {
        var (gruppe, vektoren) = Ereignis(Vektor(1, 0, 0, 0));
        var knapp = Aufnahme(30);
        var y = MathF.Sqrt(1 - (0.75f * 0.75f));
        vektoren[knapp] = Vektor(0.75f, y, 0, 0);

        Assert.True(0.75f > SceneClustering.SameOccasion);
        Assert.Null(Suche(knapp, vektoren, [gruppe]));
    }

    /// <summary>Der gemeldete Fall: EIN passendes Foto in der Gruppe reicht
    /// nicht. 15 WhatsApp-Bilder kamen in "Anna beim Zähneputzen", weil jede
    /// Nahaufnahme desselben Kindes im selben Oberteil zu IRGENDEINEM Bild
    /// darin passt. Gemessen wird gegen den Schnitt der nächsten drei.</summary>
    [Fact]
    public void EinEinzelnerTrefferReichtNicht()
    {
        var gruppe = new MediaGroup { Items = [], Theme = "Zähneputzen" };
        var vektoren = new Dictionary<MediaItem, float[]>();
        var treffer = Aufnahme(0);
        gruppe.Items.Add(treffer);
        vektoren[treffer] = Vektor(1, 0, 0, 0);
        foreach (var minute in new[] { 5, 10 })
        {
            var anderes = Aufnahme(minute);
            gruppe.Items.Add(anderes);
            vektoren[anderes] = Vektor(0, 1, 0, 0);
        }

        var ohneGps = Aufnahme(30);
        vektoren[ohneGps] = Vektor(1, 0, 0, 0);

        Assert.Null(Suche(ohneGps, vektoren, [gruppe]));
    }

    /// <summary>Und eine zu kleine Gruppe nimmt gar nichts auf — sonst
    /// entschiede bei zwei Mitgliedern wieder ein einzelnes Foto.</summary>
    [Fact]
    public void EineZuKleineGruppeNimmtNichtsAuf()
    {
        var (gruppe, vektoren) = Ereignis(Vektor(1, 0, 0, 0), anzahl: 2);
        var passend = Aufnahme(30);
        vektoren[passend] = Vektor(1, 0, 0, 0);

        Assert.Null(Suche(passend, vektoren, [gruppe]));
    }

    /// <summary>Ein Video wird über MEHRERE Bilder gemittelt: Das erste Bild ist
    /// oft dunkel oder verwackelt und damit zu allem ähnlich — so kam ein Video
    /// von Anna in den Ordner "Spülmaschine ausbauen".</summary>
    [Fact]
    public void MehrereBilderErgebenEinenVektor()
    {
        var gemittelt = SceneEmbedder.Average([Vektor(1, 0, 0, 0), Vektor(0, 1, 0, 0)]);

        Assert.NotNull(gemittelt);
        Assert.Equal(1.0f, gemittelt.Sum(v => v * v), 3);
        Assert.Equal(0.707f, gemittelt[0], 2);
    }

    [Fact]
    public void OhneBrauchbaresBildKeinVektor() =>
        Assert.Null(SceneEmbedder.Average([null, []]));

    /// <summary>Der gemeldete Fall: Die Gruppe darf während der Rückholung
    /// nicht mitwachsen. Sonst wird ein zurückgeholtes Bild sofort Mitglied und
    /// trägt das nächste herein — gemessen passten 4 von 46 Kandidaten zur
    /// ursprünglichen Gruppe, im Ordner lagen am Ende 31 Aufnahmen an einem Tag
    /// mit 11.</summary>
    [Fact]
    public void DieGruppeWaechstWaehrendDerRueckholungNichtMit()
    {
        var (gruppe, vektoren) = Ereignis(Vektor(1, 0, 0, 0));
        var vorher = gruppe.Items.ToList();
        var stand = new Dictionary<MediaGroup, IReadOnlyList<MediaItem>>
        {
            [gruppe] = vorher,
        };

        // Ein Doppel der Gruppe wird aufgenommen …
        var doppel = Aufnahme(30);
        vektoren[doppel] = Vektor(1, 0, 0, 0);
        Assert.Same(gruppe, SceneClustering.FindHome(
            doppel, vektoren[doppel], [gruppe], stand, vektoren, EinTag));
        gruppe.Items.Add(doppel);

        // … und darf danach niemanden hereintragen, der nur ihm ähnlich ist.
        var nurDemDoppelAehnlich = Aufnahme(35);
        vektoren[nurDemDoppelAehnlich] = Vektor(1, 0, 0, 0);

        Assert.Same(gruppe, SceneClustering.FindHome(
            nurDemDoppelAehnlich, vektoren[nurDemDoppelAehnlich], [gruppe], stand, vektoren, EinTag));
        Assert.Equal(vorher.Count, stand[gruppe].Count);
    }

    /// <summary>Bei mehreren Kandidaten gewinnt der ähnlichste, nicht der
    /// erste.</summary>
    [Fact]
    public void DerAehnlichsteKandidatGewinnt()
    {
        var (fern, vektoren) = Ereignis(Vektor(0.85f, 0.53f, 0, 0));
        var (nah, weitere) = Ereignis(Vektor(1, 0.02f, 0, 0), startMinute: 60);
        foreach (var (k, v) in weitere)
        {
            vektoren[k] = v;
        }

        var ohneGps = Aufnahme(120);
        vektoren[ohneGps] = Vektor(1, 0, 0, 0);

        Assert.Same(nah, Suche(ohneGps, vektoren, [fern, nah]));
    }
}
