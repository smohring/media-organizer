using MediaOrganizer.Core;
using MediaOrganizer.Core.Grouping;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Naming;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.Services;
using MediaOrganizer.Core.Video;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>What the EXIF-only mode puts on disk: one dateless folder for
/// everything shot at home, and "YYYYMMDD_place" for everything else.
///
/// <para>Reported case: without the model every folder came out as
/// "20260513_Aufnahmen" — the date the file name already carried, plus a word
/// that says nothing. Shots at home cannot be told apart without a model, so
/// they stop pretending to be events; the rest can at least be named after
/// where they were taken.</para></summary>
public class ExifOnlyModeTests
{
    private static readonly HomeZone Home = new()
    {
        Name = "Zuhause",
        Latitude = Aufnahmen.ZuhauseLat,
        Longitude = Aufnahmen.ZuhauseLon,
        Kind = ZoneKind.Home,
    };

    private static List<MediaGroup> Gather(List<MediaGroup> groups, params HomeZone[] zones) =>
        OrganizePipeline.MergeEverydayGroups(groups, zones, []);

    private static MediaGroup AtHome(string theme, int count, DateTime start) =>
        Aufnahmen.Gruppe(theme, count, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, start);

    private static MediaGroup AwayFromHome(string theme, int count, DateTime start) =>
        Aufnahmen.Gruppe(theme, count, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, start);

    // --- Gathering the everyday shots ---

    [Fact]
    public void EveryDayAtHomeEndsUpInOneFolder()
    {
        var result = Gather(
        [
            AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0)),
            AtHome("Aufnahmen", 6, new DateTime(2026, 5, 13, 18, 0, 0)),
            AtHome("Aufnahmen", 4, new DateTime(2026, 8, 2, 9, 0, 0)),
        ], Home);

        var everyday = Assert.Single(result);
        Assert.True(everyday.IsEveryday);
        Assert.Equal(20, everyday.Items.Count);
    }

    /// <summary>The point of the whole thing: no date on the folder. It holds
    /// May and August alike, so any single date would be a lie.</summary>
    [Fact]
    public void TheEverydayFolderCarriesNoDate()
    {
        var result = Gather(
        [
            AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0)),
            AtHome("Aufnahmen", 4, new DateTime(2026, 8, 2, 9, 0, 0)),
        ], Home);

        Assert.Equal("Zuhause", result[0].FolderName);
    }

    [Fact]
    public void ItsItemsStayInChronologicalOrder()
    {
        var result = Gather(
        [
            AtHome("Aufnahmen", 3, new DateTime(2026, 8, 2, 9, 0, 0)),
            AtHome("Aufnahmen", 3, new DateTime(2026, 5, 1, 10, 0, 0)),
        ], Home);

        var taken = result[0].Items.Select(i => i.TakenLocal).ToList();
        Assert.Equal(taken.OrderBy(t => t).ToList(), taken);
    }

    [Fact]
    public void ShotsAwayFromHomeKeepTheirOwnGroups()
    {
        var result = Gather(
        [
            AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0)),
            AwayFromHome("Aufnahmen", 13, new DateTime(2026, 5, 23, 14, 0, 0)),
            AwayFromHome("Aufnahmen", 9, new DateTime(2026, 6, 14, 11, 0, 0)),
        ], Home);

        Assert.Equal(3, result.Count);
        Assert.Equal(2, result.Count(g => !g.IsEveryday));
    }

    /// <summary>Each home zone gets its own folder. Two of them are two places,
    /// even if both are everyday life.</summary>
    [Fact]
    public void EachHomeZoneGetsItsOwnFolder()
    {
        var second = new HomeZone
        {
            Name = "Zweitwohnung",
            Latitude = Aufnahmen.ReisezielLat,
            Longitude = Aufnahmen.ReisezielLon,
            Kind = ZoneKind.Home,
        };

        var result = Gather(
        [
            AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0)),
            AwayFromHome("Aufnahmen", 6, new DateTime(2026, 5, 23, 14, 0, 0)),
        ], Home, second);

        Assert.Equal(2, result.Count);
        Assert.All(result, g => Assert.True(g.IsEveryday));
        Assert.Contains(result, g => g.FolderName == "Zuhause");
        Assert.Contains(result, g => g.FolderName == "Zweitwohnung");
    }

    /// <summary>A zone on the map is not automatically everyday life — a
    /// holiday flat is precisely the opposite.</summary>
    [Fact]
    public void AVacationZoneIsNotEverydayLife()
    {
        var vacation = new HomeZone
        {
            Name = "Ferienwohnung",
            Latitude = Aufnahmen.ZuhauseLat,
            Longitude = Aufnahmen.ZuhauseLon,
            Kind = ZoneKind.Vacation,
        };

        var result = Gather([AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0))], vacation);

        Assert.Single(result);
        Assert.False(result[0].IsEveryday);
    }

    [Fact]
    public void WithoutAnyZoneNothingIsGathered()
    {
        var result = Gather(
        [
            AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0)),
            AtHome("Aufnahmen", 6, new DateTime(2026, 5, 13, 18, 0, 0)),
        ]);

        Assert.Equal(2, result.Count);
    }

    /// <summary>Nothing shows a shot without GPS to be at home. Sweeping it in
    /// would bury it in a folder nobody looks through again.</summary>
    [Fact]
    public void ShotsWithoutGpsAreNotGathered()
    {
        var withoutGps = new MediaGroup
        {
            Items = [.. Enumerable.Range(0, 5)
                .Select(i => Aufnahmen.OhneGps(new DateTime(2026, 5, 4, 10, 0, 0).AddMinutes(i * 5)))],
        };

        var result = Gather([AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0)), withoutGps], Home);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, g => !g.IsEveryday && g.Items.Count == 5);
    }

    /// <summary>Reported case: pictures from a race track ended up in "Zuhause".
    /// They were messenger images without GPS whose timestamp is when they
    /// arrived, not where they were taken — so they shared a time window with
    /// shots actually taken at home, and the group's centroid put the whole lot
    /// inside the zone. Only a shot's own coordinates may put it there.</summary>
    [Fact]
    public void AShotWithoutGpsInAHomeGroupIsLeftBehind()
    {
        var mixed = AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0));
        mixed.Items.AddRange(Enumerable.Range(0, 4)
            .Select(i => Aufnahmen.OhneGps(new DateTime(2026, 5, 1, 18, 0, 0).AddMinutes(i * 5))));

        var result = Gather([mixed], Home);

        var everyday = Assert.Single(result, g => g.IsEveryday);
        Assert.Equal(10, everyday.Items.Count);
        Assert.All(everyday.Items, i => Assert.NotNull(i.Location));

        var rest = Assert.Single(result, g => !g.IsEveryday);
        Assert.Equal(4, rest.Items.Count);
    }

    /// <summary>Everything inside the zone is gathered, whatever the model made
    /// of it. Events at home come back out later and only in the model modes —
    /// <c>SplitPoolsIntoEventsAsync</c> — never here.</summary>
    [Fact]
    public void EvenAThemedGroupIsGathered()
    {
        var party = AtHome("Geburtstagsfeier", 24, new DateTime(2026, 5, 1, 15, 0, 0));

        Assert.True(Assert.Single(Gather([party], Home)).IsEveryday);
    }

    /// <summary>The leftover keeps the theme its group had. Without the model
    /// that is the default theme and nothing changes — but the combined mode
    /// gathers the everyday shots too, and there the theme is what the model
    /// worked out from the pictures. Dropping it would mean asking for it
    /// again.</summary>
    [Fact]
    public void TheLeftoverKeepsTheThemeOfItsGroup()
    {
        var mixed = AtHome("Grillen im Garten", 6, new DateTime(2026, 5, 1, 10, 0, 0));
        mixed.Items.AddRange(Enumerable.Range(0, 3)
            .Select(i => Aufnahmen.OhneGps(new DateTime(2026, 5, 1, 18, 0, 0).AddMinutes(i * 5))));

        var rest = Assert.Single(Gather([mixed], Home), g => !g.IsEveryday);

        Assert.Equal("Grillen im Garten", rest.Theme);
        Assert.Equal(3, rest.Items.Count);
    }

    /// <summary>The flip side: a group that is only at home must not be torn
    /// apart, and no empty leftover may survive it.</summary>
    [Fact]
    public void AGroupEntirelyAtHomeLeavesNothingBehind()
    {
        var result = Gather([AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0))], Home);

        Assert.Single(result);
        Assert.Equal(10, result[0].Items.Count);
    }

    // --- Ohne Modell schneidet nur die Zeit ---

    /// <summary>Im EXIF-Modus trennt allein die Pause. Ein Ortswechsel mitten am
    /// Tag bildet KEINE Grenze mehr — das erledigt <c>SplitByPlace</c> danach
    /// genauer. Vorher zerschnitt die 8-Stunden-Regel dieselbe Reise zusätzlich
    /// nach zwei benachbarten Aufnahmen.</summary>
    [Fact]
    public async Task WithoutTheModelOnlyThePauseSplits()
    {
        var items = new List<MediaItem>();
        items.AddRange(Aufnahmen.Serie(new DateTime(2026, 7, 1, 9, 0, 0), 3,
            Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon));
        items.AddRange(Aufnahmen.Serie(new DateTime(2026, 7, 1, 20, 0, 0), 3,
            Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));

        var groups = await new ExifGroupingStrategy(TimeSpan.FromDays(7), splitOnPlaceChange: false)
            .GroupAsync(items, new Progress<PipelineProgress>(), CancellationToken.None);

        Assert.Single(groups);
        Assert.Equal(6, groups[0].Items.Count);
    }

    /// <summary>Und die Pause trennt weiterhin: Zwei Besuche am selben Ort,
    /// mehr als die eingestellte Lücke auseinander, bleiben zwei Ereignisse.</summary>
    [Fact]
    public async Task ALongerPauseStillSplits()
    {
        var items = new List<MediaItem>();
        items.AddRange(Aufnahmen.Serie(new DateTime(2026, 7, 1, 9, 0, 0), 3,
            Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));
        items.AddRange(Aufnahmen.Serie(new DateTime(2026, 7, 12, 9, 0, 0), 3,
            Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));

        var groups = await new ExifGroupingStrategy(TimeSpan.FromDays(7), splitOnPlaceChange: false)
            .GroupAsync(items, new Progress<PipelineProgress>(), CancellationToken.None);

        Assert.Equal(2, groups.Count);
    }

    /// <summary>Gegenprobe für den kombinierten Modus, der dieselbe Klasse als
    /// Vorschnitt benutzt: Dort trennt der Ortswechsel weiterhin, weil danach
    /// weder die GPS-Aussortierung noch die Ortszerlegung läuft.</summary>
    [Fact]
    public async Task WithThePlaceRuleAChangeOfPlaceStillSplits()
    {
        var items = new List<MediaItem>();
        items.AddRange(Aufnahmen.Serie(new DateTime(2026, 7, 1, 9, 0, 0), 3,
            Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon));
        items.AddRange(Aufnahmen.Serie(new DateTime(2026, 7, 1, 20, 0, 0), 3,
            Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));

        var groups = await new ExifGroupingStrategy(TimeSpan.FromDays(7))
            .GroupAsync(items, new Progress<PipelineProgress>(), CancellationToken.None);

        Assert.Equal(2, groups.Count);
    }

    // --- Gruppen nach Ort zerlegen ---

    /// <summary>Reported case: a folder "20260614_Beispielburg" held shots taken
    /// at home — coordinates 200 m from the home zone, in the same time window
    /// as the outing. Within eight hours the time-based grouping never splits at
    /// all, so the drive home and the evening at home stayed in the outing's
    /// group and inherited its name.</summary>
    [Fact]
    public void ShotsFromAnotherPlaceLeaveTheGroup()
    {
        var group = AwayFromHome("Aufnahmen", 5, new DateTime(2026, 6, 14, 10, 0, 0));
        group.Items.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 6, 14, 17, 0, 0), 3, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon));

        var result = OrganizePipeline.SplitByPlace([group], radiusKm: 2);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, g => g.Items.Count == 5);
        Assert.Contains(result, g => g.Items.Count == 3);
    }

    /// <summary>Gegenfall: Ein Stadtbummel darf nicht in Häppchen zerfallen.
    /// Was innerhalb des Radius liegt, bleibt eine Gruppe.</summary>
    [Fact]
    public void ShotsWithinTheRadiusStayTogether()
    {
        // Rund 900 m auseinander — derselbe Ort bei 2 km Radius.
        var group = AtHome("Aufnahmen", 4, new DateTime(2026, 6, 14, 10, 0, 0));
        group.Items.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 6, 14, 12, 0, 0), 4,
            Aufnahmen.ZuhauseLat + 0.008, Aufnahmen.ZuhauseLon));

        var result = OrganizePipeline.SplitByPlace([group], radiusKm: 2);

        Assert.Single(result);
        Assert.Equal(8, result[0].Items.Count);
    }

    /// <summary>Weggehen und wiederkommen ergibt EINEN Ordner, nicht zwei.
    /// Sonst standen "Musterstadt am Neckar", "… 2" und "… 3" desselben Tages
    /// nebeneinander im Plan.</summary>
    [Fact]
    public void LeavingAPlaceAndComingBackStaysOneGroup()
    {
        var group = AtHome("Aufnahmen", 3, new DateTime(2026, 6, 21, 9, 0, 0));
        group.Items.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 6, 21, 12, 0, 0), 3, Aufnahmen.NahesZielLat, Aufnahmen.NahesZielLon));
        group.Items.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 6, 21, 17, 0, 0), 4, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon));

        var result = OrganizePipeline.SplitByPlace([group], radiusKm: 2);

        Assert.Equal(2, result.Count);
        var home = Assert.Single(result, g => g.Items.Count == 7);
        Assert.Single(result, g => g.Items.Count == 3);

        // Und die zurückgekehrten Aufnahmen stehen wieder in der Reihenfolge
        // der Uhr, nicht in der ihrer Häufchen.
        var taken = home.Items.Select(i => i.TakenLocal).ToList();
        Assert.Equal(taken.OrderBy(t => t).ToList(), taken);
    }

    /// <summary>Radius 0 schaltet die Zerlegung ab — wie bei den übrigen
    /// Radien der Anwendung.</summary>
    [Fact]
    public void ARadiusOfZeroSplitsNothing()
    {
        var group = AwayFromHome("Aufnahmen", 5, new DateTime(2026, 6, 14, 10, 0, 0));
        group.Items.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 6, 14, 17, 0, 0), 3, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon));

        var result = OrganizePipeline.SplitByPlace([group], radiusKm: 0);

        Assert.Single(result);
    }

    // --- Die Pause wirkt INNERHALB eines Ortes ---

    /// <summary>Reported case: a folder "20260428_Beispielstadt an der Alb" opened
    /// with two shots from a completely different date. The pause is measured
    /// between CONSECUTIVE shots, and at a place photographed every few days
    /// that chain never breaks — 28.04 → 30.04 → 03.05 → … stayed one group
    /// across weeks, dated by its first shot. The pause has to separate two
    /// visits to the same place, so it is applied inside the place.</summary>
    [Fact]
    public void TwoVisitsToTheSamePlaceBecomeTwoGroups()
    {
        var group = AwayFromHome("Aufnahmen", 4, new DateTime(2026, 4, 28, 10, 0, 0));
        group.Items.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 5, 20, 10, 0, 0), 5, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));

        var result = OrganizePipeline.SplitByPause([group], TimeSpan.FromDays(7));

        Assert.Equal(2, result.Count);
        Assert.Contains(result, g => g.Items.Count == 4 && g.StartLocal.Month == 4);
        Assert.Contains(result, g => g.Items.Count == 5 && g.StartLocal.Month == 5);
    }

    /// <summary>Gegenfall: Ruhige Tage im selben Urlaub trennen nicht — dafür
    /// ist die Pause großzügig eingestellt.</summary>
    [Fact]
    public void QuietDaysWithinTheSameTripDoNotSplit()
    {
        var group = AwayFromHome("Aufnahmen", 3, new DateTime(2026, 7, 1, 10, 0, 0));
        group.Items.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 7, 5, 10, 0, 0), 3, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));

        var result = OrganizePipeline.SplitByPause([group], TimeSpan.FromDays(7));

        Assert.Single(result);
        Assert.Equal(6, result[0].Items.Count);
    }

    /// <summary>Der Sammelordner bleibt ganz: Er ist ohnehin ein Topf über die
    /// ganze Sammlung, kein Ereignis.</summary>
    [Fact]
    public void TheCollectingFolderIsNotSplitByPause()
    {
        var collection = Aufnahmen.Sammelordner(
            3, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 4, 28, 10, 0, 0));
        collection.Items.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 6, 1, 10, 0, 0), 3, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon));

        var result = OrganizePipeline.SplitByPause([collection], TimeSpan.FromDays(7));

        Assert.Single(result);
    }

    // --- Aufnahmen ohne GPS aus den Ortsordnern heraushalten ---

    /// <summary>Reported case: a folder "20260725_Beispielstadt an der Alb" held
    /// pictures from a race track 400 km away. Of 271 shots in that time window
    /// 60 had coordinates, 28 of them near Beispielstadt — and those 28 named the
    /// folder for everything else. A group is a stretch of TIME; only a shot's
    /// own coordinates say where it was taken.</summary>
    [Fact]
    public void ShotsWithoutGpsLeaveThePlaceGroup()
    {
        var mixed = AwayFromHome("Aufnahmen", 6, new DateTime(2026, 7, 25, 10, 0, 0));
        mixed.Items.AddRange(Enumerable.Range(0, 9)
            .Select(i => Aufnahmen.OhneGps(new DateTime(2026, 7, 25, 20, 0, 0).AddMinutes(i * 5))));

        var result = OrganizePipeline.SeparateUnlocatedShots([mixed], false, "Vermischt");

        Assert.Equal(2, result.Count);
        var place = Assert.Single(result, g => g.Items.Count == 6);
        Assert.All(place.Items, i => Assert.NotNull(i.Location));
        var rest = Assert.Single(result, g => g.Items.Count == 9);
        Assert.All(rest.Items, i => Assert.Null(i.Location));
    }

    /// <summary>With the collecting folder switched on they go straight into
    /// it. They can never be named by place, and a detour through naming would
    /// only hand them one they do not have — in the model modes it would even be
    /// an event name invented from content alone.</summary>
    [Fact]
    public void WithTheCollectingFolderOnTheyGoStraightIntoIt()
    {
        var mixed = AwayFromHome("Aufnahmen", 6, new DateTime(2026, 7, 25, 10, 0, 0));
        mixed.Items.AddRange(Enumerable.Range(0, 9)
            .Select(i => Aufnahmen.OhneGps(new DateTime(2026, 7, 25, 20, 0, 0).AddMinutes(i * 5))));

        var result = OrganizePipeline.SeparateUnlocatedShots([mixed], true, "Vermischt");

        var collection = Assert.Single(result, g => g.IsCollection);
        Assert.Equal("Vermischt", collection.Theme);
        Assert.Equal(9, collection.Items.Count);
        Assert.Equal(6, Assert.Single(result, g => !g.IsCollection).Items.Count);
    }

    /// <summary>Everything unlocated shares ONE collecting folder, not one per
    /// group it came from.</summary>
    [Fact]
    public void AllUnlocatedShotsShareOneCollectingFolder()
    {
        var first = AwayFromHome("Aufnahmen", 2, new DateTime(2026, 7, 25, 10, 0, 0));
        first.Items.Add(Aufnahmen.OhneGps(new DateTime(2026, 7, 25, 11, 0, 0)));
        var second = AwayFromHome("Aufnahmen", 2, new DateTime(2026, 7, 27, 10, 0, 0));
        second.Items.Add(Aufnahmen.OhneGps(new DateTime(2026, 7, 27, 11, 0, 0)));

        var result = OrganizePipeline.SeparateUnlocatedShots([first, second], true, "Vermischt");

        Assert.Equal(2, Assert.Single(result, g => g.IsCollection).Items.Count);
    }

    /// <summary>Gegenfall: Eine Gruppe, die ganz aus georteten Aufnahmen
    /// besteht, wird nicht angefasst — und eine ganz ohne GPS ebenso wenig, statt
    /// in eine leere und eine volle Hälfte zu zerfallen.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGroupOfOneKindStaysWhole(bool located)
    {
        var group = located
            ? AwayFromHome("Aufnahmen", 5, new DateTime(2026, 7, 25, 10, 0, 0))
            : new MediaGroup
            {
                Items = [.. Enumerable.Range(0, 5)
                    .Select(i => Aufnahmen.OhneGps(new DateTime(2026, 7, 25, 10, 0, 0).AddMinutes(i * 5)))],
            };

        var result = OrganizePipeline.SeparateUnlocatedShots([group], false, "Vermischt");

        Assert.Single(result);
        Assert.Equal(5, result[0].Items.Count);
    }

    /// <summary>Der Sammelordner selbst ist gemischt und bleibt es: Er sammelt
    /// gerade das, was sich nicht verorten lässt.</summary>
    [Fact]
    public void TheCollectingFolderIsNotSplit()
    {
        var collection = Aufnahmen.Sammelordner(
            4, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 7, 25, 10, 0, 0));
        collection.Items.Add(Aufnahmen.OhneGps(new DateTime(2026, 7, 25, 12, 0, 0)));

        var result = OrganizePipeline.SeparateUnlocatedShots([collection], true, "Vermischt");

        Assert.Single(result);
        Assert.Equal(5, result[0].Items.Count);
    }

    [Fact]
    public void TheCollectingFolderIsNeverGathered()
    {
        var result = Gather(
        [
            AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0)),
            Aufnahmen.Sammelordner(5, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 2, 10, 0, 0)),
        ], Home);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, g => g.IsCollection && !g.IsEveryday);
    }

    // --- What the rest of the pipeline does with it ---

    /// <summary>The minimum-size rule must not push the everyday folder into
    /// the collecting folder. It is the aggregate already; a handful of shots
    /// at home is all there was.</summary>
    [Fact]
    public void TheMinimumSizeRuleLeavesTheEverydayFolderAlone()
    {
        var everyday = Gather([AtHome("Aufnahmen", 1, new DateTime(2026, 5, 1, 10, 0, 0))], Home);

        var result = OrganizePipeline.MergeUndersizedGroups(everyday, minimum: 5);

        Assert.Single(result);
        Assert.True(result[0].IsEveryday);
    }

    /// <summary>It holds shots from the whole collection, so it cannot sit
    /// under a year — it goes beside the year levels, like the collecting
    /// folder.</summary>
    [Theory]
    [InlineData(TargetHierarchy.Flat)]
    [InlineData(TargetHierarchy.Year)]
    [InlineData(TargetHierarchy.YearMonth)]
    public void TheEverydayFolderSitsDirectlyInTheBaseFolder(TargetHierarchy hierarchy)
    {
        var path = PlanPaths.BuildTargetPath(@"C:\ziel", hierarchy, "", "Zuhause", isDateless: true);

        Assert.Equal(@"C:\ziel\Zuhause", path);
    }

    // --- Naming everything else ---

    [Fact]
    public async Task AGroupAwayFromHomeIsNamedAfterItsPlace()
    {
        var group = AwayFromHome(MediaGroup.DefaultTheme, 9, new DateTime(2026, 5, 23, 14, 0, 0));

        await NameAsync(group);

        Assert.NotEqual(MediaGroup.DefaultTheme, group.Theme);
        Assert.StartsWith("20260523_", group.FolderName);

        // Deliberately no "Urlaub"/"Ausflug" any more: the folder name is the
        // date and the place, nothing else.
        Assert.Equal($"20260523_{group.Theme}", group.FolderName);
        Assert.DoesNotContain("Urlaub", group.Theme);
        Assert.DoesNotContain("Ausflug", group.Theme);
    }

    /// <summary>No GPS, no place — and then the generic name is the honest
    /// answer rather than a guess.</summary>
    [Fact]
    public async Task AGroupWithoutGpsKeepsTheFallbackName()
    {
        var group = new MediaGroup
        {
            Items = [.. Enumerable.Range(0, 4)
                .Select(i => Aufnahmen.OhneGps(new DateTime(2026, 5, 4, 10, 0, 0).AddMinutes(i * 5)))],
        };

        await NameAsync(group);

        Assert.Equal(MediaGroup.DefaultTheme, group.Theme);
    }

    /// <summary>The everyday folder is named by the pipeline after its zone.
    /// The naming service must leave that alone — it did not for the collecting
    /// folder once, and the fixed name was gone.</summary>
    [Fact]
    public async Task TheEverydayFolderKeepsTheNameOfItsZone()
    {
        var group = Gather([AtHome("Aufnahmen", 10, new DateTime(2026, 5, 1, 10, 0, 0))], Home)[0];

        await NameAsync(group);

        Assert.Equal("Zuhause", group.FolderName);
    }

    // --- Zone und Mindestgröße ---

    /// <summary>Reported case: Mit einer Zone von 100 m landeten Spielplatz und
    /// Supermarkt trotzdem in „Zuhause". Über die Zugehörigkeit entschied der
    /// SCHWERPUNKT der Gruppe, und weil vorher auf den GPS-Radius (2 km)
    /// gebündelt wird, zog das die ganze Nachbarschaft mit hinein. Jede
    /// Aufnahme wird jetzt einzeln gefragt.</summary>
    [Fact]
    public void OnlyShotsInsideTheZoneItselfBecomeEverydayLife()
    {
        var enge = new HomeZone
        {
            Name = "Zuhause",
            Latitude = Aufnahmen.ZuhauseLat,
            Longitude = Aufnahmen.ZuhauseLon,
            Kind = ZoneKind.Home,
            RadiusKm = 0.1,
        };

        // Wohnung und ein Spielplatz 900 m weiter — im selben Zeitfenster.
        var group = AtHome("Aufnahmen", 4, new DateTime(2026, 5, 17, 10, 0, 0));
        group.Items.AddRange(Aufnahmen.Serie(
            new DateTime(2026, 5, 17, 14, 0, 0), 3,
            Aufnahmen.ZuhauseLat + 0.008, Aufnahmen.ZuhauseLon));

        var result = Gather([group], enge);

        var everyday = Assert.Single(result, g => g.IsEveryday);
        Assert.Equal(4, everyday.Items.Count);
        Assert.All(everyday.Items, i => Assert.True(enge.Covers(i.Location!.Value)));

        var draussen = Assert.Single(result, g => !g.IsEveryday);
        Assert.Equal(3, draussen.Items.Count);
    }

    /// <summary>Der Radius gilt, wie er eingestellt ist. Vorher lag ein
    /// stiller Boden von 200 m darunter — wer 100 m eintrug, bekam 200.</summary>
    [Fact]
    public void TheZoneRadiusIsTakenLiterally()
    {
        var zone = new HomeZone
        {
            Name = "Zuhause",
            Latitude = Aufnahmen.ZuhauseLat,
            Longitude = Aufnahmen.ZuhauseLon,
            Kind = ZoneKind.Home,
            RadiusKm = 0.1,
        };

        // 150 m nördlich: außerhalb von 100 m, innerhalb der alten 200 m.
        var draussen = new GeoPoint(Aufnahmen.ZuhauseLat + 0.00135, Aufnahmen.ZuhauseLon);

        Assert.True(zone.Covers(new GeoPoint(Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon)));
        Assert.False(zone.Covers(draussen));
    }

    /// <summary>Eine kleine Gruppe MIT Ort bleibt bestehen — zwei Aufnahmen vom
    /// Spielplatz sind ein Spielplatzbesuch. Im Sammelordner wären sie
    /// verloren; der ist für das gedacht, wofür es keinen Beleg gibt.</summary>
    [Fact]
    public void ASmallGroupWithAPlaceSurvivesTheMinimumSize()
    {
        var spielplatz = AwayFromHome("Aufnahmen", 2, new DateTime(2026, 5, 17, 14, 0, 0));

        var result = OrganizePipeline.MergeUndersizedGroups(
            [spielplatz], minimum: 5, keepLocated: true);

        var behalten = Assert.Single(result);
        Assert.False(behalten.IsCollection);
        Assert.Equal(2, behalten.Items.Count);
    }

    /// <summary>Die Gegenrichtung, und sie war ausdrücklich gefordert: EINE
    /// Aufnahme ist kein Ereignis, so genau ihr Ort auch sein mag. Ein Ordner
    /// für ein Bild ist keiner.</summary>
    [Fact]
    public void ASingleShotNeverGetsItsOwnFolder()
    {
        var einzeln = AwayFromHome("Aufnahmen", 1, new DateTime(2026, 3, 12, 14, 0, 0));

        var result = OrganizePipeline.MergeUndersizedGroups(
            [einzeln], minimum: 2, keepLocated: true);

        var sammel = Assert.Single(result);
        Assert.True(sammel.IsCollection);
        Assert.Single(sammel.Items);
    }

    /// <summary>Reported case: ein Ordner "20260606_Zuhause" — also ein zweiter
    /// Alltagsordner, diesmal mit Datum. Der Name der HEIMATZONE gehört
    /// ausschließlich dem einen datumslosen Ordner. Was hier ankommt, liegt
    /// nicht in der Zone; nur der Schwerpunkt seiner Gruppe tat es.</summary>
    [Fact]
    public async Task AHomeZoneNeverNamesADatedFolder()
    {
        var zone = new HomeZone
        {
            Name = "Zuhause",
            Latitude = Aufnahmen.ZuhauseLat,
            Longitude = Aufnahmen.ZuhauseLon,
            Kind = ZoneKind.Home,
            RadiusKm = 5,
        };
        var group = AtHome(MediaGroup.DefaultTheme, 6, new DateTime(2026, 6, 6, 10, 0, 0));

        await new GroupNamingService(
                ollama: null, model: "", imagePreprocessor: new UnusedImagePreprocessor(),
                ffmpeg: new FfmpegService(), homeZones: [zone])
            .NameGroupsAsync([group], new Progress<PipelineProgress>(), CancellationToken.None);

        Assert.NotEqual("Zuhause", group.Theme);
        Assert.DoesNotContain("Zuhause", group.FolderName);
    }

    // --- Selbst benannte Zonen ---

    /// <summary>Eine Urlaubszone hatte bisher keinerlei Wirkung: Der
    /// Kontext-Klassifizierer sah nur Zonen der Art "Zuhause", also tauchte ihr
    /// Name nirgends auf. Wer einen Ort selbst benennt, meint diesen Namen — er
    /// schlägt die Ortsdatenbank.</summary>
    [Fact]
    public async Task AZoneNamesTheFolderInsteadOfTheGeocodedPlace()
    {
        var ferien = new HomeZone
        {
            Name = "Ferienhaus Sylt",
            Latitude = Aufnahmen.ReisezielLat,
            Longitude = Aufnahmen.ReisezielLon,
            Kind = ZoneKind.Vacation,
            RadiusKm = 2,
        };
        var group = AwayFromHome(MediaGroup.DefaultTheme, 6, new DateTime(2026, 7, 12, 10, 0, 0));

        await new GroupNamingService(
                ollama: null, model: "", imagePreprocessor: new UnusedImagePreprocessor(),
                ffmpeg: new FfmpegService(), homeZones: [Home, ferien])
            .NameGroupsAsync([group], new Progress<PipelineProgress>(), CancellationToken.None);

        Assert.Equal("Ferienhaus Sylt", group.Theme);
        Assert.False(group.IsUnnamed);
        Assert.Equal("20260712_Ferienhaus Sylt", group.FolderName);
    }

    /// <summary>Und die Zone gilt nur, wo sie liegt: Außerhalb ihres Radius
    /// benennt weiterhin die Ortsdatenbank.</summary>
    [Fact]
    public async Task OutsideTheZoneThePlaceDatabaseStillNames()
    {
        var ferien = new HomeZone
        {
            Name = "Ferienhaus Sylt",
            Latitude = Aufnahmen.NachbarortLat,
            Longitude = Aufnahmen.NachbarortLon,
            Kind = ZoneKind.Vacation,
            RadiusKm = 2,
        };
        var group = AwayFromHome(MediaGroup.DefaultTheme, 6, new DateTime(2026, 7, 12, 10, 0, 0));

        await new GroupNamingService(
                ollama: null, model: "", imagePreprocessor: new UnusedImagePreprocessor(),
                ffmpeg: new FfmpegService(), homeZones: [Home, ferien])
            .NameGroupsAsync([group], new Progress<PipelineProgress>(), CancellationToken.None);

        Assert.NotEqual("Ferienhaus Sylt", group.Theme);
        Assert.False(group.IsUnnamed);
    }

    // --- Groups nothing could name ---

    /// <summary>The reported case: 282 of 419 folders were called
    /// "YYYYMMDD_Aufnahmen" because their shots had no GPS.</summary>
    [Fact]
    public async Task AGroupWithoutAPlaceGoesIntoTheCollectingFolder()
    {
        var withoutGps = new MediaGroup
        {
            Items = [.. Enumerable.Range(0, 6)
                .Select(i => Aufnahmen.OhneGps(new DateTime(2026, 5, 4, 10, 0, 0).AddMinutes(i * 5)))],
        };
        var named = AwayFromHome(MediaGroup.DefaultTheme, 9, new DateTime(2026, 5, 23, 14, 0, 0));

        await NameAsync(withoutGps, named);
        var result = OrganizePipeline.MoveUnnamedToCollection([withoutGps, named], "Vermischt", []);

        Assert.Equal(2, result.Count);
        var collection = Assert.Single(result, g => g.IsCollection);
        Assert.Equal(6, collection.Items.Count);
        Assert.Equal("Vermischt", collection.FolderName);
        Assert.DoesNotContain(result, g => g.Theme == MediaGroup.DefaultTheme && !g.IsCollection);
    }

    /// <summary>A group that HAS a place must stay where it is — the collecting
    /// folder is the last resort, not the default.</summary>
    [Fact]
    public async Task ANamedGroupIsNeverMoved()
    {
        var named = AwayFromHome(MediaGroup.DefaultTheme, 9, new DateTime(2026, 5, 23, 14, 0, 0));

        await NameAsync(named);
        var result = OrganizePipeline.MoveUnnamedToCollection([named], "Vermischt", []);

        Assert.Single(result);
        Assert.False(result[0].IsCollection);
    }

    /// <summary>The existing collecting folder is used, not a second one.</summary>
    [Fact]
    public async Task ShotsJoinAnExistingCollectingFolder()
    {
        var withoutGps = new MediaGroup
        {
            Items = [.. Enumerable.Range(0, 3)
                .Select(i => Aufnahmen.OhneGps(new DateTime(2026, 5, 4, 10, 0, 0).AddMinutes(i * 5)))],
        };
        var existing = Aufnahmen.Sammelordner(
            5, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 2, 10, 0, 0));
        existing.Theme = "Vermischt";

        await NameAsync(withoutGps);
        var result = OrganizePipeline.MoveUnnamedToCollection([withoutGps, existing], "Vermischt", []);

        var collection = Assert.Single(result);
        Assert.Equal(8, collection.Items.Count);
    }

    /// <summary>Without naming nothing is marked, and nothing may move — the
    /// flag comes from the naming service, not from the theme text.</summary>
    [Fact]
    public void WithoutTheUnnamedFlagNothingMoves()
    {
        var group = AwayFromHome(MediaGroup.DefaultTheme, 4, new DateTime(2026, 5, 23, 14, 0, 0));

        var result = OrganizePipeline.MoveUnnamedToCollection([group], "Vermischt", []);

        Assert.Single(result);
        Assert.False(result[0].IsCollection);
    }

    /// <summary>Naming without a model: no Ollama, no images, no network. The
    /// place comes from the offline place database embedded in the core.</summary>
    private static Task NameAsync(params MediaGroup[] groups) =>
        new GroupNamingService(
                ollama: null,
                model: "",
                imagePreprocessor: new UnusedImagePreprocessor(),
                ffmpeg: new FfmpegService(),
                homeZones: [Home])
            .NameGroupsAsync([.. groups], new Progress<PipelineProgress>(), CancellationToken.None);

    /// <summary>Without a model nothing decodes an image. Every call here is a
    /// bug in the code under test, not a missing test double.</summary>
    private sealed class UnusedImagePreprocessor : IImagePreprocessor
    {
        public byte[]? GetJpegForAnalysis(string imagePath, int maxDimension = 896) =>
            throw new InvalidOperationException("EXIF mode must not prepare images.");

        public byte[]? GetVideoFrameJpeg(string videoPath, int maxDimension = 896) =>
            throw new InvalidOperationException("EXIF mode must not prepare video frames.");

        public bool TryWriteDescription(string imagePath, string description) =>
            throw new InvalidOperationException("Naming must not write to files.");
    }
}
