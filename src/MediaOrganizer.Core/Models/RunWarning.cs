namespace MediaOrganizer.Core.Models;

/// <summary>A warning from a run: which one it is and what to fill in — not
/// the finished sentence.
///
/// <para>The core cannot write that sentence. It does not know the interface
/// language, and it must not: the language of the folder names
/// (<see cref="AppSettings.OutputLanguage"/>) and the language of the window
/// are two different settings, and the warnings belong to the window. Handing
/// finished German text up the stack is how one English line ended up between
/// four German ones in the warning list.</para>
///
/// <para><see cref="Key"/> is a key of the interface dictionaries; the
/// available ones are in <c>RunWarnings</c>, and a test holds them against
/// both dictionaries.</para></summary>
/// <param name="Key">Resource key, "S.Warn.…".</param>
/// <param name="Args">Values for the placeholders, in order.</param>
public sealed record RunWarning(string Key, params object?[] Args)
{
    /// <summary>Last resort for displaying one without a dictionary — the log,
    /// a test, a console. Never what the user reads in the window.</summary>
    public override string ToString() =>
        Args.Length == 0 ? Key : $"{Key} [{string.Join(", ", Args)}]";
}
