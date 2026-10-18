namespace MediaOrganizer.Core.Models;

/// <summary>What kind of medium this is.</summary>
public enum MediaKind
{
    Image,
    Video,
}

/// <summary>Where the best available timestamp came from.</summary>
public enum TimestampSource
{
    /// <summary>EXIF DateTimeOriginal (images).</summary>
    ExifOriginal,

    /// <summary>Creation date from the container (MP4/QuickTime atoms).</summary>
    ContainerCreated,

    /// <summary>Read out of the file name, "IMG-20250910-WA0001.jpg". What
    /// messengers and screenshot tools leave behind: no EXIF at all, but the
    /// date right there in the name. Worth far more than the file system time,
    /// which only says when the file was copied.</summary>
    FileName,

    /// <summary>Fallback: the file system, taking the older of the creation
    /// and modification time. Says when the file was copied, not when the
    /// photo was taken — hundreds of copied files share one.</summary>
    FileSystem,
}

/// <summary>A GPS coordinate, with distance by the haversine formula.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    public double DistanceKm(GeoPoint other)
    {
        const double earthRadiusKm = 6371.0;
        var dLat = DegToRad(other.Latitude - Latitude);
        var dLon = DegToRad(other.Longitude - Longitude);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(DegToRad(Latitude)) * Math.Cos(DegToRad(other.Latitude))
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegToRad(double deg) => deg * Math.PI / 180.0;

    public override string ToString() => $"{Latitude:F5}, {Longitude:F5}";
}

/// <summary>One photo or video file with the metadata read from it.</summary>
public sealed class MediaItem
{
    public required string SourcePath { get; init; }

    public required MediaKind Kind { get; init; }

    /// <summary>Best available capture time, in local time.</summary>
    public required DateTime TakenLocal { get; init; }

    public required TimestampSource TimestampSource { get; init; }

    public GeoPoint? Location { get; init; }

    /// <summary>Video length, where known.</summary>
    public TimeSpan? Duration { get; init; }

    public long FileSizeBytes { get; init; }

    public string? CameraModel { get; init; }

    // --- Analysis fields filled by the model (LLM and combined modes only) ---

    /// <summary>Short description of the content, one or two sentences.</summary>
    public string? Description { get; set; }

    /// <summary>Guessed place or surroundings ("beach", "city centre", …).</summary>
    public string? LocationGuess { get; set; }

    /// <summary>Topic category used to form subgroups when naming
    /// ("beach", "restaurant", …).</summary>
    public string? Category { get; set; }

    /// <summary>Note the user pinned to this image, taken from the knowledge
    /// base and matched by file hash. Feeds into analysis and naming.</summary>
    public string? UserNote { get; set; }

    /// <summary>People face recognition matched with certainty, by name.</summary>
    public List<string> RecognizedPeople { get; } = [];

    /// <summary>The same people with their place in the frame, for example
    /// "Mia (rechts im Bild)". Without the position the model puts names on
    /// the wrong faces.</summary>
    public List<string> RecognizedPeopleWithPosition { get; } = [];

    /// <summary>How many faces were found in total. When this exceeds the
    /// number of matched people, strangers are in the picture — the model has
    /// to know, or it spreads known names over them.</summary>
    public int DetectedFaceCount { get; set; }

    /// <summary>Where the unmatched faces are, for example "in der Bildmitte"
    /// (the hint is written in the output language). Without it the model
    /// mixes up WHO is unknown and describes the wrong person — in one test an
    /// adult woman became "a small child".</summary>
    public List<string> UnidentifiedPositions { get; } = [];

    public string FileName => Path.GetFileName(SourcePath);
}
