using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Grouping;

/// <summary>Groups shots by how similar they look, inside one day.
///
/// <para>Replaces the question to the language model. It only ever saw a
/// one-line description and merged "Anna on a wooden structure by a tree" with
/// "Anna with a wooden toy on a carpet"; the vectors put those 0.59 apart.</para></summary>
public static class SceneClustering
{
    /// <summary>How similar two shots must look to count as one occasion.
    ///
    /// <para>Set high on purpose. Measured on real shots: near-duplicates
    /// 0.97–0.99, the same occasion from another angle about 0.67, different
    /// occasions 0.09–0.61 — the band between the last two is narrow, so the
    /// threshold sits at the strict end of it. Splitting too much costs a second
    /// folder; merging too much costs a folder that claims something
    /// untrue.</para></summary>
    public const float SameOccasion = 0.72f;

    /// <summary>The pause that ends an occasion.
    ///
    /// <para>Deliberately NOT the event gap: that answers "is this still the
    /// same trip" and is a day. An occasion at home is a stretch of continuous
    /// shooting — and what ends it is a PAUSE, not a fixed length. Measured on
    /// one reported folder: chocolate at 09:02 and 09:24, hair clips at 10:02,
    /// a video at 10:19. The 38 minutes between 09:24 and 10:02 are the
    /// boundary; a fixed window of three hours swallowed all of it, because to
    /// an appearance encoder every shot is the same child in the same
    /// flat.</para></summary>
    public static readonly TimeSpan OccasionPause = TimeSpan.FromMinutes(30);

    /// <summary>How similar a shot without GPS must be to join an EXISTING
    /// group. Stricter than <see cref="SameOccasion"/> on purpose: a wrong
    /// merge inside a pool produces one bad folder, a wrong attachment pollutes
    /// a folder that was right.</summary>
    public const float JoinExisting = 0.85f;

    /// <summary>How many members of the group the shot is measured against.
    ///
    /// <para>A single match is not enough, and that was the reported defect: 15
    /// WhatsApp pictures joined "Anna beim Zähneputzen" because every close-up
    /// of the same child in the same top scores high against SOME photo in it.
    /// Measured against the average of the closest few, one lucky match can no
    /// longer carry a shot in.</para></summary>
    private const int VotersPerGroup = 3;

    /// <summary>The group this shot belongs to, or null. A candidate must cover
    /// the shot's time window, and the shot must fit the GROUP — the mean
    /// similarity to its closest <see cref="VotersPerGroup"/> members has to
    /// clear the threshold, not just its single best photo.</summary>
    /// <param name="members">The groups as they were BEFORE any shot was moved.
    /// A group that grows while shots are being placed drifts away from what it
    /// was: the newcomer becomes a member and carries in the next one.</param>
    public static MediaGroup? FindHome(
        MediaItem item,
        float[] vector,
        IReadOnlyList<MediaGroup> candidates,
        IReadOnlyDictionary<MediaGroup, IReadOnlyList<MediaItem>> members,
        IReadOnlyDictionary<MediaItem, float[]> vectors,
        TimeSpan maxGap,
        float threshold = JoinExisting)
    {
        MediaGroup? best = null;
        var bestScore = threshold;

        foreach (var group in candidates)
        {
            if (!members.TryGetValue(group, out var original))
            {
                continue;
            }

            var first = original.Min(m => m.TakenLocal);
            var last = original.Max(m => m.TakenLocal);
            if (item.TakenLocal < first - maxGap || item.TakenLocal > last + maxGap)
            {
                continue;
            }

            var scores = original
                .Select(m => vectors.TryGetValue(m, out var other)
                    ? SceneEmbedder.Similarity(vector, other)
                    : float.NaN)
                .Where(s => !float.IsNaN(s))
                .OrderByDescending(s => s)
                .Take(VotersPerGroup)
                .ToList();

            // Fewer members than voters would let a two-shot group decide on a
            // single photo again — the very thing this guards against.
            if (scores.Count < VotersPerGroup)
            {
                continue;
            }

            var score = scores.Average();
            if (score >= bestScore)
            {
                bestScore = score;
                best = group;
            }
        }

        return best;
    }

    /// <summary>A shot joins a cluster when it is close to the cluster's
    /// NEAREST member, not to its average: an occasion drifts — the table, then
    /// the garden, then the table again — and an average would sit between all
    /// of them and match nothing.</summary>
    public static List<List<MediaItem>> Cluster(
        IReadOnlyList<MediaItem> items,
        IReadOnlyDictionary<MediaItem, float[]> vectors,
        TimeSpan maxGap,
        float threshold = SameOccasion)
    {
        var clusters = new List<List<MediaItem>>();

        foreach (var item in items.OrderBy(i => i.TakenLocal))
        {
            if (!vectors.TryGetValue(item, out var vector))
            {
                // No vector, no evidence — its own cluster, which the minimum
                // size then drops back into the pool.
                clusters.Add([item]);
                continue;
            }

            var target = clusters
                .Select(c => (Cluster: c, Score: Closeness(c, item, vector, vectors, maxGap)))
                .Where(c => c.Score >= threshold)
                .OrderByDescending(c => c.Score)
                .Select(c => c.Cluster)
                .FirstOrDefault();

            if (target is null)
            {
                clusters.Add([item]);
            }
            else
            {
                target.Add(item);
            }
        }

        return clusters;
    }

    /// <summary>Similarity to the closest member, or 0 when the shot would
    /// stretch the cluster beyond the time window.
    ///
    /// <para>The window is measured over the WHOLE cluster, not to the nearest
    /// member. Measured to the neighbour it only bounds one step, and a cluster
    /// walks across days: 29th → 30th → 1st, every step inside a day, every
    /// step similar enough because it is the same child in the same flat.
    /// Reported as "20260430_Anna beim Zähneputzen" holding 31 shots where the
    /// day itself has 11.</para></summary>
    private static float Closeness(
        List<MediaItem> cluster,
        MediaItem item,
        float[] vector,
        IReadOnlyDictionary<MediaItem, float[]> vectors,
        TimeSpan maxGap)
    {
        var first = cluster.Min(m => m.TakenLocal);
        var last = cluster.Max(m => m.TakenLocal);
        if (Max(last, item.TakenLocal) - Min(first, item.TakenLocal) > maxGap)
        {
            return 0;
        }

        var best = 0f;
        foreach (var member in cluster)
        {
            if (!vectors.TryGetValue(member, out var other))
            {
                continue;
            }

            var score = SceneEmbedder.Similarity(vector, other);
            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
