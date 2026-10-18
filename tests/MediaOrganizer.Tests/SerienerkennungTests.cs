using MediaOrganizer.Core.Grouping;
using MediaOrganizer.Core.Models;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Aufnahmeserien werden nur einmal analysiert.
///
/// <para>Hintergrund: In einem echten Lauf standen sechs Auslösungen aus 15
/// Sekunden mit sechs Vision-Aufrufen zu Buche und lieferten sechsmal
/// denselben Satz. Bei zehntausend Aufnahmen ist das ein erheblicher Anteil
/// der Laufzeit.</para>
///
/// <para>Die Kehrseite ist der eigentliche Prüfgegenstand: Es gab schon einmal
/// eine Vererbung zwischen "ähnlichen" Bildern, und sie verteilte auf echten
/// Fotos falsche Beschreibungen. Die Tests hier belegen deshalb vor allem,
/// wann NICHT zusammengefasst wird.</para></summary>
public class SerienerkennungTests
{
    private const string Kamera = "Pixel 9";

    private static readonly DateTime Start = new(2026, 5, 1, 10, 4, 30);

    /// <summary>Aufnahme, wie eine Kamera sie liefert: echter EXIF-Zeitstempel,
    /// Kameramodell, GPS.</summary>
    private static MediaItem Foto(
        double sekundenNachStart,
        string kamera = Kamera,
        MediaKind art = MediaKind.Image,
        TimestampSource quelle = TimestampSource.ExifOriginal,
        double? lat = null,
        double? lon = null)
    {
        var zeitpunkt = Start.AddSeconds(sekundenNachStart);
        return new MediaItem
        {
            SourcePath = $@"C:\test\{zeitpunkt:yyyyMMdd_HHmmss}_{kamera}.jpg",
            Kind = art,
            TimestampSource = quelle,
            TakenLocal = zeitpunkt,
            Location = new GeoPoint(lat ?? Aufnahmen.ZuhauseLat, lon ?? Aufnahmen.ZuhauseLon),
            CameraModel = kamera,
        };
    }

    private static List<List<MediaItem>> Serien(params MediaItem[] items) =>
        BurstDetector.Group(items);

    /// <summary>Der Protokollfall: PXL_…080430, 080432, 080433, 080440,
    /// 080442, 080445 — sechs Auslösungen, sechs identische Beschreibungen.
    ///
    /// <para>Die Sieben-Sekunden-Pause in der Mitte trennt bewusst: Sie liegt
    /// über der zulässigen Lücke, und im Zweifel wird getrennt statt
    /// zusammengefasst. Aus sechs Analysen werden dadurch zwei statt einer —
    /// die vorsichtigere Zahl ist hier die gewollte.</para></summary>
    [Fact]
    public void Der_Protokollfall_braucht_statt_sechs_Analysen_nur_zwei()
    {
        var serien = Serien(Foto(0), Foto(2), Foto(3), Foto(10), Foto(12), Foto(15));

        Assert.Equal(2, serien.Count);
        Assert.All(serien, serie => Assert.Equal(3, serie.Count));
    }

    [Fact]
    public void Eine_Pause_beendet_die_Serie()
    {
        var serien = Serien(Foto(0), Foto(2), Foto(60), Foto(62));

        Assert.Equal(2, serien.Count);
        Assert.All(serien, serie => Assert.Equal(2, serie.Count));
    }

    /// <summary>Sonst wüchse eine Kette aus lauter knappen Abständen beliebig
    /// weit und zeigte am Ende etwas anderes als am Anfang.</summary>
    [Fact]
    public void Eine_lange_Kette_knapper_Abstaende_wird_begrenzt()
    {
        var lang = Enumerable.Range(0, 20).Select(i => Foto(i * 4)).ToArray();

        var serien = Serien(lang);

        Assert.True(serien.Count > 1);
        Assert.All(serien, serie =>
            Assert.True((serie[^1].TakenLocal - serie[0].TakenLocal).TotalSeconds
                        <= BurstDetector.MaxSpanSeconds));
    }

    [Fact]
    public void Zwei_Kameras_bilden_keine_gemeinsame_Serie()
    {
        var serien = Serien(Foto(0), Foto(1, "iPhone 14"), Foto(2));

        Assert.Equal(3, serien.Count);
    }

    /// <summary>Der gefährlichste Fall: Bilder aus Messenger-Ordnern tragen
    /// keinen Aufnahme-Zeitstempel. Beim Kopieren bekommen hunderte davon
    /// denselben Dateisystem-Zeitstempel — als Serie behandelt bekäme die ganze
    /// Sammlung eine einzige Beschreibung.</summary>
    [Fact]
    public void Ohne_echten_Aufnahme_Zeitstempel_wird_nie_zusammengefasst()
    {
        var kopiert = Enumerable.Range(0, 5)
            .Select(_ => Foto(0, quelle: TimestampSource.FileSystem))
            .ToArray();

        var serien = Serien(kopiert);

        Assert.Equal(5, serien.Count);
    }

    [Fact]
    public void Ohne_Kameramodell_wird_nie_zusammengefasst()
    {
        var serien = Serien(Foto(0, ""), Foto(1, ""), Foto(2, ""));

        Assert.Equal(3, serien.Count);
    }

    [Fact]
    public void Verschiedene_Orte_beenden_die_Serie()
    {
        var dort = Foto(2, lat: Aufnahmen.NahesZielLat, lon: Aufnahmen.NahesZielLon);

        var serien = Serien(Foto(0), dort);

        Assert.Equal(2, serien.Count);
    }

    /// <summary>Eine Notiz gilt genau diesem Bild und verändert seinen Prompt —
    /// die Aufnahme braucht ihre eigene Analyse.</summary>
    [Fact]
    public void Eine_Aufnahme_mit_Notiz_bleibt_allein()
    {
        var mitNotiz = Foto(2);
        mitNotiz.UserNote = "Das ist der Notebookhalter für den Heimtrainer";

        var serien = Serien(Foto(0), mitNotiz, Foto(4));

        Assert.Equal(3, serien.Count);
    }

    /// <summary>Wer im Bild ist, steht im Prompt und damit in der Beschreibung.
    /// Sonst trüge eine Aufnahme einen Namen, auf der die Person fehlt.</summary>
    [Fact]
    public void Verschiedene_erkannte_Personen_beenden_die_Serie()
    {
        var mitMia = Foto(0);
        mitMia.DetectedFaceCount = 1;
        mitMia.RecognizedPeopleWithPosition.Add("Mia (in der Bildmitte)");

        var mitLena = Foto(2);
        mitLena.DetectedFaceCount = 1;
        mitLena.RecognizedPeopleWithPosition.Add("Lena (in der Bildmitte)");

        var serien = Serien(mitMia, mitLena);

        Assert.Equal(2, serien.Count);
    }

    [Fact]
    public void Videos_gehoeren_nie_zu_einer_Serie()
    {
        var serien = Serien(Foto(0), Foto(2, art: MediaKind.Video), Foto(4));

        Assert.Equal(3, serien.Count);
    }
}
