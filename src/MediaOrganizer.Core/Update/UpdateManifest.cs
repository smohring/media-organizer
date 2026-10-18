using System.Text.Json.Serialization;

namespace MediaOrganizer.Core.Update;

/// <summary>The update feed: one small JSON document describing the newest
/// release. It is fetched over HTTPS from
/// <see cref="Models.AppSettings.UpdateFeedUrl"/>.
///
/// <para>Deliberately a flat document rather than a list of releases. The
/// application always replaces itself with the newest build regardless of how
/// old it is, so the only question the client ever asks is "what is the newest
/// one" — a history of releases would be data nobody reads.</para>
///
/// <example>
/// <code>
/// {
///   "version": "1.0.1",
///   "released": "2026-08-20",
///   "notes": "Faster analysis, fixes a crash on HEIC files without EXIF.",
///   "packageUrl": "https://github.com/&lt;owner&gt;/MediaOrganizer/releases/download/v1.0.1/MediaOrganizer-1.0.1-win-x64.zip",
///   "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
///   "sizeBytes": 158123456
/// }
/// </code>
/// </example></summary>
public sealed class UpdateManifest
{
    /// <summary>Release number, e.g. <c>1.0.1</c>. A leading <c>v</c> is
    /// tolerated so the field can be copied straight from a git tag.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>Release date, ISO 8601. Shown to the user, never compared —
    /// the version decides.</summary>
    [JsonPropertyName("released")]
    public string Released { get; set; } = "";

    /// <summary>What changed, in prose. Shown verbatim in the update
    /// panel.</summary>
    [JsonPropertyName("notes")]
    public string Notes { get; set; } = "";

    /// <summary>HTTPS address of the release archive: the same self-contained
    /// application directory that ships as a download, packed as a zip.
    /// Anything but <c>https</c> is rejected before a single byte is
    /// fetched.</summary>
    [JsonPropertyName("packageUrl")]
    public string PackageUrl { get; set; } = "";

    /// <summary>SHA-256 of the archive as lower-case hex.
    ///
    /// <para>Mandatory. Without it the application would unpack whatever the
    /// download produced over its own program files, and a truncated transfer
    /// is far more likely than an attack. This is the field that makes the
    /// download verifiable.</para></summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>Size in bytes, used for the progress bar only.</summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    /// <summary>Parsed <see cref="Version"/>, or null if the feed carries
    /// something unusable.</summary>
    [JsonIgnore]
    public Version? ParsedVersion => AppVersion.Parse(Version);

    /// <summary>Is this document complete enough to act on? A feed that lost a
    /// field during editing must not turn into a half-finished download.</summary>
    public bool IsUsable(out string problem)
    {
        if (ParsedVersion is null)
        {
            problem = $"The update feed carries no readable version (\"{Version}\").";
            return false;
        }

        if (!Uri.TryCreate(PackageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            problem = "The update feed does not point to an https address.";
            return false;
        }

        if (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))
        {
            problem = "The update feed carries no valid SHA-256 checksum.";
            return false;
        }

        problem = "";
        return true;
    }
}
