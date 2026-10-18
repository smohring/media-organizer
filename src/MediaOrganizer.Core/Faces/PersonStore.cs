using System.Text.Json;
using System.Text.Json.Serialization;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Faces;

/// <summary>Eine bekannte Person der Wissensbasis.</summary>
public sealed class KnownPerson
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>Beziehung/Rolle, z.B. "Tochter" — fließt als Fakt in die Prompts.</summary>
    public string Relation { get; set; } = "";

    /// <summary>Merkmalsvektoren aus zugeordneten Referenzgesichtern. Mehrere
    /// Vektoren erhöhen die Trefferquote deutlich (verschiedenes Licht,
    /// Alter, Blickwinkel).</summary>
    public List<float[]> Embeddings { get; set; } = [];

    /// <summary>Dateinamen der Gesichts-Vorschaubilder (im Ordner "faces").</summary>
    public List<string> ThumbnailFiles { get; set; } = [];

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Zeile für den Prompt-Kontext.</summary>
    [JsonIgnore]
    public string FactLine => string.IsNullOrWhiteSpace(Relation)
        ? Name
        : $"{Name} ({Relation})";
}

/// <summary>Persistente Personen-Datenbank inkl. Gesichtsvektoren und
/// Vorschaubildern unter %AppData%\MediaOrganizer.</summary>
public sealed class PersonStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly List<KnownPerson> _persons;

    public static string DefaultPath => Path.Combine(AppDataFolder.Root, "persons.json");

    public static string FaceDirectory => Path.Combine(AppDataFolder.Root, "faces");

    private PersonStore(string filePath, List<KnownPerson> persons)
    {
        _filePath = filePath;
        _persons = persons;
    }

    public IReadOnlyList<KnownPerson> Persons => _persons;

    public static PersonStore Load(string? filePath = null)
    {
        var path = filePath ?? DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<List<KnownPerson>>(File.ReadAllText(path), JsonOptions);
                if (data is not null)
                {
                    return new PersonStore(path, data);
                }
            }
        }
        catch
        {
            // Defekte Datei: leer starten.
        }

        return new PersonStore(path, []);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);

            // Angelernte Gesichter sind nicht reproduzierbar (das Ausgangsfoto
            // kann längst woanders liegen) — vor jedem Überschreiben sichern.
            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, _filePath + ".bak", overwrite: true);
            }

            File.WriteAllText(_filePath, JsonSerializer.Serialize(_persons, JsonOptions));
        }
        catch
        {
            // Speichern darf den Lauf nie stoppen.
        }
    }

    public KnownPerson GetOrCreate(string name, string relation = "")
    {
        var existing = _persons.FirstOrDefault(p =>
            p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(relation))
            {
                existing.Relation = relation.Trim();
            }

            return existing;
        }

        var person = new KnownPerson { Name = name.Trim(), Relation = relation.Trim() };
        _persons.Add(person);
        return person;
    }

    /// <summary>Fügt ein Referenzgesicht hinzu (Vektor + Vorschaubild).</summary>
    public void AddReferenceFace(KnownPerson person, DetectedFace face)
    {
        person.Embeddings.Add(face.Embedding);

        if (face.CropJpeg is { Length: > 0 })
        {
            try
            {
                Directory.CreateDirectory(FaceDirectory);
                var fileName = $"{person.Id}_{person.ThumbnailFiles.Count:D2}.jpg";
                File.WriteAllBytes(Path.Combine(FaceDirectory, fileName), face.CropJpeg);
                person.ThumbnailFiles.Add(fileName);
            }
            catch
            {
                // Vorschaubild ist Komfort.
            }
        }
    }

    /// <summary>Benennt eine Person um und/oder ändert ihre Beziehung.
    /// Trägt der neue Name bereits eine andere Person, werden beide
    /// zusammengeführt (statt einen Doppeleintrag zu erzeugen).</summary>
    public void Rename(KnownPerson person, string newName, string newRelation)
    {
        newName = newName.Trim();
        if (newName.Length == 0)
        {
            return;
        }

        var conflict = _persons.FirstOrDefault(p =>
            p != person && p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            Merge(person, conflict);
            conflict.Relation = string.IsNullOrWhiteSpace(newRelation)
                ? conflict.Relation
                : newRelation.Trim();
            return;
        }

        person.Name = newName;
        person.Relation = newRelation.Trim();
    }

    /// <summary>Führt <paramref name="source"/> in <paramref name="target"/>
    /// zusammen: Alle Referenzgesichter wandern mit, die Quelle verschwindet.
    /// Nützlich, wenn die automatische Gruppierung dieselbe Person auf zwei
    /// Gruppen verteilt hat.</summary>
    public void Merge(KnownPerson source, KnownPerson target)
    {
        if (source == target)
        {
            return;
        }

        target.Embeddings.AddRange(source.Embeddings);
        target.ThumbnailFiles.AddRange(source.ThumbnailFiles);

        if (string.IsNullOrWhiteSpace(target.Relation) && !string.IsNullOrWhiteSpace(source.Relation))
        {
            target.Relation = source.Relation;
        }

        // Vorschaubilder werden übernommen, daher hier NICHT löschen.
        _persons.Remove(source);
    }

    /// <summary>Verschiebt ein einzelnes Referenzgesicht zu einer anderen
    /// Person — für Fehlzuordnungen aus der automatischen Gruppierung.</summary>
    public void MoveReferenceFace(KnownPerson source, int index, KnownPerson target)
    {
        if (source == target || index < 0 || index >= source.Embeddings.Count)
        {
            return;
        }

        target.Embeddings.Add(source.Embeddings[index]);
        source.Embeddings.RemoveAt(index);

        if (index < source.ThumbnailFiles.Count)
        {
            target.ThumbnailFiles.Add(source.ThumbnailFiles[index]);
            source.ThumbnailFiles.RemoveAt(index);
        }
    }

    public void Remove(KnownPerson person)
    {
        foreach (var file in person.ThumbnailFiles)
        {
            try
            {
                var path = Path.Combine(FaceDirectory, file);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Aufräumen ist best effort.
            }
        }

        _persons.Remove(person);
    }

    /// <summary>Entfernt ein einzelnes Referenzgesicht.</summary>
    public void RemoveReferenceFace(KnownPerson person, int index)
    {
        if (index < 0 || index >= person.Embeddings.Count)
        {
            return;
        }

        person.Embeddings.RemoveAt(index);
        if (index < person.ThumbnailFiles.Count)
        {
            try
            {
                var path = Path.Combine(FaceDirectory, person.ThumbnailFiles[index]);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Aufräumen ist best effort.
            }

            person.ThumbnailFiles.RemoveAt(index);
        }
    }
}
