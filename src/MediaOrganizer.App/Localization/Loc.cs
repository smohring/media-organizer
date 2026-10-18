using System.Globalization;
using System.Windows;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.App.Localization;

/// <summary>The interface language, switchable while the application runs.
///
/// <para><b>How it works.</b> Every visible string lives in one of two
/// <see cref="ResourceDictionary"/> files keyed by name (<c>S.Start</c>,
/// <c>S.Cancel</c> …). XAML reaches them with <c>{DynamicResource S.Start}</c>,
/// which re-evaluates whenever the dictionary is replaced — so switching the
/// language is a matter of swapping one merged dictionary, with no restart and
/// no window rebuild. Code-behind and view models use
/// <see cref="Get"/>/<see cref="Format"/>.</para>
///
/// <para><b>Why dictionaries and not .resx.</b> Generated resource classes are
/// resolved once when a binding is created. Switching the culture afterwards
/// leaves every already-rendered label in the old language until the window is
/// recreated — exactly the thing this is supposed to avoid.</para>
///
/// <para>Missing keys return the key itself rather than throwing. A German
/// dictionary that fell one string behind should show <c>S.Something</c> in one
/// label, not take the window down.</para></summary>
public static class Loc
{
    private static readonly Uri GermanSource =
        new("pack://application:,,,/Localization/UiStrings.de.xaml", UriKind.Absolute);

    private static readonly Uri EnglishSource =
        new("pack://application:,,,/Localization/UiStrings.en.xaml", UriKind.Absolute);

    private static ResourceDictionary? _current;

    /// <summary>Raised after the language changed. View models listen so they
    /// can invalidate strings they build in code — those are plain properties
    /// and no dynamic resource reaches them.</summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>The language actually in use, with
    /// <see cref="UiLanguage.System"/> already resolved.</summary>
    public static UiLanguage Effective { get; private set; } = UiLanguage.English;

    /// <summary>Applies a language. Safe to call repeatedly and safe to call
    /// with the language already in use.</summary>
    public static void Apply(UiLanguage language)
    {
        var effective = Resolve(language);
        var source = effective == UiLanguage.German ? GermanSource : EnglishSource;

        var replacement = new ResourceDictionary { Source = source };
        var merged = Application.Current.Resources.MergedDictionaries;

        if (_current is not null)
        {
            merged.Remove(_current);
        }

        // Appended last so the strings win over anything a theme dictionary
        // might happen to define under the same key.
        merged.Add(replacement);
        _current = replacement;
        Effective = effective;

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Turns <see cref="UiLanguage.System"/> into a concrete language.
    /// German only for a genuinely German Windows; everything else gets
    /// English, which is the safer default for a language nobody translated
    /// into.</summary>
    public static UiLanguage Resolve(UiLanguage language) => language switch
    {
        UiLanguage.German => UiLanguage.German,
        UiLanguage.English => UiLanguage.English,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("de", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.German
            : UiLanguage.English,
    };

    /// <summary>The string for a key, or the key itself when it is missing.</summary>
    public static string Get(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    /// <summary>A string with <c>{0}</c>-style placeholders filled in.</summary>
    public static string Format(string key, params object?[]? args)
    {
        var template = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args ?? []);
        }
        catch (FormatException)
        {
            // A translation with a mistyped placeholder must not take a status
            // line down; show the untranslated template instead.
            return template;
        }
    }
}
