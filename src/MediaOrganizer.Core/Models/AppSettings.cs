namespace MediaOrganizer.Core.Models;

/// <summary>The numbers are pinned because settings are stored as numbers: a
/// removed member must not shift the others, or a saved "1" would silently mean
/// something else after an update. <b>0 used to be "LlmAuto"</b> — the model
/// deciding on its own, without the counter-check that the combined mode does
/// afterwards. It was the same thing the combined mode does, only worse, and a
/// stored 0 is therefore read as <see cref="Combined"/>.</summary>
public enum GroupingMode
{
    /// <summary>EXIF and container metadata only, no model call.</summary>
    ExifOnly = 1,

    /// <summary>EXIF provides the base groups, the model refines them by
    /// topic.</summary>
    Combined = 2,
}

public enum TransferMode
{
    Move,
    Copy,
}

/// <summary>The language of the user interface itself.
///
/// <para>Separate from <see cref="OutputLanguage"/> on purpose: someone can
/// perfectly well want English buttons over a German photo collection, or the
/// other way round. Tying both to one setting would force a choice nobody
/// asked for.</para></summary>
public enum UiLanguage
{
    /// <summary>Follow Windows. The default, because it is right without
    /// anyone having to touch a setting.</summary>
    System,

    German,

    English,
}

/// <summary>The language the model answers in — and therefore the language of
/// folder names and image descriptions.
///
/// <para>Separate from the user interface language on purpose. The interface is
/// English; a collection being sorted usually is not. Someone sorting German
/// family photos wants <c>20260701_Urlaub Sylt</c> on disk no matter what
/// language the buttons are in.</para>
///
/// <para>Switching this does not invalidate anything: the language is part of
/// the analysis cache key, so descriptions in both languages live side by side
/// and switching back finds the old ones still there.</para></summary>
public enum OutputLanguage
{
    /// <summary>Default. What the application shipped with, and what every
    /// existing cache entry and target folder was produced in.</summary>
    German,

    English,
}

public enum TargetHierarchy
{
    /// <summary>base/20260701_Topic</summary>
    Flat,

    /// <summary>base/2026/20260701_Topic</summary>
    Year,

    /// <summary>base/2026/07/20260701_Topic</summary>
    YearMonth,
}

/// <summary>What a named zone stands for. Without this distinction every zone
/// counted as home — so a zone called "holiday" suppressed exactly the outing
/// logic it was created for.</summary>
public enum ZoneKind
{
    /// <summary>Everyday life: shots here are not travel. Different
    /// activities at the same place stay separate events, and the place does
    /// not belong in the folder name.</summary>
    Home,

    /// <summary>A recurring holiday or outing place (holiday flat, campsite).
    /// Does NOT count as home: shots here belong to an outing, and the name
    /// may appear in the title.</summary>
    Vacation,

    /// <summary>Another named place the model should know about (nursery,
    /// sports club). Like a holiday place, not home.</summary>
    Place,
}

/// <summary>A named zone, for example "Zuhause", "Oma &amp; Opa" or a holiday
/// flat. What it does depends on <see cref="Kind"/>.
///
/// <para>The class name dates from when only home zones existed. It stays so
/// that existing settings files keep deserialising.</para></summary>
public sealed class HomeZone
{
    public string Name { get; set; } = "Zuhause";

    /// <summary>Deliberately <see cref="ZoneKind.Home"/> by default: zones in
    /// older settings files carry no such field, and every one of them was
    /// meant as a home zone.</summary>
    public ZoneKind Kind { get; set; } = ZoneKind.Home;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public double RadiusKm { get; set; } = 0.7;

    public GeoPoint Center => new(Latitude, Longitude);

    /// <summary>Untergrenze des Radius — 20 m, etwa die Streuung eines
    /// GPS-Empfängers. Sie fängt nur den unsinnigen Wert 0 ab.
    ///
    /// <para>Hier standen einmal 200 m. Wer seine Zone bewusst auf 100 m
    /// stellte, um genau die eigene Wohnung zu treffen, bekam trotzdem 200 —
    /// eine stillschweigend überschriebene Eingabe.</para></summary>
    private const double MinRadiusKm = 0.02;

    /// <summary>Liegt dieser Punkt in der Zone? Die eine Stelle, an der das
    /// beantwortet wird: Vorher stand derselbe Vergleich sechsmal im Code, und
    /// eine Änderung daran hätte man leicht an fünf Stellen vergessen.</summary>
    public bool Covers(GeoPoint point) =>
        Center.DistanceKm(point) <= Math.Max(MinRadiusKm, RadiusKm);
}

/// <summary>The persisted application settings, written to
/// <c>%AppData%\MediaOrganizer\settings.json</c>.
///
/// <para>Every default has to reproduce the behaviour that came before it: a
/// field missing from an existing settings file falls back to the initial
/// value, and that must not change anything silently.</para></summary>
public sealed class AppSettings
{
    public string SourceFolder { get; set; } = "";

    public string TargetFolder { get; set; } = "";

    public TransferMode TransferMode { get; set; } = TransferMode.Move;

    public TargetHierarchy TargetHierarchy { get; set; } = TargetHierarchy.Flat;

    /// <summary>Split the dateless folders — the collecting folder and the
    /// everyday folder — along <see cref="TargetHierarchy"/> instead of keeping
    /// one of each for the whole collection: <c>2026\Vermischt</c> and
    /// <c>2026\Zuhause</c> rather than <c>Vermischt</c> and <c>Zuhause</c>
    /// beside the year levels.
    ///
    /// <para>The parts keep their name and stay without a date prefix; only
    /// their place in the tree changes. One collecting folder over ten thousand
    /// shots is a folder nobody opens a second time.</para>
    ///
    /// <para>On by default: without the model the collecting folder holds most
    /// of the collection — measured 5024 of 6039 files — and one folder of that
    /// size is one nobody opens twice. With
    /// <see cref="TargetHierarchy.Flat"/> there is no level to split along, so
    /// the setting does nothing and the run says so.</para></summary>
    public bool SplitDatelessByHierarchy { get; set; } = true;

    public GroupingMode GroupingMode { get; set; } = GroupingMode.Combined;

    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    /// <summary>The one model used for image analysis, grouping and
    /// naming.</summary>
    public string ModelTag { get; set; } = "gemma4:12b";

    /// <summary>Language of the user interface. See
    /// <see cref="Models.UiLanguage"/>.
    ///
    /// <para><see cref="Models.UiLanguage.System"/> by default: an existing
    /// settings file carries no such field, and following Windows is right for
    /// almost everyone.</para></summary>
    public UiLanguage UiLanguage { get; set; } = UiLanguage.System;

    /// <summary>Language of the folder names and descriptions the model
    /// produces. See <see cref="Models.OutputLanguage"/>.
    ///
    /// <para>German by default: an existing settings file carries no such field,
    /// and everything those installations have already sorted is German.</para></summary>
    public OutputLanguage OutputLanguage { get; set; } = OutputLanguage.German;

    /// <summary>Put shots that fit no event — no relation to one, single
    /// images — into a collecting folder rather than into event folders. Only
    /// takes effect in the modes that use the model.</summary>
    public bool CollectUnmatched { get; set; } = true;

    /// <summary>Name of the one collecting folder, which sits directly in the
    /// base folder.</summary>
    public string UnmatchedFolderName { get; set; } = "Vermischt";

    /// <summary>How many shots a group needs before it gets a folder of its
    /// own. Smaller groups go to the collecting folder. 1 switches the check
    /// off, so every group gets a folder even with a single image.</summary>
    public int MinItemsPerFolder { get; set; } = 2;

    /// <summary>Leave a shot out of the run when the same picture is already in
    /// it — the WhatsApp copy beside the original, or the same file in two
    /// source folders. The better copy is sorted, the other stays untouched
    /// where it is; nothing is deleted.
    ///
    /// <para>Only pictures proven identical are dropped: same aspect ratio and
    /// a pixel comparison. Measured on one collection, 1346 of 5471 images were
    /// such copies.</para></summary>
    public bool SkipDuplicates { get; set; } = true;

    /// <summary>Radius in kilometres within which shots count as being "in the
    /// same place".
    ///
    /// <para>A shot further than this from the GPS centre of its own group,
    /// but within the radius of another group in the same time window, moves
    /// there — a counter-check against the model's decision on content. 0
    /// switches it off.</para></summary>
    public double GpsGroupRadiusKm { get; set; } = 2.0;

    /// <summary>Radius in kilometres within which neighbouring groups count as
    /// the same outing and are merged.
    ///
    /// <para>The model likes to split a trip by activity — to the user that is
    /// ONE outing. Applies only away from home, never to shots inside a home
    /// zone. 0 switches the merging off.</para></summary>
    public double RegionMergeRadiusKm { get; set; } = 10.0;

    /// <summary>Local face recognition: recognised people are given to the
    /// model as fact instead of leaving it to guess.</summary>
    public bool FaceRecognitionEnabled { get; set; }

    /// <summary>Also write generated descriptions into the image file (XMP
    /// <c>dc:description</c>, losslessly). Off by default, because it modifies
    /// the user's original files.
    ///
    /// <para>The name still says EXIF because the stored settings key does —
    /// the two EXIF fields it used to write are gone, and renaming the property
    /// would silently reset the setting for everyone who already has it on.</para></summary>
    public bool WriteDescriptionToExif { get; set; }

    /// <summary>Cache descriptions per file hash so that repeat runs skip
    /// images already analysed.</summary>
    public bool UseAnalysisCache { get; set; } = true;

    /// <summary>How many images go to Ollama for analysis at the same time.
    ///
    /// <para>The biggest speed lever in a run. Measured on 80 real photos with
    /// gemma4:26b and the cache off: 1594 ms per image serially against
    /// 1309 ms with four at a time. These are exactly the same calls — the
    /// results do not change.</para>
    ///
    /// <para>Ollama itself would give a factor of 2.2 on the same images; why
    /// the application only gets part of that is still open.</para>
    ///
    /// <para>1 restores the earlier, strictly serial behaviour. Values that
    /// are too high cost VRAM for each concurrent request's context and can
    /// push an already tight model onto the CPU.</para></summary>
    public int MaxParallelAnalyses { get; set; } = 4;

    /// <summary>Analyse a burst only once: shots from the same camera taken
    /// within seconds at the same place take the description of the first.
    ///
    /// <para>On by default. Measured on 80 real photos, 24 of the 80 were
    /// taken from bursts — the analysis phase fell from 1309 ms to 921 ms per
    /// image, with the same grouping coming out.</para>
    ///
    /// <para>It stays a bet that a burst shows the same thing. It is narrowly
    /// guarded (same camera, a few seconds, same place, same recognised
    /// people, a real EXIF timestamp, no note) — unlike the earlier and much
    /// broader inheritance between "similar" images, which proved unreliable
    /// on real photos. Anyone who needs a description of every single shot
    /// switches it off.</para></summary>
    public bool ReuseBurstDescriptions { get; set; } = true;

    /// <summary>The longest pause without a shot that still counts as one
    /// thing, in days — <b>the single time boundary of the application</b>. It
    /// separates two visits to the same place (<c>SplitByPause</c>), it decides
    /// when a place counts as visited a SECOND time (<c>Geo.PlaceIndex</c>, and
    /// with it everyday against trip), and it bounds how far apart two groups
    /// may be to count as the same outing (<c>MergeSameRegion</c>).
    ///
    /// <para>One day by default. Raising it keeps a holiday with quiet days in
    /// one folder — but it also merges visits: at seven days a home
    /// photographed weekly collapses into a single visit and would look like a
    /// trip. Small is the safe direction.</para></summary>
    public double MaxEventGapDays { get; set; } = 1;

    /// <summary>How many preview tiles stand side by side — the zoom slider
    /// above the image groups. Their size follows from it and from the width of
    /// the window, so that a row always fills the whole width; a size in pixels
    /// could not do that and left a ragged edge.</summary>
    public int ThumbnailsPerRow { get; set; } = 8;

    /// <summary>Optional explicit path to ffmpeg.exe. Empty means search the
    /// PATH.</summary>
    public string FfmpegPath { get; set; } = "";

    /// <summary>Playback volume of the viewer, 0 to 1. Full by default: a video
    /// opened for review is meant to be heard, and a slider that starts silent
    /// looks like a broken player.</summary>
    public double PlaybackVolume { get; set; } = 1.0;

    /// <summary>Home zones for the context classification, which decides
    /// everyday life against travel.</summary>
    public List<HomeZone> HomeZones { get; set; } = [];

    /// <summary>Look for a newer release shortly after start.
    ///
    /// <para>On by default. The check is one small HTTPS request and never
    /// downloads or installs anything on its own — it only decides whether the
    /// user is told that something newer exists.</para></summary>
    public bool CheckForUpdatesOnStart { get; set; } = true;

    /// <summary>HTTPS address of the update feed, a single JSON document (see
    /// <see cref="Update.UpdateManifest"/>).
    ///
    /// <para>Configurable rather than compiled in, so a build can be pointed at
    /// a test feed without being rebuilt. Anything that is not <c>https</c> is
    /// refused — an update channel over plain HTTP would let whoever controls
    /// the network decide which code runs on this machine.</para></summary>
    /// <remarks>The address below is a placeholder and points at no real
    /// repository. Set it to the actual one before the first release, or the
    /// check simply fails silently and nobody is ever offered an update.</remarks>
    public string UpdateFeedUrl { get; set; } =
        "https://raw.githubusercontent.com/smohring/media-organizer/main/release/latest.json";

    /// <summary>Optional: the certificate subject the downloaded installer must
    /// be signed with, e.g. <c>CN=Media Organizer</c>.
    ///
    /// <para>Empty by default, and that is the honest state until a real code
    /// signing certificate exists: a build signed with a self-created test
    /// certificate cannot pass the check, and a guard that has to be switched
    /// off to get any work done teaches people to switch it off. The download
    /// is still verified against the SHA-256 from the feed, which arrives over
    /// TLS. Fill this in once releases are signed for real.</para></summary>
    public string UpdateExpectedSigner { get; set; } = "";

    /// <summary>When the last successful check ran (UTC). Only used to keep the
    /// automatic check from running on every single start.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }
}
