using System.Runtime.InteropServices;
using System.Text.Json;
using MediaOrganizer.Core.Knowledge;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Faces;

/// <summary>A found face, reduced to what is still needed after detection:
/// the feature vector and where it sits in the frame.</summary>
public sealed record FaceSignature(float[] Embedding, double RelativeCenterX)
{
    /// <summary>The position given to the prompt, so the model puts names on
    /// the right people.</summary>
    public string PositionHint(OutputLanguage language) =>
        DetectedFace.PositionHintFor(RelativeCenterX, language);
}

/// <summary>Persistent cache of the face recognition.
///
/// <para>What is stored are the <b>feature vectors</b>, not the finished
/// assignment to people. That is the decisive difference: the expensive part
/// is finding and measuring the faces — decoding the image and running two
/// neural networks, measured at 47 ms per image. Comparing against the known
/// people afterwards is a dot product and takes microseconds.</para>
///
/// <para>Storing the finished assignment would make the cache worthless the
/// moment a person is added or a reference face comes in — which is exactly
/// when someone starts a new run. This way it stays valid: the assignment is
/// recomputed from the stored vectors on every run.</para></summary>
public sealed class FaceCache
{
    /// <summary>Part of the key. Raised whenever the detector, its threshold
    /// or the alignment changes — old vectors would no longer be
    /// comparable.</summary>
    private const string DetectorVersion = "yunet-sface-0.40";

    private const int MaxEntries = 40_000;
    private const int PruneTo = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly Dictionary<string, List<CachedFace>> _entries;
    private bool _dirty;

    private FaceCache(string filePath, Dictionary<string, List<CachedFace>> entries)
    {
        _filePath = filePath;
        _entries = entries;
    }

    public static string DefaultPath => Path.Combine(AppDataFolder.Root, "face-cache.json");

    public static FaceCache Load() => Load(DefaultPath);

    public static FaceCache Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, List<CachedFace>>>(
                    File.ReadAllText(filePath), JsonOptions);
                if (entries is not null)
                {
                    return new FaceCache(filePath, entries);
                }
            }
        }
        catch
        {
            // Defekter Cache: leer neu aufbauen.
        }

        return new FaceCache(filePath, []);
    }

    /// <summary>Discards every stored face and reports how many shots were
    /// affected.</summary>
    public static int ClearDefault()
    {
        try
        {
            if (!File.Exists(DefaultPath))
            {
                return 0;
            }

            var count = Load(DefaultPath)._entries.Count;
            File.Delete(DefaultPath);
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Key for a file; null when it cannot be read. Uses the same
    /// content hash as the notes — renaming and moving do not change
    /// it.</summary>
    public static string? ComputeKey(string filePath) =>
        KnowledgeStore.ComputeContentKey(filePath) is { } hash
            ? $"{hash}|{DetectorVersion}"
            : null;

    public List<FaceSignature>? TryGet(string key)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var faces))
            {
                return null;
            }

            var result = new List<FaceSignature>(faces.Count);
            foreach (var face in faces)
            {
                if (Decode(face.E) is { } embedding)
                {
                    result.Add(new FaceSignature(embedding, face.X));
                }
            }

            return result;
        }
    }

    public void Store(string key, IReadOnlyList<FaceSignature> faces)
    {
        lock (_sync)
        {
            _entries[key] = [.. faces.Select(f => new CachedFace
            {
                E = Encode(f.Embedding),
                X = f.RelativeCenterX,
            })];
            _dirty = true;
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            if (!_dirty)
            {
                return;
            }

            try
            {
                if (_entries.Count > MaxEntries)
                {
                    // No usage timestamps here, so simply drop the ones
                    // inserted first. The cache is pure speed-up; a wrong
                    // choice costs one recomputation.
                    foreach (var stale in _entries.Keys.Take(_entries.Count - PruneTo).ToList())
                    {
                        _entries.Remove(stale);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries, JsonOptions));
                _dirty = false;
            }
            catch
            {
                // The cache is pure optimisation — a failure here must never
                // stop the run.
            }
        }
    }

    /// <summary>128 floats as Base64 — as a JSON list of numbers the file
    /// would be several times larger and noticeably slower to read.</summary>
    private static string Encode(float[] embedding) =>
        Convert.ToBase64String(MemoryMarshal.AsBytes<float>(embedding));

    private static float[]? Decode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(text);
            return bytes.Length % sizeof(float) != 0
                ? null
                : MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Short field names: across tens of thousands of faces the
    /// difference between "embedding" and "e" runs to several
    /// megabytes.</summary>
    private sealed class CachedFace
    {
        public string? E { get; set; }

        public double X { get; set; }
    }
}
