using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaOrganizer.App.Localization;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core;
using MediaOrganizer.Core.FileOps;
using MediaOrganizer.Core.Faces;
using MediaOrganizer.Core.Geo;
using MediaOrganizer.Core.Knowledge;
using MediaOrganizer.Core.Llm;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Naming;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.Video;

namespace MediaOrganizer.App.ViewModels;

public enum AppState
{
    /// <summary>Kein Lauf; zeigt den Ist-Zustand des Quellordners.</summary>
    Idle,

    Running,

    /// <summary>Fertiger Plan (Soll-Zustand) — editierbar, noch nichts geschrieben.</summary>
    Plan,

    /// <summary>Plan übernommen; zeigt, was tatsächlich passiert ist.</summary>
    Results,
}

public enum OllamaState
{
    Checking,
    Online,
    Offline,
}

/// <summary>Die drei Hauptansichten des Fensters.</summary>
public enum MainView
{
    Organize,
    Knowledge,
    Settings,
}

/// <summary>One entry of a dropdown. Holds the resource KEY and resolves the
/// text on demand, announcing it again after a language change.
///
/// <para>Deliberately not "rebuild the list on a language change": handing a
/// ComboBox a new ItemsSource clears its SelectedItem and writes that null back
/// through the two-way binding — which would reset the setting the user just
/// had selected. Refreshing the label leaves the selection untouched.</para></summary>
public sealed class OptionItem<T> : INotifyPropertyChanged
{
    public OptionItem(T value, string key)
    {
        Value = value;
        Key = key;

        // The lists live as long as the view model, so there is nothing to
        // unsubscribe from — same arrangement as the view model itself.
        Loc.LanguageChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public T Value { get; }

    public string Key { get; }

    public string Label => Loc.Get(Key);

    /// <summary>What a screen reader announces for the entry: the ComboBox item
    /// carries the object itself, and only DisplayMemberPath knows about
    /// <see cref="Label"/>. Without this the automation name is the type
    /// name.</summary>
    public override string ToString() => Label;
}

/// <summary>Entry of the model selection, including whether it is installed
/// locally. Same arrangement as <see cref="OptionItem{T}"/>: the label is
/// refreshed on a language change rather than the list being rebuilt, which
/// would clear the ComboBox selection.</summary>
public sealed partial class ModelOptionViewModel : ObservableObject
{
    public ModelOptionViewModel(VisionModel model)
    {
        Model = model;
        Loc.LanguageChanged += (_, _) => OnPropertyChanged(nameof(DisplayText));
    }

    public VisionModel Model { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    private bool _isInstalled;

    public string DisplayText =>
        $"{Model.DisplayName}  ·  {Model.RamHint}" +
        (IsInstalled ? $"  ·  {Loc.Get("S.Model.Installed")}" : "");

    /// <summary>What a screen reader announces for the entry — see
    /// <see cref="OptionItem{T}.ToString"/>.</summary>
    public override string ToString() => DisplayText;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore = new();
    private readonly WpfImagePreprocessor _imagePreprocessor = new();
    /// <summary>Wie viele Vorschaubilder gleichzeitig entstehen dürfen. Drei
    /// war eine Vorsichtsmaßnahme aus der Zeit, als jedes Bild vollständig
    /// dekodiert wurde; inzwischen liefert meist der Windows-Vorschau-Cache,
    /// und die Kacheln sollen beim Aufklappen zügig erscheinen.</summary>
    private readonly SemaphoreSlim _thumbnailThrottle =
        new(Math.Clamp(Environment.ProcessorCount / 2, 3, 8));

    private readonly AppSettings _settings;
    private readonly bool _initialized;
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _pullCts;
    private CancellationTokenSource? _thumbnailCts;
    private CancellationTokenSource? _sourceScanCts;
    private CancellationTokenSource? _sourceThumbnailCts;

    /// <summary>Protokoll des zuletzt übernommenen Plans — Grundlage für
    /// "Letzten Lauf rückgängig machen".</summary>
    private RunJournal? _lastJournal;

    /// <summary>Dasselbe Protokoll, solange es zum noch offenen Plan gehört.
    /// Wird ein zweiter Teil desselben Plans übernommen, hängt sein Protokoll
    /// hier an — sonst überschriebe jeder Teil den vorigen, und "Rückgängig"
    /// nähme nur den letzten zurück.</summary>
    private RunJournal? _planJournal;

    public MainViewModel()
    {
        _settings = _settingsStore.Load();

        // Vor allem, was RefreshKnowledgeUi() auslösen kann.
        // Die eingelesenen Aufnahmen dienen als Grundlage der Ortsvorschläge
        // beim Anlegen einer Zone. Bewusst als Rückruf: Der Quellordner wird
        // erst nach dem Erzeugen gelesen, und der Nutzer kann ihn wechseln.
        Knowledge = new KnowledgeViewModel(
            _settings,
            () => _settingsStore.Save(_settings),
            () => [.. SourceFolders.SelectMany(f => f.Items).Select(i => i.Item)]);

        Update = new UpdateViewModel(_settings, () => _settingsStore.Save(_settings));

        SourceFolder = _settings.SourceFolder;
        TargetFolder = _settings.TargetFolder;
        IsCopyMode = _settings.TransferMode == TransferMode.Copy;
        SelectedHierarchy = HierarchyOptions.FirstOrDefault(o => o.Value == _settings.TargetHierarchy) ?? HierarchyOptions[0];
        SplitDatelessByHierarchy = _settings.SplitDatelessByHierarchy;
        // Ein gespeicherter Wert, den es nicht mehr gibt (die frühere
        // LLM-Automatik als 0), landet beim kombinierten Modus: Wer sie gewählt
        // hatte, wollte das Modell — und bekommt es hier samt Gegenprüfung.
        SelectedGroupingMode = GroupingOptions.FirstOrDefault(o => o.Value == _settings.GroupingMode)
                               ?? GroupingOptions.First(o => o.Value == GroupingMode.Combined);
        SelectedOutputLanguage = OutputLanguageOptions.FirstOrDefault(o => o.Value == _settings.OutputLanguage)
                                 ?? OutputLanguageOptions[0];
        SelectedUiLanguage = UiLanguageOptions.FirstOrDefault(o => o.Value == _settings.UiLanguage)
                             ?? UiLanguageOptions[0];

        // Strings built in code — status lines, hints — carry no dynamic
        // resource, so nothing would refresh them on a language change. An
        // empty name invalidates every binding on this object at once, which is
        // cheap enough for something a user does once.
        Loc.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);

        // The notes block shows a count and hides itself when empty; neither
        // follows from an item binding, so both are refreshed by hand.
        Warnings.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasWarnings));
            OnPropertyChanged(nameof(WarningsHeader));
        };

        OllamaEndpoint = _settings.OllamaEndpoint;
        Models = [.. VisionModels.All.Select(m => new ModelOptionViewModel(m))];
        // Through EnsureSupported, so a stored tag that is no longer offered
        // picks the same entry here as it does in the pipeline.
        var storedModel = VisionModels.EnsureSupported(_settings.ModelTag);
        SelectedModel = Models.FirstOrDefault(
            m => m.Model.Tag.Equals(storedModel, StringComparison.OrdinalIgnoreCase)) ?? Models[0];
        CollectUnmatched = _settings.CollectUnmatched;
        SkipDuplicates = _settings.SkipDuplicates;
        UnmatchedFolderName = _settings.UnmatchedFolderName;
        MinItemsPerFolder = _settings.MinItemsPerFolder.ToString();
        MaxEventGapDays = _settings.MaxEventGapDays.ToString("0.#", CultureInfo.CurrentCulture);
        GpsGroupRadiusKm = _settings.GpsGroupRadiusKm.ToString("0.#", CultureInfo.CurrentCulture);
        RegionMergeRadiusKm = _settings.RegionMergeRadiusKm.ToString("0.#", CultureInfo.CurrentCulture);
        WriteDescriptionToExif = _settings.WriteDescriptionToExif;
        RefreshKnowledgeUi();
        UseAnalysisCache = _settings.UseAnalysisCache;
        MaxParallelAnalyses = _settings.MaxParallelAnalyses.ToString(CultureInfo.CurrentCulture);
        ReuseBurstDescriptions = _settings.ReuseBurstDescriptions;
        ThumbnailsPerRow = Math.Clamp(
            _settings.ThumbnailsPerRow, MinThumbnailsPerRow, MaxThumbnailsPerRow);

        FaceRecognitionEnabled = _settings.FaceRecognitionEnabled;

        _initialized = true;
        RefreshFfmpeg();
        RefreshFaceStatus();
        _lastJournal = RunJournal.Load();
        OnPropertyChanged(nameof(CanUndoLastRun));
        _ = RefreshOllamaAsync();
        _ = RescanSourceAsync();
        _ = Update.CheckOnStartAsync();
    }

    /// <summary>Versionsanzeige und Aktualisierung. Eigenes Ansichtsmodell,
    /// weil es mit dem Rest dieser Klasse nichts teilt.</summary>
    public UpdateViewModel Update { get; }

    // ------------------------------------------------------------------
    // Einstellungen
    // ------------------------------------------------------------------

    public IReadOnlyList<OptionItem<TargetHierarchy>> HierarchyOptions { get; } =
    [
        new(TargetHierarchy.Flat, "S.Opt.Hierarchy.Flat"),
        new(TargetHierarchy.Year, "S.Opt.Hierarchy.Year"),
        new(TargetHierarchy.YearMonth, "S.Opt.Hierarchy.YearMonth"),
    ];

    /// <summary>Interface language. The labels stay in their own language —
    /// "Deutsch" is what someone looking for German scans for, and they cannot
    /// read the current language to find it.</summary>
    public IReadOnlyList<OptionItem<UiLanguage>> UiLanguageOptions { get; } =
    [
        new(UiLanguage.System, "S.Language.System"),
        new(UiLanguage.German, "S.Language.German"),
        new(UiLanguage.English, "S.Language.English"),
    ];

    /// <summary>Sprache der erzeugten Ordnernamen und Beschreibungen —
    /// unabhängig von der Sprache der Oberfläche. Beide Fassungen liegen im
    /// Analyse-Cache nebeneinander; ein Wechsel verwirft daher nichts.</summary>
    public IReadOnlyList<OptionItem<OutputLanguage>> OutputLanguageOptions { get; } =
    [
        new(OutputLanguage.German, "S.Opt.Output.German"),
        new(OutputLanguage.English, "S.Opt.Output.English"),
    ];

    public IReadOnlyList<OptionItem<GroupingMode>> GroupingOptions { get; } =
    [
        new(GroupingMode.ExifOnly, "S.Opt.Grouping.ExifOnly"),
        new(GroupingMode.Combined, "S.Opt.Grouping.Combined"),
    ];

    public ObservableCollection<ModelOptionViewModel> Models { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(StartBlockReason))]
    private string _sourceFolder = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(StartBlockReason))]
    private string _targetFolder = "";

    [ObservableProperty]
    private bool _isCopyMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SplitDatelessHint), nameof(HierarchyHasLevels),
                              nameof(CanEditSplitDateless))]
    private OptionItem<TargetHierarchy> _selectedHierarchy;

    /// <summary>Ein Sammel- und Alltagsordner je Jahr bzw. Monat statt je einer
    /// für die ganze Sammlung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SplitDatelessHint))]
    private bool _splitDatelessByHierarchy = true;

    /// <summary>Ohne Jahres-Ebene gibt es nichts, worin der Sammelordner liegen
    /// könnte — der Schalter bleibt dann gesperrt, statt wirkungslos zu
    /// wirken.</summary>
    public bool HierarchyHasLevels => SelectedHierarchy?.Value is not TargetHierarchy.Flat;

    /// <summary>Zeigt den entstehenden Pfad statt die Regel zu beschreiben.
    /// Welche Ebene gilt, steht in der Hierarchie-Auswahl darüber, und ohne
    /// Beispiel ist der Zusammenhang beider Felder nicht zu sehen.</summary>
    public string SplitDatelessHint
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(UnmatchedFolderName)
                ? "Vermischt"
                : UnmatchedFolderName.Trim();

            if (!HierarchyHasLevels)
            {
                return Loc.Get("S.Hint.SplitDateless.Flat");
            }

            if (!SplitDatelessByHierarchy)
            {
                return Loc.Format("S.Hint.SplitDateless.Off", name);
            }

            return Loc.Format(
                SelectedHierarchy?.Value == TargetHierarchy.YearMonth
                    ? "S.Hint.SplitDateless.YearMonth"
                    : "S.Hint.SplitDateless.Year",
                name);
        }
    }

    [ObservableProperty]
    private OptionItem<UiLanguage> _selectedUiLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputLanguageHint))]
    private OptionItem<OutputLanguage> _selectedOutputLanguage;

    /// <summary>Says in words what a switch costs — namely nothing. Without the
    /// hint the choice reads like something that invalidates the analysis
    /// cache, and nobody dares touch it.</summary>
    public string OutputLanguageHint => Loc.Get(
        SelectedOutputLanguage?.Value == OutputLanguage.English
            ? "S.Hint.OutputLanguage.En"
            : "S.Hint.OutputLanguage.De");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(StartBlockReason))]
    [NotifyPropertyChangedFor(nameof(NeedsLlm))]
    [NotifyPropertyChangedFor(nameof(CanEditCollectUnmatched), nameof(CanEditWriteDescription))]
    [NotifyPropertyChangedFor(nameof(GroupingModeHint))]
    private OptionItem<GroupingMode> _selectedGroupingMode;

    /// <summary>What the chosen mode actually does differently. The two names in
    /// the list do not say — the difference is WHO draws the folder boundaries,
    /// and what that costs in runtime.</summary>
    public string GroupingModeHint => Loc.Get(SelectedGroupingMode?.Value switch
    {
        GroupingMode.ExifOnly => "S.Hint.Mode.ExifOnly",
        _ => "S.Hint.Mode.Combined",
    });

    [ObservableProperty]
    private string _ollamaEndpoint = "http://localhost:11434";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullModelCommand))]
    [NotifyPropertyChangedFor(nameof(StartBlockReason))]
    private ModelOptionViewModel? _selectedModel;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditUnmatchedName))]
    private bool _collectUnmatched = true;

    /// <summary>Leave the lesser copy of a picture out of the run — the
    /// WhatsApp version beside the original. Nothing is deleted.</summary>
    [ObservableProperty]
    private bool _skipDuplicates = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SplitDatelessHint))]
    private string _unmatchedFolderName = "Vermischt";

    /// <summary>Mindestgröße einer Gruppe für einen eigenen Ordner. Als Text
    /// gebunden, damit das Feld beim Tippen nicht sofort auf 0 zurückspringt;
    /// ausgewertet wird beim Verlassen des Feldes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinItemsPerFolderHint))]
    private string _minItemsPerFolder = "2";

    public string MinItemsPerFolderHint => Loc.Get(
        ParseMinItems(MinItemsPerFolder) <= 1 ? "S.Hint.MinItems.Off" : "S.Hint.MinItems.On");

    /// <summary>Liest die Eingabe als Zahl; unsinnige Eingaben fallen auf 1
    /// zurück (= Prüfung aus), damit ein Tippfehler nie Aufnahmen einsammelt,
    /// die der Nutzer getrennt haben wollte.</summary>
    private static int ParseMinItems(string text) =>
        int.TryParse(text?.Trim(), out var value) ? Math.Clamp(value, 1, 999) : 1;

    /// <summary>Längste Pause ohne Aufnahme, die noch zum selben Ereignis
    /// zählt — in Tagen, als Text gebunden wie die übrigen Zahlenfelder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxEventGapHint))]
    private string _maxEventGapDays = "1";

    /// <summary>Ein- und Mehrzahl als zwei Schlüssel, wie bei der Dateizählung:
    /// "1 Tage" wäre falsch, und ein angehängtes "e" ist keine Übersetzung.</summary>
    public string MaxEventGapHint
    {
        get
        {
            var days = ParseEventGap(MaxEventGapDays);
            return Math.Abs(days - 1) < 0.001
                ? Loc.Get("S.Hint.MaxEventGap.One")
                : Loc.Format("S.Hint.MaxEventGap.Many", days);
        }
    }

    /// <summary>Grenzen wie im Kern. Bruchteile sind erlaubt — 0,8 Tage sind
    /// gut 19 Stunden; unter 0,1 zerfiele jede Serie, über zwei Monate wäre
    /// nichts mehr getrennt.</summary>
    private static double ParseEventGap(string text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
        || double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            ? Math.Clamp(value, 0.1, 60)
            : 1;

    /// <summary>Radius der räumlichen Gegenprobe in Kilometern. Wie oben als
    /// Text gebunden, damit das Feld beim Tippen bedienbar bleibt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GpsGroupRadiusHint))]
    private string _gpsGroupRadiusKm = "2";

    public string GpsGroupRadiusHint => Loc.Get(
        ParseRadius(GpsGroupRadiusKm) <= 0 ? "S.Hint.GpsRadius.Off" : "S.Hint.GpsRadius.On");

    /// <summary>Liest den Radius; unbrauchbare Eingaben schalten die Prüfung
    /// ab, statt einen zufälligen Wert anzuwenden.</summary>
    private static double ParseRadius(string text) =>
        double.TryParse(text?.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out var value)
        || double.TryParse(text?.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value)
            ? Math.Clamp(value, 0, 500)
            : 0;

    /// <summary>Radius, innerhalb dessen benachbarte Gruppen als dieselbe
    /// Unternehmung gelten.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegionMergeHint))]
    private string _regionMergeRadiusKm = "10";

    public string RegionMergeHint => Loc.Get(
        ParseRadius(RegionMergeRadiusKm) <= 0 ? "S.Hint.RegionMerge.Off" : "S.Hint.RegionMerge.On");

    [ObservableProperty]
    private bool _writeDescriptionToExif;

    [ObservableProperty]
    private bool _useAnalysisCache = true;

    /// <summary>Wie viele Bilder gleichzeitig zur Analyse gehen. Der größte
    /// Tempo-Hebel des Laufs; als Text gebunden wie die übrigen Zahlenfelder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaxParallelAnalysesHint))]
    private string _maxParallelAnalyses = "4";

    public string MaxParallelAnalysesHint => ParseParallel(MaxParallelAnalyses) <= 1
        ? Loc.Get("S.Hint.Parallel.Serial")
        : Loc.Format("S.Hint.Parallel.Concurrent", ParseParallel(MaxParallelAnalyses));

    /// <summary>Liest die Zahl gleichzeitiger Analysen; unsinnige Eingaben
    /// fallen auf 1 zurück (= altes serielles Verhalten), nie auf einen
    /// höheren Wert als eingegeben.</summary>
    private static int ParseParallel(string text) =>
        int.TryParse(text?.Trim(), out var value) ? Math.Clamp(value, 1, 16) : 1;

    /// <summary>Aufnahmeserien nur einmal analysieren.</summary>
    [ObservableProperty]
    private bool _reuseBurstDescriptions = true;

    // ------------------------------------------------------------------
    // Größe der Vorschaukacheln (globaler Zoom über allen Ansichten)
    // ------------------------------------------------------------------

    /// <summary>Grenzen des Zoom-Reglers, gezählt in Kacheln je Reihe. Die
    /// Untergrenze ist keine Geschmacksfrage: Bei weniger Kacheln je Reihe wird
    /// jede einzelne so groß, dass das in
    /// <c>ResultItemViewModel.ThumbnailDecodeSize</c> dekodierte Vorschaubild
    /// hochskaliert werden müsste und unscharf würde.</summary>
    public double MinThumbnailsPerRow => 4;

    public double MaxThumbnailsPerRow => 20;

    /// <summary>Als <c>double</c>, weil der Schieberegler nichts anderes
    /// bindet; gespeichert und gerechnet wird mit ganzen Kacheln.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThumbnailsPerRowText))]
    private double _thumbnailsPerRow = 8;

    public string ThumbnailsPerRowText => Loc.Format("S.Thumb.PerRow", (int)ThumbnailsPerRow);

    partial void OnThumbnailsPerRowChanged(double value) => PersistSettings();

    /// <summary>Strg + Mausrad über der Kachelfläche zoomt stufenweise. Nach
    /// oben heißt größer, also WENIGER Kacheln je Reihe.</summary>
    public void ZoomThumbnails(int wheelDelta)
    {
        ThumbnailsPerRow = Math.Clamp(
            ThumbnailsPerRow + (wheelDelta > 0 ? -1 : 1),
            MinThumbnailsPerRow,
            MaxThumbnailsPerRow);
    }

    public bool NeedsLlm => SelectedGroupingMode.Value != GroupingMode.ExifOnly;

    partial void OnSourceFolderChanged(string value)
    {
        PersistSettings();
        _ = RescanSourceAsync();
    }

    partial void OnTargetFolderChanged(string value)
    {
        PersistSettings();
        RefreshPlanIfEditing();

        // Where the target sits inside the source folder, it is not part of
        // the current state.
        _ = RescanSourceAsync();
    }

    partial void OnIsCopyModeChanged(bool value)
    {
        PersistSettings();
        RefreshPlanIfEditing();
    }

    partial void OnSelectedHierarchyChanged(OptionItem<TargetHierarchy> value)
    {
        PersistSettings();
        RefreshPlanIfEditing();
    }

    /// <summary>Filing settings act on an open plan at once — otherwise the
    /// preview showed something other than what applying would do.</summary>
    private void RefreshPlanIfEditing()
    {
        if (State == AppState.Plan)
        {
            RefreshPlan();
        }
    }

    /// <summary>Where a file ends up — target folder, hierarchy, copy against
    /// move, name of the collecting folder. These are recomputed on the open
    /// plan, so they stay editable while one stands. Only a running pipeline
    /// must not have them pulled out from under it: it reads the settings
    /// object as it goes.</summary>
    public bool CanEditFilingSettings => State != AppState.Running;

    /// <summary>Everything only a new run can act on — grouping, model,
    /// thresholds, source folder. Locked while a plan is open: a control that
    /// silently does nothing is worse than one that says why it is
    /// unavailable, and the settings ARE applied again the moment the plan is
    /// applied or discarded.</summary>
    public bool CanEditRunSettings => State is not (AppState.Running or AppState.Plan);

    /// <summary>Shown next to the locked settings so the reason is where the
    /// controls are.</summary>
    public bool ShowRunSettingsLockedHint => State == AppState.Plan;

    /// <summary>Two reasons this switch can be unavailable: no level to split
    /// along, or an open plan. One binding, because a control takes one.</summary>
    public bool CanEditSplitDateless => HierarchyHasLevels && CanEditRunSettings;

    /// <summary>Without the model there is no collecting folder to fill, and
    /// an open plan has filled it already.</summary>
    public bool CanEditCollectUnmatched => NeedsLlm && CanEditRunSettings;

    /// <summary>Only the NAME of the collecting folder — that is filing, and
    /// it is renamed in an open plan.</summary>
    public bool CanEditUnmatchedName => CollectUnmatched && CanEditFilingSettings;

    /// <summary>Read when the plan is applied, not while it is built, so it
    /// still counts for an open plan.</summary>
    public bool CanEditWriteDescription => NeedsLlm && CanEditFilingSettings;

    /// <summary>A running download holds the choice as well as an open plan
    /// does — swapping the model mid-pull would download the wrong one.</summary>
    public bool CanEditModel => !IsPulling && CanEditRunSettings;

    partial void OnSelectedGroupingModeChanged(OptionItem<GroupingMode> value) => PersistSettings();

    partial void OnSelectedOutputLanguageChanged(OptionItem<OutputLanguage> value) => PersistSettings();

    partial void OnSelectedUiLanguageChanged(OptionItem<UiLanguage> value)
    {
        Loc.Apply(value.Value);
        PersistSettings();
    }

    partial void OnSelectedModelChanged(ModelOptionViewModel? value) => PersistSettings();


    /// <summary>A run setting, not a filing one, and therefore locked while a
    /// plan is open: splitting the dateless folders afterwards would mean
    /// running the grouping again, not recomputing a path.</summary>
    partial void OnSplitDatelessByHierarchyChanged(bool value) => PersistSettings();

    partial void OnCollectUnmatchedChanged(bool value) => PersistSettings();

    partial void OnSkipDuplicatesChanged(bool value) => PersistSettings();

    partial void OnUnmatchedFolderNameChanged(string value)
    {
        // The previous name before PersistSettings overwrites it — the themed
        // subfolders point at it by name.
        var previous = _settings.UnmatchedFolderName;
        PersistSettings();
        RenameCollectionInPlan(previous, _settings.UnmatchedFolderName);
        RefreshPlanIfEditing();
    }

    /// <summary>Renames the collecting folder in an open plan: the card itself,
    /// every part of it after a period split, and the themed subfolders that
    /// sit inside it. A card the user renamed by hand keeps its name.</summary>
    private void RenameCollectionInPlan(string previous, string current)
    {
        if (State != AppState.Plan || previous == current)
        {
            return;
        }

        foreach (var folder in Folders)
        {
            if (folder.IsFrozen)
            {
                continue;
            }

            if (folder.IsCollection && !folder.HasManualName && folder.Theme == previous)
            {
                folder.Theme = current;
            }

            if (folder.InsideFolder == previous)
            {
                folder.InsideFolder = current;
            }
        }
    }

    partial void OnMinItemsPerFolderChanged(string value) => PersistSettings();


    partial void OnMaxEventGapDaysChanged(string value) => PersistSettings();

    partial void OnGpsGroupRadiusKmChanged(string value) => PersistSettings();

    partial void OnRegionMergeRadiusKmChanged(string value) => PersistSettings();

    partial void OnWriteDescriptionToExifChanged(bool value) => PersistSettings();

    partial void OnUseAnalysisCacheChanged(bool value) => PersistSettings();

    partial void OnMaxParallelAnalysesChanged(string value) => PersistSettings();

    partial void OnReuseBurstDescriptionsChanged(bool value) => PersistSettings();

    /// <summary>Verwirft alle gespeicherten Bildbeschreibungen. Nötig, wenn
    /// sie nicht mehr zum aktuellen Stand passen — etwa nach neuen
    /// Referenzgesichtern, geänderten Fakten oder korrigierten Notizen.</summary>
    [RelayCommand]
    private void ClearAnalysisCacheFile()
    {
        var removed = DescriptionCache.ClearDefault();

        // Die gespeicherten Gesichter gehören mit dazu: Sie sind derselbe
        // Zwischenstand über dieselben Dateien. Bliebe hier ein halbes
        // Gedächtnis zurück, wäre "alles neu analysieren" eine Unwahrheit.
        var faces = FaceCache.ClearDefault();

        StatusMessage = removed switch
        {
            < 0 => Loc.Get("S.Cache.ClearFailed"),
            0 when faces <= 0 => Loc.Get("S.Cache.AlreadyEmpty"),
            _ => Loc.Format(
                "S.Cache.Cleared",
                Math.Max(removed, 0),
                faces > 0 ? Loc.Format("S.Cache.ClearedFaces", faces) : ""),
        };
    }

    partial void OnOllamaEndpointChanged(string value) => PersistSettings();

    private void PersistSettings()
    {
        if (!_initialized)
        {
            return;
        }

        _settings.SourceFolder = SourceFolder;
        _settings.TargetFolder = TargetFolder;
        _settings.TransferMode = IsCopyMode ? TransferMode.Copy : TransferMode.Move;
        _settings.TargetHierarchy = SelectedHierarchy.Value;
        _settings.SplitDatelessByHierarchy = SplitDatelessByHierarchy;
        _settings.GroupingMode = SelectedGroupingMode.Value;
        _settings.OutputLanguage = SelectedOutputLanguage.Value;
        _settings.UiLanguage = SelectedUiLanguage.Value;
        _settings.OllamaEndpoint = string.IsNullOrWhiteSpace(OllamaEndpoint)
            ? "http://localhost:11434"
            : OllamaEndpoint.Trim();
        _settings.ModelTag = SelectedModel?.Model.Tag ?? _settings.ModelTag;
        _settings.CollectUnmatched = CollectUnmatched;
        _settings.SkipDuplicates = SkipDuplicates;
        _settings.UnmatchedFolderName = string.IsNullOrWhiteSpace(UnmatchedFolderName)
            ? "Vermischt"
            : UnmatchedFolderName.Trim();
        _settings.MinItemsPerFolder = ParseMinItems(MinItemsPerFolder);
        _settings.MaxEventGapDays = ParseEventGap(MaxEventGapDays);
        _settings.GpsGroupRadiusKm = ParseRadius(GpsGroupRadiusKm);
        _settings.RegionMergeRadiusKm = ParseRadius(RegionMergeRadiusKm);
        _settings.WriteDescriptionToExif = WriteDescriptionToExif;
        _settings.FaceRecognitionEnabled = FaceRecognitionEnabled;
        _settings.UseAnalysisCache = UseAnalysisCache;
        _settings.MaxParallelAnalyses = ParseParallel(MaxParallelAnalyses);
        _settings.ReuseBurstDescriptions = ReuseBurstDescriptions;
        _settings.ThumbnailsPerRow = (int)ThumbnailsPerRow;
        _settingsStore.Save(_settings);
    }

    [RelayCommand]
    private void BrowseSource()
    {
        if (BrowseFolder(Loc.Get("S.Dlg.BrowseSource")) is { } folder)
        {
            SourceFolder = folder;
        }
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        if (BrowseFolder(Loc.Get("S.Dlg.BrowseTarget")) is { } folder)
        {
            TargetFolder = folder;
        }
    }

    private static string? BrowseFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    // ------------------------------------------------------------------
    // Ollama / Modelle
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullModelCommand))]
    [NotifyPropertyChangedFor(nameof(StartBlockReason))]
    [NotifyPropertyChangedFor(nameof(OllamaStatusText))]
    private OllamaState _ollamaState = OllamaState.Checking;

    public string OllamaStatusText => Loc.Get(OllamaState switch
    {
        OllamaState.Checking => "S.Msg.OllamaChecking",
        OllamaState.Online => "S.Msg.OllamaOnline",
        _ => "S.Msg.OllamaOffline",
    });

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PullModelCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditModel))]
    private bool _isPulling;

    [ObservableProperty]
    private string _pullStatusText = "";

    [ObservableProperty]
    private double _pullFraction;

    [ObservableProperty]
    private bool _pullIndeterminate;

    [RelayCommand]
    private async Task RefreshOllamaAsync()
    {
        OllamaState = OllamaState.Checking;
        try
        {
            using var client = new OllamaClient(_settings.OllamaEndpoint);
            if (!await client.IsReachableAsync())
            {
                OllamaState = OllamaState.Offline;
                return;
            }

            var installed = await client.ListLocalModelsAsync();
            foreach (var model in Models)
            {
                model.IsInstalled = installed.Contains(model.Model.Tag, StringComparer.OrdinalIgnoreCase);
            }

            OllamaState = OllamaState.Online;
        }
        catch
        {
            OllamaState = OllamaState.Offline;
        }

        StartCommand.NotifyCanExecuteChanged();
        PullModelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(StartBlockReason));
    }

    private bool CanPullModel(ModelOptionViewModel? model) =>
        OllamaState == OllamaState.Online && !IsPulling && model is { IsInstalled: false };

    [RelayCommand(CanExecute = nameof(CanPullModel))]
    private async Task PullModelAsync(ModelOptionViewModel? model)
    {
        if (model is null)
        {
            return;
        }

        var tag = model.Model.Tag;
        _pullCts = new CancellationTokenSource();
        IsPulling = true;
        PullIndeterminate = true;
        PullStatusText = Loc.Format("S.Msg.PullStarting", tag);

        try
        {
            using var client = new OllamaClient(_settings.OllamaEndpoint);
            await foreach (var progress in client.PullModelAsync(tag, _pullCts.Token))
            {
                if (progress.Fraction is { } fraction)
                {
                    PullIndeterminate = false;
                    PullFraction = fraction;
                    PullStatusText = $"{tag}: {progress.Status}  ·  {fraction:P0}  ({FormatBytes(progress.Completed)} / {FormatBytes(progress.Total)})";
                }
                else
                {
                    PullStatusText = $"{tag}: {progress.Status}";
                }
            }

            PullStatusText = Loc.Format("S.Msg.PullDone", tag);
            await RefreshOllamaAsync();
        }
        catch (OperationCanceledException)
        {
            PullStatusText = Loc.Get("S.Msg.PullCancelled");
        }
        catch (Exception ex)
        {
            PullStatusText = Loc.Format("S.Msg.DownloadFailed", ex.Message);
        }
        finally
        {
            IsPulling = false;
            _pullCts?.Dispose();
            _pullCts = null;
            PullModelCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CancelPull() => _pullCts?.Cancel();

    // ------------------------------------------------------------------
    // Gesichtserkennung
    // ------------------------------------------------------------------

    private readonly FaceModelProvider _faceModels = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FaceModelsStatusText), nameof(CanDownloadFaceModels))]
    [NotifyCanExecuteChangedFor(nameof(DownloadFaceModelsCommand))]
    private bool _faceModelsInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadFaceModels))]
    [NotifyCanExecuteChangedFor(nameof(DownloadFaceModelsCommand))]
    private bool _isDownloadingFaceModels;

    [ObservableProperty]
    private double _faceModelProgress;

    [ObservableProperty]
    private string? _faceModelStatusDetail;

    [ObservableProperty]
    private bool _faceRecognitionEnabled;

    [ObservableProperty]
    private int _knownPersonCount;

    public string FaceModelsStatusText => FaceModelsInstalled
        ? Loc.Get("S.Msg.FaceModelsInstalled")
        : Loc.Format("S.Msg.FaceModelsMissing", FaceModelProvider.TotalDownloadBytes / 1024 / 1024);

    public bool CanDownloadFaceModels => !FaceModelsInstalled && !IsDownloadingFaceModels;

    partial void OnFaceRecognitionEnabledChanged(bool value) => PersistSettings();

    partial void OnKnownPersonCountChanged(int value) => OnPropertyChanged(nameof(KnowledgeSummary));

    [RelayCommand]
    private void RefreshFaceStatus()
    {
        FaceModelsInstalled = _faceModels.IsInstalled;
        KnownPersonCount = PersonStore.Load().Persons.Count;
    }

    [RelayCommand(CanExecute = nameof(CanDownloadFaceModels))]
    private async Task DownloadFaceModelsAsync()
    {
        IsDownloadingFaceModels = true;
        FaceModelStatusDetail = Loc.Get("S.Msg.DownloadStarting");
        try
        {
            var progress = new Progress<(StatusReport Message, double Fraction)>(p =>
            {
                FaceModelStatusDetail = Loc.Format(
                    "S.Msg.DownloadDetail",
                    Loc.Format(p.Message.MessageKey, p.Message.MessageArgs),
                    p.Fraction);
                FaceModelProgress = p.Fraction;
            });
            await _faceModels.DownloadAsync(progress, CancellationToken.None);
            FaceModelProgress = 1;
            FaceModelStatusDetail = Loc.Get("S.Msg.FaceModelsReady");
        }
        catch (Exception ex)
        {
            FaceModelStatusDetail = Loc.Format("S.Msg.DownloadFailed", ex.Message);
        }
        finally
        {
            IsDownloadingFaceModels = false;
            RefreshFaceStatus();
        }
    }

    /// <summary>Öffnet den Zuordnungsdialog für ein Foto; liefert false, wenn
    /// die Voraussetzungen fehlen (Meldung steht dann in StatusMessage).</summary>
    public FaceService? TryCreateFaceService()
    {
        if (!_faceModels.IsInstalled)
        {
            StatusMessage = Loc.Get("S.Msg.FaceModelsNotInstalled");
            return null;
        }

        try
        {
            return new FaceService(_faceModels);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("S.Msg.FaceUnavailable", ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // ffmpeg (Videoanalyse)
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FfmpegStatusText), nameof(CanInstallFfmpeg))]
    [NotifyCanExecuteChangedFor(nameof(InstallFfmpegCommand))]
    private bool _ffmpegAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallFfmpeg))]
    [NotifyCanExecuteChangedFor(nameof(InstallFfmpegCommand))]
    private bool _isInstallingFfmpeg;

    [ObservableProperty]
    private string? _ffmpegStatusDetail;

    public string FfmpegStatusText =>
        Loc.Get(FfmpegAvailable ? "S.Msg.FfmpegFound" : "S.Msg.FfmpegMissing");

    public bool CanInstallFfmpeg => !FfmpegAvailable && !IsInstallingFfmpeg;

    [RelayCommand]
    private void RefreshFfmpeg()
    {
        var path = FfmpegService.Locate(_settings.FfmpegPath);
        FfmpegAvailable = path is not null;
        FfmpegStatusDetail = path;

        // Gefundenen Pfad festhalten: Ein frisch installiertes ffmpeg ist im
        // laufenden Prozess sonst erst nach einem Neustart im PATH.
        if (path is not null && !string.Equals(_settings.FfmpegPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _settings.FfmpegPath = path;
            _settingsStore.Save(_settings);
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallFfmpeg))]
    private async Task InstallFfmpegAsync()
    {
        if (!FfmpegInstaller.IsWingetAvailable)
        {
            FfmpegStatusDetail = Loc.Get("S.Msg.WingetMissing");
            return;
        }

        IsInstallingFfmpeg = true;
        FfmpegStatusDetail = Loc.Get("S.Msg.FfmpegInstalling");
        try
        {
            var output = new Progress<string>(line => FfmpegStatusDetail = line);
            var path = await FfmpegInstaller.InstallAsync(output);
            if (path is not null)
            {
                _settings.FfmpegPath = path;
                _settingsStore.Save(_settings);
                FfmpegStatusDetail = Loc.Format("S.Msg.FfmpegInstalled", path);
            }
            else
            {
                FfmpegStatusDetail = Loc.Get("S.Msg.FfmpegStillMissing");
            }
        }
        catch (Exception ex)
        {
            FfmpegStatusDetail = Loc.Format("S.Msg.InstallFailed", ex.Message);
        }
        finally
        {
            IsInstallingFfmpeg = false;
            RefreshFfmpeg();
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F0} MB",
        _ => $"{bytes / 1024.0:F0} KB",
    };

    // ------------------------------------------------------------------
    // Orte & Wissen (Heimatzonen, Fakten, Notizen)
    // ------------------------------------------------------------------

    /// <summary>Wissensverwaltung — als Ansicht im Hauptfenster, nicht mehr
    /// als eigenes Fenster.</summary>
    public KnowledgeViewModel Knowledge { get; }

    /// <summary>Welche der drei Hauptansichten sichtbar ist.
    ///
    /// <para>Die Einstellungsseite nimmt auf, was NICHT zu jedem Lauf gehört —
    /// Modell, Tempo, ffmpeg, Aktualisierung. Links bleibt, was man vor einem
    /// Lauf tatsächlich anfasst; die Leiste war sonst länger als der
    /// Bildschirm.</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOrganize), nameof(ShowKnowledge), nameof(ShowSettings))]
    private MainView _activeView = MainView.Organize;

    public bool ShowOrganize => ActiveView == MainView.Organize;

    public bool ShowKnowledge => ActiveView == MainView.Knowledge;

    public bool ShowSettings => ActiveView == MainView.Settings;

    [ObservableProperty]
    private string? _homeSuggestionText;

    private GeoPoint? _homeSuggestionPoint;

    [RelayCommand]
    private void OpenKnowledge()
    {
        Knowledge.Reload();
        ActiveView = MainView.Knowledge;
    }

    [RelayCommand]
    private void OpenOrganize() => ActiveView = MainView.Organize;

    [RelayCommand]
    private void OpenSettings() => ActiveView = MainView.Settings;

    private void RefreshKnowledgeUi()
    {
        Knowledge.Reload();
        OnPropertyChanged(nameof(KnowledgeSummary));
    }

    /// <summary>Kurzfassung für die Einstellungsleiste.</summary>
    public string KnowledgeSummary => Knowledge.SummaryText;

    public void AddHomeZone(string name, GeoPoint center)
    {
        _settings.HomeZones.Add(new HomeZone
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Zuhause" : name.Trim(),
            Latitude = center.Latitude,
            Longitude = center.Longitude,
        });
        _settingsStore.Save(_settings);
        RefreshKnowledgeUi();
        StatusMessage = Loc.Format("S.Msg.ZoneSaved", name);
    }

    public void AcceptHomeSuggestion(string name)
    {
        if (_homeSuggestionPoint is { } point)
        {
            AddHomeZone(name, point);
        }

        HomeSuggestionText = null;
        _homeSuggestionPoint = null;
    }

    [RelayCommand]
    private void DismissHomeSuggestion()
    {
        HomeSuggestionText = null;
        _homeSuggestionPoint = null;
    }

    /// <summary>Setzt/ändert die Notiz eines Bildes (Wissensbasis, per
    /// Inhalts-Hash) — wirkt ab dem nächsten Lauf auch auf Analyse/Benennung.</summary>
    public void SetItemNote(ResultItemViewModel item, string note)
    {
        var path = item.CurrentPath is { } cp && File.Exists(cp)
            ? cp
            : File.Exists(item.Item.SourcePath) ? item.Item.SourcePath : null;
        if (path is null || KnowledgeStore.ComputeContentKey(path) is not { } key)
        {
            StatusMessage = Loc.Get("S.Msg.NoteUnreadable");
            return;
        }

        var store = KnowledgeStore.Load();

        // Eine eigene Miniatur in den Nutzerdaten ablegen: Sonst zeigt die
        // Wissensverwaltung nichts, sobald die Datei verschoben wurde — und
        // ohne den Pfad hier war die Vorschau bisher IMMER leer.
        var thumbnail = store.HasThumbnail(key) ? null : CreateNoteThumbnail(item, path);
        store.SetNote(key, note, item.FileName, path, thumbnail);
        store.Save();
        RefreshKnowledgeUi();
        item.UserNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        StatusMessage = Loc.Format(
            string.IsNullOrWhiteSpace(note) ? "S.Msg.NoteRemoved" : "S.Msg.NoteSaved",
            item.FileName);
    }

    /// <summary>Ändert oder entfernt (leerer Text) die Bildbeschreibung.
    /// <para>Die Änderung wandert zusätzlich in den Analyse-Cache: Sonst holt
    /// der nächste Lauf die alte LLM-Fassung von dort zurück, und die
    /// Handarbeit wäre still überschrieben.</para></summary>
    public void SetItemDescription(IReadOnlyList<ResultItemViewModel> items, string? description)
    {
        var model = SelectedModel?.Model.Tag ?? _settings.ModelTag;
        DescriptionCache? cache = null;
        foreach (var item in items)
        {
            item.Description = description;

            if (DescriptionCache.ComputeKey(item.PathOnDisk, model) is not { } key)
            {
                continue;
            }

            cache ??= DescriptionCache.Load(DescriptionCache.DefaultPath);
            cache.Store(key, item.Item);
        }

        cache?.Save();

        var cleared = string.IsNullOrWhiteSpace(description);
        var subject = items.Count == 1
            ? $"\"{items[0].FileName}\""
            : Loc.Format("S.Msg.SubjectFiles", items.Count);
        StatusMessage = Loc.Format(
            cleared ? "S.Msg.DescriptionRemoved" : "S.Msg.DescriptionChanged", subject);

        if (!UseAnalysisCache)
        {
            StatusMessage += Loc.Get("S.Msg.CacheOffSuffix");
        }
    }

    /// <summary>Kleines JPEG für die Notiz-Vorschau; null, wenn die Datei
    /// nicht dekodierbar ist (dann bleibt es beim Platzhalter).</summary>
    private byte[]? CreateNoteThumbnail(ResultItemViewModel item, string path)
    {
        try
        {
            return item.IsVideo
                ? _imagePreprocessor.GetVideoFrameJpeg(path, 320)
                : _imagePreprocessor.GetJpegForAnalysis(path, 320);
        }
        catch
        {
            // Vorschau ist Komfort — die Notiz selbst zählt.
            return null;
        }
    }

    /// <summary>Prüft nach einem Lauf, ob sich ein Heimatzonen-Vorschlag
    /// aufdrängt (viele Aufnahmen am selben, noch nicht erfassten Ort).</summary>
    private void ComputeHomeSuggestion(IEnumerable<MediaItem> items)
    {
        HomeSuggestionText = null;
        _homeSuggestionPoint = null;

        var located = items
            .Select(i => i.Location)
            .Where(l => l is not null)
            .Select(l => l!.Value)
            .ToList();
        if (located.Count < 5)
        {
            return;
        }

        // ~1-km-Raster: dichtester Ort der Aufnahmen.
        var top = located
            .GroupBy(p => (Math.Round(p.Latitude, 2), Math.Round(p.Longitude, 2)))
            .OrderByDescending(g => g.Count())
            .First();
        if (top.Count() < Math.Max(5, located.Count * 2 / 5))
        {
            return;
        }

        var centroid = new GeoPoint(
            top.Average(p => p.Latitude), top.Average(p => p.Longitude));
        if (_settings.HomeZones.Any(z => z.Center.DistanceKm(centroid) <= Math.Max(1.0, z.RadiusKm)))
        {
            return;
        }

        var placeText = $"{centroid.Latitude:F3}, {centroid.Longitude:F3}";
        try
        {
            if (OfflineGeocoder.Instance.FindNearest(centroid) is { } place)
            {
                placeText = place.DisplayName;
            }
        }
        catch
        {
            // Ortsauflösung ist Komfort.
        }

        _homeSuggestionPoint = centroid;
        HomeSuggestionText = Loc.Format("S.Msg.HomeSuggestion", top.Count(), placeText);
    }

    // ------------------------------------------------------------------
    // Verarbeitung
    // ------------------------------------------------------------------

    [ObservableProperty]
    private AppState _state = AppState.Idle;

    [ObservableProperty]
    private string _phaseTitle = "";

    [ObservableProperty]
    private string _progressMessage = "";

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private bool _progressIndeterminate;

    [ObservableProperty]
    private string? _runError;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<ResultFolderViewModel> Folders { get; } = [];

    /// <summary>The notes of a run, each with the warning it came from: the
    /// text for the window, the warning itself so that a plan rebuilt after
    /// editing still carries it. Formatting it once and throwing the original
    /// away would mean re-parsing a sentence to get the facts back.</summary>
    public ObservableCollection<WarningViewModel> Warnings { get; } = [];

    /// <summary>Whether the notes are listed or only counted. They sit above
    /// the result and are not scrolled with it, so a long list pushes the
    /// folders off screen — collapsed, the whole block costs one line. That is
    /// why it starts collapsed: the header still carries the count, and the
    /// result is what the user came to look at.</summary>
    [ObservableProperty]
    private bool _warningsExpanded;

    public bool HasWarnings => Warnings.Count > 0;

    public string WarningsHeader => Loc.Format("S.Warnings.Header", Warnings.Count);

    [RelayCommand]
    private void ToggleWarnings() => WarningsExpanded = !WarningsExpanded;

    /// <summary>Hides one note. It is gone for this run — the plan is built
    /// from what is left, so a dismissed note does not come back after
    /// applying.</summary>
    [RelayCommand]
    private void DismissWarning(WarningViewModel? warning)
    {
        if (warning is not null)
        {
            Warnings.Remove(warning);
        }
    }

    [RelayCommand]
    private void DismissAllWarnings() => Warnings.Clear();

    public string? StartBlockReason
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourceFolder) || !Directory.Exists(SourceFolder))
            {
                return Loc.Get("S.Block.NoSource");
            }

            if (string.IsNullOrWhiteSpace(TargetFolder))
            {
                return Loc.Get("S.Block.NoTarget");
            }

            if (Path.GetFullPath(TargetFolder).Equals(Path.GetFullPath(SourceFolder), StringComparison.OrdinalIgnoreCase))
            {
                return Loc.Get("S.Block.SameFolder");
            }

            if (NeedsLlm)
            {
                if (OllamaState != OllamaState.Online)
                {
                    return Loc.Get("S.Block.OllamaOffline");
                }

                if (SelectedModel is not { IsInstalled: true })
                {
                    return Loc.Get("S.Block.ModelMissing");
                }
            }

            return null;
        }
    }

    private bool CanStart => State is not (AppState.Running or AppState.Plan) && StartBlockReason is null;

    private Progress<PipelineProgress> CreateProgress() => new(p =>
    {
        PhaseTitle = Loc.Get(p.Phase switch
        {
            PipelinePhase.Scan => "S.Phase.Scan",
            PipelinePhase.Metadata => "S.Phase.Metadata",
            PipelinePhase.Faces => "S.Phase.RecognizePeople",
            PipelinePhase.Analyze => "S.Phase.Analyze",
            PipelinePhase.Group => "S.Phase.Group",
            PipelinePhase.Name => "S.Phase.Name",
            PipelinePhase.Plan => "S.Phase.Plan",
            PipelinePhase.Verify => "S.Phase.Verify",
            PipelinePhase.Transfer => IsCopyMode ? "S.Phase.Copy" : "S.Phase.Move",
            PipelinePhase.Undo => "S.Phase.Undo",
            _ => "S.Phase.Done",
        });
        ProgressMessage = Loc.Format(p.MessageKey, p.MessageArgs);
        ProgressIndeterminate = p.Total <= 1 && p.Phase is PipelinePhase.Scan or PipelinePhase.Group or PipelinePhase.Plan;
        ProgressFraction = p.Fraction;
    });

    /// <summary>Ein Lauf schreibt nichts: Er endet mit einem Plan, den der
    /// Nutzer prüfen, umsortieren und verwerfen kann.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        RunError = null;
        StatusMessage = null;
        _sourceThumbnailCts?.Cancel();
        _sourceScanCts?.Cancel();
        State = AppState.Running;
        PhaseTitle = Loc.Get("S.Phase.Prepare");
        ProgressMessage = "";
        ProgressIndeterminate = true;
        _runCts = new CancellationTokenSource();

        var progress = CreateProgress();
        try
        {
            var pipeline = new OrganizePipeline(_settings, _imagePreprocessor);
            var plan = await Task.Run(() => pipeline.PlanAsync(progress, _runCts.Token));
            PopulatePlan(plan);
            ComputeHomeSuggestion(plan.Folders.SelectMany(f => f.Items).Select(i => i.Item));
            RefreshKnowledgeUi();
            ShowSourceView = false;
            State = AppState.Plan;
        }
        catch (OperationCanceledException)
        {
            RunError = Loc.Get("S.Msg.RunCancelled");
            State = AppState.Idle;
        }
        catch (Exception ex)
        {
            RunError = ex.Message;
            State = AppState.Idle;
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private void CancelRun() => _runCts?.Cancel();

    [RelayCommand]
    private void NewRun()
    {
        _thumbnailCts?.Cancel();
        Folders.Clear();
        Warnings.Clear();
        StatusMessage = null;
        PlanSummaryText = "";
        ShowSourceView = false;
        State = AppState.Idle;
        _ = RescanSourceAsync();
    }

    private void PopulateResults(OrganizeRunResult result)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        Folders.Clear();
        Warnings.Clear();

        foreach (var warning in result.Warnings)
        {
            Warnings.Add(new WarningViewModel(warning));
        }

        var ffmpeg = new FfmpegService(_settings.FfmpegPath);
        foreach (var folder in result.Folders.OrderBy(f => f.GroupStart))
        {
            var folderVm = new ResultFolderViewModel(folder.FolderPath, folder.Theme, folder.GroupStart)
            {
                IsExpanded = ShouldExpand(result.Folders.Count),
            };

            foreach (var op in folder.Operations)
            {
                folderVm.Add(new ResultItemViewModel(op.Item)
                {
                    CurrentPath = op.FinalPath,
                    Status = op.Status,
                    Error = op.Error,
                });
            }

            AttachThumbnailLoader(folderVm, ffmpeg, _thumbnailCts.Token);
            Folders.Add(folderVm);
        }
    }

    /// <summary>Karten starten nur bei überschaubar vielen Ordnern aufgeklappt.
    /// Bei einem großen Lauf sind es hunderte — dann ist die Übersicht
    /// nützlicher, und es werden nicht zehntausend Vorschaubilder auf einmal
    /// erzeugt.</summary>
    private static bool ShouldExpand(int folderCount) => folderCount <= 8;

    /// <summary>Attaches the thumbnail loader to a card and starts it when the
    /// card is expanded. Collapsed cards load on expanding.</summary>
    private void AttachThumbnailLoader(
        ResultFolderViewModel folder, FfmpegService ffmpeg, CancellationToken ct) =>
        AttachThumbnailLoader(folder, ffmpeg, () => ct);

    /// <summary>The same, but the card asks for the token every time instead of
    /// keeping the one it was built with. Cards that outlive a cancellation need
    /// this: the source cards stay on screen while a run cancels their decodes,
    /// and a card holding the cancelled token never shows a picture again.</summary>
    private void AttachThumbnailLoader(
        ResultFolderViewModel folder, FfmpegService ffmpeg, Func<CancellationToken> token)
    {
        folder.ThumbnailLoader = target => LoadFolderThumbnailsAsync(target, ffmpeg, token());
        if (folder.IsExpanded)
        {
            _ = folder.ThumbnailLoader(folder);
        }
    }

    /// <summary>Token for the thumbnails of the current-state cards. Starting a
    /// run cancels the decodes running at that moment, but the cards remain and
    /// must be able to load again afterwards — so a cancelled token is replaced
    /// rather than handed out. Deliberately not disposed: decodes still in
    /// flight hold it, and a disposed source makes them fail instead.</summary>
    private CancellationToken SourceThumbnailToken()
    {
        if (_sourceThumbnailCts is null || _sourceThumbnailCts.IsCancellationRequested)
        {
            _sourceThumbnailCts = new CancellationTokenSource();
        }

        return _sourceThumbnailCts.Token;
    }

    /// <summary>Lädt die Vorschaubilder einer Karte. Die Drosselung begrenzt,
    /// wie viele gleichzeitig entstehen; bereits geladene werden übersprungen.</summary>
    private async Task LoadFolderThumbnailsAsync(
        ResultFolderViewModel folder, FfmpegService ffmpeg, CancellationToken ct)
    {
        try
        {
            // Nur die gezeigten Kacheln: Was hinter "weitere anzeigen" liegt,
            // hat kein Bild auf dem Schirm, für das ein Vorschaubild nötig wäre.
            await Task.WhenAll(folder.VisibleItems
                .ToList()
                .Select(item => item.LoadThumbnailAsync(ffmpeg, _thumbnailThrottle, ct)));
        }
        catch (OperationCanceledException)
        {
            // Ansicht gewechselt oder App geschlossen.
        }
    }

    // ------------------------------------------------------------------
    // Ist-Zustand des Quellordners (kein LLM, kein Datei-I/O)
    // ------------------------------------------------------------------

    /// <summary>Vorhandene Struktur des Quellordners — sichtbar, bevor
    /// überhaupt ein Lauf startet.</summary>
    public ObservableCollection<ResultFolderViewModel> SourceFolders { get; } = [];

    [ObservableProperty]
    private bool _isScanningSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceSnapshot))]
    private string _sourceSummaryText = "";

    /// <summary>Im Plan-Zustand: zurück auf den Ist-Zustand umschalten.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveViewTitle), nameof(ActiveViewHint))]
    private bool _showSourceView;

    public bool HasSourceSnapshot => SourceFolders.Count > 0;

    public string ActiveViewTitle => Loc.Get(ShowSourceView ? "S.View.Current" : "S.View.Plan");

    public string ActiveViewHint =>
        Loc.Get(ShowSourceView ? "S.Msg.SourceViewHint" : "S.Msg.PlanViewHint");

    [RelayCommand]
    private async Task RescanSourceAsync()
    {
        _sourceScanCts?.Cancel();
        _sourceScanCts?.Dispose();
        var cts = new CancellationTokenSource();
        _sourceScanCts = cts;

        var folder = SourceFolder;
        var target = TargetFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            ClearSourceView();
            return;
        }

        try
        {
            // Der Quellordner wird beim Tippen zeichenweise gemeldet — kurz
            // abwarten, statt jeden Zwischenstand zu durchsuchen.
            await Task.Delay(400, cts.Token);
            IsScanningSource = true;
            SourceSummaryText = Loc.Get("S.Msg.ReadingSource");

            var snapshot = await Task.Run(
                () => SourceSnapshotScanner.Scan(folder, target, null, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            PopulateSourceView(snapshot);
        }
        catch (OperationCanceledException)
        {
            // Neuer Scan oder Lauf gestartet.
        }
        catch (Exception ex)
        {
            ClearSourceView();
            SourceSummaryText = Loc.Format("S.Msg.SourceUnreadable", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_sourceScanCts, cts))
            {
                IsScanningSource = false;
            }
        }
    }

    private void ClearSourceView()
    {
        _sourceThumbnailCts?.Cancel();
        SourceFolders.Clear();
        SourceSummaryText = "";
        OnPropertyChanged(nameof(HasSourceSnapshot));
    }

    private void PopulateSourceView(SourceSnapshot snapshot)
    {
        // Only cancelled here, never renewed: the token provider hands out a
        // fresh one, so there is one place that creates it.
        _sourceThumbnailCts?.Cancel();
        SourceFolders.Clear();

        var ffmpeg = new FfmpegService(_settings.FfmpegPath);
        foreach (var folder in snapshot.Folders)
        {
            var folderVm = new ResultFolderViewModel(folder.Path, folder.DisplayName, folder.Start)
            {
                IsReadOnly = true,
                IsNew = false,
                // The current state is a snapshot and nothing else: no plan,
                // no actions, no status badges.
                IsExpanded = ShouldExpand(snapshot.Folders.Count),
            };

            foreach (var item in folder.Items)
            {
                folderVm.Add(new ResultItemViewModel(item)
                {
                    IsReadOnly = true,
                    CurrentPath = item.SourcePath,
                });
            }

            AttachThumbnailLoader(folderVm, ffmpeg, SourceThumbnailToken);
            folderVm.RefreshSummary();
            SourceFolders.Add(folderVm);
        }

        SourceSummaryText = snapshot.TotalFiles == 0
            ? Loc.Get("S.Msg.SourceEmpty")
            : Loc.Format("S.Msg.SourceSummary", snapshot.Folders.Count, snapshot.TotalFiles);
        OnPropertyChanged(nameof(HasSourceSnapshot));
    }

    // ------------------------------------------------------------------
    // Plan-Editor (Soll-Zustand) — alle Änderungen ohne Datei-I/O
    // ------------------------------------------------------------------

    [ObservableProperty]
    private string _planSummaryText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDuplicatesCommand))]
    private bool _isCheckingDuplicates;

    public bool IsPlanState => State == AppState.Plan;

    partial void OnStateChanged(AppState value)
    {
        OnPropertyChanged(nameof(IsPlanState));
        OnPropertyChanged(nameof(CanEditFilingSettings));
        OnPropertyChanged(nameof(CanEditRunSettings));
        OnPropertyChanged(nameof(CanEditSplitDateless));
        OnPropertyChanged(nameof(CanEditCollectUnmatched));
        OnPropertyChanged(nameof(CanEditUnmatchedName));
        OnPropertyChanged(nameof(CanEditWriteDescription));
        OnPropertyChanged(nameof(CanEditModel));
        OnPropertyChanged(nameof(ShowRunSettingsLockedHint));
        StartCommand.NotifyCanExecuteChanged();
        ApplyPlanCommand.NotifyCanExecuteChanged();
        CheckDuplicatesCommand.NotifyCanExecuteChanged();
    }

    private void PopulatePlan(OrganizePlan plan)
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        Folders.Clear();
        Warnings.Clear();
        _planJournal = null;

        foreach (var warning in plan.Warnings)
        {
            Warnings.Add(new WarningViewModel(warning));
        }

        var ffmpeg = new FfmpegService(_settings.FfmpegPath);
        foreach (var planned in plan.Folders)
        {
            var folderVm = new ResultFolderViewModel(planned.TargetPath, planned.Theme, planned.GroupStart)
            {
                IsPlanned = true,
                IsNew = planned.IsNew,
                IsCollection = planned.IsCollection,
                IsDateless = planned.IsDateless,
                InsideFolder = planned.InsideFolder,
                DatePrefix = planned.DatePrefix,
                HasManualName = planned.HasManualName,
                IsExpanded = ShouldExpand(plan.Folders.Count),
                SelectionChanged = OnFolderSelectionChanged,
            };

            foreach (var item in planned.Items)
            {
                folderVm.Add(new ResultItemViewModel(item.Item)
                {
                    IsPlanned = true,
                    PlanStatus = item.Status,
                    PlanReason = item.Reason,
                });
            }

            AttachThumbnailLoader(folderVm, ffmpeg, _thumbnailCts.Token);
            folderVm.RefreshSummary();
            Folders.Add(folderVm);
        }

        UpdatePlanSummary();
    }

    /// <summary>Materialisiert den aktuellen Bearbeitungsstand als Plan.
    /// Leere Ordner bleiben bewusst draußen — es wird kein leeres Verzeichnis
    /// angelegt.</summary>
    private OrganizePlan BuildPlan(bool selectedOnly = false)
    {
        var plan = new OrganizePlan
        {
            BaseFolder = _settings.TargetFolder,
            Hierarchy = _settings.TargetHierarchy,
            Mode = _settings.TransferMode,
            Warnings = [.. Warnings.Select(w => w.Source)],
        };

        var folders = PlannedFoldersWithItems();
        if (selectedOnly)
        {
            folders = [.. folders.Where(f => f.IsSelected)];
        }

        foreach (var folder in folders)
        {
            var planned = new PlannedFolder
            {
                TargetPath = folder.FolderPath,
                Theme = folder.Theme,
                DatePrefix = folder.DatePrefix,
                IsCollection = folder.IsCollection,
                IsDateless = folder.IsDateless,
                InsideFolder = folder.InsideFolder,
                IsNew = folder.IsNew,
                HasManualName = folder.HasManualName,
            };

            foreach (var item in folder.Items)
            {
                planned.Items.Add(new PlannedItem
                {
                    Item = item.Item,
                    Status = item.PlanStatus,
                    Reason = item.PlanReason,
                });
            }

            plan.Folders.Add(planned);
        }

        return plan;
    }

    /// <summary>Die Karten, die noch Plan sind. Nach einem Teil-Übernehmen
    /// stehen daneben Karten, die bereits auf der Platte liegen — die dürfen
    /// weder in einen weiteren Plan noch in seine Kennzahlen.</summary>
    private List<ResultFolderViewModel> PlannedFoldersWithItems() =>
        [.. Folders.Where(f => f.IsPlanned && f.ItemCount > 0)];

    /// <summary>Nach einem Teil-Übernehmen: Die betroffenen Karten sind kein
    /// Plan mehr, sondern Ergebnis. Die Dateien liegen im Ziel, also bekommen
    /// die Kacheln ihren neuen Pfad — daran hängen Vorschau, Umsortieren und
    /// "Im Explorer anzeigen".</summary>
    private void MarkFoldersApplied(OrganizeRunResult result)
    {
        foreach (var applied in result.Folders)
        {
            var card = Folders.FirstOrDefault(f => f.IsPlanned
                && string.Equals(f.FolderPath, applied.FolderPath, StringComparison.OrdinalIgnoreCase));
            if (card is null)
            {
                continue;
            }

            foreach (var op in applied.Operations)
            {
                // Dieselbe MediaItem-Instanz wandert durch Plan und Ausführung,
                // deshalb reicht der Referenzvergleich.
                if (card.Items.FirstOrDefault(i => ReferenceEquals(i.Item, op.Item)) is not { } item)
                {
                    continue;
                }

                item.CurrentPath = op.FinalPath;
                item.Status = op.Status;
                item.Error = op.Error;
                item.IsPlanned = false;
            }

            card.IsPlanned = false;
            card.IsNew = false;
            card.IsSelected = false;
            card.IsFrozen = true;
            card.RefreshSummary();
        }

        // Das Ergebnis trägt die Plan-Hinweise bereits mit sich und ergänzt die
        // der Ausführung; neu aufbauen ist deshalb richtig und nicht doppelt.
        Warnings.Clear();
        foreach (var warning in result.Warnings)
        {
            Warnings.Add(new WarningViewModel(warning));
        }

        UpdatePlanSummary();
    }

    /// <summary>Hängt das Protokoll eines Teil-Übernehmens an das vorige an, so
    /// dass "Rückgängig" alle Teile desselben Plans zusammen zurücknimmt.
    ///
    /// <para>Nur bei gleicher Betriebsart: Kopieren wird zurückgenommen, indem
    /// die Kopie gelöscht wird, Verschieben, indem die Datei zurückwandert. Ein
    /// Protokoll trägt genau eine davon — wer die Einstellung zwischen zwei
    /// Teilen umstellt, kann daher nur den zweiten Teil zurücknehmen.</para></summary>
    private static RunJournal? MergeJournals(RunJournal? previous, RunJournal? current)
    {
        if (current is null || previous is null || previous.Mode != current.Mode)
        {
            return current ?? previous;
        }

        current.Entries.InsertRange(0, previous.Entries);
        current.CreatedDirectories.InsertRange(0, previous.CreatedDirectories);
        current.Save();
        return current;
    }

    /// <summary>Schreibt Kollisions-/Duplikat-Status aus dem Plan zurück in die
    /// Oberfläche. Reihenfolge und Anzahl stimmen, weil der Plan direkt aus
    /// denselben Karten gebaut wurde.</summary>
    private void ApplyPlanStatus(OrganizePlan plan)
    {
        var folders = PlannedFoldersWithItems();
        for (var f = 0; f < plan.Folders.Count && f < folders.Count; f++)
        {
            var planned = plan.Folders[f];
            var folder = folders[f];
            folder.IsNew = planned.IsNew;

            for (var i = 0; i < planned.Items.Count && i < folder.Items.Count; i++)
            {
                folder.Items[i].PlanStatus = planned.Items[i].Status;
                folder.Items[i].PlanReason = planned.Items[i].Reason;
            }

            folder.RefreshSummary();
        }
    }

    /// <summary>Nach jeder Änderung am Plan: Datums-Präfixe und Zielpfade
    /// nachziehen, neu sortieren, Kollisionen billig neu bestimmen.</summary>
    private void RefreshPlan()
    {
        foreach (var folder in Folders)
        {
            // An applied card is not a plan any more: its files lie in that
            // folder, and a new target folder cannot move them there
            // retroactively.
            if (folder.IsFrozen)
            {
                continue;
            }

            folder.RecomputePath(_settings.TargetFolder, _settings.TargetHierarchy);
        }

        SortPlanFolders();

        var plan = BuildPlan();
        PlanConflicts.RefreshFolderState(plan);
        PlanConflicts.MarkNameConflicts(plan);
        ApplyPlanStatus(plan);
        UpdatePlanSummary();
    }

    /// <summary>Chronologisch; die datumslosen Ordner stehen am Ende, der
    /// Sammelordner ganz zuletzt.</summary>
    private void SortPlanFolders()
    {
        var sorted = Folders
            .OrderBy(f => f.IsCollection ? 2 : f.IsDateless ? 1 : 0)
            .ThenBy(f => f.GroupStart)
            .ToList();

        for (var target = 0; target < sorted.Count; target++)
        {
            var current = Folders.IndexOf(sorted[target]);
            if (current != target)
            {
                Folders.Move(current, target);
            }
        }
    }

    private void UpdatePlanSummary()
    {
        var folders = PlannedFoldersWithItems();
        var items = folders.SelectMany(f => f.Items).ToList();
        var duplicates = items.Count(i => i.IsPlannedDuplicate);
        var conflicts = items.Count(i => i.IsConflict);
        var verb = Loc.Get(IsCopyMode ? "S.Verb.Copy" : "S.Verb.Move");

        var text = Loc.Format(
            "S.Msg.PlanSummary",
            folders.Count, folders.Count(f => f.IsNew), items.Count, items.Count - duplicates, verb);
        if (duplicates > 0)
        {
            text += Loc.Format("S.Msg.PlanDuplicates", duplicates);
        }

        if (conflicts > 0)
        {
            text += Loc.Format("S.Msg.PlanConflicts", conflicts);
        }

        // Nur leere PLAN-Ordner. Nach einem Teil-Übernehmen stehen daneben
        // Karten, die bereits auf der Platte liegen — die sind nicht leer,
        // sondern fertig, und wurden als Differenz mitgezählt.
        var empty = Folders.Count(f => f.IsPlanned && f.ItemCount == 0);
        if (empty > 0)
        {
            text += Loc.Format("S.Msg.PlanEmptyFolders", empty);
        }

        PlanSummaryText = text;
    }

    private bool CanApplyPlan => State == AppState.Plan && !IsCheckingDuplicates;

    /// <summary>Wie viele Ordner für ein Teil-Übernehmen angehakt sind.</summary>
    public int SelectedFolderCount => PlannedFoldersWithItems().Count(f => f.IsSelected);

    public bool HasFolderSelection => SelectedFolderCount > 0;

    public string ApplyButtonText => HasFolderSelection
        ? Loc.Format("S.ApplySelected", SelectedFolderCount)
        : Loc.Get("S.Apply");

    public string ApplyButtonTip =>
        Loc.Get(HasFolderSelection ? "S.ApplySelectedTip" : "S.ApplyTip");

    /// <summary>Die Karten melden jede Änderung am Häkchen hierher — die
    /// Beschriftung des Übernehmen-Knopfes hängt daran.</summary>
    private void OnFolderSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedFolderCount));
        OnPropertyChanged(nameof(HasFolderSelection));
        OnPropertyChanged(nameof(ApplyButtonText));
        OnPropertyChanged(nameof(ApplyButtonTip));
    }

    /// <summary>Übernehmen: erst hier wird tatsächlich geschrieben.
    ///
    /// <para>Sind Ordner angehakt, wandern nur diese auf die Platte; der Rest
    /// bleibt als Plan stehen und lässt sich später übernehmen. Die
    /// übernommenen Karten hören auf, Plan zu sein — daran erkennt der nächste
    /// Durchgang, dass er sie nicht noch einmal anfassen darf.</para></summary>
    [RelayCommand(CanExecute = nameof(CanApplyPlan))]
    private async Task ApplyPlanAsync()
    {
        var partial = HasFolderSelection;
        var plan = BuildPlan(partial);
        if (plan.TotalFiles == 0)
        {
            StatusMessage = Loc.Get("S.Msg.PlanNoFiles");
            return;
        }

        RunError = null;
        StatusMessage = null;
        State = AppState.Running;
        PhaseTitle = Loc.Get(IsCopyMode ? "S.Phase.Copy" : "S.Phase.Move");
        ProgressMessage = "";
        ProgressIndeterminate = false;
        ProgressFraction = 0;
        _runCts = new CancellationTokenSource();

        var progress = CreateProgress();
        try
        {
            var executor = new PlanExecutor(_imagePreprocessor);

            // Vor dem Lauf sichern: Der Executor schreibt ein NEUES Protokoll
            // und überschreibt damit das vorige. Ohne das nähme "Rückgängig"
            // nach dem zweiten Teil-Übernehmen nur den zweiten Teil zurück.
            var previousJournal = _planJournal;
            var result = await Task.Run(() => executor.ExecuteAsync(
                plan, _settings.WriteDescriptionToExif, progress, _runCts.Token));

            if (partial)
            {
                MarkFoldersApplied(result);
            }
            else
            {
                PopulateResults(result);
            }

            _lastJournal = MergeJournals(previousJournal, RunJournal.Load());
            _planJournal = PlannedFoldersWithItems().Count > 0 ? _lastJournal : null;
            OnPropertyChanged(nameof(CanUndoLastRun));

            // Ist nichts Geplantes mehr übrig, ist der Plan abgearbeitet — dann
            // ist es dieselbe Lage wie nach einem Übernehmen ohne Auswahl.
            var stillPlanned = PlannedFoldersWithItems().Count > 0;
            if (!stillPlanned)
            {
                // Thaw the cards applied part by part: from here on this is the
                // result view, and there a move really moves the file.
                foreach (var folder in Folders)
                {
                    folder.IsFrozen = false;
                }
            }

            State = stillPlanned ? AppState.Plan : AppState.Results;
            OnFolderSelectionChanged();
            StatusMessage = Loc.Format("S.Msg.PlanApplied", result.TotalFiles, result.Folders.Count);
        }
        catch (Exception ex)
        {
            RunError = ex.Message;
            State = AppState.Plan;
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private void DiscardPlan()
    {
        _thumbnailCts?.Cancel();
        Folders.Clear();
        Warnings.Clear();
        _planJournal = null;
        PlanSummaryText = "";
        ShowSourceView = false;
        OnFolderSelectionChanged();
        StatusMessage = Loc.Get("S.Msg.PlanDiscarded");
        State = AppState.Idle;
        _ = RescanSourceAsync();
    }

    [RelayCommand]
    private void ToggleSourceView() => ShowSourceView = !ShowSourceView;

    /// <summary>Die beiden Segmente des Umschalters setzen den Zustand, statt
    /// ihn zu kippen: Ein zweiter Klick auf das bereits gewählte Segment darf
    /// nicht zur anderen Ansicht springen.</summary>
    [RelayCommand]
    private void ShowPlanView() => ShowSourceView = false;

    [RelayCommand]
    private void ShowSourceViewMode() => ShowSourceView = true;

    private bool CanCheckDuplicates => State == AppState.Plan && !IsCheckingDuplicates;

    /// <summary>Gründliche Prüfung auf Wunsch: vergleicht bei jeder
    /// Namenskollision die Inhalte per SHA-256. Standardmäßig bleibt es bei
    /// der billigen Namensprüfung, damit die Vorschau schnell bleibt.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckDuplicates))]
    private async Task CheckDuplicatesAsync()
    {
        var plan = BuildPlan();
        if (plan.ConflictCount == 0 && plan.DuplicateCount == 0)
        {
            StatusMessage = Loc.Get("S.Msg.NoCollisions");
            return;
        }

        IsCheckingDuplicates = true;
        ApplyPlanCommand.NotifyCanExecuteChanged();
        _runCts = new CancellationTokenSource();
        var progress = CreateProgress();

        try
        {
            var duplicates = await Task.Run(
                () => PlanConflicts.MarkContentDuplicates(plan, progress, _runCts.Token));
            ApplyPlanStatus(plan);
            UpdatePlanSummary();
            StatusMessage = duplicates == 0
                ? Loc.Get("S.Msg.NoDuplicates")
                : Loc.Format("S.Msg.DuplicatesFound", duplicates);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.Get("S.Msg.DupCheckCancelled");
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("S.Msg.DupCheckFailed", ex.Message);
        }
        finally
        {
            IsCheckingDuplicates = false;
            ApplyPlanCommand.NotifyCanExecuteChanged();
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    // ------------------------------------------------------------------
    // Letzten Lauf rückgängig machen
    // ------------------------------------------------------------------

    public bool CanUndoLastRun => _lastJournal is { IsEmpty: false };

    /// <summary>Text der Sicherheitsabfrage — beschreibt genau, was passiert.</summary>
    public string UndoQuestionText
    {
        get
        {
            if (_lastJournal is not { } journal)
            {
                return "";
            }

            var what = Loc.Format(
                journal.Mode == TransferMode.Copy ? "S.Msg.UndoCopies" : "S.Msg.UndoMoves",
                journal.Entries.Count);

            return Loc.Format("S.Msg.UndoQuestion", journal.CompletedLocal.ToString("g"))
                   + Environment.NewLine + Environment.NewLine
                   + what + Environment.NewLine
                   + Loc.Get("S.Msg.UndoRemovesEmpty");
        }
    }

    public async Task UndoLastRunAsync()
    {
        if (_lastJournal is not { } journal)
        {
            return;
        }

        State = AppState.Running;
        PhaseTitle = Loc.Get("S.Phase.Undo");
        ProgressIndeterminate = false;
        ProgressFraction = 0;
        _runCts = new CancellationTokenSource();
        var progress = CreateProgress();

        try
        {
            var outcome = await Task.Run(() => journal.Undo(progress, _runCts.Token));
            RunJournal.Clear();
            _lastJournal = null;
            OnPropertyChanged(nameof(CanUndoLastRun));

            _thumbnailCts?.Cancel();
            Folders.Clear();
            Warnings.Clear();
            foreach (var note in outcome.Notes.Distinct().Take(20))
            {
                Warnings.Add(new WarningViewModel(note));
            }

            var parts = new List<string>();
            if (outcome.Restored > 0)
            {
                parts.Add(Loc.Format("S.Msg.UndoRestored", outcome.Restored));
            }

            if (outcome.Deleted > 0)
            {
                parts.Add(Loc.Format("S.Msg.UndoDeleted", outcome.Deleted));
            }

            if (outcome.Skipped > 0)
            {
                parts.Add(Loc.Format("S.Msg.UndoSkipped", outcome.Skipped));
            }

            if (outcome.Failed > 0)
            {
                parts.Add(Loc.Format("S.Msg.UndoFailed", outcome.Failed));
            }

            if (outcome.RemovedDirectories > 0)
            {
                parts.Add(Loc.Format("S.Msg.UndoRemovedDirs", outcome.RemovedDirectories));
            }

            StatusMessage = parts.Count == 0
                ? Loc.Get("S.Msg.UndoNothing")
                : Loc.Format("S.Msg.UndoDone", string.Join(", ", parts));
            State = AppState.Idle;
            _ = RescanSourceAsync();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.Get("S.Msg.UndoCancelled");
            State = AppState.Results;
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("S.Msg.UndoError", ex.Message);
            State = AppState.Results;
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    // ------------------------------------------------------------------
    // Nachträgliche Korrekturen (Ergebnisansicht)
    // ------------------------------------------------------------------

    /// <summary>Verschiebt eine Datei in einen anderen bestehenden Zielordner —
    /// Dateisystem-Operation und UI werden konsistent nachgezogen.</summary>
    public async Task MoveItemToFolderAsync(ResultItemViewModel item, ResultFolderViewModel targetFolder)
    {
        if (!item.CanRelocate || item.ParentFolder == targetFolder)
        {
            return;
        }

        try
        {
            var (finalPath, status) = await Task.Run(() =>
                FileOrganizerService.TransferFile(item.CurrentPath!, targetFolder.FolderPath, TransferMode.Move));

            if (status == FileOpStatus.SkippedDuplicate)
            {
                StatusMessage = Loc.Format("S.Msg.AlreadyThere", item.FileName, targetFolder.DisplayName);
                return;
            }

            // Protokoll nachziehen, damit "Rückgängig" weiter stimmt.
            if (item.CurrentPath is { } previous && finalPath is not null
                && _lastJournal?.Relocate(previous, finalPath) == true)
            {
                _lastJournal.Save();
            }

            item.ParentFolder?.Remove(item);
            item.CurrentPath = finalPath;
            targetFolder.Add(item);
            StatusMessage = Loc.Format("S.Msg.FileMoved", item.FileName, targetFolder.DisplayName);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("S.Msg.MoveFailed", ex.Message);
        }
    }

    /// <summary>Verschiebt eine oder mehrere Dateien in einen neuen
    /// thematischen Zielordner (Datum der chronologisch ersten Datei).</summary>
    public async Task MoveItemsToNewFolderAsync(IReadOnlyList<ResultItemViewModel> items, string theme)
    {
        var movable = items.Where(i => i.CanRelocate).OrderBy(i => i.Item.TakenLocal).ToList();
        if (movable.Count == 0)
        {
            return;
        }

        var sanitized = GroupNamingService.Sanitize(theme);
        var start = movable[0].Item.TakenLocal;

        if (State == AppState.Plan)
        {
            var prefix = PlanPaths.Prefix(start);
            var path = PlanPaths.BuildTargetPath(
                _settings.TargetFolder, _settings.TargetHierarchy, prefix, sanitized, false);
            var folder = Folders.FirstOrDefault(f =>
                f.FolderPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                folder = new ResultFolderViewModel(path, sanitized, start)
                {
                    IsPlanned = true,
                    DatePrefix = prefix,
                    IsNew = !Directory.Exists(path),
                };
                Folders.Add(folder);
            }

            MovePlannedItems(movable, folder);
            return;
        }

        var group = new MediaGroup { Items = [movable[0].Item], Theme = sanitized };
        var targetDir = FileOrganizerService.BuildTargetDirectory(
            _settings.TargetFolder, _settings.TargetHierarchy, group);

        var existing = Folders.FirstOrDefault(f =>
            f.FolderPath.Equals(targetDir, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ResultFolderViewModel(targetDir, sanitized, start.Date);
            var insertAt = Folders.TakeWhile(f => f.GroupStart <= existing.GroupStart).Count();
            Folders.Insert(insertAt, existing);
        }

        await MoveItemsToFolderAsync(movable, existing);
    }

    public IReadOnlyList<ResultItemViewModel> SelectedItems =>
        Folders.SelectMany(f => f.Items).Where(i => i.IsSelected).ToList();

    /// <summary>Alle Items in Anzeigereihenfolge — Basis für Shift-Auswahl.</summary>
    public List<ResultItemViewModel> AllItemsInOrder =>
        Folders.SelectMany(f => f.Items).ToList();

    public void ClearSelection()
    {
        foreach (var item in Folders.SelectMany(f => f.Items))
        {
            item.IsSelected = false;
        }
    }

    /// <summary>Markiert den Bereich zwischen Anker und Ziel (Shift-Klick).</summary>
    public void SelectRange(ResultItemViewModel? anchor, ResultItemViewModel target)
    {
        var all = AllItemsInOrder;
        var from = anchor is null ? -1 : all.IndexOf(anchor);
        var to = all.IndexOf(target);
        if (from < 0 || to < 0)
        {
            ClearSelection();
            target.IsSelected = true;
            return;
        }

        var (start, end) = from <= to ? (from, to) : (to, from);
        ClearSelection();
        for (var i = start; i <= end; i++)
        {
            all[i].IsSelected = true;
        }
    }

    /// <summary>Legt einen leeren Zielordner an (Datum + Thema). Im Plan
    /// entsteht dabei kein Verzeichnis — der Ordner wird erst beim Übernehmen
    /// angelegt, und nur, wenn dann Dateien darin liegen.</summary>
    public void CreateEmptyFolder(string theme, DateTime date)
    {
        var sanitized = GroupNamingService.Sanitize(theme);
        var prefix = PlanPaths.Prefix(date);
        var targetDir = PlanPaths.BuildTargetPath(
            _settings.TargetFolder, _settings.TargetHierarchy, prefix, sanitized, false);

        if (Folders.Any(f => f.FolderPath.Equals(targetDir, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = Loc.Get("S.Msg.FolderExists");
            return;
        }

        if (State == AppState.Plan)
        {
            var planned = new ResultFolderViewModel(targetDir, sanitized, date.Date)
            {
                IsPlanned = true,
                DatePrefix = prefix,
                HasManualName = true,
                IsNew = !Directory.Exists(targetDir),
            };
            Folders.Add(planned);
            SortPlanFolders();
            UpdatePlanSummary();
            StatusMessage = Loc.Format("S.Msg.FolderPlanned", planned.DisplayName);
            return;
        }

        try
        {
            Directory.CreateDirectory(targetDir);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("S.Msg.FolderCreateFailed", ex.Message);
            return;
        }

        var vm = new ResultFolderViewModel(targetDir, sanitized, date.Date);
        var insertAt = Folders.TakeWhile(f => f.GroupStart <= vm.GroupStart).Count();
        Folders.Insert(insertAt, vm);
        StatusMessage = Loc.Format("S.Msg.FolderCreated", vm.DisplayName);
    }

    /// <summary>Entfernt einen leeren Ordner aus Ansicht und (nach dem
    /// Übernehmen) aus dem Dateisystem.</summary>
    [RelayCommand]
    private void DeleteFolder(ResultFolderViewModel folder)
    {
        if (folder.Items.Count > 0)
        {
            StatusMessage = Loc.Get("S.Msg.OnlyEmptyDeletable");
            return;
        }

        if (State == AppState.Plan)
        {
            Folders.Remove(folder);
            UpdatePlanSummary();
            StatusMessage = Loc.Format("S.Msg.FolderRemovedFromPlan", folder.DisplayName);
            return;
        }

        try
        {
            if (Directory.Exists(folder.FolderPath)
                && Directory.EnumerateFileSystemEntries(folder.FolderPath).Any())
            {
                StatusMessage = Loc.Format("S.Msg.FolderNotEmpty", folder.DisplayName);
                return;
            }

            if (Directory.Exists(folder.FolderPath))
            {
                Directory.Delete(folder.FolderPath);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("S.Msg.DeleteFailed", ex.Message);
            return;
        }

        Folders.Remove(folder);
        StatusMessage = Loc.Format("S.Msg.EmptyFolderDeleted", folder.DisplayName);
    }

    /// <summary>Entfernt alle leeren Ordner auf einmal.</summary>
    [RelayCommand]
    private void RemoveEmptyFolders()
    {
        var empty = Folders.Where(f => f.Items.Count == 0).ToList();
        var removed = 0;

        if (State == AppState.Plan)
        {
            foreach (var folder in empty)
            {
                Folders.Remove(folder);
                removed++;
            }

            UpdatePlanSummary();
            StatusMessage = removed == 0
                ? Loc.Get("S.Msg.NoEmptyInPlan")
                : Loc.Format("S.Msg.EmptyRemovedFromPlan", removed);
            return;
        }

        foreach (var folder in empty)
        {
            try
            {
                if (Directory.Exists(folder.FolderPath)
                    && Directory.EnumerateFileSystemEntries(folder.FolderPath).Any())
                {
                    continue;
                }

                if (Directory.Exists(folder.FolderPath))
                {
                    Directory.Delete(folder.FolderPath);
                }

                Folders.Remove(folder);
                removed++;
            }
            catch
            {
                // Nicht löschbare Ordner überspringen.
            }
        }

        StatusMessage = removed == 0
            ? Loc.Get("S.Msg.NoEmptyFound")
            : Loc.Format("S.Msg.EmptyDeleted", removed);
    }

    /// <summary>Verschiebt mehrere Dateien (Auswahl/Drag &amp; Drop) in einen
    /// anderen Zielordner.</summary>
    public async Task MoveItemsToFolderAsync(
        IReadOnlyList<ResultItemViewModel> items, ResultFolderViewModel targetFolder)
    {
        // Im Plan kostet das Umsortieren nichts — es wird nur umgehängt.
        if (State == AppState.Plan)
        {
            MovePlannedItems(items, targetFolder);
            return;
        }

        var movable = items.Where(i => i.CanRelocate && i.ParentFolder != targetFolder).ToList();
        foreach (var item in movable)
        {
            await MoveItemToFolderAsync(item, targetFolder);
        }

        if (movable.Count > 1)
        {
            StatusMessage = Loc.Format("S.Msg.FilesMoved", movable.Count, targetFolder.DisplayName);
        }
    }

    /// <summary>Umsortieren im Plan: reines Umhängen, kein Dateisystemzugriff.</summary>
    private void MovePlannedItems(
        IReadOnlyList<ResultItemViewModel> items, ResultFolderViewModel targetFolder)
    {
        var moved = items.Where(i => i.CanRelocate && i.ParentFolder != targetFolder).ToList();
        if (moved.Count == 0)
        {
            return;
        }

        foreach (var item in moved)
        {
            item.ParentFolder?.Remove(item);
            targetFolder.Add(item);
        }

        RefreshPlan();
        StatusMessage = moved.Count == 1
            ? Loc.Format("S.Msg.MovedOne", moved[0].FileName, targetFolder.DisplayName)
            : Loc.Format("S.Msg.MovedMany", moved.Count, targetFolder.DisplayName);
    }

    /// <summary>Erkennt Personen auf den bereits einsortierten Dateien und
    /// benennt die Ordner daraufhin neu. Löst das Henne-Ei-Problem: Wer
    /// Personen erst nach dem Lauf zuordnet, muss nicht alles neu verarbeiten
    /// — es werden KEINE Dateien bewegt und keine Bilder neu analysiert, nur
    /// Gesichter erkannt (~50 ms/Bild) und die Ordnernamen neu bestimmt.</summary>
    [RelayCommand]
    private async Task RenameWithPeopleAsync()
    {
        var faceService = TryCreateFaceService();
        if (faceService is null)
        {
            return;
        }

        var personStore = PersonStore.Load();
        if (personStore.Persons.Count == 0)
        {
            StatusMessage = Loc.Get("S.Msg.NoPeopleKnown");
            faceService.Dispose();
            return;
        }

        // Im Plan arbeitet der Vorgang auf den Quelldateien und schreibt nur
        // neue Ordnernamen in den Plan — es wird nichts bewegt.
        var returnState = State == AppState.Plan ? AppState.Plan : AppState.Results;
        State = AppState.Running;
        PhaseTitle = Loc.Get("S.Phase.RecognizePeople");
        ProgressIndeterminate = false;
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;

        var folders = Folders.ToList();
        try
        {
            using (faceService)
            {
                // 1. Gesichter auf allen einsortierten Bildern erkennen.
                var items = folders
                    .SelectMany(f => f.Items)
                    .Where(i => !i.IsVideo && File.Exists(i.PathOnDisk))
                    .ToList();

                await Task.Run(() =>
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var item = items[i];
                        item.Item.RecognizedPeople.Clear();
                        item.Item.RecognizedPeopleWithPosition.Clear();
                        item.Item.UnidentifiedPositions.Clear();
                        try
                        {
                            var detected = faceService.DetectFaces(item.PathOnDisk);
                            item.Item.DetectedFaceCount = detected.Count;
                            foreach (var face in detected.OrderBy(f => f.RelativeCenterX))
                            {
                                var position = face.PositionHint(_settings.OutputLanguage);
                                var match = FaceService.Match(face.Embedding, personStore.Persons);
                                if (match is not null && !item.Item.RecognizedPeople.Contains(match.Person.Name))
                                {
                                    item.Item.RecognizedPeople.Add(match.Person.Name);
                                    item.Item.RecognizedPeopleWithPosition.Add(
                                        $"{match.Person.Name} ({position})");
                                }
                                else
                                {
                                    item.Item.UnidentifiedPositions.Add(position);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            // Einzelne Bilder dürfen den Vorgang nicht stoppen.
                        }

                        var done = i + 1;
                        ProgressFraction = done / (double)Math.Max(1, items.Count);
                        ProgressMessage = Loc.Format("S.Msg.FaceProgress", done, items.Count);
                    }
                }, token);

                var recognized = items.Count(i => i.Item.RecognizedPeople.Count > 0);
                if (recognized == 0)
                {
                    StatusMessage = Loc.Get("S.Msg.NoPeopleRecognized");
                    State = returnState;
                    return;
                }

                // 2. Betroffene Ordner neu benennen (nur Namensfindung, kein
                //    erneutes Beschreiben, keine Dateiverschiebung).
                PhaseTitle = Loc.Get("S.Phase.UpdateFolderNames");
                var useLlm = SelectedGroupingMode.Value != GroupingMode.ExifOnly;
                OllamaClient? ollama = null;
                var renamed = 0;
                try
                {
                    if (useLlm)
                    {
                        ollama = new OllamaClient(_settings.OllamaEndpoint);
                    }

                    var naming = new GroupNamingService(
                        ollama, _settings.ModelTag, _imagePreprocessor,
                        new FfmpegService(_settings.FfmpegPath),
                        _settings.HomeZones, KnowledgeStore.Load().Facts,
                        prompts: Prompts.For(_settings.OutputLanguage));

                    for (var i = 0; i < folders.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var folder = folders[i];
                        if (folder.Items.Count == 0
                            || folder.Items.All(it => it.Item.RecognizedPeople.Count == 0))
                        {
                            continue;
                        }

                        // Der Sammelordner behält seinen festen Namen, und ein
                        // von Hand vergebener Name ist Handarbeit. Beides hat
                        // hier gefehlt: Weil unten eine nackte MediaGroup ohne
                        // IsCollection entstand, griff die Schutzregel im
                        // Namensdienst nicht — aus "Mixed" wurde der Titel
                        // eines beliebigen enthaltenen Bildes.
                        if (folder.IsDateless || folder.HasManualName)
                        {
                            continue;
                        }

                        ProgressFraction = (i + 1) / (double)folders.Count;
                        ProgressMessage = Loc.Format("S.Msg.NamingFolder", folder.DisplayName);

                        var group = new MediaGroup
                        {
                            Items = folder.Items.Select(it => it.Item).OrderBy(it => it.TakenLocal).ToList(),
                        };
                        await naming.NameGroupsAsync([group], new Progress<PipelineProgress>(), token);

                        if (!group.Theme.Equals(folder.ThemePart, StringComparison.Ordinal))
                        {
                            if (returnState == AppState.Plan)
                            {
                                // Nur der Plan ändert sich; HasManualName bleibt
                                // aus, weil das keine Handarbeit ist.
                                folder.Theme = GroupNamingService.Sanitize(group.Theme);
                            }
                            else
                            {
                                RenameFolder(folder, group.Theme);
                            }

                            renamed++;
                        }
                    }
                }
                finally
                {
                    ollama?.Dispose();
                }

                StatusMessage = Loc.Format("S.Msg.RecognizedAndRenamed", recognized, renamed);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.Get("S.Msg.OperationCancelled");
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("S.Msg.OperationFailed", ex.Message);
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            State = returnState;
            if (returnState == AppState.Plan)
            {
                RefreshPlan();
            }
        }
    }

    /// <summary>Benennt einen Zielordner um (Thema-Anteil; das Datums-Präfix
    /// bleibt) — Dateisystem und UI werden konsistent nachgezogen.</summary>
    public void RenameFolder(ResultFolderViewModel folder, string newTheme)
    {
        var sanitized = GroupNamingService.Sanitize(newTheme);
        var name = folder.DisplayName;
        var underscore = name.IndexOf('_');
        var prefix = underscore > 0 ? name[..underscore] : folder.GroupStart.ToString("yyyyMMdd");
        var newName = PlanPaths.FolderName(prefix, sanitized, folder.IsDateless);
        if (newName.Equals(name, StringComparison.Ordinal))
        {
            return;
        }

        if (State == AppState.Plan)
        {
            var newPlanPath = PlanPaths.BuildTargetPath(
                _settings.TargetFolder, _settings.TargetHierarchy,
                folder.DatePrefix, sanitized, folder.IsDateless, folder.InsideFolder);
            if (Folders.Any(f => f != folder
                                 && f.FolderPath.Equals(newPlanPath, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = Loc.Format("S.Msg.RenameTakenInPlan", newName);
                return;
            }

            folder.Theme = sanitized;

            // Ab jetzt wird das Datums-Präfix nicht mehr automatisch
            // nachgezogen — die Handarbeit hat Vorrang.
            folder.HasManualName = true;
            RefreshPlan();
            StatusMessage = Loc.Format("S.Msg.RenamedInPlan", folder.DisplayName);
            return;
        }

        var parent = Path.GetDirectoryName(folder.FolderPath)!;
        var newPath = Path.Combine(parent, newName);
        if (Directory.Exists(newPath) || File.Exists(newPath))
        {
            StatusMessage = Loc.Format("S.Msg.RenameTaken", newName);
            return;
        }

        try
        {
            Directory.Move(folder.FolderPath, newPath);
        }
        catch (Exception ex)
        {
            StatusMessage = Loc.Format("S.Msg.RenameFailed", ex.Message);
            return;
        }

        var oldPath = folder.FolderPath;
        folder.FolderPath = newPath;
        folder.Theme = sanitized;
        foreach (var item in folder.Items)
        {
            if (item.CurrentPath is { } current
                && current.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase))
            {
                var moved = Path.Combine(newPath, Path.GetFileName(current));

                // Protokoll nachziehen, damit "Rückgängig" weiter stimmt.
                if (_lastJournal?.Relocate(current, moved) == true)
                {
                    _lastJournal.Save();
                }

                item.CurrentPath = moved;
            }
        }

        StatusMessage = Loc.Format("S.Msg.Renamed", newName);
    }

    /// <summary>Öffnet den Ordner im Explorer. Ein erst geplanter Ordner
    /// existiert noch nicht — dann sagt die Statuszeile das, statt dass der
    /// Klick ins Leere geht.</summary>
    [RelayCommand]
    private void OpenFolder(ResultFolderViewModel folder)
    {
        if (Directory.Exists(folder.FolderPath))
        {
            Process.Start("explorer.exe", $"\"{folder.FolderPath}\"");
            return;
        }

        StatusMessage = Loc.Format("S.Msg.FolderNotYet", folder.DisplayName);
    }
}
