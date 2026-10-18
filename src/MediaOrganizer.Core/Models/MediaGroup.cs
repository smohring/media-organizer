namespace MediaOrganizer.Core.Models;

/// <summary>A set of shots that belong together in time and topic, and that
/// end up in one target folder.</summary>
public sealed class MediaGroup
{
    /// <summary>Placeholder theme of a group nothing has named yet. Compared
    /// against rather than written down twice: the naming service uses it to
    /// tell "never named" apart from "named this".</summary>
    public const string DefaultTheme = "Aufnahmen";

    public required List<MediaItem> Items { get; init; }

    /// <summary>The theme the model (or a fallback) gave this group, for
    /// example "Urlaub Sylt".</summary>
    public string Theme { get; set; } = DefaultTheme;

    /// <summary>The collecting folder for shots that fit no event. There is
    /// exactly one, and the model never names it.</summary>
    public bool IsCollection { get; set; }

    /// <summary>Nothing could name this group. Set in EXIF-only mode when no
    /// place could be resolved for it — no GPS, or nothing within reach in the
    /// place database. Its name would be the bare placeholder, and a folder
    /// called "20250709_Aufnahmen" says nothing the file name does not.</summary>
    public bool IsUnnamed { get; set; }

    /// <summary>Everyday life: everything shot inside a home zone, gathered in
    /// EXIF-only mode. One folder per home zone for the whole collection
    /// instead of one per day — without the model there is nothing that tells
    /// one evening at home from another, so the date would be the only
    /// content of the name, and the file names carry it already.</summary>
    public bool IsEveryday { get; set; }

    /// <summary>A themed subfolder INSIDE the collecting folder
    /// ("Vermischt\Screenshot"): shots that fit no event but clearly belong
    /// together. Holds the name of the parent, empty for everything else.
    ///
    /// <para>The collecting folder is the last resort, and it grows large. What
    /// the model can still recognise as one kind — screenshots, documents,
    /// pictures of a device — does not become an event, but it does not have to
    /// lie loose in one heap of thousands either.</para></summary>
    public string InsideFolder { get; set; } = "";

    /// <summary>Folder without a date prefix: the collecting folder, the
    /// everyday folder and the themed subfolders of the collecting folder. Each
    /// spans a stretch of time rather than one day, so any single date in their
    /// name would be a lie.</summary>
    public bool IsDateless => IsCollection || IsEveryday || InsideFolder.Length > 0;

    /// <summary>A dateless folder that belongs under one year or month level
    /// instead of beside them — the split of
    /// <see cref="AppSettings.SplitDatelessByHierarchy"/>, which turns one
    /// collecting folder for the whole collection into one per period.
    ///
    /// <para>The name still carries no date; <see cref="StartLocal"/> only
    /// decides which level the folder sits under. Every shot in such a group
    /// comes from the same period, so that start is the period.</para></summary>
    public bool IsPeriodScoped { get; set; }

    public DateTime StartLocal => Items.Count > 0 ? Items.Min(i => i.TakenLocal) : DateTime.MinValue;

    public DateTime EndLocal => Items.Count > 0 ? Items.Max(i => i.TakenLocal) : DateTime.MinValue;

    /// <summary>Folder name in the form YYYYMMDD_&lt;theme&gt;. Dateless folders
    /// carry only their theme, so that there is exactly one of each rather than
    /// one per run.</summary>
    public string FolderName => IsDateless ? Theme : $"{StartLocal:yyyyMMdd}_{Theme}";

    /// <summary>Centre of the GPS coordinates, where there are any.</summary>
    public GeoPoint? Centroid
    {
        get
        {
            var located = Items.Where(i => i.Location is not null).Select(i => i.Location!.Value).ToList();
            if (located.Count == 0)
            {
                return null;
            }

            return new GeoPoint(located.Average(p => p.Latitude), located.Average(p => p.Longitude));
        }
    }
}
