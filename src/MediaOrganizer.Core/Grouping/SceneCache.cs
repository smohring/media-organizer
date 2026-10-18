using System.Text.Json;
using MediaOrganizer.Core.Knowledge;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Grouping;

/// <summary>Stores one scene vector per file, keyed by content hash — the same
/// pattern as <see cref="Faces.FaceCache"/>: the vector is kept, never the
/// grouping made from it. Thresholds and rules change; a stored answer would be
/// worthless the moment they do, a stored vector never is.</summary>
public sealed class SceneCache
{
    /// <summary>Part of the key. Raised whenever the model or its
    /// preprocessing changes — old vectors would not be comparable.</summary>
    private const string EncoderVersion = "mobilenetv2-1000-v2";

    private const int MaxEntries = 40_000;
    private const int PruneTo = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly Dictionary<string, float[]> _entries;
    private bool _dirty;

    private SceneCache(string filePath, Dictionary<string, float[]> entries)
    {
        _filePath = filePath;
        _entries = entries;
    }

    public static string DefaultPath => Path.Combine(AppDataFolder.Root, "scene-cache.json");

    public static SceneCache Load() => Load(DefaultPath);

    public static SceneCache Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, float[]>>(
                    File.ReadAllText(filePath), JsonOptions);
                if (entries is not null)
                {
                    return new SceneCache(filePath, entries);
                }
            }
        }
        catch
        {
            // A broken cache is rebuilt empty.
        }

        return new SceneCache(filePath, []);
    }

    /// <summary>Discards every stored vector and reports how many shots were
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

    /// <summary>Key for a file; null when it cannot be read. Same content hash
    /// as the notes and the face cache — renaming and moving do not change
    /// it.</summary>
    public static string? ComputeKey(string filePath) =>
        KnowledgeStore.ComputeContentKey(filePath) is { } hash
            ? $"{hash}|{EncoderVersion}"
            : null;

    public float[]? TryGet(string key)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(key, out var vector) ? vector : null;
        }
    }

    public void Store(string key, float[] vector)
    {
        lock (_sync)
        {
            _entries[key] = vector;
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
                    foreach (var key in _entries.Keys.Take(_entries.Count - PruneTo).ToList())
                    {
                        _entries.Remove(key);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries, JsonOptions));
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The cache is a convenience — a run must not fail over it.
            }
        }
    }
}
