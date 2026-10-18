using System.Diagnostics;
using System.Reflection;

namespace MediaOrganizer.Core.Update;

/// <summary>The version of the running build, and the one comparison that
/// decides whether an update is worth offering.
///
/// <para>The number comes from the entry assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/>, which the build fills
/// from <c>VersionPrefix</c> in <c>Directory.Build.props</c>. That is the only
/// place a release number is written down; nothing here keeps a copy that
/// could drift away from it.</para></summary>
public static class AppVersion
{
    /// <summary>Version of the running executable. Falls back to
    /// <c>0.0.0</c> when no assembly carries one — that happens in unit test
    /// hosts, and a zero version simply means "anything is newer".</summary>
    public static Version Current { get; } = ReadCurrent();

    /// <summary>What the user sees, e.g. <c>1.0.0</c>.</summary>
    public static string CurrentDisplay => Current.ToString(3);

    private static Version ReadCurrent()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;

        // The informational version wins because it is the one that may carry a
        // suffix such as "1.0.0+abc1234"; Parse below drops the metadata part.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (Parse(informational) is { } fromAttribute)
        {
            return fromAttribute;
        }

        // A published single-file or R2R build can lose the managed attribute;
        // the native file version stays put in either case.
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && Parse(FileVersionInfo.GetVersionInfo(path).FileVersion) is { } fromFile)
            {
                return fromFile;
            }
        }
        catch
        {
            // No access to the own executable — fall through to the default.
        }

        return new Version(0, 0, 0);
    }

    /// <summary>Reads a version out of text that may carry a leading <c>v</c>
    /// or a trailing <c>+build</c> suffix. Returns null when nothing usable is
    /// in there.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Trim();
        if (cleaned.StartsWith('v') || cleaned.StartsWith('V'))
        {
            cleaned = cleaned[1..];
        }

        // Strip "+build" and "-preview" style suffixes: Version.TryParse rejects
        // the whole string over them.
        var cut = cleaned.IndexOfAny(['+', '-', ' ']);
        if (cut > 0)
        {
            cleaned = cleaned[..cut];
        }

        return Version.TryParse(cleaned, out var version) ? Normalize(version) : null;
    }

    /// <summary>Is <paramref name="candidate"/> a later release than what is
    /// running?
    ///
    /// <para>Compared on three components only. A four-part
    /// <see cref="Version"/> compares unspecified parts as -1, so an advertised
    /// <c>1.0.0</c> would otherwise look older than the running
    /// <c>1.0.0.0</c> and an update would never be offered.</para></summary>
    public static bool IsNewerThanCurrent(Version candidate) =>
        Normalize(candidate) > Normalize(Current);

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
