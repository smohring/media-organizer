using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Localization;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core.Faces;
using MediaOrganizer.Core.Geo;
using MediaOrganizer.Core.Knowledge;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.App.ViewModels;

/// <summary>Ein Referenzgesicht einer Person in der Verwaltungsansicht.</summary>
public sealed class FaceThumbViewModel(KnownPerson person, int index, ImageSource? image)
{
    public KnownPerson Person { get; } = person;

    public int Index { get; } = index;

    public ImageSource? Image { get; } = image;
}

/// <summary>Eine bekannte Person mit Referenzgesichtern.</summary>
public sealed partial class PersonViewModel : ObservableObject
{
    public PersonViewModel(KnownPerson person)
    {
        Person = person;
        Faces = [];
        Reload();
    }

    public KnownPerson Person { get; }

    public ObservableCollection<FaceThumbViewModel> Faces { get; }

    public string Name => Person.Name;

    public string Subtitle => string.IsNullOrWhiteSpace(Person.Relation)
        ? Loc.Format("S.Know.ReferenceFaces", Person.Embeddings.Count)
        : Loc.Format("S.Know.ReferenceFacesWithRelation", Person.Relation, Person.Embeddings.Count);

    public void Reload()
    {
        Faces.Clear();
        for (var i = 0; i < Person.ThumbnailFiles.Count; i++)
        {
            var path = Path.Combine(PersonStore.FaceDirectory, Person.ThumbnailFiles[i]);
            Faces.Add(new FaceThumbViewModel(Person, i, ImageLoading.LoadScaled(path, 96)));
        }

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Subtitle));
    }
}

/// <summary>Eine Bild-Notiz mit Vorschau.</summary>
public sealed partial class NoteViewModel : ObservableObject
{
    [ObservableProperty]
    private ImageSource? _preview;

    public required string Key { get; init; }

    public required string FileName { get; init; }

    public required string Note { get; init; }
}

/// <summary>Eine benannte Zone in der Verwaltung.</summary>
public sealed record HomeZoneViewModel(HomeZone Zone, string Subtitle)
{
    public string Name => Zone.Name;

    public string KindLabel => Loc.Get(Zone.Kind switch
    {
        ZoneKind.Home => "S.Know.ZoneHome",
        ZoneKind.Vacation => "S.Know.ZoneVacation",
        _ => "S.Know.ZoneOther",
    });

    /// <summary>Line under the name: kind, place and radius.</summary>
    public string Details =>
        Loc.Format("S.Know.ZoneSubtitle", KindLabel, Subtitle, Zone.RadiusKm.ToString("0.##"));
}

/// <summary>Verwaltung der Wissensbasis: Personen (mit Gesichtern), Fakten,
/// Heimatzonen und Bild-Notizen.</summary>
public sealed partial class KnowledgeViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private KnowledgeStore _knowledge = KnowledgeStore.Load();
    private PersonStore _persons = PersonStore.Load();

    /// <summary>Die Einstellungen werden geteilt statt neu geladen: Sonst
    /// überschriebe das Hauptfenster beim nächsten Speichern eine hier
    /// entfernte Heimatzone wieder.</summary>
    /// <param name="knownItems">Liefert die derzeit eingelesenen Aufnahmen —
    /// Grundlage der Ortsvorschläge beim Anlegen einer Zone. Null, solange
    /// noch kein Quellordner gelesen wurde.</param>
    public KnowledgeViewModel(
        AppSettings settings,
        Action saveSettings,
        Func<IReadOnlyList<MediaItem>>? knownItems = null)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _knownItems = knownItems;
        Reload();
    }

    private readonly Func<IReadOnlyList<MediaItem>>? _knownItems;

    /// <summary>The most frequented places, as suggestions for a new zone.
    ///
    /// <para>The search runs off the interface thread: it goes over the places
    /// of every shot read so far, and on a large collection that is long
    /// enough to freeze the window. The snapshot of the items is taken here,
    /// on the caller's thread, because the underlying collections belong to
    /// the interface.</para></summary>
    public Task<IReadOnlyList<PlaceSuggestion>> PlaceSuggestionsForZonesAsync(CancellationToken ct)
    {
        var items = _knownItems?.Invoke() ?? [];
        return items.Count == 0
            ? Task.FromResult<IReadOnlyList<PlaceSuggestion>>([])
            : Task.Run(
                () => PlaceSuggestions.Find(items, radiusKm: 1.5, maxGap: TimeSpan.FromHours(48)),
                ct);
    }

    /// <summary>Legt eine Zone an oder ersetzt eine vorhandene.</summary>
    public void SaveZone(HomeZone? replaced, HomeZone zone)
    {
        if (replaced is not null)
        {
            var index = _settings.HomeZones.IndexOf(replaced);
            if (index >= 0)
            {
                _settings.HomeZones[index] = zone;
            }
            else
            {
                _settings.HomeZones.Add(zone);
            }
        }
        else
        {
            _settings.HomeZones.Add(zone);
        }

        _saveSettings();
        Reload();
        StatusMessage = Loc.Format("S.Know.ZoneSaved", zone.Name);
    }

    public ObservableCollection<PersonViewModel> People { get; } = [];

    public ObservableCollection<string> Facts { get; } = [];

    public ObservableCollection<HomeZoneViewModel> HomeZones { get; } = [];

    public ObservableCollection<NoteViewModel> Notes { get; } = [];

    [ObservableProperty]
    private string? _statusMessage;

    public bool HasPeople => People.Count > 0;

    public bool HasFacts => Facts.Count > 0;

    public bool HasHomeZones => HomeZones.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    /// <summary>Kurzfassung für die Einstellungsleiste.</summary>
    public string SummaryText => Loc.Format(
        "S.Know.Summary", People.Count, Facts.Count, HomeZones.Count, Notes.Count);

    public void Reload()
    {
        _knowledge = KnowledgeStore.Load();
        _persons = PersonStore.Load();

        People.Clear();
        foreach (var person in _persons.Persons.OrderBy(p => p.Name))
        {
            People.Add(new PersonViewModel(person));
        }

        Facts.Clear();
        foreach (var fact in _knowledge.Facts)
        {
            Facts.Add(fact);
        }

        HomeZones.Clear();
        foreach (var zone in _settings.HomeZones)
        {
            var subtitle = $"{zone.Latitude:F4}, {zone.Longitude:F4}";
            try
            {
                if (OfflineGeocoder.Instance.FindNearest(zone.Center) is { } place)
                {
                    subtitle = Loc.Format("S.Know.NearPlace", place.DisplayName);
                }
            }
            catch
            {
                // Ortsauflösung ist Komfort.
            }

            HomeZones.Add(new HomeZoneViewModel(zone, subtitle));
        }

        Notes.Clear();
        foreach (var info in _knowledge.AllNotes)
        {
            var vm = new NoteViewModel
            {
                Key = info.Key,
                FileName = info.FileName,
                Note = info.Note,
            };

            // Eine Notiz zeigt IMMER das Bild aus den Nutzerdaten. Der
            // Quellordner darf dafür keine Rolle spielen: Er wird beim
            // Sortieren geleert, umbenannt, gewechselt — die Notiz überlebt
            // das, weil sie am Dateiinhalt hängt.
            var thumbnailPath = info.ThumbnailPath ?? Backfill(info);
            if (thumbnailPath is not null)
            {
                vm.Preview = ImageLoading.LoadScaled(thumbnailPath, 160);
            }

            Notes.Add(vm);
        }

        OnPropertyChanged(nameof(HasPeople));
        OnPropertyChanged(nameof(HasFacts));
        OnPropertyChanged(nameof(HasHomeZones));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>Holt die eigene Miniatur nach, solange das Original noch
    /// greifbar ist, und liefert ihren Pfad in den Nutzerdaten.
    ///
    /// <para>Nur für Altbestand: Notizen aus einer Zeit, in der noch keine
    /// eigene Kopie angelegt wurde. Danach ist die Notiz endgültig vom
    /// Originalort gelöst — und wenn das Original schon weg ist, bleibt eben
    /// der Platzhalter. Ein Bild aus dem Quellordner wird bewusst NICHT mehr
    /// direkt angezeigt: Sonst hinge die Anzeige wieder daran, welcher Ordner
    /// gerade geöffnet ist.</para></summary>
    private string? Backfill(KnowledgeStore.NoteInfo info)
    {
        if (info.LastKnownPath is not { } original || !File.Exists(original))
        {
            return null;
        }

        var source = ImageLoading.LoadScaled(original, 320);
        if (source is null || !_knowledge.TryStoreThumbnail(info.Key, ImageLoading.EncodeJpeg(source)))
        {
            return null;
        }

        _knowledge.Save();
        return _knowledge.AllNotes.FirstOrDefault(n => n.Key == info.Key)?.ThumbnailPath;
    }

    // --- Heimatzonen ---

    [RelayCommand]
    private void RemoveHomeZone(HomeZoneViewModel zone)
    {
        _settings.HomeZones.Remove(zone.Zone);
        _saveSettings();
        Reload();
        StatusMessage = Loc.Format("S.Know.ZoneRemoved", zone.Name);
    }

    // --- Personen ---

    /// <summary>Zugriff für die Bearbeiten-Dialoge (Umbenennen, Zusammenführen).</summary>
    public PersonStore Store => _persons;

    public void AddOrUpdatePerson(string name, string relation)
    {
        _persons.GetOrCreate(name, relation);
        _persons.Save();
        Reload();
        StatusMessage = Loc.Format("S.Know.PersonSaved", name);
    }

    /// <summary>Nach einer Bearbeitung durch einen Dialog neu einlesen.</summary>
    public void AfterPersonEdit(string message)
    {
        Reload();
        StatusMessage = message;
    }

    /// <summary>Verschiebt ein Referenzgesicht zu einer anderen Person —
    /// für Fehlzuordnungen aus der automatischen Gruppierung.</summary>
    public void MoveFace(FaceThumbViewModel face, KnownPerson target)
    {
        _persons.MoveReferenceFace(face.Person, face.Index, target);
        _persons.Save();
        Reload();
        StatusMessage = Loc.Format("S.Know.FaceMoved", target.Name);
    }

    public IReadOnlyList<KnownPerson> OtherPersons(KnownPerson person) =>
        _persons.Persons.Where(p => p != person).OrderBy(p => p.Name).ToList();

    [RelayCommand]
    private void RemovePerson(PersonViewModel person)
    {
        _persons.Remove(person.Person);
        _persons.Save();
        Reload();
        StatusMessage = Loc.Format("S.Msg.PersonRemoved", person.Name);
    }

    [RelayCommand]
    private void RemoveFace(FaceThumbViewModel face)
    {
        _persons.RemoveReferenceFace(face.Person, face.Index);
        _persons.Save();
        Reload();
        StatusMessage = Loc.Get("S.Know.FaceRemoved");
    }

    // --- Fakten ---

    public void AddFact(string fact)
    {
        if (_knowledge.AddFact(fact))
        {
            _knowledge.Save();
            Reload();
            StatusMessage = Loc.Get("S.Know.FactAdded");
        }
    }

    /// <summary>Ersetzt einen Fakt an Ort und Stelle.</summary>
    public void ReplaceFact(string oldFact, string newFact)
    {
        newFact = newFact.Trim();
        if (newFact.Length == 0)
        {
            _knowledge.RemoveFact(oldFact);
            _knowledge.Save();
            Reload();
            StatusMessage = Loc.Get("S.Know.FactRemoved");
            return;
        }

        if (newFact.Equals(oldFact, StringComparison.Ordinal))
        {
            return;
        }

        _knowledge.ReplaceFact(oldFact, newFact);
        _knowledge.Save();
        Reload();
        StatusMessage = Loc.Get("S.Know.FactUpdated");
    }

    [RelayCommand]
    private void RemoveFact(string fact)
    {
        _knowledge.RemoveFact(fact);
        _knowledge.Save();
        Reload();
        StatusMessage = Loc.Get("S.Know.FactRemoved");
    }

    // --- Notes ---

    public void UpdateNote(NoteViewModel note, string text)
    {
        _knowledge.SetNote(note.Key, text, note.FileName);
        _knowledge.Save();
        Reload();
        StatusMessage = Loc.Get(
            string.IsNullOrWhiteSpace(text) ? "S.Know.NoteRemoved" : "S.Know.NoteUpdated");
    }

    [RelayCommand]
    private void RemoveNote(NoteViewModel note)
    {
        _knowledge.SetNote(note.Key, "", note.FileName);
        _knowledge.Save();
        Reload();
        StatusMessage = Loc.Get("S.Know.NoteRemoved");
    }

    /// <summary>Gibt alle Notizen zur erneuten Auswertung frei. Der nächste
    /// Lauf gewinnt daraus wieder Fakten — sinnvoll, wenn die Auswertung
    /// verbessert wurde und der vorhandene Bestand davon profitieren soll.</summary>
    [RelayCommand]
    private void RedistillNotes()
    {
        var released = _knowledge.MarkAllUndistilled();
        _knowledge.Save();
        Reload();
        StatusMessage = released == 0
            ? Loc.Get("S.Know.NoDistilled")
            : Loc.Format("S.Know.Released", released);
    }

    /// <summary>Entfernt alle Bild-Notizen. Die daraus destillierten Fakten
    /// bleiben — sie sind eigenständiges Wissen und hängen nicht mehr an der
    /// einzelnen Aufnahme.</summary>
    [RelayCommand]
    private void RemoveAllNotes()
    {
        var removed = _knowledge.RemoveAllNotes();
        _knowledge.Save();
        Reload();
        StatusMessage = removed == 0
            ? Loc.Get("S.Know.NoNotes")
            : Loc.Format("S.Know.NotesRemoved", removed);
    }

    // "Verwaiste entfernen" gab es hier einmal: Es warf Notizen weg, deren
    // Originaldatei nicht mehr am zuletzt bekannten Ort lag. Das ist entfallen,
    // weil es den Begriff nicht mehr gibt — eine Notiz hängt am Dateiinhalt und
    // trägt ihr Bild in den Nutzerdaten. Nach jedem Sortierlauf lag der Pfad
    // ohnehin kurzzeitig daneben; der Knopf hätte dann gültige Notizen
    // gelöscht.
}
