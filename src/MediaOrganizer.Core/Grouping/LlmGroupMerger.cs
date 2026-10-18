using System.Globalization;
using System.Text;
using MediaOrganizer.Core.Llm;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Grouping;

/// <summary>Asks the model whether two neighbouring groups are the same event
/// and joins them if so — the counter-check against a cut the clock made but
/// the content does not support.</summary>
public sealed class LlmGroupMerger(
    OllamaClient ollama,
    string model,
    IReadOnlyList<HomeZone> homeZones,
    Prompts prompts)
{
    /// <summary>Walks the dated groups in time order and merges each with its
    /// predecessor where the model agrees. Serial on purpose: whether a group
    /// merges depends on how far its predecessor has already grown.</summary>
    /// <param name="window">Largest gap between two groups still worth asking
    /// about. Must exceed the event gap, or the check would never reach the
    /// pairs the pause split apart.</param>
    public async Task<List<MediaGroup>> MergeAsync(
        List<MediaGroup> groups,
        TimeSpan window,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        // The everyday and collecting folders span the whole collection and
        // belong to nothing — they take no part in this.
        var pending = groups.Where(g => !g.IsDateless).OrderBy(g => g.StartLocal).ToList();
        if (pending.Count < 2)
        {
            return groups;
        }

        var merged = new List<MediaGroup>();
        var joins = 0;

        using var phase = RunLog.Phase("Grouping: merge");
        phase.Units = pending.Count;

        for (var i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new PipelineProgress(
                PipelinePhase.Group, i + 1, pending.Count,
                ProgressKeys.CheckingBoundaries, i + 1, pending.Count));

            var current = pending[i];
            if (merged.Count == 0)
            {
                merged.Add(current);
                continue;
            }

            var previous = merged[^1];
            if (current.StartLocal - previous.EndLocal <= window
                && !IsHomeAwayMismatch(previous, current)
                && await BelongTogetherAsync(previous, current, ct).ConfigureAwait(false))
            {
                previous.Items.AddRange(current.Items);
                previous.Items.Sort((a, b) => a.TakenLocal.CompareTo(b.TakenLocal));
                joins++;
            }
            else
            {
                merged.Add(current);
            }
        }

        RunLog.Log($"Model merge: {joins} of {pending.Count - 1} neighbouring pair(s) joined.");
        return [.. groups.Where(g => g.IsDateless), .. merged];
    }

    /// <summary>A group at home never merges with a clearly distant one, whatever
    /// the model says. Only real home zones count — a holiday place may well
    /// merge with its surroundings.</summary>
    private bool IsHomeAwayMismatch(MediaGroup a, MediaGroup b)
    {
        var zones = homeZones.Where(z => z.Kind == ZoneKind.Home).ToList();
        if (zones.Count == 0 || a.Centroid is not { } ca || b.Centroid is not { } cb)
        {
            return false;
        }

        var aHome = zones.Any(z => z.Covers(ca));
        var bHome = zones.Any(z => z.Covers(cb));
        return (aHome && zones.Min(z => z.Center.DistanceKm(cb)) > 5)
               || (bHome && zones.Min(z => z.Center.DistanceKm(ca)) > 5);
    }

    private async Task<bool> BelongTogetherAsync(MediaGroup a, MediaGroup b, CancellationToken ct)
    {
        string raw;
        try
        {
            raw = await ollama.ChatAsync(
                model, prompts.MergeSystem, DescribePair(a, b), ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // No answer: keep them apart.
            return false;
        }

        using var json = LlmJson.TryParse(raw);
        var together = json is not null
                       && json.RootElement.TryGetProperty("zusammen", out var flag)
                       && flag.ValueKind == System.Text.Json.JsonValueKind.True;
        RunLog.Log($"Merge-Check \"{a.Theme}\" ({a.Items.Count}) + \"{b.Theme}\" ({b.Items.Count}): " +
                   (together ? "ZUSAMMEN" : "getrennt"));
        return together;
    }

    private static string DescribePair(MediaGroup a, MediaGroup b)
    {
        var sb = new StringBuilder();
        AppendGroup(sb, "Gruppe A", a);
        AppendGroup(sb, "Gruppe B (zeitlich direkt danach)", b);
        return sb.ToString();
    }

    private static void AppendGroup(StringBuilder sb, string label, MediaGroup group)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        sb.AppendLine($"{label}: {group.Items.Count} Aufnahmen, " +
                      $"{group.StartLocal.ToString("yyyy-MM-dd ddd HH:mm", de)} bis " +
                      $"{group.EndLocal.ToString("yyyy-MM-dd ddd HH:mm", de)}");
        if (group.Centroid is { } c)
        {
            sb.AppendLine($"GPS-Schwerpunkt: {c}");
        }

        foreach (var sample in SampleItems(group))
        {
            var desc = string.IsNullOrWhiteSpace(sample.Description) ? "(keine Bildanalyse)" : sample.Description;
            var ort = string.IsNullOrWhiteSpace(sample.LocationGuess) ? "?" : sample.LocationGuess;
            sb.AppendLine($"- {sample.TakenLocal:HH:mm}: {desc} (Ort: {ort})");
        }

        sb.AppendLine();
    }

    /// <summary>First, middle and last shot as a sample.</summary>
    private static IEnumerable<MediaItem> SampleItems(MediaGroup group) =>
        group.Items.Count <= 3
            ? group.Items
            : [group.Items[0], group.Items[group.Items.Count / 2], group.Items[^1]];
}
