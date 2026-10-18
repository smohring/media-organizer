using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Grouping;

/// <summary>Finds bursts: several shutter releases of the same camera within
/// a few seconds at the same place.
///
/// <para>The only purpose is to stop the model describing the same scene over
/// and over. In a real run, six shots taken over 15 seconds cost six vision
/// calls and produced the same sentence six times about the same smiling
/// child.</para>
///
/// <para><b>Deliberately narrow.</b> There was once a broader inheritance
/// between "similar" images; it was removed because it spread wrong
/// descriptions on real photos. So nothing here is decided by a similarity
/// measure — only by hard metadata, and any uncertainty ends the burst rather
/// than extending it.</para></summary>
internal static class BurstDetector
{
    /// <summary>Longest gap between two releases of one burst.</summary>
    internal const double MaxGapSeconds = 5;

    /// <summary>Longest a burst may run in total. Without this limit a chain
    /// of four-second gaps could grow without end and show something entirely
    /// different at the end than at the start.</summary>
    internal const double MaxSpanSeconds = 30;

    /// <summary>50 m — the spread of two GPS readings taken on the same
    /// spot.</summary>
    internal const double MaxDistanceKm = 0.05;

    /// <summary>Splits the shots into chronological bursts. Every list holds
    /// at least one entry; its first is the stand-in that actually gets
    /// analysed.</summary>
    internal static List<List<MediaItem>> Group(IReadOnlyList<MediaItem> items)
    {
        var result = new List<List<MediaItem>>();
        foreach (var item in items.OrderBy(i => i.TakenLocal))
        {
            if (result.Count > 0 && BelongsToBurst(result[^1], item))
            {
                result[^1].Add(item);
            }
            else
            {
                result.Add([item]);
            }
        }

        return result;
    }

    private static bool BelongsToBurst(List<MediaItem> burst, MediaItem candidate)
    {
        var first = burst[0];
        var previous = burst[^1];

        if (!CanBurst(first) || !CanBurst(candidate))
        {
            return false;
        }

        // Different devices never fire one burst together — and two people
        // photographing at the same moment rarely show the same thing.
        if (!string.Equals(first.CameraModel, candidate.CameraModel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if ((candidate.TakenLocal - previous.TakenLocal).TotalSeconds > MaxGapSeconds
            || (candidate.TakenLocal - first.TakenLocal).TotalSeconds > MaxSpanSeconds)
        {
            return false;
        }

        // Place: either both without GPS (a camera with no reception) or both
        // on the same spot. If only one carries coordinates the situation is
        // unclear — and unclear separates.
        if (first.Location is { } a && candidate.Location is { } b)
        {
            if (a.DistanceKm(b) > MaxDistanceKm)
            {
                return false;
            }
        }
        else if (first.Location is not null || candidate.Location is not null)
        {
            return false;
        }

        // Who is in the picture goes into the prompt and therefore into the
        // description. If the recognised people differ, the sentence must not
        // be reused — otherwise a shot carries the name of someone who is not
        // in it.
        return first.DetectedFaceCount == candidate.DetectedFaceCount
               && first.RecognizedPeopleWithPosition
                   .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                   .SequenceEqual(
                       candidate.RecognizedPeopleWithPosition.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
                       StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>May this shot be part of a burst at all?</summary>
    private static bool CanBurst(MediaItem item) =>
        // A video shows more than one moment over its running time.
        item.Kind == MediaKind.Image

        // Only a timestamp from the device itself. Copying gives hundreds of
        // files the same file system timestamp, and they would look like one
        // enormous burst; a date out of the file name has no time of day worth
        // measuring a burst against. Images from messenger folders arrive
        // exactly like that.
        && item.TimestampSource is TimestampSource.ExifOriginal or TimestampSource.ContainerCreated

        // Without a camera model there is no evidence that the same device
        // fired.
        && !string.IsNullOrWhiteSpace(item.CameraModel)

        // A note applies to that one image and changes its prompt.
        && string.IsNullOrWhiteSpace(item.UserNote);
}
