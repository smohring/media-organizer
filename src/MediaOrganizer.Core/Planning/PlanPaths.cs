using System.Globalization;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Planning;

/// <summary>Target path calculation for the plan. Deliberately one single
/// place: the planner, the plan editor (after files are moved around) and the
/// executor must all build exactly the same path, or a file lands somewhere
/// the plan did not promise.</summary>
public static class PlanPaths
{
    /// <summary>Folder name, "20260701_Urlaub Sylt". A dateless folder — the
    /// collecting folder, the everyday folder — carries only its theme, so that
    /// there is exactly one of it.</summary>
    public static string FolderName(string datePrefix, string theme, bool isDateless) =>
        isDateless || string.IsNullOrEmpty(datePrefix) ? theme : $"{datePrefix}_{theme}";

    /// <summary>The full target path including the year and month level.
    ///
    /// <para>The level follows the date prefix, not the current contents.
    /// Otherwise a folder renamed by hand would jump to a different place in
    /// the hierarchy the moment something is moved into it.</para>
    ///
    /// <para>For a dateless folder the prefix says nothing about its name — it
    /// never gets one — but everything about where it sits. An empty prefix
    /// means the whole collection and therefore no year to file it under, so it
    /// goes beside the levels, directly in the base folder. A prefix means the
    /// folder was split per period
    /// (<see cref="AppSettings.SplitDatelessByHierarchy"/>) and belongs inside
    /// that period's level: <c>2026\Vermischt</c>. Keeping the period in the
    /// prefix is what makes it survive editing — a shot dragged into
    /// <c>2026\Vermischt</c> from another year must not move the folder.</para></summary>
    /// <param name="insideFolder">Name of the folder this one sits INSIDE, for
    /// the themed subfolders of the collecting folder ("Vermischt\Screenshot").
    /// They inherit its place in the hierarchy — the parent is where the
    /// question "which year" was already answered.</param>
    public static string BuildTargetPath(
        string baseFolder,
        TargetHierarchy hierarchy,
        string datePrefix,
        string theme,
        bool isDateless,
        string? insideFolder = null)
    {
        var name = FolderName(datePrefix, theme, isDateless);
        if (!string.IsNullOrEmpty(insideFolder))
        {
            return Path.Combine(
                BuildTargetPath(baseFolder, hierarchy, datePrefix, insideFolder, isDateless: true),
                name);
        }

        if (isDateless && string.IsNullOrEmpty(datePrefix))
        {
            return Path.Combine(baseFolder, name);
        }

        var start = ParsePrefix(datePrefix);
        return hierarchy switch
        {
            TargetHierarchy.Year => Path.Combine(baseFolder, start.ToString("yyyy"), name),
            TargetHierarchy.YearMonth => Path.Combine(baseFolder, start.ToString("yyyy"), start.ToString("MM"), name),
            _ => Path.Combine(baseFolder, name),
        };
    }

    public static string Prefix(DateTime start) => start.ToString("yyyyMMdd");

    /// <summary>Reads a date prefix back. Anything unparseable becomes
    /// today — a folder with a nonsensical prefix still needs to land
    /// somewhere.</summary>
    public static DateTime ParsePrefix(string datePrefix) =>
        DateTime.TryParseExact(datePrefix, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.Today;
}
