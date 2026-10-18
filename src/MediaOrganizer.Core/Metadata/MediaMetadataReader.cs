using System.Collections.Frozen;
using System.Text.RegularExpressions;
using MediaOrganizer.Core.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace MediaOrganizer.Core.Metadata;

/// <summary>Liest Zeit-, GPS- und Basis-Metadaten aus Bildern (EXIF) und
/// Videos (MP4/QuickTime-Container-Atoms) über MetadataExtractor.</summary>
public sealed partial class MediaMetadataReader
{
    private static readonly FrozenSet<string> ImageExtensions =
        new[]
        {
            ".jpg", ".jpeg", ".png", ".heic", ".heif", ".tif", ".tiff", ".webp",
            ".bmp", ".gif", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> VideoExtensions =
        new[]
        {
            ".mp4", ".mov", ".m4v", ".avi", ".mts", ".m2ts", ".mkv", ".webm", ".3gp", ".wmv",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files that sit beside photos without being photos: sidecars
    /// and marker files written by phones, cameras and backup tools.
    ///
    /// <para>They are skipped like any other unsupported file, but silently.
    /// A phone backup brings hundreds of them — 699 Apple <c>.aae</c> edit
    /// sidecars in one measured collection — and a warning counting them as
    /// "unsupported format" reads as if photos had been lost.</para></summary>
    private static readonly FrozenSet<string> CompanionExtensions =
        new[]
        {
            ".aae",     // Apple: non-destructive edits beside the original
            ".xmp",     // Adobe and others: metadata sidecar
            ".thm",     // camera thumbnail beside a video
            ".lrv",     // GoPro low-resolution preview
            ".json",    // Google Takeout metadata beside every file
            ".nomedia", // Android: "do not index this folder"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Same idea, matched on the whole name: what Windows and macOS
    /// leave lying around in every folder.</summary>
    private static readonly FrozenSet<string> CompanionFileNames =
        new[] { "thumbs.db", "desktop.ini", ".ds_store" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vor 1980 liegende Zeitstempel gelten als ungültig
    /// (z.B. ungesetzte QuickTime-Epoche 1904).</summary>
    private static readonly DateTime MinPlausibleDate = new(1980, 1, 1);

    // ISO-6709-Ortsangabe wie "+54.6321+008.3410/" aus QuickTime-Metadaten.
    [GeneratedRegex(@"^([+-]\d{1,3}(?:\.\d+)?)([+-]\d{1,3}(?:\.\d+)?)")]
    private static partial Regex Iso6709Regex();

    // Date and time in a file name: "IMG_20260114_100607",
    // "Screenshot_20251105-172535", "2026-01-14 10.05.23",
    // "Screenshot_2026-01-14-10-05-23-511". Milliseconds are matched so they
    // do not break the "no further digit" guard.
    [GeneratedRegex(@"(?<!\d)(\d{4})[-_.]?(\d{2})[-_.]?(\d{2})[-_ T]*(\d{2})[-_.:]?(\d{2})[-_.:]?(\d{2})(?:[-_.]?\d{3})?(?!\d)")]
    private static partial Regex FileNameDateTimeRegex();

    // Date alone: "IMG-20250910-WA0001", "2026-03-08". Tried second, so a name
    // that carries a time keeps it.
    [GeneratedRegex(@"(?<!\d)(\d{4})[-_.]?(\d{2})[-_.]?(\d{2})(?!\d)")]
    private static partial Regex FileNameDateRegex();

    public static MediaKind? Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext))
        {
            return MediaKind.Image;
        }

        if (VideoExtensions.Contains(ext))
        {
            return MediaKind.Video;
        }

        return null;
    }

    /// <summary>The capture time as written in the file name, or null when the
    /// name holds no plausible date.
    ///
    /// <para>Made for what a phone backup is full of: WhatsApp
    /// ("IMG-20250910-WA0001.jpg"), Android camera and screenshots
    /// ("IMG_20260114_100607.jpg", "Screenshot_20251105-172535.png"). None of
    /// them carries EXIF, and all of them carry the date.</para>
    ///
    /// <para>Every candidate is checked against the calendar and against the
    /// clock: an impossible date and one in the future are both rejected, which
    /// is what keeps a serial number like "IMG_12345678" from becoming a
    /// timestamp. The date is read as local time — that is what the naming
    /// devices write.</para></summary>
    internal static DateTime? TryReadTimestampFromFileName(string fileName)
    {
        var withTime = FileNameDateTimeRegex().Match(fileName);
        if (withTime.Success
            && Build(withTime, hasTime: true) is { } exact)
        {
            return exact;
        }

        var dateOnly = FileNameDateRegex().Match(fileName);
        return dateOnly.Success ? Build(dateOnly, hasTime: false) : null;

        static DateTime? Build(Match match, bool hasTime)
        {
            var year = int.Parse(match.Groups[1].Value);
            var month = int.Parse(match.Groups[2].Value);
            var day = int.Parse(match.Groups[3].Value);

            if (year < MinPlausibleDate.Year || month is < 1 or > 12
                || day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                return null;
            }

            var hour = hasTime ? int.Parse(match.Groups[4].Value) : 0;
            var minute = hasTime ? int.Parse(match.Groups[5].Value) : 0;
            var second = hasTime ? int.Parse(match.Groups[6].Value) : 0;
            if (hour > 23 || minute > 59 || second > 59)
            {
                return null;
            }

            var stamp = new DateTime(year, month, day, hour, minute, second);

            // A shot cannot have been taken tomorrow. One day of slack for
            // time zones and a clock that runs ahead.
            return stamp > DateTime.Now.AddDays(1) ? null : stamp;
        }
    }

    /// <summary>A file that belongs beside photos without being one. Not media,
    /// but nothing worth reporting either.</summary>
    public static bool IsCompanionFile(string path) =>
        CompanionExtensions.Contains(Path.GetExtension(path))
        || CompanionFileNames.Contains(Path.GetFileName(path));

    /// <summary>Liest die Metadaten einer Datei. Wirft nie — bei Problemen
    /// wird auf Dateisystem-Zeitstempel zurückgefallen.</summary>
    public MediaItem Read(string path, MediaKind kind)
    {
        DateTime? taken = null;
        var source = TimestampSource.FileSystem;
        GeoPoint? location = null;
        TimeSpan? duration = null;
        string? cameraModel = null;

        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);

            if (kind == MediaKind.Image)
            {
                var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                if (subIfd is not null
                    && subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto)
                    && dto >= MinPlausibleDate)
                {
                    // EXIF-Zeit ist bereits lokale Aufnahmezeit.
                    taken = dto;
                    source = TimestampSource.ExifOriginal;
                }

                var gps = directories.OfType<GpsDirectory>().FirstOrDefault()?.GetGeoLocation();
                if (gps is not null && !gps.IsZero)
                {
                    location = new GeoPoint(gps.Latitude, gps.Longitude);
                }

                cameraModel = directories.OfType<ExifIfd0Directory>().FirstOrDefault()
                    ?.GetString(ExifDirectoryBase.TagModel)?.Trim();
            }
            else
            {
                var mvhd = directories.OfType<QuickTimeMovieHeaderDirectory>().FirstOrDefault();
                if (mvhd is not null)
                {
                    if (mvhd.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var created)
                        && created >= MinPlausibleDate)
                    {
                        // Container-Zeiten sind per Spezifikation UTC.
                        taken = DateTime.SpecifyKind(created, DateTimeKind.Utc).ToLocalTime();
                        source = TimestampSource.ContainerCreated;
                    }

                    duration = TryReadDuration(mvhd);
                }

                location = TryReadIso6709(directories);
            }
        }
        catch
        {
            // Nicht lesbare/unbekannte Formate: Fallback unten greift.
        }

        // Before the file system: messenger images and screenshots carry no
        // EXIF at all, but their name says when they were taken. The file
        // system only says when they were copied — which put thousands of
        // shots on the day of the backup and mixed them into real events.
        if (taken is null && TryReadTimestampFromFileName(Path.GetFileName(path)) is { } fromName)
        {
            taken = fromName;
            source = TimestampSource.FileName;
        }

        var fileInfo = new FileInfo(path);
        if (taken is null)
        {
            var created = fileInfo.CreationTime;
            var written = fileInfo.LastWriteTime;
            taken = written < created ? written : created;
        }

        return new MediaItem
        {
            SourcePath = path,
            Kind = kind,
            TakenLocal = taken.Value,
            TimestampSource = source,
            Location = location,
            Duration = duration,
            FileSizeBytes = fileInfo.Length,
            CameraModel = cameraModel,
        };
    }

    private static TimeSpan? TryReadDuration(QuickTimeMovieHeaderDirectory mvhd)
    {
        // Je nach Bibliotheksversion liegt die Dauer als TimeSpan oder als
        // Ticks + TimeScale vor — beide Varianten abdecken.
        var raw = mvhd.GetObject(QuickTimeMovieHeaderDirectory.TagDuration);
        if (raw is TimeSpan ts)
        {
            return ts;
        }

        if (mvhd.TryGetInt64(QuickTimeMovieHeaderDirectory.TagDuration, out var units)
            && mvhd.TryGetInt64(QuickTimeMovieHeaderDirectory.TagTimeScale, out var scale)
            && scale > 0)
        {
            return TimeSpan.FromSeconds((double)units / scale);
        }

        return null;
    }

    private static GeoPoint? TryReadIso6709(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        foreach (var dir in directories)
        {
            foreach (var tag in dir.Tags)
            {
                var value = tag.Description;
                if (string.IsNullOrEmpty(value) || (!value.StartsWith('+') && !value.StartsWith('-')))
                {
                    continue;
                }

                var match = Iso6709Regex().Match(value);
                if (match.Success
                    && double.TryParse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var lat)
                    && double.TryParse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out var lon)
                    && (lat != 0 || lon != 0))
                {
                    return new GeoPoint(lat, lon);
                }
            }
        }

        return null;
    }
}
