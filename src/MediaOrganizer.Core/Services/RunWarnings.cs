using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Services;

/// <summary>Every warning a run can produce, in one place.
///
/// <para><b>A warning is something the user may have to act on</b> — data that
/// limits what the run can do, a zone worth checking, a step that failed. What
/// the pipeline merely DID with its own rules (groups split by place, groups
/// merged, shots moved into the collecting folder) is not a warning; it goes
/// into the run log. Eleven of them stacked up in the window and buried the
/// three that mattered.</para>
///
/// <para>Two things depend on that. The keys have to exist in both interface
/// dictionaries with the same placeholders — <c>UiStringsTests</c> reads the
/// constants below and checks exactly that, so a warning added here and
/// forgotten in the English file fails the build of the test run rather than
/// showing a bare key to whoever runs the application in English. And a factory
/// per warning keeps the number of arguments with the key instead of leaving it
/// to each call site.</para></summary>
public static class RunWarnings
{
    public const string NoFfmpeg = "S.Warn.NoFfmpeg";
    public const string SkippedFormats = "S.Warn.SkippedFormats";
    public const string NoCaptureMetadata = "S.Warn.NoCaptureMetadata";
    public const string DateFromFileName = "S.Warn.DateFromFileName";
    public const string LittleGps = "S.Warn.LittleGps";
    public const string FaceModelsMissing = "S.Warn.FaceModelsMissing";
    public const string NoPersonsKnown = "S.Warn.NoPersonsKnown";
    public const string FaceRecognitionUnavailable = "S.Warn.FaceRecognitionUnavailable";
    public const string HomeZoneGuessed = "S.Warn.HomeZoneGuessed";
    public const string WideHomeZone = "S.Warn.WideHomeZone";
    public const string EverydayFolders = "S.Warn.EverydayFolders";
    public const string UnnamedToCollection = "S.Warn.UnnamedToCollection";
    public const string SplitWithoutHierarchy = "S.Warn.SplitWithoutHierarchy";
    public const string NoMediaFound = "S.Warn.NoMediaFound";
    public const string AnalysisFailures = "S.Warn.AnalysisFailures";
    public const string GroupingFailed = "S.Warn.GroupingFailed";
    public const string TargetFolderFailed = "S.Warn.TargetFolderFailed";
    public const string TransferCancelled = "S.Warn.TransferCancelled";
    public const string TransferFailed = "S.Warn.TransferFailed";
    public const string DuplicatesSkipped = "S.Warn.DuplicatesSkipped";
    public const string DescriptionsWritten = "S.Warn.DescriptionsWritten";
    public const string DescriptionsPartlyWritten = "S.Warn.DescriptionsPartlyWritten";
    public const string DescriptionsNotWritten = "S.Warn.DescriptionsNotWritten";
    public const string UndoCopyNotRemoved = "S.Warn.UndoCopyNotRemoved";
    public const string UndoSourceOccupied = "S.Warn.UndoSourceOccupied";
    public const string UndoFileFailed = "S.Warn.UndoFileFailed";

    // --- Scan and metadata ---

    public static RunWarning Ffmpeg() => new(NoFfmpeg);

    /// <param name="total">How many files were skipped.</param>
    /// <param name="extensions">The extensions with their counts, ready to
    /// read: ".psd (2), .txt (1)". Formatting a list is not something a
    /// resource string can do.</param>
    public static RunWarning Skipped(int total, string extensions) =>
        new(SkippedFormats, total, extensions);

    public static RunWarning NoCaptureTime(int count) => new(NoCaptureMetadata, count);

    public static RunWarning TimeFromFileName(int count) => new(DateFromFileName, count);

    public static RunWarning FewLocations(int without, int total) => new(LittleGps, without, total);

    public static RunWarning NoMedia() => new(NoMediaFound);

    // --- Faces ---

    public static RunWarning FaceModels() => new(FaceModelsMissing);

    public static RunWarning NoPersons() => new(NoPersonsKnown);

    public static RunWarning FacesUnavailable(string reason) =>
        new(FaceRecognitionUnavailable, reason);

    // --- Zones and grouping ---

    public static RunWarning GuessedHomeZone(string place) => new(HomeZoneGuessed, place);

    public static RunWarning HomeZoneTooWide(string zone, double radiusKm) =>
        new(WideHomeZone, zone, radiusKm);

    /// <param name="folders">The folders with their counts: "Zuhause" (2018),
    /// "Urlaub" (1168).</param>
    public static RunWarning Everyday(int groups, string folders) =>
        new(EverydayFolders, groups, folders);

    public static RunWarning Unnamed(int groups, int shots, string folder) =>
        new(UnnamedToCollection, groups, shots, folder);

    /// <summary>One collecting folder per year/month is switched on, but the
    /// target hierarchy is flat — there is no level for the parts to sit in, so
    /// nothing was split. Without this the switch looks broken rather than
    /// unused.</summary>
    public static RunWarning SplitNeedsHierarchy() => new(SplitWithoutHierarchy);

    public static RunWarning AnalysisFailed(int count) => new(AnalysisFailures, count);

    public static RunWarning ThematicGroupingFailed(int shots) => new(GroupingFailed, shots);

    // --- Applying the plan ---

    public static RunWarning TargetFolder(string path, string reason) =>
        new(TargetFolderFailed, path, reason);

    public static RunWarning Cancelled() => new(TransferCancelled);

    public static RunWarning Failed(int count) => new(TransferFailed, count);

    public static RunWarning Duplicates(int count) => new(DuplicatesSkipped, count);

    public static RunWarning Descriptions(int written) => new(DescriptionsWritten, written);

    public static RunWarning DescriptionsSome(int written, int skipped) =>
        new(DescriptionsPartlyWritten, written, skipped);

    public static RunWarning DescriptionsNone(int skipped) => new(DescriptionsNotWritten, skipped);

    // --- Taking a run back ---

    /// <summary>A copy stays where it is: undoing may not leave the only
    /// remaining version of a file behind.</summary>
    public static RunWarning UndoCopyKept(string fileName) => new(UndoCopyNotRemoved, fileName);

    public static RunWarning UndoSourceTaken(string fileName) => new(UndoSourceOccupied, fileName);

    public static RunWarning UndoFailed(string fileName, string reason) =>
        new(UndoFileFailed, fileName, reason);
}
