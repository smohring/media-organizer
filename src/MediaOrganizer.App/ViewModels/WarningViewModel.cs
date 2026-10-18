using MediaOrganizer.App.Localization;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.App.ViewModels;

/// <summary>One note of a run, put into words.
///
/// <para>The core reports which case fired and with what numbers; the sentence
/// is made here, in the interface language, out of the dictionaries. That is
/// the whole point of the split: the core knows nothing about the language of
/// the window, and before this the warning list showed German and English lines
/// side by side depending on which file a warning came from.</para>
///
/// <para><see cref="Source"/> is kept because a plan rebuilt after editing has
/// to carry its warnings on. Recovering the numbers out of a finished sentence
/// would be parsing our own prose.</para></summary>
public sealed class WarningViewModel(RunWarning source)
{
    public RunWarning Source { get; } = source;

    /// <summary>The sentence for the window. An unknown key shows up as the
    /// bare key rather than as a crash — deliberate, and covered by a test that
    /// holds every key of <c>RunWarnings</c> against both dictionaries.</summary>
    public string Text { get; } = Loc.Format(source.Key, source.Args);

    public override string ToString() => Text;
}
