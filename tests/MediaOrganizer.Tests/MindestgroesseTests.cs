using MediaOrganizer.Core;
using MediaOrganizer.Core.Models;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Gruppen unterhalb der eingestellten Mindestgröße bekommen keinen
/// eigenen Ordner, sondern wandern in den Sammelordner.</summary>
public class MindestgroesseTests
{
    private static List<MediaGroup> Zusammenfassen(List<MediaGroup> gruppen, int mindestgroesse) =>
        OrganizePipeline.MergeUndersizedGroups(gruppen, mindestgroesse);

    [Fact]
    public void Einzelbild_wandert_in_neu_angelegten_Sammelordner()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("Urlaub", 5, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 10, 0, 0)),
            Aufnahmen.Gruppe("Einzelbild", 1, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 25, 10, 0, 0)),
        ], mindestgroesse: 2);

        Assert.Equal(2, ergebnis.Count);
        Assert.Single(ergebnis, g => g.IsCollection && g.Items.Count == 1);
        Assert.Single(ergebnis, g => !g.IsCollection && g.Theme == "Urlaub");
    }

    [Fact]
    public void Vorhandener_Sammelordner_wird_genutzt_statt_verdoppelt()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("Urlaub", 5, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 10, 0, 0)),
            Aufnahmen.Gruppe("Zwei", 2, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 25, 10, 0, 0)),
            Aufnahmen.Sammelordner(4, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 20, 10, 0, 0)),
        ], mindestgroesse: 3);

        Assert.Equal(2, ergebnis.Count);
        Assert.Single(ergebnis, g => g.IsCollection);
        Assert.Equal(6, ergebnis.Single(g => g.IsCollection).Items.Count);
    }

    [Fact]
    public void Mindestgroesse_eins_laesst_alles_unberuehrt()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("Urlaub", 5, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 10, 0, 0)),
            Aufnahmen.Gruppe("Einzelbild", 1, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 25, 10, 0, 0)),
        ], mindestgroesse: 1);

        Assert.Equal(2, ergebnis.Count);
        Assert.DoesNotContain(ergebnis, g => g.IsCollection);
    }

    [Fact]
    public void Genau_die_Mindestgroesse_behaelt_einen_eigenen_Ordner()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("Zwei", 2, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 10, 0, 0)),
        ], mindestgroesse: 2);

        Assert.Single(ergebnis);
        Assert.DoesNotContain(ergebnis, g => g.IsCollection);
    }

    [Fact]
    public void Sind_alle_Gruppen_zu_klein_bleibt_genau_ein_Sammelordner()
    {
        var ergebnis = Zusammenfassen(
        [
            Aufnahmen.Gruppe("A", 1, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 1, 10, 0, 0)),
            Aufnahmen.Gruppe("B", 2, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 5, 10, 0, 0)),
            Aufnahmen.Gruppe("C", 3, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 9, 10, 0, 0)),
        ], mindestgroesse: 4);

        Assert.Single(ergebnis);
        Assert.True(ergebnis[0].IsCollection);
        Assert.Equal(6, ergebnis[0].Items.Count);
    }
}
