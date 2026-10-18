using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;
using OpenCvSharp;

namespace MediaOrganizer.Core.Grouping;

/// <summary>Finds shots that are the SAME picture twice — the WhatsApp copy
/// beside the original, or the same file in two source folders.
///
/// <para>A file hash cannot see it: the WhatsApp copy is re-encoded, smaller
/// and carries no EXIF. So a perceptual hash pre-filters and a pixel comparison
/// decides.</para></summary>
public static class DuplicateFinder
{
    /// <summary>Largest mean brightness deviation on 64×64 that still counts as
    /// the same picture.
    ///
    /// <para>Measured on the reported collection: a WhatsApp copy against its
    /// original scores 0.04, two burst shots 149 ms apart already 4.13, the same
    /// occasion nine seconds later 20.55. The threshold sits at 1.0 — 25× above
    /// the duplicates, 4× below the closest picture that is merely
    /// similar.</para></summary>
    private const double SamePicture = 1.0;

    /// <summary>Groups of shots that are the same picture, each ordered best
    /// first: the copy worth keeping comes first.</summary>
    public static List<List<MediaItem>> Find(
        IReadOnlyList<MediaItem> items,
        DuplicateCache cache,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        // First pass: the hash of every picture, from the cache where possible.
        // Decoding is the whole cost of this step.
        var byHash = new Dictionary<ulong, List<(MediaItem Item, int Width, int Height)>>();
        var keysInUse = new HashSet<string>();
        var pictures = items.Where(i => i.Kind == MediaKind.Image).ToList();
        var done = 0;

        foreach (var item in pictures)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 25 == 0 || done == pictures.Count)
            {
                progress.Report(new PipelineProgress(
                    PipelinePhase.Metadata, done, pictures.Count,
                    ProgressKeys.FindingDuplicates, done, pictures.Count));
            }

            var key = DuplicateCache.ComputeKey(item.SourcePath);
            if (key is not null)
            {
                keysInUse.Add(key);
            }

            var entry = key is null ? null : cache.TryGet(key);
            if (entry is null)
            {
                using var image = Cv2.ImRead(item.SourcePath, ImreadModes.Grayscale);
                if (image.Empty())
                {
                    continue;
                }

                entry = new DuplicateCache.Entry(DHash(image), image.Width, image.Height);
                if (key is not null)
                {
                    cache.Store(key, entry);
                }
            }

            if (!byHash.TryGetValue(entry.Hash, out var bucket))
            {
                byHash[entry.Hash] = bucket = [];
            }

            bucket.Add((item, entry.Width, entry.Height));
        }

        cache.Save(keysInUse);

        // Second pass: only the candidates are decoded again, for the pixel
        // comparison that decides. Measured, that is a fraction of the files.
        return [.. byHash.Values
            .Where(b => b.Count > 1)
            .SelectMany(Verify)
            .Where(g => g.Count > 1)];
    }

    /// <summary>Splits a hash bucket into groups that really are the same
    /// picture. The hash only pre-filters: burst shots and low-detail pictures
    /// collide, and a folder is not worth a lost photo.</summary>
    private static List<List<MediaItem>> Verify(
        List<(MediaItem Item, int Width, int Height)> bucket)
    {
        var decoded = new List<(MediaItem Item, Mat Small, int Width, int Height)>();
        try
        {
            foreach (var (item, width, height) in bucket)
            {
                using var image = Cv2.ImRead(item.SourcePath, ImreadModes.Grayscale);
                if (image.Empty())
                {
                    continue;
                }

                var small = new Mat();
                Cv2.Resize(image, small, new Size(64, 64), interpolation: InterpolationFlags.Area);
                decoded.Add((item, small, width, height));
            }

            var groups = new List<List<(MediaItem Item, Mat Small, int Width, int Height)>>();
            foreach (var candidate in decoded)
            {
                var home = groups.FirstOrDefault(g => IsSamePicture(g[0], candidate));
                if (home is null)
                {
                    groups.Add([candidate]);
                }
                else
                {
                    home.Add(candidate);
                }
            }

            // Best first: the most pixels win, then the larger file — the
            // WhatsApp copy is the one that was thrown away, and it is the
            // smaller one.
            return [.. groups.Select(g => g
                .OrderByDescending(x => (long)x.Width * x.Height)
                .ThenByDescending(x => Size(x.Item))
                .Select(x => x.Item)
                .ToList())];
        }
        finally
        {
            foreach (var (_, small, _, _) in decoded)
            {
                small.Dispose();
            }
        }
    }

    private static bool IsSamePicture(
        (MediaItem Item, Mat Small, int Width, int Height) a,
        (MediaItem Item, Mat Small, int Width, int Height) b)
    {
        // A different aspect ratio is a different picture, whatever the hash
        // says — measured, 258 of 926 buckets were collisions of this kind.
        if (Math.Abs(((double)a.Width / a.Height) - ((double)b.Width / b.Height)) > 0.01)
        {
            return false;
        }

        using var diff = new Mat();
        Cv2.Absdiff(a.Small, b.Small, diff);
        return Cv2.Mean(diff).Val0 <= SamePicture;
    }

    private static long Size(MediaItem item)
    {
        try
        {
            return new FileInfo(item.SourcePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>64-bit perceptual hash: scale to 9×8, compare neighbours. Blind
    /// to scaling and re-encoding, which is exactly what a WhatsApp copy
    /// is.</summary>
    private static ulong DHash(Mat gray)
    {
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(9, 8), interpolation: InterpolationFlags.Area);

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (small.At<byte>(y, x) < small.At<byte>(y, x + 1))
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return hash;
    }
}
