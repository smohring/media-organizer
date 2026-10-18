using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Llm;

/// <summary>Persistent cache of the model's image descriptions, so that repeat
/// and follow-up runs skip files already analysed.
///
/// <para>The key is a SHA-256 over the file length, the first and last 64 KB
/// and the model tag. Hashing only the ends survives renaming and moving
/// without reading a large video from end to end. The model tag is part of it
/// so that switching models does not recycle descriptions another model
/// wrote.</para></summary>
public sealed class DescriptionCache
{
    private const int MaxEntries = 6000;
    private const int PruneTo = 4000;
    private const int SaveEvery = 25;

    /// <summary>Shortest interval between two intermediate saves.
    ///
    /// <para>Every save serialises the <b>whole</b> file again. Triggered by
    /// count alone, ten thousand shots meant 400 full writes of a file that can
    /// run to tens of megabytes. The time bar brings that down to a few dozen
    /// without giving up the point of it: after a crash, at most half a minute
    /// of analysis is lost.</para></summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Analysis runs concurrently — without this lock several tasks
    /// write into the same <see cref="Dictionary{K,V}"/> at once and corrupt it
    /// silently.</summary>
    private readonly object _sync = new();

    private readonly string _filePath;
    private readonly Dictionary<string, CacheEntry> _entries;
    private int _dirtyCount;
    private long _lastSaveTick = Environment.TickCount64;

    private DescriptionCache(string filePath, Dictionary<string, CacheEntry> entries)
    {
        _filePath = filePath;
        _entries = entries;
    }

    /// <summary>Where it lives in the user data. Needed in two places: during
    /// a run, and when a description is edited afterwards.</summary>
    public static string DefaultPath => Path.Combine(AppDataFolder.Root, "analysis-cache.json");

    /// <summary>Discards every stored description and reports how many there
    /// were.
    ///
    /// <para>Meant for the case where the entries no longer match the current
    /// state — after new reference faces or changed facts, for instance. The
    /// next run then analyses everything again, which costs noticeable
    /// time.</para></summary>
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

    public static DescriptionCache Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(
                    File.ReadAllText(filePath), JsonOptions);
                if (entries is not null)
                {
                    return new DescriptionCache(filePath, entries);
                }
            }
        }
        catch
        {
            // Corrupted cache: start again from empty.
        }

        return new DescriptionCache(filePath, []);
    }

    /// <summary>Applies a cache hit to the item; false on a miss.</summary>
    public bool TryApply(string key, MediaItem item)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            item.Description = entry.Beschreibung;
            item.LocationGuess = entry.Ort;
            item.Category = entry.Kategorie;

            // The timestamp only serves the pruning of old entries, so it
            // deliberately does NOT mark the file as needing a write: before
            // this, a run that landed nothing but hits still triggered dozens
            // of full rewrites.
            entry.LastUsedUtc = DateTime.UtcNow.Ticks;
            return true;
        }
    }

    public void Store(string key, MediaItem item)
    {
        var saveNow = false;
        lock (_sync)
        {
            _entries[key] = new CacheEntry
            {
                Beschreibung = item.Description,
                Ort = item.LocationGuess,
                Kategorie = item.Category,
                LastUsedUtc = DateTime.UtcNow.Ticks,
            };

            if (++_dirtyCount >= SaveEvery
                && Environment.TickCount64 - _lastSaveTick >= SaveInterval.TotalMilliseconds)
            {
                saveNow = true;
            }
        }

        if (saveNow)
        {
            Save();
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            if (_dirtyCount == 0)
            {
                return;
            }

            try
            {
                if (_entries.Count > MaxEntries)
                {
                    foreach (var stale in _entries
                                 .OrderBy(e => e.Value.LastUsedUtc)
                                 .Take(_entries.Count - PruneTo)
                                 .Select(e => e.Key)
                                 .ToList())
                    {
                        _entries.Remove(stale);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries, JsonOptions));
                _dirtyCount = 0;
                _lastSaveTick = Environment.TickCount64;
            }
            catch
            {
                // The cache is pure optimisation — a failure here must never
                // stop the run.
            }
        }
    }

    /// <summary>Computes the cache key; null when the file cannot be
    /// read.</summary>
    public static string? ComputeKey(string filePath, string modelTag)
    {
        const int sampleSize = 64 * 1024;
        try
        {
            using var stream = File.OpenRead(filePath);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            hash.AppendData(BitConverter.GetBytes(stream.Length));

            var buffer = new byte[sampleSize];
            var read = stream.Read(buffer, 0, buffer.Length);
            hash.AppendData(buffer.AsSpan(0, read));

            if (stream.Length > sampleSize * 2)
            {
                stream.Seek(-sampleSize, SeekOrigin.End);
                read = stream.Read(buffer, 0, buffer.Length);
                hash.AppendData(buffer.AsSpan(0, read));
            }

            hash.AppendData(Encoding.UTF8.GetBytes(modelTag));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch
        {
            return null;
        }
    }

    private sealed class CacheEntry
    {
        public string? Beschreibung { get; set; }

        public string? Ort { get; set; }

        public string? Kategorie { get; set; }

        public long LastUsedUtc { get; set; }
    }
}
