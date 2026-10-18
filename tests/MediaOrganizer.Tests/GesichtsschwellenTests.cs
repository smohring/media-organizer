using MediaOrganizer.Core.Faces;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Zwei Schwellen für zwei Zwecke.
///
/// <para>Gemeldet: Eine Kaffeetasse wurde als Gesicht erkannt (Zuversicht
/// 0,472; echte Gesichter liegen bei 0,87–0,92) und mit 0,417 „Ben"
/// zugeordnet. Dieser Name ging als gesicherte Tatsache in den Bild-Prompt, das
/// Modell schrieb „Ben hält Anna auf dem Schoß", und der Ordner wurde danach
/// benannt. Ein echter Treffer desselben Bestands liegt bei 0,581.</para></summary>
public class GesichtsschwellenTests
{
    private static float[] Vektor(float x, float y)
    {
        var laenge = MathF.Sqrt((x * x) + (y * y));
        return [x / laenge, y / laenge];
    }

    /// <summary>Ein Vektor mit gewünschter Ähnlichkeit zu (1,0).</summary>
    private static float[] MitAehnlichkeit(double s) =>
        Vektor((float)s, (float)Math.Sqrt(1 - (s * s)));

    private static List<KnownPerson> Ben(double aehnlichkeit) =>
    [
        new() { Name = "Ben", Embeddings = [MitAehnlichkeit(aehnlichkeit)] },
    ];

    [Fact]
    public void DieTasseWirdKeineTatsache() =>
        Assert.Null(FaceService.Match(
            Vektor(1, 0), Ben(0.417), FaceService.FactMatchThreshold));

    /// <summary>Im Dialog bleibt sie ein Vorschlag — dort kostet ein falscher
    /// Treffer einen Klick, kein erfundener Satz.</summary>
    [Fact]
    public void ImDialogBleibtSieEinVorschlag() =>
        Assert.Equal("Ben", FaceService.Match(
            Vektor(1, 0), Ben(0.417), FaceService.DefaultMatchThreshold)?.Person.Name);

    /// <summary>Der Gegenfall: Ein echter Treffer (gemessen 0,581) muss die
    /// strenge Schwelle nehmen, sonst kennt das Modell niemanden mehr.</summary>
    [Fact]
    public void EinEchterTrefferBleibtEineTatsache() =>
        Assert.Equal("Ben", FaceService.Match(
            Vektor(1, 0), Ben(0.581), FaceService.FactMatchThreshold)?.Person.Name);

    [Fact]
    public void DieStrengeSchwelleLiegtUeberDerFuerVorschlaege() =>
        Assert.True(FaceService.FactMatchThreshold > FaceService.DefaultMatchThreshold);
}
