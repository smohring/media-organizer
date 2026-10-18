using MediaOrganizer.Core;
using MediaOrganizer.Core.FileOps;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.Services;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Splitting the dateless folders — the collecting folder and the
/// everyday folder — along the target hierarchy
/// (<see cref="AppSettings.SplitDatelessByHierarchy"/>).
///
/// <para>Reported case: everything unplaceable from every year landed in ONE
/// "Vermischt". Over a collection spanning years that is a folder nobody opens
/// a second time. With the switch on there is one per year, or one per month,
/// sitting inside the level it belongs to.</para>
///
/// <para>What has to hold either way: the parts never grow a date in their
/// NAME. The period decides where the folder sits, nothing else — otherwise the
/// name would claim the whole folder was one day.</para></summary>
public class DatumsloseOrdnerTests
{
    private static MediaGroup Collection(params DateTime[] taken)
    {
        var group = new MediaGroup
        {
            Items = [.. taken.Select(t => Aufnahmen.Am(t, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon))],
            Theme = "Vermischt",
            IsCollection = true,
        };
        return group;
    }

    private static List<MediaGroup> Split(
        TargetHierarchy hierarchy, List<RunWarning> warnings, params MediaGroup[] groups) =>
        OrganizePipeline.SplitDatelessByPeriod([.. groups], hierarchy, warnings);

    private static readonly IReadOnlySet<string> Kinds =
        MediaOrganizer.Core.Llm.Prompts.German.FileKindCategories;

    // --- Splitting ---

    [Fact]
    public void OneCollectingFolderPerYear()
    {
        var result = Split(TargetHierarchy.Year, [], Collection(
            new DateTime(2024, 3, 4, 10, 0, 0),
            new DateTime(2024, 11, 9, 10, 0, 0),
            new DateTime(2026, 1, 2, 10, 0, 0)));

        Assert.Equal(2, result.Count);
        Assert.All(result, g => Assert.True(g.IsCollection && g.IsPeriodScoped));
        Assert.Equal([2, 1], result.Select(g => g.Items.Count));
    }

    /// <summary>Two shots in the same year but different months are one folder
    /// under the year hierarchy and two under year+month. The level decides how
    /// finely it is cut, not a second setting.</summary>
    [Fact]
    public void OneCollectingFolderPerMonth()
    {
        var shots = new[]
        {
            new DateTime(2026, 3, 4, 10, 0, 0),
            new DateTime(2026, 3, 19, 10, 0, 0),
            new DateTime(2026, 7, 1, 10, 0, 0),
        };

        Assert.Single(Split(TargetHierarchy.Year, [], Collection(shots)));
        Assert.Equal(2, Split(TargetHierarchy.YearMonth, [], Collection(shots)).Count);
    }

    /// <summary>The point of the whole thing: the parts keep the one name. A
    /// date in it would say the folder was one day, and it is a year.</summary>
    [Fact]
    public void ThePartsKeepTheNameAndStayDateless()
    {
        var result = Split(TargetHierarchy.Year, [], Collection(
            new DateTime(2024, 3, 4, 10, 0, 0),
            new DateTime(2026, 1, 2, 10, 0, 0)));

        Assert.All(result, g => Assert.True(g.IsDateless));
        Assert.All(result, g => Assert.Equal("Vermischt", g.FolderName));
    }

    /// <summary>The everyday folder of the EXIF-only mode is split by the same
    /// rule — it is the same kind of folder, and the same argument applies to
    /// it.</summary>
    [Fact]
    public void TheEverydayFolderIsSplitToo()
    {
        var everyday = new MediaGroup
        {
            Items =
            [
                Aufnahmen.Am(new DateTime(2024, 5, 1, 10, 0, 0), Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon),
                Aufnahmen.Am(new DateTime(2026, 5, 1, 10, 0, 0), Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon),
            ],
            Theme = "Zuhause",
            IsEveryday = true,
        };

        var result = Split(TargetHierarchy.Year, [], everyday);

        Assert.Equal(2, result.Count);
        Assert.All(result, g => Assert.True(g.IsEveryday));
        Assert.All(result, g => Assert.Equal("Zuhause", g.FolderName));
    }

    // --- Themed subfolders inside the collecting folder ---

    /// <summary>A themed subfolder of the collecting folder ("Vermischt\Screenshot")
    /// is dateless because it sits INSIDE another folder. Splitting it must not
    /// drop that parent.
    ///
    /// <para>Reported case, measured on 6039 files: 167 such subfolders lost
    /// their parent here, climbed out of the collecting folder, grew a date in
    /// their name and were cut into one folder per month on top — 563 folders,
    /// 290 of them holding a single file. Nothing catches them afterwards
    /// either: the minimum size ran long before this step.</para></summary>
    [Fact]
    public void AThemedSubfolderKeepsItsParent()
    {
        var screenshots = new MediaGroup
        {
            Items =
            [
                Aufnahmen.OhneGps(new DateTime(2024, 3, 4, 10, 0, 0)),
                Aufnahmen.OhneGps(new DateTime(2026, 1, 2, 10, 0, 0)),
            ],
            Theme = "Screenshot",
            InsideFolder = "Vermischt",
        };

        var result = Split(TargetHierarchy.Year, [], screenshots);

        Assert.Equal(2, result.Count);
        Assert.All(result, g => Assert.Equal("Vermischt", g.InsideFolder));
        Assert.All(result, g => Assert.True(g.IsDateless));
        Assert.All(result, g => Assert.Equal("Screenshot", g.FolderName));
    }

    /// <summary>The two steps in the order the pipeline runs them: first the
    /// collecting folder is cut into periods, then each part gets its themed
    /// subfolders. That order is what makes the minimum size mean something —
    /// measured inside the period the folder actually is, not across the whole
    /// collection. Eight shots of one kind spread over eight months are not
    /// eight folders.</summary>
    [Fact]
    public void TheMinimumSizeCountsInsideThePeriod()
    {
        var collection = new MediaGroup
        {
            Items =
            [
                // Two months reach the minimum — each gets its own subfolder.
                Kind(new DateTime(2026, 3, 4, 10, 0, 0), "Screenshot"),
                Kind(new DateTime(2026, 3, 5, 10, 0, 0), "Screenshot"),
                Kind(new DateTime(2026, 4, 6, 10, 0, 0), "Screenshot"),
                Kind(new DateTime(2026, 4, 7, 10, 0, 0), "Screenshot"),
                // The single one stays in the heap of its month.
                Kind(new DateTime(2026, 5, 8, 10, 0, 0), "Screenshot"),
            ],
            Theme = "Vermischt",
            IsCollection = true,
        };

        var parts = Split(TargetHierarchy.YearMonth, [], collection);
        var result = OrganizePipeline.SplitCollectionByCategory(parts, minItems: 2, fileKinds: Kinds);

        var subfolders = result.Where(g => g.Theme == "Screenshot").ToList();
        Assert.Equal(2, subfolders.Count);
        Assert.All(subfolders, g => Assert.Equal(2, g.Items.Count));
        Assert.All(subfolders, g => Assert.Equal("Vermischt", g.InsideFolder));

        Assert.Single(result, g => g.IsCollection && g.Items.Count == 1);
        Assert.DoesNotContain(result, g => g.IsCollection && g.Items.Count == 0);
    }

    /// <summary>The subfolder inherits the level of its parent part: the
    /// collecting folder of March sits in 2026\03, and so does its subfolder.
    /// Without the period scope the parent path collapses to the base folder and
    /// the subfolder leaves the level with it.</summary>
    [Fact]
    public void TheSubfolderSitsInsideTheParentPart()
    {
        var collection = new MediaGroup
        {
            Items =
            [
                Kind(new DateTime(2026, 3, 4, 10, 0, 0), "Screenshot"),
                Kind(new DateTime(2026, 3, 5, 10, 0, 0), "Screenshot"),
            ],
            Theme = "Vermischt",
            IsCollection = true,
        };

        var groups = OrganizePipeline.SplitCollectionByCategory(
            Split(TargetHierarchy.YearMonth, [], collection), minItems: 2, fileKinds: Kinds);
        var plan = PlanBuilder.Build(groups, new AppSettings
        {
            TargetFolder = @"C:\ziel",
            TargetHierarchy = TargetHierarchy.YearMonth,
            SplitDatelessByHierarchy = true,
        }, []);

        Assert.Equal(
            @"C:\ziel\2026\03\Vermischt\Screenshot",
            Assert.Single(plan.Folders, f => f.Theme == "Screenshot").TargetPath);
    }

    private static MediaItem Kind(DateTime taken, string category)
    {
        var item = Aufnahmen.OhneGps(taken);
        item.Category = category;
        return item;
    }

    /// <summary>Dated event folders are none of this step's business.</summary>
    [Fact]
    public void DatedFoldersAreLeftAlone()
    {
        var event1 = Aufnahmen.Gruppe(
            "Urlaub", 3, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2024, 7, 1, 10, 0, 0));
        var event2 = Aufnahmen.Gruppe(
            "Fasching", 3, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 3, 1, 10, 0, 0));

        var result = Split(TargetHierarchy.Year, [], event1, event2);

        Assert.Equal([event1, event2], result);
        Assert.All(result, g => Assert.False(g.IsPeriodScoped));
    }

    [Fact]
    public void TheItemsOfEachPartStayInChronologicalOrder()
    {
        var result = Split(TargetHierarchy.Year, [], Collection(
            new DateTime(2026, 9, 1, 10, 0, 0),
            new DateTime(2026, 2, 1, 10, 0, 0),
            new DateTime(2026, 5, 1, 10, 0, 0)));

        var taken = Assert.Single(result).Items.Select(i => i.TakenLocal).ToList();
        Assert.Equal(taken.OrderBy(t => t).ToList(), taken);
    }

    /// <summary>Without a hierarchy there is no level to put the parts in.
    /// Splitting anyway would produce several folders of the same name in the
    /// same place — so nothing happens, and the run says so rather than leaving
    /// the switch looking broken.</summary>
    [Fact]
    public void WithoutAHierarchyNothingIsSplitAndTheRunSaysSo()
    {
        var warnings = new List<RunWarning>();
        var collection = Collection(
            new DateTime(2024, 3, 4, 10, 0, 0),
            new DateTime(2026, 1, 2, 10, 0, 0));

        var result = Split(TargetHierarchy.Flat, warnings, collection);

        Assert.Equal([collection], result);
        Assert.Equal(RunWarnings.SplitWithoutHierarchy, Assert.Single(warnings).Key);
    }

    /// <summary>Nothing dateless in the run: no warning either, not even
    /// without a hierarchy. There was nothing to split.</summary>
    [Fact]
    public void NothingDatelessMeansNoWarning()
    {
        var warnings = new List<RunWarning>();
        var dated = Aufnahmen.Gruppe(
            "Urlaub", 3, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2024, 7, 1, 10, 0, 0));

        Split(TargetHierarchy.Flat, warnings, dated);

        Assert.Empty(warnings);
    }

    // --- Where the parts end up ---

    /// <summary>Unsplit, the folder still sits beside the year levels. This is
    /// the default and must not have moved.</summary>
    [Theory]
    [InlineData(TargetHierarchy.Year)]
    [InlineData(TargetHierarchy.YearMonth)]
    public void UnsplitItStaysBesideTheYearLevels(TargetHierarchy hierarchy)
    {
        var path = PlanPaths.BuildTargetPath(@"C:\ziel", hierarchy, "", "Vermischt", isDateless: true);

        Assert.Equal(@"C:\ziel\Vermischt", path);
    }

    [Fact]
    public void SplitItSitsInsideTheYearLevel()
    {
        var path = PlanPaths.BuildTargetPath(
            @"C:\ziel", TargetHierarchy.Year, "20260102", "Vermischt", isDateless: true);

        Assert.Equal(@"C:\ziel\2026\Vermischt", path);
    }

    [Fact]
    public void SplitItSitsInsideTheMonthLevel()
    {
        var path = PlanPaths.BuildTargetPath(
            @"C:\ziel", TargetHierarchy.YearMonth, "20260702", "Vermischt", isDateless: true);

        Assert.Equal(@"C:\ziel\2026\07\Vermischt", path);
    }

    /// <summary>The plan is where the two halves meet: the prefix decides the
    /// level, the name never shows it.</summary>
    [Fact]
    public void ThePlanPutsEachPartUnderItsOwnYear()
    {
        var groups = Split(TargetHierarchy.Year, [], Collection(
            new DateTime(2024, 3, 4, 10, 0, 0),
            new DateTime(2026, 1, 2, 10, 0, 0)));

        var plan = PlanBuilder.Build(groups, new AppSettings
        {
            TargetFolder = @"C:\ziel",
            TargetHierarchy = TargetHierarchy.Year,
            SplitDatelessByHierarchy = true,
        }, []);

        Assert.Equal(
            [@"C:\ziel\2024\Vermischt", @"C:\ziel\2026\Vermischt"],
            plan.Folders.Select(f => f.TargetPath));
        Assert.All(plan.Folders, f => Assert.Equal("Vermischt", Path.GetFileName(f.TargetPath)));
    }

    /// <summary>An unsplit run must still write the collecting folder into the
    /// base folder — the change must not have moved the default.</summary>
    [Fact]
    public void ThePlanKeepsTheUnsplitFolderInTheBaseFolder()
    {
        var plan = PlanBuilder.Build(
            [Collection(new DateTime(2024, 3, 4, 10, 0, 0), new DateTime(2026, 1, 2, 10, 0, 0))],
            new AppSettings { TargetFolder = @"C:\ziel", TargetHierarchy = TargetHierarchy.Year },
            []);

        Assert.Equal(@"C:\ziel\Vermischt", Assert.Single(plan.Folders).TargetPath);
    }

    /// <summary>The plan, the plan editor and the executor have to agree on the
    /// path. They did not once, and files landed where the plan had not
    /// promised.</summary>
    [Fact]
    public void TheExecutorBuildsTheSamePathAsThePlan()
    {
        var part = Split(TargetHierarchy.Year, [], Collection(new DateTime(2026, 1, 2, 10, 0, 0)))[0];

        Assert.Equal(
            PlanPaths.BuildTargetPath(
                @"C:\ziel", TargetHierarchy.Year, PlanPaths.Prefix(part.StartLocal), part.Theme, isDateless: true),
            FileOrganizerService.BuildTargetDirectory(@"C:\ziel", TargetHierarchy.Year, part));
    }
}
