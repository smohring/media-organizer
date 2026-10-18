using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.FileOps;

/// <summary>Eine ausgeführte Dateioperation — Quelle und endgültiges Ziel.</summary>
public sealed class JournalEntry
{
    public string SourcePath { get; set; } = "";

    public string FinalPath { get; set; } = "";

    /// <summary>Kopiert (Quelle liegt noch am Ursprung) statt verschoben.</summary>
    public bool WasCopy { get; set; }
}

/// <summary>Ergebnis eines Rückgängig-Vorgangs.</summary>
public sealed record UndoOutcome(
    int Restored,
    int Deleted,
    int Skipped,
    int Failed,
    int RemovedDirectories,
    List<Models.RunWarning> Notes);

/// <summary>Protokoll des letzten ausgeführten Plans (Quelle → Ziel), damit
/// sich ein Lauf rückwärts abspielen lässt.
/// <para>Datensicherheit hat auch beim Rückgängigmachen Vorrang: Es wird nie
/// überschrieben, und eine Kopie wird nur dann gelöscht, wenn das Original
/// nachweislich (Größe + SHA-256) noch am Ursprungsort liegt.</para></summary>
public sealed class RunJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public DateTime CompletedLocal { get; set; } = DateTime.Now;

    public TransferMode Mode { get; set; }

    public List<JournalEntry> Entries { get; set; } = [];

    /// <summary>Beim Lauf neu angelegte Verzeichnisse (tiefste zuerst
    /// entfernbar), damit kein leeres Gerippe zurückbleibt.</summary>
    public List<string> CreatedDirectories { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Entries.Count == 0;

    public static string DefaultPath => Path.Combine(AppDataFolder.Root, "last-run.json");

    public void Save(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Das Protokoll ist Komfort — sein Fehlschlag darf nichts stoppen.
        }
    }

    public static RunJournal? Load(string? path = null)
    {
        try
        {
            var source = path ?? DefaultPath;
            if (!File.Exists(source))
            {
                return null;
            }

            var journal = JsonSerializer.Deserialize<RunJournal>(File.ReadAllText(source));
            return journal is { Entries.Count: > 0 } ? journal : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Clear(string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unkritisch.
        }
    }

    /// <summary>Nachträgliche Korrektur in der Ergebnisansicht nachziehen,
    /// damit das Protokoll die Wirklichkeit weiter abbildet.</summary>
    public bool Relocate(string oldFinalPath, string newFinalPath)
    {
        var entry = Entries.FirstOrDefault(e =>
            e.FinalPath.Equals(oldFinalPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }

        entry.FinalPath = newFinalPath;
        return true;
    }

    /// <summary>Spielt den Lauf rückwärts ab.</summary>
    public UndoOutcome Undo(IProgress<PipelineProgress>? progress, CancellationToken ct)
    {
        var restored = 0;
        var deleted = 0;
        var skipped = 0;
        var failed = 0;
        var notes = new List<Models.RunWarning>();

        // Rückwärts, damit spätere Operationen zuerst zurückgenommen werden.
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var entry = Entries[i];
            var done = Entries.Count - i;
            progress?.Report(new PipelineProgress(
                PipelinePhase.Undo, done, Entries.Count,
                ProgressKeys.Restoring, done, Entries.Count));

            if (string.IsNullOrWhiteSpace(entry.FinalPath) || !File.Exists(entry.FinalPath))
            {
                skipped++;
                continue;
            }

            try
            {
                if (entry.WasCopy)
                {
                    // Eine Kopie darf nur verschwinden, wenn das Original noch
                    // da und nachweislich identisch ist — sonst wäre sie die
                    // letzte verbliebene Fassung.
                    if (File.Exists(entry.SourcePath)
                        && AreIdentical(entry.SourcePath, entry.FinalPath))
                    {
                        File.Delete(entry.FinalPath);
                        deleted++;
                    }
                    else
                    {
                        skipped++;
                        notes.Add(Services.RunWarnings.UndoCopyKept(Path.GetFileName(entry.FinalPath)));
                    }

                    continue;
                }

                if (File.Exists(entry.SourcePath))
                {
                    // Am Ursprungsort liegt wieder etwas: nicht überschreiben.
                    skipped++;
                    notes.Add(Services.RunWarnings.UndoSourceTaken(Path.GetFileName(entry.SourcePath)));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(entry.SourcePath)!);
                File.Move(entry.FinalPath, entry.SourcePath);
                restored++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                notes.Add(Services.RunWarnings.UndoFailed(Path.GetFileName(entry.FinalPath), ex.Message));
            }
        }

        // Angelegte Verzeichnisse abräumen, tiefste zuerst — aber nur, wenn
        // sie tatsächlich leer sind.
        var removedDirs = 0;
        foreach (var dir in CreatedDirectories
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    removedDirs++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nicht entfernbare Ordner bleiben stehen.
            }
        }

        return new UndoOutcome(restored, deleted, skipped, failed, removedDirs, notes);
    }

    private static bool AreIdentical(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length)
        {
            return false;
        }

        using var streamA = File.OpenRead(a);
        var hashA = SHA256.HashData(streamA);
        using var streamB = File.OpenRead(b);
        return hashA.AsSpan().SequenceEqual(SHA256.HashData(streamB));
    }
}
