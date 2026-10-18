using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Localization;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.Video;

namespace MediaOrganizer.App.ViewModels;

/// <summary>Eine Datei — je nach Ansicht ein Eintrag im Ist-Zustand, im Plan
/// (noch nichts geschrieben) oder im Ergebnis eines ausgeführten Plans.</summary>
public sealed partial class ResultItemViewModel(MediaItem item) : ObservableObject
{
    /// <summary>Auflösung, in der Vorschaubilder dekodiert werden. Zugleich
    /// die Obergrenze des Zoom-Reglers: Größere Kacheln würden hochskaliert
    /// und dadurch unscharf.</summary>
    public const int ThumbnailDecodeSize = 260;

    public MediaItem Item { get; } = item;

    /// <summary>Aktueller Pfad im Zielordner (bewegt sich bei Korrekturen mit).
    /// Im Plan noch null — dort zählt allein <see cref="MediaItem.SourcePath"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(CanRelocate), nameof(ToolTipText))]
    private string? _currentPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailed), nameof(IsSkipped), nameof(CanRelocate), nameof(ToolTipText))]
    private FileOpStatus _status;

    /// <summary>Die Kachel gehört zu einem Plan oder zum Ist-Zustand — es hat
    /// noch keine Dateioperation stattgefunden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRelocate), nameof(ToolTipText))]
    private bool _isPlanned;

    /// <summary>Ist-Zustand: nur anschauen, nicht umsortieren.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRelocate))]
    private bool _isReadOnly;

    /// <summary>Was mit der Datei beim Übernehmen geschähe.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConflict), nameof(IsPlannedDuplicate), nameof(IsDimmed), nameof(ToolTipText))]
    private PlannedStatus _planStatus;

    /// <summary>Begründung des Plan-Status (Kollision, Duplikat).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _planReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _error;

    /// <summary>Nutzer-Notiz am Bild (Wissensbasis, per Datei-Hash).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(ToolTipText))]
    private string? _userNote = item.UserNote;

    public bool HasNote => !string.IsNullOrWhiteSpace(UserNote);

    /// <summary>Die erzeugte Bildbeschreibung. Sie lebt am <see cref="MediaItem"/>,
    /// weil Plan, EXIF-Schreiber und Analyse-Cache von dort lesen — hier nur
    /// durchgereicht, damit die Oberfläche Änderungen mitbekommt.</summary>
    public string? Description
    {
        get => Item.Description;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (Item.Description == trimmed)
            {
                return;
            }

            Item.Description = trimmed;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDescription));
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Item.Description);

    /// <summary>Markierung für Mehrfachauswahl (Drag &amp; Drop, Kontextmenü).</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private ImageSource? _thumbnail;

    /// <summary>Der Ordner, in dem das Item aktuell angezeigt wird.</summary>
    public ResultFolderViewModel? ParentFolder { get; set; }

    public bool IsVideo => Item.Kind == MediaKind.Video;

    public bool IsFailed => !IsPlanned && Status == FileOpStatus.Failed;

    public bool IsSkipped => !IsPlanned && Status == FileOpStatus.SkippedDuplicate;

    public bool IsConflict => IsPlanned && PlanStatus == PlannedStatus.NameConflict;

    public bool IsPlannedDuplicate => IsPlanned && PlanStatus == PlannedStatus.PossibleDuplicate;

    /// <summary>Blass darstellen: übersprungen oder als Duplikat vorgemerkt.</summary>
    public bool IsDimmed => IsSkipped || IsPlannedDuplicate;

    /// <summary>In the plan every file can be resorted (it costs nothing); in
    /// the result only what actually lies in the target. A card that has been
    /// applied while the rest of the plan is still open is out either way —
    /// see <see cref="ResultFolderViewModel.IsFrozen"/>.</summary>
    public bool CanRelocate => !IsReadOnly
        && ParentFolder?.IsFrozen != true
        && (IsPlanned || (!IsFailed && CurrentPath is not null && File.Exists(CurrentPath)));

    public string FileName => CurrentPath is null ? Item.FileName : Path.GetFileName(CurrentPath);

    /// <summary>The file as it actually lies on disk right now — the source
    /// while the plan is open, the target once it has been applied. Anything
    /// that touches the file itself goes through here;
    /// <see cref="CurrentPath"/> alone is null in every view before applying.
    /// (The rest of this file is still German.)</summary>
    public string PathOnDisk => CurrentPath ?? Item.SourcePath;

    public string ToolTipText
    {
        get
        {
            var lines = new List<string>
            {
                FileName,
                Loc.Format("S.Tile.Taken", Item.TakenLocal.ToString("g")),
            };
            if (IsPlanned)
            {
                lines.Add(Loc.Get(PlanStatus switch
                {
                    PlannedStatus.Copy => "S.Tile.WillCopy",
                    PlannedStatus.NameConflict => "S.Tile.NameConflict",
                    PlannedStatus.PossibleDuplicate => "S.Tile.PlannedDuplicate",
                    _ => "S.Tile.WillMove",
                }));

                if (PlanReason is not null)
                {
                    lines.Add(PlanReason);
                }
            }
            else if (Status == FileOpStatus.SkippedDuplicate)
            {
                lines.Add(Loc.Get("S.Tile.Skipped"));
            }

            if (Error is not null)
            {
                lines.Add(Loc.Format("S.Tile.Error", Error));
            }

            if (!string.IsNullOrWhiteSpace(Item.Description))
            {
                lines.Add(Item.Description);
            }

            if (HasNote)
            {
                lines.Add(Loc.Format("S.Tile.Note", UserNote));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>Zahl der gerade im Speicher gehaltenen Vorschaubilder.
    /// Jedes belegt rund 260 KB — bei zehntausend Kacheln wären das mehrere
    /// Gigabyte, und genau so viele wurden vorher beim Öffnen einer großen
    /// Sammlung auf einen Schlag erzeugt.</summary>
    private static int _loadedThumbnails;

    public static int LoadedThumbnails => Volatile.Read(ref _loadedThumbnails);

    public bool HasThumbnail => Thumbnail is not null;

    public async Task LoadThumbnailAsync(FfmpegService ffmpeg, SemaphoreSlim throttle, CancellationToken ct)
    {
        if (Thumbnail is not null)
        {
            return;
        }

        var path = CurrentPath ?? Item.SourcePath;
        if (!File.Exists(path))
        {
            return;
        }

        await throttle.WaitAsync(ct);
        try
        {
            ImageSource? source;
            if (IsVideo)
            {
                var bytes = await ffmpeg.ExtractThumbnailAsync(path, ThumbnailDecodeSize, ct);
                source = bytes is null ? null : ImageLoading.FromBytes(bytes);

                // Ohne ffmpeg (oder bei Extraktionsfehlern): Windows-Vorschaubild.
                source ??= ShellThumbnail.Get(path, ThumbnailDecodeSize);
            }
            else
            {
                // Zuerst der Windows-Vorschau-Cache: Er liefert ein fertiges
                // Bild in wenigen Millisekunden, während das eigene Dekodieren
                // die komplette Datei liest. Erst wenn Windows nichts hat
                // (frisch kopiert, exotisches Format), wird selbst dekodiert.
                source = await Task.Run(() => ShellThumbnail.Get(path, ThumbnailDecodeSize), ct)
                         ?? await Task.Run(() => ImageLoading.LoadScaled(path, ThumbnailDecodeSize), ct);
            }

            if (source is not null)
            {
                Thumbnail = source;
                Interlocked.Increment(ref _loadedThumbnails);
            }
        }
        catch (OperationCanceledException)
        {
            // App/Ansicht geschlossen.
        }
        catch
        {
            // Kein Thumbnail möglich — Platzhalter-Glyph bleibt sichtbar.
        }
        finally
        {
            throttle.Release();
        }
    }

    /// <summary>Gibt das Vorschaubild wieder frei (eingeklappte Karte). Der
    /// Platzhalter-Glyph erscheint erneut; beim Aufklappen wird es aus dem
    /// Windows-Cache in Millisekunden wiederhergestellt.</summary>
    public void ReleaseThumbnail()
    {
        if (Thumbnail is null)
        {
            return;
        }

        Thumbnail = null;
        Interlocked.Decrement(ref _loadedThumbnails);
    }
}

/// <summary>Ein Ordner — vorhandener Ordner im Ist-Zustand, geplanter
/// Zielordner oder befüllter Zielordner nach der Ausführung.</summary>
public sealed partial class ResultFolderViewModel(string folderPath, string theme, DateTime groupStart) : ObservableObject
{
    /// <summary>Aktueller Pfad — ändert sich beim Umbenennen/Umsortieren.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(ThemePart), nameof(RenamePrefix))]
    private string _folderPath = folderPath;

    [ObservableProperty]
    private string _theme = theme;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private DateTime _groupStart = groupStart;

    /// <summary>Datums-Präfix "yyyyMMdd" des geplanten Ordnernamens.</summary>
    public string DatePrefix { get; set; } = "";

    /// <summary>Geplanter Zielordner (Soll-Zustand) statt Ergebnis/Ist.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText), nameof(CanEdit), nameof(StateBadge),
                              nameof(ShowStateBadge), nameof(CanSelect))]
    private bool _isPlanned;

    /// <summary>Ist-Zustand: reine Anzeige, kein Umsortieren.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText), nameof(CanEdit), nameof(ShowStateBadge),
                              nameof(CanSelect))]
    private bool _isReadOnly;

    /// <summary>Applied while the rest of the plan is still open. The files
    /// lie in the target, so nothing here may be dragged out any more: the
    /// plan would only rehang the card and then try to move the file a second
    /// time, from a source path that a move has already emptied. It thaws when
    /// the plan is finished — from then on the result view moves files for
    /// real. Deliberately not <see cref="IsReadOnly"/>, which would also swap
    /// the card's summary for the current-state one and hide how many files
    /// failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isFrozen;

    /// <summary>Der Ordner existiert im Ziel noch nicht.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBadge))]
    private bool _isNew = true;

    /// <summary>Sammelordner (ohne Datums-Präfix, direkt im Basisordner).</summary>
    public bool IsCollection { get; set; }

    /// <summary>Folder without a date prefix in its name: the collecting folder
    /// and the everyday folder of the EXIF-only mode. It must not grow one when
    /// shots are moved into it — it holds a stretch of time, not a day.
    ///
    /// <para><see cref="DatePrefix"/> can still be set on it, and then decides
    /// the hierarchy level alone: that is a folder split per year or month. It
    /// stays fixed for the same reason, so that dragging a 2019 shot into
    /// <c>2026\Vermischt</c> does not move the folder to another
    /// year.</para></summary>
    public bool IsDateless { get; set; }

    /// <summary>Name des Ordners, in dem dieser liegt — gesetzt bei den
    /// Themenordnern des Sammelordners ("Vermischt\Screenshot"). Muss beim
    /// Neuberechnen des Pfades mitgeführt werden, sonst rutscht so ein Ordner
    /// beim ersten Umsortieren eine Ebene nach oben.</summary>
    public string InsideFolder { get; set; } = "";

    /// <summary>Vom Nutzer umbenannt — das Datums-Präfix wird dann nicht mehr
    /// automatisch nachgezogen (sonst würde die Handarbeit überschrieben).</summary>
    public bool HasManualName { get; set; }

    /// <summary>Inhalt der Karte ein-/ausgeklappt.</summary>
    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>Für das Teil-Übernehmen angehakt. Wirkt nur, solange die Karte
    /// ein Plan ist — ein bereits übernommener Ordner liegt auf der Platte und
    /// kann nicht noch einmal übernommen werden.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Ob das Auswahlkästchen überhaupt erscheint.</summary>
    public bool CanSelect => IsPlanned && !IsReadOnly;

    /// <summary>Meldet dem Fenster, dass sich die Auswahl geändert hat. Die
    /// Karte kennt das Hauptfenster nicht, also ruft dieses hier zurück.</summary>
    public Action? SelectionChanged { get; set; }

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();

    /// <summary>Lädt die Vorschaubilder dieser Karte. Wird von der Ansicht
    /// gesetzt, weil die Karte selbst weder ffmpeg noch die Drosselung kennt.
    ///
    /// <para>Vorher wurden die Bilder ALLER Dateien sofort beim Aufbau der
    /// Ansicht angestoßen — bei zehntausend Aufnahmen zehntausend
    /// Dekodiervorgänge und mehrere Gigabyte Speicher, für Kacheln, die
    /// niemand ansieht.</para></summary>
    public Func<ResultFolderViewModel, Task>? ThumbnailLoader { get; set; }

    /// <summary>Ab so vielen gehaltenen Vorschaubildern werden die einer
    /// eingeklappten Karte wieder freigegeben. Darunter bleiben sie liegen:
    /// Bei kleinen Sammlungen soll das Auf- und Zuklappen nichts kosten.</summary>
    private const int ThumbnailBudget = 1500;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = ThumbnailLoader?.Invoke(this);
            return;
        }

        if (ResultItemViewModel.LoadedThumbnails <= ThumbnailBudget)
        {
            return;
        }

        foreach (var item in Items)
        {
            item.ReleaseThumbnail();
        }
    }

    /// <summary>Der Name wird gerade direkt in der Karte bearbeitet.</summary>
    [ObservableProperty]
    private bool _isRenaming;

    /// <summary>Bearbeitungsfassung des Themen-Anteils.</summary>
    [ObservableProperty]
    private string _editTheme = "";

    /// <summary>Beim Umbenennen stehenbleibendes Datums-Präfix ("20260501_").</summary>
    public string RenamePrefix
    {
        get
        {
            var name = DisplayName;
            var underscore = name.IndexOf('_');
            return IsDateless || underscore <= 0 ? "" : name[..(underscore + 1)];
        }
    }

    public string DisplayName => Path.GetFileName(FolderPath);

    /// <summary>Karte darf umsortiert, umbenannt und gelöscht werden.</summary>
    public bool CanEdit => !IsReadOnly && !IsFrozen;

    public bool ShowStateBadge => IsPlanned && !IsReadOnly;

    public string StateBadge => Loc.Get(IsNew ? "S.Folder.New" : "S.Folder.Exists");

    /// <summary>Thema-Anteil des Ordnernamens (nach dem Datums-Präfix).</summary>
    public string ThemePart
    {
        get
        {
            var name = DisplayName;
            var underscore = name.IndexOf('_');
            return underscore >= 0 && underscore < name.Length - 1 ? name[(underscore + 1)..] : name;
        }
    }

    public ObservableCollection<ResultItemViewModel> Items { get; } = [];

    /// <summary>How many tiles a card shows before it stops, and how many each
    /// press of "show more" adds.
    ///
    /// <para>Nothing virtualises here: every tile of an expanded card is a real
    /// visual element with about ten of its own inside it. A card holding 3600
    /// shots therefore built tens of thousands of elements at once and asked for
    /// as many preview images — which is what made the overview
    /// sluggish.</para></summary>
    public const int TileChunk = 300;

    /// <summary>The prefix of <see cref="Items"/> the card actually shows. Only
    /// these tiles exist, and only they get a preview image; everything else
    /// waits behind "show more".</summary>
    public ObservableCollection<ResultItemViewModel> VisibleItems { get; } = [];

    private int _tileLimit = TileChunk;

    public bool HasHiddenItems => Items.Count > VisibleItems.Count;

    public string HiddenItemsText =>
        Loc.Format("S.Folder.ShowMoreTiles", Items.Count - VisibleItems.Count);

    /// <summary>Shows the next block of tiles and loads their previews.</summary>
    [RelayCommand]
    private void ShowMoreTiles()
    {
        _tileLimit += TileChunk;
        var target = Math.Min(_tileLimit, Items.Count);
        for (var i = VisibleItems.Count; i < target; i++)
        {
            VisibleItems.Add(Items[i]);
        }

        NotifyTileWindow();
        _ = ThumbnailLoader?.Invoke(this);
    }

    private void NotifyTileWindow()
    {
        OnPropertyChanged(nameof(HasHiddenItems));
        OnPropertyChanged(nameof(HiddenItemsText));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText), nameof(IsEmpty))]
    private int _itemCount;

    public bool IsEmpty => ItemCount == 0;

    public string SummaryText
    {
        get
        {
            // Singular and plural as two separate keys rather than a suffix
            // glued on: German appends "en", English appends "s", and other
            // languages do neither.
            var text = Loc.Format(
                ItemCount == 1 ? "S.Folder.FileCountOne" : "S.Folder.FileCountMany", ItemCount);
            if (IsReadOnly)
            {
                return ItemCount == 0
                    ? text
                    : Loc.Format(
                        "S.Folder.DateRange",
                        text,
                        GroupStart.ToString("d"),
                        Items.Max(i => i.Item.TakenLocal).ToString("d"));
            }

            if (IsPlanned)
            {
                var conflicts = Items.Count(i => i.IsConflict);
                var duplicates = Items.Count(i => i.IsPlannedDuplicate);
                if (conflicts > 0)
                {
                    text += Loc.Format("S.Folder.Conflicts", conflicts);
                }

                if (duplicates > 0)
                {
                    text += Loc.Format("S.Folder.Duplicates", duplicates);
                }

                return text;
            }

            var failed = Items.Count(i => i.IsFailed);
            var skipped = Items.Count(i => i.Status == FileOpStatus.SkippedDuplicate);
            if (skipped > 0)
            {
                text += Loc.Format("S.Folder.SkippedDuplicates", skipped);
            }

            if (failed > 0)
            {
                text += Loc.Format("S.Folder.Errors", failed);
            }

            return text;
        }
    }

    /// <summary><see cref="VisibleItems"/> stays the front part of
    /// <see cref="Items"/>: appending only shows while there is room, and
    /// removing a shown tile pulls the next hidden one up in its place.</summary>
    public void Add(ResultItemViewModel item)
    {
        item.ParentFolder = this;
        Items.Add(item);
        if (VisibleItems.Count < _tileLimit)
        {
            VisibleItems.Add(item);
        }

        ItemCount = Items.Count;
        NotifyTileWindow();
    }

    public void Remove(ResultItemViewModel item)
    {
        Items.Remove(item);
        if (VisibleItems.Remove(item) && VisibleItems.Count < Items.Count)
        {
            VisibleItems.Add(Items[VisibleItems.Count]);
        }

        ItemCount = Items.Count;
        NotifyTileWindow();
    }

    public void RefreshSummary() => OnPropertyChanged(nameof(SummaryText));

    /// <summary>Zieht nach dem Umsortieren Datum und Zielpfad nach. Das
    /// Datums-Präfix richtet sich nach der frühesten enthaltenen Aufnahme —
    /// außer der Ordner wurde manuell umbenannt.</summary>
    public void RecomputePath(string baseFolder, TargetHierarchy hierarchy)
    {
        if (Items.Count > 0)
        {
            GroupStart = Items.Min(i => i.Item.TakenLocal);
        }

        if (!IsDateless && !HasManualName && Items.Count > 0)
        {
            DatePrefix = PlanPaths.Prefix(GroupStart);
        }

        FolderPath = PlanPaths.BuildTargetPath(
            baseFolder, hierarchy, DatePrefix, Theme, IsDateless, InsideFolder);
        RefreshSummary();
    }
}
