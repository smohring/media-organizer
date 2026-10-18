using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Faces;

/// <summary>A cluster of visually matching faces — presumably the same
/// person.</summary>
public sealed class FaceCluster
{
    public List<DetectedFace> Faces { get; } = [];

    /// <summary>The best face — largest and clearest — used as the
    /// preview.</summary>
    public DetectedFace Representative =>
        Faces.OrderByDescending(f => f.Score).ThenByDescending(f => f.Box.Width * f.Box.Height).First();

    /// <summary>A suggestion from the people already known, where one
    /// fits.</summary>
    public PersonMatch? ExistingMatch { get; set; }

    /// <summary>Up to four reference faces, as different from each other as
    /// possible — more variety means more robust recognition later.</summary>
    public List<DetectedFace> SelectReferences(int max = 4)
    {
        var ordered = Faces.OrderByDescending(f => f.Score).ToList();
        var chosen = new List<DetectedFace> { ordered[0] };

        foreach (var candidate in ordered.Skip(1))
        {
            if (chosen.Count >= max)
            {
                break;
            }

            // Only take faces that differ noticeably from the ones already
            // chosen — a different angle or light. Near-duplicates add
            // nothing.
            var maxSimilarity = chosen.Max(c => FaceService.Similarity(c.Embedding, candidate.Embedding));
            if (maxSimilarity < 0.85)
            {
                chosen.Add(candidate);
            }
        }

        return chosen;
    }
}

/// <summary>Clusters every face in a folder into people, so that each person
/// is named once instead of photo by photo.</summary>
public static class FaceClusterer
{
    /// <summary>Deliberately stricter than the recognition threshold: better
    /// two clusters of the same person, which are easy to merge, than two
    /// people in one cluster, which corrupts the reference vectors for
    /// good.</summary>
    private const double ClusterThreshold = 0.45;

    public static List<FaceCluster> ClusterFolder(
        string folder,
        FaceService faces,
        IReadOnlyList<KnownPerson> knownPersons,
        int maxFiles,
        IProgress<(int Done, int Total, string File)> progress,
        CancellationToken ct)
    {
        var files = Directory
            .EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            })
            .Where(f => MediaMetadataReader.Classify(f) == MediaKind.Image)
            .OrderBy(f => f)
            .Take(maxFiles)
            .ToList();

        var clusters = new List<FaceCluster>();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report((i + 1, files.Count, Path.GetFileName(files[i])));

            List<DetectedFace> detected;
            try
            {
                detected = faces.DetectFaces(files[i], includeCrops: true);
            }
            catch
            {
                continue;
            }

            foreach (var face in detected)
            {
                var best = clusters
                    .Select(c => (Cluster: c, Score: c.Faces.Max(f => FaceService.Similarity(f.Embedding, face.Embedding))))
                    .OrderByDescending(x => x.Score)
                    .FirstOrDefault();

                if (best.Cluster is not null && best.Score >= ClusterThreshold)
                {
                    best.Cluster.Faces.Add(face);
                }
                else
                {
                    var cluster = new FaceCluster();
                    cluster.Faces.Add(face);
                    cluster.ExistingMatch = FaceService.Match(face.Embedding, knownPersons);
                    clusters.Add(cluster);
                }
            }
        }

        // A face found once is usually a stranger or a misdetection, so put
        // the most frequent ones first.
        return clusters.OrderByDescending(c => c.Faces.Count).ToList();
    }
}
