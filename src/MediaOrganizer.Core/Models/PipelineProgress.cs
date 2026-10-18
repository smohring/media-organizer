namespace MediaOrganizer.Core.Models;

public enum PipelinePhase
{
    Scan,
    Metadata,

    /// <summary>Face recognition. Its own phase on purpose: it runs in every
    /// mode, including "EXIF only", and reporting it as <see cref="Analyze"/>
    /// told the user the model was describing images when no model was running
    /// at all.</summary>
    Faces,

    Analyze,
    Group,
    Name,

    /// <summary>Assembling the plan, still without any file operation.</summary>
    Plan,

    /// <summary>Checking the plan for duplicates by content (SHA-256).</summary>
    Verify,

    Transfer,

    /// <summary>Taking the last run back.</summary>
    Undo,

    Done,
}

/// <summary>A progress report from the processing pipeline.
///
/// <para>The line carries a resource KEY and its arguments, not finished text.
/// The core has no interface language — it does not know one, and it must not:
/// the run may be started while the window shows German and be read while it
/// shows English. Only the interface resolves the key, and it does so every time
/// it displays the report.</para>
///
/// <para>The keys live in <see cref="ProgressKeys"/> so that a typo fails to
/// compile and a missing translation fails a test.</para></summary>
public readonly record struct PipelineProgress(
    PipelinePhase Phase,
    int Current,
    int Total,
    string MessageKey,
    params object[] MessageArgs)
{
    public double Fraction => Total <= 0 ? 0 : (double)Current / Total;
}

/// <summary>Every message key the pipeline reports. Both interface dictionaries
/// have to define each of them — <c>ProgressKeyTests</c> holds that in place.
///
/// <para>Numbers in a message are passed as arguments rather than read from
/// <see cref="PipelineProgress.Current"/> and <see cref="PipelineProgress.Total"/>:
/// the two do not always agree. The combined strategy, for one, counts its two
/// passes into one bar while each message names its own pass.</para></summary>
public static class ProgressKeys
{
    public const string Scanning = "S.Progress.Scanning";
    public const string ReadingMetadata = "S.Progress.ReadingMetadata";
    public const string RecognizingFaces = "S.Progress.RecognizingFaces";
    public const string DistillingKnowledge = "S.Progress.DistillingKnowledge";
    public const string LoadingModel = "S.Progress.LoadingModel";
    public const string Analyzing = "S.Progress.Analyzing";
    public const string AnalysisDone = "S.Progress.AnalysisDone";
    public const string GroupingByTimeAndGps = "S.Progress.GroupingByTimeAndGps";
    public const string GroupingByTopic = "S.Progress.GroupingByTopic";
    public const string SceneVectors = "S.Progress.SceneVectors";
    public const string ReadingNotes = "S.Progress.ReadingNotes";
    public const string FindingDuplicates = "S.Progress.FindingDuplicates";
    public const string Rescuing = "S.Progress.Rescuing";
    public const string CheckingBoundaries = "S.Progress.CheckingBoundaries";
    public const string GroupsFormed = "S.Progress.GroupsFormed";
    public const string NamingGroup = "S.Progress.NamingGroup";
    public const string NamingDone = "S.Progress.NamingDone";
    public const string BuildingPlan = "S.Progress.BuildingPlan";
    public const string PlanReady = "S.Progress.PlanReady";
    public const string CheckingDuplicate = "S.Progress.CheckingDuplicate";
    public const string DuplicateCheckDone = "S.Progress.DuplicateCheckDone";
    public const string Moving = "S.Progress.Moving";
    public const string Copying = "S.Progress.Copying";
    public const string Restoring = "S.Progress.Restoring";
    public const string ReadingSourceStructure = "S.Progress.ReadingSourceStructure";
    public const string SourceStructureRead = "S.Progress.SourceStructureRead";
    public const string Done = "S.Progress.Done";
}
