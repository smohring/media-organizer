using System.Security.Cryptography;
using System.Text.Json;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Knowledge;

/// <summary>Persistente Wissensbasis der App:
/// - <b>Fakten</b>: kurze, dauerhafte Sätze ("Mia ist die Tochter, Kleinkind"),
///   die als Kontext in Analyse- und Benennungs-Prompts einfließen. Entstehen
///   durch Destillation von Bild-Notizen oder manuell in der UI.
/// - <b>Notizen</b>: vom Nutzer an einzelne Bilder angeheftete Hinweise,
///   per Inhalts-Hash zugeordnet (überleben Verschieben/Umbenennen).
/// Gespeichert unter %AppData%\MediaOrganizer\knowledge.json.</summary>
public sealed class KnowledgeStore
{
    private const int MaxFacts = 40;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly StoreData _data;

    public static string DefaultPath => Path.Combine(AppDataFolder.Root, "knowledge.json");

    /// <summary>Ablage der Notiz-Vorschaubilder. Eine Notiz soll auch dann
    /// noch etwas zeigen, wenn das Originalbild verschoben, umbenannt oder
    /// gelöscht wurde — deshalb liegt eine eigene Miniatur in den Nutzerdaten
    /// und nicht nur ein Verweis auf die Originaldatei.</summary>
    public static string NotesDirectory => Path.Combine(AppDataFolder.Root, "notes");

    private KnowledgeStore(string filePath, StoreData data)
    {
        _filePath = filePath;
        _data = data;
    }

    public static KnowledgeStore Load(string? filePath = null)
    {
        var path = filePath ?? DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path), JsonOptions);
                if (data is not null)
                {
                    return new KnowledgeStore(path, data);
                }
            }
        }
        catch
        {
            // Defekte Datei: leer starten (Notizen/Fakten sind Komfort).
        }

        return new KnowledgeStore(path, new StoreData());
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            // Handgepflegtes Wissen ist nicht reproduzierbar: Vor jedem
            // Überschreiben eine Sicherung anlegen — und eine zweite, die den
            // letzten Stand MIT Inhalt festhält. Ein versehentliches Leeren
            // bleibt damit rückholbar.
            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, _filePath + ".bak", overwrite: true);
                if (_data.Facts.Count == 0 && _data.Notes.Count == 0
                    && new FileInfo(_filePath).Length > 40)
                {
                    File.Copy(_filePath, _filePath + ".lastNonEmpty.bak", overwrite: true);
                }
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, JsonOptions));
        }
        catch
        {
            // Speichern darf nie einen Lauf stoppen.
        }
    }

    // ------------------------------------------------------------------
    // Fakten
    // ------------------------------------------------------------------

    public IReadOnlyList<string> Facts => _data.Facts;

    public bool AddFact(string fact)
    {
        fact = fact.Trim();
        if (fact.Length == 0
            || _data.Facts.Any(f => f.Equals(fact, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        _data.Facts.Add(fact);
        if (_data.Facts.Count > MaxFacts)
        {
            _data.Facts.RemoveAt(0);
        }

        return true;
    }

    public void RemoveFact(string fact) => _data.Facts.Remove(fact);

    /// <summary>Ersetzt einen Fakt unter Beibehaltung seiner Position.</summary>
    public void ReplaceFact(string oldFact, string newFact)
    {
        var index = _data.Facts.IndexOf(oldFact);
        if (index < 0)
        {
            AddFact(newFact);
            return;
        }

        if (_data.Facts.Any(f => f.Equals(newFact, StringComparison.OrdinalIgnoreCase)))
        {
            _data.Facts.RemoveAt(index);
            return;
        }

        _data.Facts[index] = newFact.Trim();
    }

    // ------------------------------------------------------------------
    // Notizen
    // ------------------------------------------------------------------

    /// <summary>Gibt es überhaupt Notizen? Ohne sie muss keine einzige Datei
    /// für die Zuordnung gehasht werden — das spart bei großen Sammlungen
    /// einen kompletten Lesedurchgang über alle Aufnahmen.</summary>
    public bool HasNotes => _data.Notes.Count > 0;

    public string? GetNote(string contentKey) =>
        _data.Notes.TryGetValue(contentKey, out var entry) ? entry.Note : null;

    /// <summary>Eine Notiz samt eigener Vorschau in den Nutzerdaten.
    ///
    /// <para><see cref="ThumbnailPath"/> ist das, was angezeigt wird — es liegt
    /// unter <c>%AppData%\MediaOrganizer\notes\</c> und ist vom Quellordner
    /// unabhängig. <see cref="LastKnownPath"/> ist reine Buchführung: Es dient
    /// nur noch dazu, für Altbestand ohne eigene Kopie einmalig eine
    /// anzulegen. Ob die Datei dort noch liegt, ist für die Gültigkeit der
    /// Notiz ohne Bedeutung — zugeordnet wird über den Inhalts-Hash.</para></summary>
    public sealed record NoteInfo(
        string Key,
        string Note,
        string FileName,
        string? LastKnownPath,
        string? ThumbnailPath);

    public IReadOnlyList<NoteInfo> AllNotes =>
        _data.Notes
            .OrderByDescending(kv => kv.Value.AddedUtc)
            .Select(kv => new NoteInfo(
                kv.Key,
                kv.Value.Note,
                kv.Value.FileName,
                kv.Value.LastKnownPath,
                ThumbnailPathFor(kv.Key, kv.Value.ThumbnailFile)))
            .ToList();

    private static string? ThumbnailPathFor(string key, string? file)
    {
        if (file is null)
        {
            return null;
        }

        var path = Path.Combine(NotesDirectory, file);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Setzt/aktualisiert eine Notiz; leerer Text entfernt sie.
    /// <para><paramref name="thumbnailJpeg"/> wird als eigene Miniatur in den
    /// Nutzerdaten abgelegt, damit die Verwaltung das Bild auch dann noch
    /// zeigt, wenn das Original später verschoben oder gelöscht wird. Die
    /// Zuordnung selbst läuft weiter über den Inhalts-Hash und überlebt damit
    /// Verschieben und Umbenennen.</para></summary>
    public void SetNote(
        string contentKey,
        string note,
        string fileName,
        string? lastKnownPath = null,
        byte[]? thumbnailJpeg = null)
    {
        note = note.Trim();
        if (note.Length == 0)
        {
            if (_data.Notes.Remove(contentKey, out var removed) && removed.ThumbnailFile is { } stale)
            {
                TryDeleteThumbnail(stale);
            }

            return;
        }

        var wasDistilled = _data.Notes.TryGetValue(contentKey, out var previous)
                           && previous.Distilled
                           && previous.Note.Equals(note, StringComparison.Ordinal);

        var thumbnailFile = previous?.ThumbnailFile;
        if (thumbnailJpeg is { Length: > 0 })
        {
            thumbnailFile = StoreThumbnail(contentKey, thumbnailJpeg) ?? thumbnailFile;
        }

        _data.Notes[contentKey] = new NoteEntry
        {
            Note = note,
            FileName = fileName,
            LastKnownPath = lastKnownPath ?? previous?.LastKnownPath,
            ThumbnailFile = thumbnailFile,
            Distilled = wasDistilled,
            AddedUtc = previous?.AddedUtc ?? DateTime.UtcNow,
        };
    }

    /// <summary>Legt nachträglich eine eigene Miniatur an, wenn noch keine
    /// vorhanden ist. Erzeugen lässt sie sich nur, solange die Datei greifbar
    /// ist — deshalb wird bei jedem Lauf nachgeholt, was beim Anlegen der
    /// Notiz nicht geklappt hat. Ohne diese Kopie zeigt die Verwaltung nichts
    /// mehr, sobald das Original verschoben oder gelöscht wurde.</summary>
    public bool TryStoreThumbnail(string contentKey, byte[] jpeg)
    {
        if (jpeg.Length == 0
            || !_data.Notes.TryGetValue(contentKey, out var entry)
            || HasThumbnail(contentKey))
        {
            return false;
        }

        entry.ThumbnailFile = StoreThumbnail(contentKey, jpeg) ?? entry.ThumbnailFile;
        return entry.ThumbnailFile is not null;
    }

    /// <summary>Entfernt alle Notizen samt ihrer Miniaturen. Die daraus
    /// destillierten Fakten bleiben bestehen — sie sind eigenständiges Wissen
    /// und hängen nicht mehr an der einzelnen Aufnahme.</summary>
    public int RemoveAllNotes()
    {
        var count = _data.Notes.Count;
        foreach (var entry in _data.Notes.Values)
        {
            if (entry.ThumbnailFile is { } file)
            {
                TryDeleteThumbnail(file);
            }
        }

        _data.Notes.Clear();
        return count;
    }

    /// <summary>Hat die Notiz bereits eine eigene Miniatur?</summary>
    public bool HasThumbnail(string contentKey) =>
        _data.Notes.TryGetValue(contentKey, out var entry)
        && ThumbnailPathFor(contentKey, entry.ThumbnailFile) is not null;

    private static string? StoreThumbnail(string contentKey, byte[] jpeg)
    {
        try
        {
            Directory.CreateDirectory(NotesDirectory);
            var file = contentKey + ".jpg";
            File.WriteAllBytes(Path.Combine(NotesDirectory, file), jpeg);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Die Vorschau ist Komfort — ohne sie bleibt die Notiz gültig.
            return null;
        }
    }

    private static void TryDeleteThumbnail(string file)
    {
        try
        {
            var path = Path.Combine(NotesDirectory, file);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Reste sind unkritisch.
        }
    }

    /// <summary>Aktualisiert den zuletzt bekannten Pfad einer Notiz.</summary>
    public void UpdateNotePath(string contentKey, string path)
    {
        if (_data.Notes.TryGetValue(contentKey, out var entry))
        {
            entry.LastKnownPath = path;
        }
    }

    /// <summary>Noch nicht in Fakten destillierte Notizen.</summary>
    public IReadOnlyList<(string Key, string Note, string FileName)> UndistilledNotes =>
        _data.Notes
            .Where(kv => !kv.Value.Distilled)
            .Select(kv => (kv.Key, kv.Value.Note, kv.Value.FileName))
            .ToList();

    /// <summary>Gibt alle Notizen zur erneuten Destillation frei.
    /// <para>Nötig, wenn sich der Destillations-Prompt geändert hat: Eine
    /// einmal ausgewertete Notiz wird sonst nie wieder angefasst, und
    /// Verbesserungen an der Auswertung erreichen den vorhandenen Bestand
    /// nicht. Die bisherigen Fakten bleiben — der nächste Lauf ergänzt, statt
    /// zu ersetzen; überflüssige lassen sich einzeln entfernen.</para></summary>
    public int MarkAllUndistilled()
    {
        var count = 0;
        foreach (var entry in _data.Notes.Values.Where(e => e.Distilled))
        {
            entry.Distilled = false;
            count++;
        }

        return count;
    }

    public void MarkDistilled(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (_data.Notes.TryGetValue(key, out var entry))
            {
                entry.Distilled = true;
            }
        }
    }

    /// <summary>Inhalts-Hash einer Datei (Länge + erste/letzte 64 KB) — ohne
    /// Modell-Anteil, damit Notizen modellunabhängig zugeordnet bleiben.
    /// Null, wenn die Datei nicht lesbar ist.</summary>
    public static string? ComputeContentKey(string filePath)
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

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch
        {
            return null;
        }
    }

    private sealed class StoreData
    {
        public List<string> Facts { get; set; } = [];

        public Dictionary<string, NoteEntry> Notes { get; set; } = [];
    }

    private sealed class NoteEntry
    {
        public string Note { get; set; } = "";

        public string FileName { get; set; } = "";

        /// <summary>Nur für die Vorschau — maßgeblich bleibt der Inhalts-Hash.</summary>
        public string? LastKnownPath { get; set; }

        /// <summary>Dateiname der eigenen Miniatur unter
        /// <see cref="NotesDirectory"/>; unabhängig vom Original.</summary>
        public string? ThumbnailFile { get; set; }

        public bool Distilled { get; set; }

        public DateTime AddedUtc { get; set; }
    }
}
