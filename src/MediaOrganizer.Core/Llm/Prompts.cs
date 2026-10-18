using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Llm;

/// <summary>Every prompt sent to the model, in one place, in one language.
///
/// <para>Two implementations exist because the prompt decides what language the
/// model answers in, and that answer becomes user-visible output: folder names
/// and image descriptions. The user interface is English; the collection being
/// sorted may well not be. Which set is used comes from
/// <see cref="AppSettings.OutputLanguage"/>.</para>
///
/// <para><b>The German set is frozen.</b> Its wording is part of the analysis
/// cache key (see <c>MediaDescriber.PromptVersion</c>), so editing a single
/// sentence invalidates every stored description — measured at roughly ten
/// hours of re-analysis for ten thousand photos. A change means raising
/// <c>MediaDescriber.PromptVersion</c> alongside it.</para>
///
/// <para>The JSON field names stay German in both sets on purpose. They are an
/// internal protocol between prompt and parser, never seen by anyone, and
/// keeping them identical means the response parsing has exactly one shape to
/// handle rather than two.</para></summary>
public abstract class Prompts
{
    public static Prompts German { get; } = new GermanPrompts();

    public static Prompts English { get; } = new EnglishPrompts();

    public static Prompts For(OutputLanguage language) =>
        language == OutputLanguage.English ? English : German;

    /// <summary>System prompt for describing a single photo or video.</summary>
    public abstract string DescribeSystem { get; }

    /// <summary>User turn that accompanies a single image.</summary>
    public abstract string DescribeUserImage { get; }

    /// <summary>User turn that accompanies the still frames of a video.</summary>
    public abstract string DescribeUserVideo(int frameCount);

    /// <summary>System prompt that splits a timeline into events.</summary>
    public abstract string GroupSystem { get; }

    /// <summary>Appended to <see cref="GroupSystem"/> when unmatched shots go
    /// into a collecting folder instead of an event folder.</summary>
    protected abstract string UnmatchedSuffix { get; }

    /// <summary>System prompt that decides whether two adjacent groups are one
    /// event.</summary>
    public abstract string MergeSystem { get; }

    /// <summary>System prompt that turns image notes into lasting facts.</summary>
    public abstract string DistillSystem { get; }

    /// <summary>Heading above the facts already known, in the distillation
    /// request.</summary>
    public abstract string DistillKnownFactsHeading { get; }

    /// <summary>Heading above the user's new notes, in the same request.</summary>
    public abstract string DistillNewNotesHeading { get; }

    /// <summary>Renders "(in file X)" behind a note.</summary>
    public abstract string DistillNoteSource(string fileName);

    /// <summary>System prompt that names a group's folder.</summary>
    public abstract string NameSystem { get; }

    /// <summary>Throwaway request that loads the model into memory, so the
    /// first real call does not pay for it.</summary>
    public abstract string WarmupSystem { get; }

    /// <summary>Appends the user's knowledge base to a system prompt.</summary>
    public abstract string WithKnowledge(string baseSystem, IReadOnlyList<string> facts);

    /// <summary>Adds the user's own note about this one image.</summary>
    public abstract string WithUserNote(string basePrompt, string? note);

    /// <summary>Adds the people that face recognition matched with certainty,
    /// together with where they are in the frame. The position matters: without
    /// it a name lands on the wrong person. The count of unmatched faces matters
    /// too, or known names get spread over strangers.</summary>
    public abstract string WithRecognizedPeople(
        string basePrompt,
        IReadOnlyList<string> peopleWithPosition,
        IReadOnlyList<string> unidentifiedPositions);

    /// <summary>Grouping prompt, with the "unmatched" field when the collecting
    /// folder is switched on.</summary>
    public string BuildGroupSystem(bool collectUnmatched) =>
        collectUnmatched ? GroupSystem + UnmatchedSuffix : GroupSystem;

    /// <summary>Folder name used when the model returns nothing usable.</summary>
    public abstract string FallbackGroupName { get; }

    /// <summary>Word that joins a theme to the place it was shot at —
    /// "Picknick im Park bei Beispielburg".</summary>
    public abstract string PlaceConnector { get; }

    /// <summary>Name of a home zone the application worked out for itself, and
    /// with it the name of the everyday folder in EXIF-only mode. A configured
    /// zone brings its own name; only the guessed one needs this.</summary>
    public abstract string DefaultHomeZoneName { get; }

    /// <summary>Default name of the collecting folder for shots that belong to
    /// no event.</summary>
    public abstract string DefaultUnmatchedFolderName { get; }

    /// <summary>Categories that name a KIND OF FILE and may therefore become a
    /// themed subfolder of the collecting folder.
    ///
    /// <para>The describer writes one word on every item, and most of those
    /// words name a subject, not a kind: "Familie", "Kind", "Natur", "Pflanze",
    /// "Fahrrad". A folder of five unrelated bicycles helps nobody, and "Kind"
    /// is the wrong word for a picture the face recognition knows by name. A
    /// screenshot, a receipt or a flight ticket is a different matter — there
    /// the kind IS the point.</para></summary>
    public abstract IReadOnlySet<string> FileKindCategories { get; }

    /// <summary>Single words that name no subject at all and therefore make no
    /// folder name — the place the group can prove is better. Deliberately
    /// short: "Holzboden" or "Kücheneinbau" say something, "Person" does
    /// not.</summary>
    public abstract IReadOnlySet<string> MeaninglessThemeWords { get; }
}
