using MediaOrganizer.Core.Grouping;
using MediaOrganizer.Core.Models;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Große Zeitfenster werden vor dem LLM-Aufruf geteilt.
///
/// <para>Hintergrund: Die frühere Fassung schnitt nur, wenn 40 Aufnahmen
/// erreicht waren UND der Tag wechselte. War die Marke beim Tageswechsel noch
/// nicht erreicht, wuchs der Block unbegrenzt — in einem echten Lauf auf 101
/// Aufnahmen in einem Aufruf. So viele Indizes hält das Modell nicht
/// auseinander, einzelne Aufnahmen rutschten in die Nachbargruppe.</para></summary>
public class FensterteilungTests
{
    private const int Obergrenze = 40;

    private static List<List<MediaItem>> Teilen(List<MediaItem> fenster) =>
        [.. LlmGroupingStrategy.ChunkWindow(fenster, Obergrenze)];

    /// <summary>Der gemeldete Fall: 26 Aufnahmen am ersten Tag, 75 am zweiten.</summary>
    private static List<MediaItem> Protokollfall()
    {
        var fenster = new List<MediaItem>();
        fenster.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 5, 23, 18, 0, 0), 26, TimeSpan.FromMinutes(4),
            Aufnahmen.NachbarortLat, Aufnahmen.NachbarortLon));
        fenster.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 5, 24, 10, 0, 0), 75, TimeSpan.FromMinutes(4),
            Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));
        return fenster;
    }

    [Fact]
    public void Kein_Block_ueberschreitet_die_Obergrenze()
    {
        Assert.All(Teilen(Protokollfall()), block => Assert.True(block.Count <= Obergrenze));
    }

    [Fact]
    public void Kein_Block_ist_zu_klein_fuer_Kontext()
    {
        Assert.All(Teilen(Protokollfall()), block => Assert.True(block.Count >= 10));
    }

    [Fact]
    public void Teilen_verliert_keine_Aufnahme_und_haelt_die_Reihenfolge()
    {
        var fenster = Protokollfall();
        var zusammen = Teilen(fenster).SelectMany(b => b).ToList();

        Assert.Equal(fenster.Count, zusammen.Count);
        Assert.Equal(fenster.Select(i => i.TakenLocal), zusammen.Select(i => i.TakenLocal));
    }

    [Fact]
    public void Erster_Schnitt_liegt_an_der_Nachtluecke()
    {
        var bloecke = Teilen(Protokollfall());

        Assert.Equal(26, bloecke[0].Count);
        Assert.Equal(23, bloecke[0][^1].TakenLocal.Day);
        Assert.Equal(24, bloecke[1][0].TakenLocal.Day);
    }

    /// <summary>Ein Ereignis über Mitternacht hat dort gerade KEINE Lücke —
    /// geschnitten wird an der größten Pause, nicht am Datumswechsel.</summary>
    [Fact]
    public void Ereignis_ueber_Mitternacht_bleibt_zusammen()
    {
        var fenster = new List<MediaItem>();
        fenster.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 5, 23, 15, 0, 0), 30, TimeSpan.FromMinutes(10),
            Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));
        fenster.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 5, 23, 22, 0, 0), 20, TimeSpan.FromMinutes(10),
            Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));

        var bloecke = Teilen(fenster);

        Assert.Equal(30, bloecke[0].Count);
        Assert.Contains(bloecke, b => b[0].TakenLocal.Date != b[^1].TakenLocal.Date);
    }

    [Fact]
    public void Kleines_Fenster_bleibt_ungeteilt()
    {
        var fenster = Aufnahmen.Serie(
            new DateTime(2026, 5, 23, 9, 0, 0), 12, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon);

        Assert.Single(Teilen(fenster));
    }
}
