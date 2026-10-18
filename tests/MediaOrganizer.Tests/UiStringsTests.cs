using System.Xml.Linq;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Naming;
using MediaOrganizer.Core.Services;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>The two interface dictionaries have to stay in step.
///
/// <para>A key added to one file and forgotten in the other does not fail the
/// build and does not throw at runtime — the label simply shows the bare key,
/// and only in the language nobody on this machine is using. That is exactly
/// the kind of defect that ships. These tests are the reason it cannot.</para></summary>
public class UiStringsTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static Dictionary<string, string> Load(string language)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Localization", $"UiStrings.{language}.xaml");
        Assert.True(File.Exists(path), $"Dictionary not copied to the test output: {path}");

        return XDocument.Load(path).Root!.Elements()
            .Where(e => e.Attribute(X + "Key") is not null)
            .ToDictionary(e => e.Attribute(X + "Key")!.Value, e => e.Value);
    }

    private static readonly Dictionary<string, string> German = Load("de");
    private static readonly Dictionary<string, string> English = Load("en");

    [Fact]
    public void BothLanguagesDefineTheSameKeys()
    {
        var onlyGerman = German.Keys.Except(English.Keys).Order().ToList();
        var onlyEnglish = English.Keys.Except(German.Keys).Order().ToList();

        Assert.True(
            onlyGerman.Count == 0,
            $"Missing from the English dictionary: {string.Join(", ", onlyGerman)}");
        Assert.True(
            onlyEnglish.Count == 0,
            $"Missing from the German dictionary: {string.Join(", ", onlyEnglish)}");
    }

    [Fact]
    public void NoDictionaryIsEmpty()
    {
        Assert.NotEmpty(German);
        Assert.NotEmpty(English);
    }

    /// <summary>An empty value renders as an empty label, which reads as a
    /// layout bug rather than as a missing translation.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void NoStringIsBlank(string language)
    {
        var blank = Load(language)
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        Assert.True(blank.Count == 0, $"Blank in {language}: {string.Join(", ", blank)}");
    }

    /// <summary>Every warning the core can raise must have a sentence in both
    /// languages.
    ///
    /// <para>The core hands up a key and its arguments; the window turns that
    /// into text. A key added in <c>RunWarnings</c> and forgotten here would
    /// show the user "S.Warn.SomethingOrOther" in place of the warning — and
    /// only in the language nobody on this machine is running.</para></summary>
    [Fact]
    public void EveryRunWarningHasBothTranslations()
    {
        var missing = WarningKeys()
            .Where(key => !German.ContainsKey(key) || !English.ContainsKey(key))
            .Order()
            .ToList();

        Assert.True(missing.Count == 0, $"Without a translation: {string.Join(", ", missing)}");
    }

    /// <summary>The other direction: a warning removed from the core leaves two
    /// dead strings behind, and the next reader has to work out whether they
    /// still matter.</summary>
    [Fact]
    public void NoWarningStringIsUnused()
    {
        var known = WarningKeys().ToHashSet(StringComparer.Ordinal);
        var orphans = German.Keys
            .Where(k => k.StartsWith("S.Warn.", StringComparison.Ordinal) && !known.Contains(k))
            .Order()
            .ToList();

        Assert.True(orphans.Count == 0, $"No warning raises these any more: {string.Join(", ", orphans)}");
    }

    /// <summary>Every "S.Warn.…" constant of <c>RunWarnings</c>. Read by
    /// reflection on purpose: a list written out here would be the very thing
    /// that goes stale.</summary>
    private static IEnumerable<string> WarningKeys() =>
        typeof(MediaOrganizer.Core.Services.RunWarnings)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    /// <summary>Placeholders are part of the contract with the calling code.
    /// A translation that drops <c>{0}</c> silently loses the file name, the
    /// count or whatever else the caller passed in.</summary>
    [Fact]
    public void TranslationsKeepTheirPlaceholders()
    {
        foreach (var (key, german) in German)
        {
            var english = English[key];
            for (var index = 0; index < 4; index++)
            {
                var placeholder = $"{{{index}}}";
                Assert.True(
                    german.Contains(placeholder) == english.Contains(placeholder),
                    $"{key}: {placeholder} appears in only one of the two languages.");
            }
        }
    }

    /// <summary>Same arrangement as the warnings, for the lines the core
    /// reports: the core names a key, the window resolves it. A key without a
    /// translation would put "S.Progress.Analyzing" in front of the user for the
    /// entire length of a run — the phase where the window is watched the
    /// most.</summary>
    [Theory]
    [InlineData(typeof(ProgressKeys))]
    [InlineData(typeof(SetupKeys))]
    public void EveryCoreKeyHasBothTranslations(Type keyHolder)
    {
        var missing = Keys(keyHolder)
            .Where(key => !German.ContainsKey(key) || !English.ContainsKey(key))
            .Order()
            .ToList();

        Assert.True(missing.Count == 0, $"Without a translation: {string.Join(", ", missing)}");
    }

    /// <summary>The other direction: a message no longer reported leaves two
    /// dead strings behind.</summary>
    [Theory]
    [InlineData(typeof(ProgressKeys), "S.Progress.")]
    [InlineData(typeof(SetupKeys), "S.Setup.")]
    public void NoCoreStringIsUnused(Type keyHolder, string prefix)
    {
        var known = Keys(keyHolder).ToHashSet(StringComparer.Ordinal);
        var orphans = German.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !known.Contains(k))
            .Order()
            .ToList();

        Assert.True(orphans.Count == 0, $"Nothing reports these any more: {string.Join(", ", orphans)}");
    }

    private static IEnumerable<string> Keys(Type keyHolder) =>
        keyHolder
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    /// <summary>A message the pipeline actually reports has to survive being
    /// formatted with the arguments it was reported with.
    ///
    /// <para>This is the one mistake the dictionary tests cannot see: key and
    /// arguments are decided in the core, the placeholders in the dictionary,
    /// and nothing forces the two to agree. The window swallows a mismatch —
    /// <c>Loc.Format</c> falls back to the raw template rather than taking the
    /// status line down — so it would ship as "Benenne Gruppe {0}/{1} …" on
    /// screen. Here it fails instead.</para></summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public async Task ReportedMessagesFormatWithTheirArguments(string language)
    {
        var strings = Load(language);
        var reports = new Collector();

        // Naming is the one phase that reports without a model, a network or a
        // single image file — and it reports both a counted line and a bare one.
        await new GroupNamingService(
                ollama: null,
                model: "",
                imagePreprocessor: new UnusedImagePreprocessor(),
                ffmpeg: new MediaOrganizer.Core.Video.FfmpegService(),
                homeZones: [])
            .NameGroupsAsync(
                [Aufnahmen.Gruppe("Aufnahmen", 3, Aufnahmen.ReisezielLat, Aufnahmen.ReisezielLon,
                    new DateTime(2026, 5, 23, 14, 0, 0))],
                reports,
                CancellationToken.None);

        Assert.NotEmpty(reports.Reports);
        foreach (var report in reports.Reports)
        {
            Assert.True(
                strings.TryGetValue(report.MessageKey, out var template),
                $"Reported without a translation in {language}: {report.MessageKey}");

            var text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture, template!, report.MessageArgs);

            Assert.DoesNotContain("{", text);
        }
    }

    /// <summary>Collects synchronously. <see cref="Progress{T}"/> would post the
    /// callback to the test runner's synchronization context, and the reports
    /// could still be in flight when the assertions run.</summary>
    private sealed class Collector : IProgress<PipelineProgress>
    {
        public List<PipelineProgress> Reports { get; } = [];

        public void Report(PipelineProgress value) => Reports.Add(value);
    }

    /// <summary>Naming without a model must not touch an image.</summary>
    private sealed class UnusedImagePreprocessor : IImagePreprocessor
    {
        public byte[]? GetJpegForAnalysis(string imagePath, int maxDimension = 896) =>
            throw new InvalidOperationException("Naming must not prepare images.");

        public byte[]? GetVideoFrameJpeg(string videoPath, int maxDimension = 896) =>
            throw new InvalidOperationException("Naming must not prepare video frames.");

        public bool TryWriteDescription(string imagePath, string description) =>
            throw new InvalidOperationException("Naming must not write to files.");
    }
}
