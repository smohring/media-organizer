using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Grouping;

/// <summary>Base clustering by time: splits a chronologically sorted list at
/// gaps larger than <c>maxGap</c>.</summary>
public static class TimeClustering
{
    public static List<List<MediaItem>> SplitAtGaps(IReadOnlyList<MediaItem> sortedItems, TimeSpan maxGap)
    {
        var clusters = new List<List<MediaItem>>();
        List<MediaItem>? current = null;
        DateTime lastTime = default;

        foreach (var item in sortedItems)
        {
            if (current is null || item.TakenLocal - lastTime > maxGap)
            {
                current = [];
                clusters.Add(current);
            }

            current.Add(item);
            lastTime = item.TakenLocal;
        }

        return clusters;
    }
}
