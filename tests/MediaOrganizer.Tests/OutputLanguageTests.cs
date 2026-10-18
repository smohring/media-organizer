using MediaOrganizer.Core.Faces;
using MediaOrganizer.Core.Llm;
using MediaOrganizer.Core.Models;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>The output language decides what the model answers in, and
/// therefore what the folders on disk are called.
///
/// <para>The promise made to the user when offering this setting is that
/// switching it costs nothing: descriptions in both languages live in the
/// analysis cache side by side, and switching back finds the old ones. That
/// promise rests entirely on how the cache key is built — which is what these
/// tests hold in place. Get it wrong and a user who tries English once pays ten
/// hours of re-analysis to go back.</para></summary>
public class OutputLanguageTests
{
    /// <summary>The load-bearing one. Every cache entry written before this
    /// setting existed was produced in German; if German gained a salt, all of
    /// them would be orphaned the moment this version is installed.</summary>
    [Fact]
    public void GermanAddsNothingToTheCacheKey() =>
        Assert.Equal("", MediaDescriber.LanguageSalt(Prompts.German));

    /// <summary>No argument at all must behave exactly like German, because
    /// that is what every caller that predates the setting passes.</summary>
    [Fact]
    public void NoLanguageBehavesLikeGerman() =>
        Assert.Equal(MediaDescriber.LanguageSalt(Prompts.German), MediaDescriber.LanguageSalt(null));

    [Fact]
    public void EnglishGetsItsOwnCacheKey()
    {
        var english = MediaDescriber.LanguageSalt(Prompts.English);

        Assert.NotEqual("", english);
        Assert.NotEqual(MediaDescriber.LanguageSalt(Prompts.German), english);
    }

    [Theory]
    [InlineData(OutputLanguage.German)]
    [InlineData(OutputLanguage.English)]
    public void EverySettingResolvesToAPromptSet(OutputLanguage language) =>
        Assert.NotNull(Prompts.For(language));

    [Fact]
    public void TheTwoSetsAreDistinct() =>
        Assert.NotSame(Prompts.For(OutputLanguage.German), Prompts.For(OutputLanguage.English));

    /// <summary>The JSON field names are an internal protocol between prompt and
    /// parser. If a translation quietly renamed them, the model would answer in
    /// a shape the parser does not read, and every group would end up unnamed.</summary>
    [Theory]
    [InlineData(OutputLanguage.German)]
    [InlineData(OutputLanguage.English)]
    public void BothSetsRequestTheSameJsonFields(OutputLanguage language)
    {
        var prompts = Prompts.For(language);

        Assert.Contains("\"beschreibung\"", prompts.DescribeSystem);
        Assert.Contains("\"kategorie\"", prompts.DescribeSystem);
        Assert.Contains("\"gruppen\"", prompts.GroupSystem);
        Assert.Contains("\"titel\"", prompts.GroupSystem);
        Assert.Contains("\"zusammen\"", prompts.MergeSystem);
        Assert.Contains("\"fakten\"", prompts.DistillSystem);
        Assert.Contains("\"thema\"", prompts.NameSystem);
        Assert.Contains("\"unpassend\"", prompts.BuildGroupSystem(collectUnmatched: true));
    }

    /// <summary>The collecting-folder instruction must appear only when the
    /// collecting folder is switched on. Offering the model a field it must not
    /// use is how shots ended up unsorted.</summary>
    [Theory]
    [InlineData(OutputLanguage.German)]
    [InlineData(OutputLanguage.English)]
    public void TheUnmatchedFieldAppearsOnlyWhenAskedFor(OutputLanguage language)
    {
        var prompts = Prompts.For(language);

        Assert.DoesNotContain("unpassend", prompts.BuildGroupSystem(collectUnmatched: false));
    }

    /// <summary>Position hints go into the prompt and thereby into the cache
    /// key. The German wording must stay byte-for-byte what it was, or every
    /// stored description of an image with a recognised person expires.</summary>
    [Theory]
    [InlineData(0.1, "links im Bild")]
    [InlineData(0.5, "in der Bildmitte")]
    [InlineData(0.9, "rechts im Bild")]
    public void GermanPositionHintsAreUnchanged(double centerX, string expected) =>
        Assert.Equal(expected, DetectedFace.PositionHintFor(centerX, OutputLanguage.German));

    [Fact]
    public void EnglishPositionHintsAreEnglish()
    {
        var hint = DetectedFace.PositionHintFor(0.1, OutputLanguage.English);

        Assert.Contains("left", hint);
        Assert.DoesNotContain("Bild", hint);
    }

    /// <summary>Both sets must name the fallbacks, or a group the model failed
    /// to name would land in a folder called "" .</summary>
    [Theory]
    [InlineData(OutputLanguage.German)]
    [InlineData(OutputLanguage.English)]
    public void FallbackNamesAreNeverEmpty(OutputLanguage language)
    {
        var prompts = Prompts.For(language);

        Assert.False(string.IsNullOrWhiteSpace(prompts.FallbackGroupName));
        Assert.False(string.IsNullOrWhiteSpace(prompts.DefaultUnmatchedFolderName));
        Assert.False(string.IsNullOrWhiteSpace(prompts.DefaultHomeZoneName));
    }

    /// <summary>The German fallback has to keep matching
    /// <see cref="MediaGroup.DefaultTheme"/>: the naming service compares
    /// against it to tell "never named" from "named this".</summary>
    [Fact]
    public void TheGermanFallbackMatchesTheUnnamedGroupPlaceholder() =>
        Assert.Equal(MediaGroup.DefaultTheme, Prompts.German.FallbackGroupName);
}
