using System.Diagnostics;
using MediaOrganizer.Core.Geo;
using MediaOrganizer.Core.Models;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Place suggestions for the zone dialog.
///
/// <para>Reported case: clicking "edit" on a zone froze the window. The search
/// compared every located shot against every other one to find the densest
/// point — on 10 899 shots with GPS that is 119 million distance calculations
/// per round, up to eight rounds, on the interface thread.</para></summary>
public class OrtsvorschlaegeTests
{
    private static readonly TimeSpan MaxGap = TimeSpan.FromHours(48);

    private static List<MediaItem> Shots(int count, double lat, double lon, DateTime start) =>
        [.. Enumerable.Range(0, count).Select(i => Aufnahmen.Am(start.AddMinutes(i), lat, lon))];

    [Fact]
    public void TheBusiestPlaceComesFirst()
    {
        List<MediaItem> items =
        [
            .. Shots(40, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 1, 10, 0, 0)),
            .. Shots(12, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 10, 0, 0)),
        ];

        var suggestions = PlaceSuggestions.Find(items, radiusKm: 1.5, MaxGap);

        Assert.Equal(2, suggestions.Count);
        Assert.Equal(40, suggestions[0].PhotoCount);
        Assert.Equal(12, suggestions[1].PhotoCount);
    }

    /// <summary>Several separate visits mark an everyday place; one visit is a
    /// trip. That is what preselects the zone kind in the dialog.</summary>
    [Fact]
    public void SeparateVisitsAreCounted()
    {
        List<MediaItem> items =
        [
            .. Shots(5, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 1, 10, 0, 0)),
            .. Shots(5, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 10, 10, 0, 0)),
            .. Shots(5, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon, new DateTime(2026, 5, 20, 10, 0, 0)),
        ];

        var suggestion = Assert.Single(PlaceSuggestions.Find(items, radiusKm: 1.5, MaxGap));

        Assert.Equal(3, suggestion.Visits);
        Assert.Equal(ZoneKind.Home, suggestion.LikelyKind);
    }

    [Fact]
    public void ASingleVisitLooksLikeATrip()
    {
        var items = Shots(9, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon, new DateTime(2026, 5, 23, 10, 0, 0));

        var suggestion = Assert.Single(PlaceSuggestions.Find(items, radiusKm: 1.5, MaxGap));

        Assert.Equal(1, suggestion.Visits);
        Assert.Equal(ZoneKind.Vacation, suggestion.LikelyKind);
    }

    /// <summary>Shots taken at the same spot in the same second are equal as
    /// values. Removing a cluster with Except dropped every copy of them, so
    /// the counts came out short.</summary>
    [Fact]
    public void IdenticalShotsAreAllCounted()
    {
        var moment = new DateTime(2026, 5, 1, 10, 0, 0);
        List<MediaItem> items =
        [
            .. Enumerable.Range(0, 6).Select(_ =>
                Aufnahmen.Am(moment, Aufnahmen.ZuhauseLat, Aufnahmen.ZuhauseLon)),
        ];

        var suggestion = Assert.Single(PlaceSuggestions.Find(items, radiusKm: 1.5, MaxGap));

        Assert.Equal(6, suggestion.PhotoCount);
    }

    [Fact]
    public void WithoutGpsThereIsNothingToSuggest()
    {
        List<MediaItem> items =
        [
            .. Enumerable.Range(0, 20).Select(i =>
                Aufnahmen.OhneGps(new DateTime(2026, 5, 1, 10, 0, 0).AddMinutes(i))),
        ];

        Assert.Empty(PlaceSuggestions.Find(items, radiusKm: 1.5, MaxGap));
    }

    /// <summary>The guard against the reported freeze. The budget is
    /// deliberately generous — this is not a benchmark, it separates
    /// milliseconds from the minutes the quadratic version took on a collection
    /// this size, and it must not fail on a loaded machine.</summary>
    [Fact]
    public void ALargeCollectionIsAnsweredAtOnce()
    {
        var start = new DateTime(2026, 1, 1, 8, 0, 0);
        var items = new List<MediaItem>();
        for (var place = 0; place < 12; place++)
        {
            // Spread over a degree apart, so they are genuinely separate places.
            items.AddRange(Shots(
                1000,
                Aufnahmen.ZuhauseLat + place,
                Aufnahmen.ZuhauseLon + place,
                start.AddDays(place * 3)));
        }

        var clock = Stopwatch.StartNew();
        var suggestions = PlaceSuggestions.Find(items, radiusKm: 1.5, MaxGap);
        clock.Stop();

        Assert.NotEmpty(suggestions);
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(5),
            $"12 000 located shots took {clock.Elapsed.TotalSeconds:0.0} s — the search is quadratic again.");
    }
}
