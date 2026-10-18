using MediaOrganizer.Core.Geo;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Naming;
using MediaOrganizer.Core.Services;
using MediaOrganizer.Core.Video;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>What the model's answer is allowed to become a folder name.
///
/// <para>The name should say WHAT happened and WHERE — "20260614_Schlossgarten
/// Beispielburg". But nothing may be invented: where the model says
/// nothing usable, the folder falls back to the plain place name it can
/// prove.</para></summary>
public class OrdnernameTests
{
    private static readonly GroupNamingService Naming = new(
        ollama: null, model: "", imagePreprocessor: new UnusedPreprocessor(),
        ffmpeg: new FfmpegService());

    [Theory]
    [InlineData("Schlossgarten Beispielburg")]
    [InlineData("Grillabend im Garten")]
    [InlineData("Mia lernt Laufrad")]
    public void ARealEventNamesTheFolder(string theme) =>
        Assert.Equal(theme, Naming.UsableTheme(theme));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyAnswerIsNoName(string? theme) =>
        Assert.Null(Naming.UsableTheme(theme));

    /// <summary>The fallback word and the default theme are what the code puts
    /// there itself. Taken as an answer they would produce "20260614_Aufnahmen"
    /// — the date the file name already carries plus a word that says
    /// nothing.</summary>
    [Fact]
    public void TheFallbackWordIsNoName()
    {
        Assert.Null(Naming.UsableTheme(MediaGroup.DefaultTheme));
        Assert.Null(Naming.UsableTheme("Aufnahmen"));
    }

    /// <summary>A single word that names nothing — a file kind or a word like
    /// "Person" — is no folder name. "20250302_Natur" came out of exactly
    /// this.</summary>
    [Theory]
    [InlineData("Screenshot")]
    [InlineData("natur")]
    [InlineData("Person")]
    [InlineData("Familie")]
    [InlineData("Dokument")]
    public void AWordThatNamesNothingIsNoName(string theme) =>
        Assert.Null(Naming.UsableTheme(theme));

    /// <summary>The counter-case, and the reported one: a single word that hits
    /// the subject stays. Seven pictures of a wooden floor came out as
    /// "20260727_Musterstadt am Neckar" because the answer "Holzboden" was thrown
    /// away for matching the item category — the category is often exactly the
    /// right word.</summary>
    [Theory]
    [InlineData("Holzboden")]
    [InlineData("Ultraschall")]
    [InlineData("Kücheneinbau")]
    [InlineData("Natur am Bodensee")]
    public void AWordThatHitsTheSubjectStays(string theme) =>
        Assert.Equal(theme, Naming.UsableTheme(theme));

    /// <summary>The reported case: the model answered "Picknick im Park", the
    /// coordinates said Beispielburg, and the folder was called after the event
    /// alone. The prompt has asked for both since the beginning; the place is
    /// therefore appended here instead of hoped for.</summary>
    [Fact]
    public void TheProvenPlaceJoinsTheTheme() =>
        Assert.Equal(
            "Picknick im Park bei Beispielburg",
            Naming.WithPlace("Picknick im Park", DayTripTo("Beispielburg")));

    /// <summary>At home the place says nothing about the occasion — and the
    /// naming prompt tells the model the same thing.</summary>
    [Fact]
    public void AtHomeThePlaceStaysOut() =>
        Assert.Equal(
            "Anna spielt im Wohnzimmer",
            Naming.WithPlace(
                "Anna spielt im Wohnzimmer",
                new GroupContextInfo(
                    GroupContextKind.Home, "Zuhause", new GeocodeResult("Musterstadt", "DE", 0.4))));

    /// <summary>Where the model did name the place, it must not be named twice.
    /// The short form counts too, or "Ausflug Musterstadt" would grow into
    /// "Ausflug Musterstadt bei Musterstadt am Neckar".</summary>
    [Theory]
    [InlineData("Schlossgarten Beispielburg", "Beispielburg")]
    [InlineData("Ausflug Musterstadt", "Musterstadt am Neckar")]
    [InlineData("Wanderung bei Beispielstadt", "Beispielstadt an der Alb")]
    public void AThemeThatNamesThePlaceIsLeftAlone(string theme, string place) =>
        Assert.Equal(theme, Naming.WithPlace(theme, DayTripTo(place)));

    /// <summary>Without coordinates there is no place to prove, and nothing may
    /// be invented.</summary>
    [Fact]
    public void WithoutAPlaceTheThemeStandsAlone() =>
        Assert.Equal(
            "Picknick im Park",
            Naming.WithPlace(
                "Picknick im Park",
                new GroupContextInfo(GroupContextKind.Unknown, null, null)));

    /// <summary>A zone the user named beats the offline database here as
    /// everywhere else.</summary>
    [Fact]
    public void AZoneNameBeatsTheDatabase() =>
        Assert.Equal(
            "Sandburg bauen bei Ferienhaus Sylt",
            Naming.WithPlace(
                "Sandburg bauen",
                new GroupContextInfo(
                    GroupContextKind.Vacation, "Ferienhaus Sylt",
                    new GeocodeResult("Westerland", "DE", 1.2))));

    /// <summary>Folder names are cut at 60 characters. Appending the place
    /// there would cut it off again, so the theme keeps the field.</summary>
    [Fact]
    public void APlaceThatNoLongerFitsIsLeftOut()
    {
        const string theme = "Anna und Ben bauen den Hochbeetrahmen im Garten fertig";
        Assert.Equal(theme, Naming.WithPlace(theme, DayTripTo("Beispielburg")));
    }

    private static GroupContextInfo DayTripTo(string place) =>
        new(GroupContextKind.DayTrip, null, new GeocodeResult(place, "DE", 2.0));

    private sealed class UnusedPreprocessor : IImagePreprocessor
    {
        public byte[]? GetJpegForAnalysis(string imagePath, int maxDimension = 896) => null;

        public byte[]? GetVideoFrameJpeg(string videoPath, int maxDimension = 896) => null;

        public bool TryWriteDescription(string imagePath, string description) => false;
    }
}
