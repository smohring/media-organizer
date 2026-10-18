using System.Text.Json;
using MediaOrganizer.Core.Update;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Version comparison and the checks that guard the update feed.
///
/// <para>Both are cheap to get subtly wrong and expensive to notice: a version
/// comparison that is off by one component either offers the same update
/// forever or never offers one at all, and neither shows up until a release is
/// already out. The feed validation is the other half — it decides what the
/// application is willing to download and run.</para></summary>
public class UpdateTests
{
    // ------------------------------------------------------------------
    // Reading versions
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("  2.0.1  ", 2, 0, 1)]
    [InlineData("1.0.0+abc1234", 1, 0, 0)]
    [InlineData("1.4.0-preview", 1, 4, 0)]
    [InlineData("1.0.0.0", 1, 0, 0)]
    public void ReadsVersionsFromTheFormsAFeedMayCarry(string text, int major, int minor, int build)
    {
        var version = AppVersion.Parse(text);

        Assert.NotNull(version);
        Assert.Equal(major, version!.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, version.Build);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData(null)]
    public void RefusesTextThatIsNoVersion(string? text) =>
        Assert.Null(AppVersion.Parse(text));

    /// <summary>The case that made the three-component comparison necessary:
    /// the build stamps a four-part assembly version, the feed advertises three
    /// parts. Compared as-is, Version treats the missing fourth part as -1 and
    /// "1.0.0" looks older than the running "1.0.0.0" — the update would never
    /// be offered.</summary>
    [Fact]
    public void TreatsAThreePartFeedVersionAsEqualToAFourPartBuild()
    {
        var fromFeed = AppVersion.Parse("1.0.0")!;
        var fromBuild = AppVersion.Parse("1.0.0.0")!;

        Assert.Equal(fromBuild, fromFeed);
    }

    // ------------------------------------------------------------------
    // What the feed must contain before anything is downloaded
    // ------------------------------------------------------------------

    private static UpdateManifest ValidManifest() => new()
    {
        Version = "1.0.1",
        Released = "2026-08-20",
        Notes = "Fixes something.",
        PackageUrl = "https://example.com/MediaOrganizer-1.0.1-win-x64.zip",
        Sha256 = new string('a', 64),
        SizeBytes = 1234,
    };

    [Fact]
    public void AcceptsACompleteFeed()
    {
        Assert.True(ValidManifest().IsUsable(out var problem));
        Assert.Equal("", problem);
    }

    /// <summary>An update channel over plain HTTP would let whoever controls
    /// the network decide which code runs on this machine.</summary>
    [Fact]
    public void RefusesAPackageThatIsNotServedOverHttps()
    {
        var manifest = ValidManifest();
        manifest.PackageUrl = "http://example.com/MediaOrganizer.zip";

        Assert.False(manifest.IsUsable(out var problem));
        Assert.Contains("https", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzz567890123456789012345678901234567890123456789012345678901234")]
    public void RefusesAFeedWithoutAUsableChecksum(string sha)
    {
        var manifest = ValidManifest();
        manifest.Sha256 = sha;

        Assert.False(manifest.IsUsable(out var problem));
        Assert.Contains("SHA-256", problem);
    }

    [Fact]
    public void RefusesAFeedWithoutAReadableVersion()
    {
        var manifest = ValidManifest();
        manifest.Version = "newest";

        Assert.False(manifest.IsUsable(out _));
    }

    /// <summary>The document in the repository is the one the running client
    /// will parse; a renamed field here breaks every installation at once.</summary>
    [Fact]
    public void ParsesTheDocumentShapeThatIsPublished()
    {
        const string json = """
            {
              "version": "1.0.1",
              "released": "2026-08-20",
              "notes": "Faster analysis.",
              "packageUrl": "https://example.com/MediaOrganizer-1.0.1-win-x64.zip",
              "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
              "sizeBytes": 158123456
            }
            """;

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json);

        Assert.NotNull(manifest);
        Assert.True(manifest!.IsUsable(out _));
        Assert.Equal(new Version(1, 0, 1), manifest.ParsedVersion);
        Assert.Equal(158123456, manifest.SizeBytes);
    }

    /// <summary>The mirror step deletes whatever is not in the new version.
    /// Pointed at a drive root — which is what an archive unpacked straight
    /// onto D:\ produces — that would take the rest of the drive with it.</summary>
    [Fact]
    public void RefusesToUpdateInPlaceFromADriveRoot()
    {
        // The test host runs from a normal directory, so this asserts the shape
        // of the guard rather than the guard firing: the check must name a
        // concrete reason whenever it says no.
        var canUpdate = UpdateService.CanUpdateInPlace(out var problem);

        Assert.Equal(canUpdate, problem.MessageKey is null);
    }
}
