using System.Text.Json;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Grouping;

/// <summary>What a file looked like last time: perceptual hash and dimensions.
/// Decoding every picture costs 32 ms and is by far the most expensive part of
/// finding duplicates — 3.2 minutes over one collection, in every run.
///
/// <para>Keyed by path, size and write time, NOT by content hash as the other
/// caches are: the content hash would have to read every file in full, which is
/// most of what it saves. The source folder is one the application never
/// writes to, so a file that has not changed in size or time is the same
/// file.</para></summary>
public sealed class DuplicateCache
{
    /// <summary>Part of the key. Raised whenever the hash or the comparison
    /// changes.</summary>
    private const string Version = "dhash64-v1";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _sync = new();
    private readonly string _filePath;
    private readonly Dictionary<string, Entry> _entries;
    private bool _dirty;

    private DuplicateCache(string filePath, Dictionary<string, Entry> entries)
    {
        _filePath = filePath;
        _entries = entries;
    }

    public sealed record Entry(ulong Hash, int Width, int Height);

    public static string DefaultPath => Path.Combine(AppDataFolder.Root, "duplicate-cache.json");

    public static DuplicateCache Load() => Load(DefaultPath);

    public static DuplicateCache Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                    File.ReadAllText(filePath), JsonOptions);
                if (entries is not null)
                {
                    return new DuplicateCache(filePath, entries);
                }
            }
        }
        catch
        {
            // A broken cache is rebuilt empty.
        }

        return new DuplicateCache(filePath, []);
    }

    public static string? ComputeKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{Version}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public Entry? TryGet(string key)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(key, out var entry) ? entry : null;
        }
    }

    public void Store(string key, Entry entry)
    {
        lock (_sync)
        {
            _entries[key] = entry;
            _dirty = true;
        }
    }

    /// <summary>Writes the cache, keeping only the files of this run: the key
    /// carries the path, so a moved or deleted file would otherwise stay in it
    /// for good.</summary>
    public void Save(IReadOnlySet<string> keysInUse)
    {
        lock (_sync)
        {
            if (!_dirty)
            {
                return;
            }

            try
            {
                foreach (var key in _entries.Keys.Where(k => !keysInUse.Contains(k)).ToList())
                {
                    _entries.Remove(key);
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
