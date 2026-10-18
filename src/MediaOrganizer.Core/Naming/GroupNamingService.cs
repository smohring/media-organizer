using System.Globalization;
using System.Text;
using MediaOrganizer.Core.Grouping;
using MediaOrganizer.Core.Llm;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;
using MediaOrganizer.Core.Video;

namespace MediaOrganizer.Core.Naming;

/// <summary>Finds the theme for the target folder name (YYYYMMDD_&lt;theme&gt;).
///
/// <para>The group is sampled by thematic subgroup — by the model's category
/// where there is one, otherwise by chronological subcluster (gap &gt; 2 h) —
/// and the first shot of each subgroup goes into the call, at most three
/// images.</para></summary>
public sealed class GroupNamingService(
    OllamaClient? ollama,
    string model,
    IImagePreprocessor imagePreprocessor,
    FfmpegService ffmpeg,
    IReadOnlyList<HomeZone>? homeZones = null,
    IReadOnlyList<string>? knowledgeFacts = null,
    int maxParallel = 1,
    Prompts? prompts = null)
{
    private const int MaxSampleImages = 3;

    /// <summary>Longest folder theme. Anything above this is cut off, which is
    /// why the place is only appended where it still fits.</summary>
    private const int MaxThemeLength = 60;

    private readonly Prompts _prompts = prompts ?? Prompts.German;

    private readonly IReadOnlyList<HomeZone> _homeZones = homeZones ?? [];
    private readonly IReadOnlyList<string> _knowledgeFacts = knowledgeFacts ?? [];

    /// <summary>Names every group. Each group is its own model call and
    /// independent of the others, so they run concurrently.
    ///
    /// <para>Making the folder names unique stays serial afterwards: it
    /// compares the groups against each other and would append different
    /// numbers depending on the order.</para></summary>
    public async Task NameGroupsAsync(
        List<MediaGroup> groups,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        var done = 0;
        await Parallel.ForEachAsync(
            groups,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(maxParallel, 1, 16),
                CancellationToken = ct,
            },
            async (group, token) =>
            {
                await NameGroupAsync(group, token).ConfigureAwait(false);

                var count = Interlocked.Increment(ref done);
                progress.Report(new PipelineProgress(
                    PipelinePhase.Name, count, groups.Count,
                    ProgressKeys.NamingGroup, count, groups.Count));
            }).ConfigureAwait(false);

        EnsureUniqueFolderNames(groups);
        progress.Report(new PipelineProgress(
            PipelinePhase.Name, groups.Count, groups.Count, ProgressKeys.NamingDone));
    }

    private async Task NameGroupAsync(MediaGroup group, CancellationToken ct)
    {
        if (group.IsDateless)
        {
            // The collecting folder and the everyday folder carry their name
            // already — the pipeline set it.
            group.Theme = Sanitize(group.Theme);
            return;
        }

        if (ollama is null)
        {
            // EXIF mode: no model call, so the place is all there is to name a
            // group after — "20260523_Weimar". Everything shot at home is
            // already in the everyday folder and never gets here.
            if (group.Theme == MediaGroup.DefaultTheme)
            {
                Apply(group, PlaceName(GroupContextClassifier.Classify(group, _homeZones)));
                return;
            }

            group.Theme = Sanitize(group.Theme);
            return;
        }

        try
        {
            var context = GroupContextClassifier.Classify(group, _homeZones);
            var samples = SelectSamples(group);
            var images = new List<byte[]>();

            foreach (var sample in samples)
            {
                if (images.Count >= MaxSampleImages)
                {
                    break;
                }

                if (sample.Kind == MediaKind.Image)
                {
                    if (imagePreprocessor.GetJpegForAnalysis(sample.SourcePath) is { } jpeg)
                    {
                        images.Add(jpeg);
                    }
                }
                else
                {
                    var frames = await ffmpeg.ExtractFramesAsync(
                        sample.SourcePath, sample.Duration, maxFrames: 1, ct: ct).ConfigureAwait(false);
                    if (frames.Count > 0)
                    {
                        images.Add(frames[0]);
                    }
                }
            }

            var raw = await ollama.ChatAsync(
                model,
                _prompts.WithKnowledge(_prompts.NameSystem, _knowledgeFacts),
                BuildContext(group, context),
                images.Count > 0 ? images : null, ct: ct).ConfigureAwait(false);

            using var json = LlmJson.TryParse(raw);
            var theme = json is null ? null : LlmJson.GetString(json.RootElement, "thema");
            var usable = UsableTheme(theme);
            var place = PlaceName(context);
            var name = usable is null ? place : WithPlace(usable, context);

            // The naming used to log nothing, so a folder named after its place
            // alone gave no clue whether the model had said nothing or the
            // filter had thrown the answer away.
            RunLog.Log($"Naming {group.StartLocal:yyyy-MM-dd} ({group.Items.Count}): " +
                       $"model=\"{theme}\"{(usable is null ? " DISCARDED" : "")}, " +
                       $"place=\"{place}\" -> \"{name ?? group.Theme}\"");
            Apply(group, name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // No answer: the place is all that is left.
            Apply(group, PlaceName(GroupContextClassifier.Classify(group, _homeZones)));
        }
    }

    /// <summary>Sets the name, or marks the group when neither the model nor the
    /// place database produced one — what happens to it then is the pipeline's
    /// decision, and it cannot read that back off the theme once unique names
    /// have been made ("Aufnahmen 2").</summary>
    private static void Apply(MediaGroup group, string? name)
    {
        group.IsUnnamed = name is null;
        group.Theme = Sanitize(name ?? group.Theme);
    }

    /// <summary>The model's answer, or null when it genuinely says nothing:
    /// empty, the fallback word, a file kind ("Screenshot") or one of the words
    /// that name no subject at all ("Person", "Natur").
    ///
    /// <para>A single word is NOT rejected just for matching the item category.
    /// That rule threw away "Holzboden" on seven pictures of a wooden floor and
    /// left the folder named after its place alone — the category is often
    /// exactly the right word.</para></summary>
    internal string? UsableTheme(string? theme)
    {
        var trimmed = theme?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || trimmed.Equals(MediaGroup.DefaultTheme, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(_prompts.FallbackGroupName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Deliberately NOT FileKindCategories: those two lists answer different
        // questions. "Ultraschall" earns a subfolder in the collecting folder
        // AND makes a perfectly good folder name; "Screenshot" does neither.
        var isEmptyWord = !trimmed.Contains(' ') && _prompts.MeaninglessThemeWords.Contains(trimmed);
        return isEmptyWord ? null : trimmed;
    }

    /// <summary>The name of the place the group was shot at — a zone the user
    /// named beats the offline database.</summary>
    private static string? PlaceName(GroupContextInfo context) =>
        context.Kind == GroupContextKind.Home
            ? context.Place?.DisplayName
            : context.ZoneName ?? context.Place?.DisplayName;

    /// <summary>Adds the place the coordinates prove to a theme that does not
    /// name it: "Picknick im Park" becomes "Picknick im Park bei Beispielburg".
    /// At home the place says nothing about the occasion and stays out.
    ///
    /// <para>The rule sits here and not in the prompt because the prompt has
    /// asked for both since the beginning and the model still answers with the
    /// event alone.</para></summary>
    internal string WithPlace(string theme, GroupContextInfo context)
    {
        if (context.Kind == GroupContextKind.Home)
        {
            return theme;
        }

        var place = PlaceName(context);
        if (string.IsNullOrWhiteSpace(place) || MentionsPlace(theme, place))
        {
            return theme;
        }

        var combined = $"{theme} {_prompts.PlaceConnector} {place}";
        return combined.Length <= MaxThemeLength ? combined : theme;
    }

    /// <summary>Whether the theme already names the place. The short form counts
    /// too, or "Ausflug Musterstadt" would become "Ausflug Musterstadt bei
    /// Musterstadt am Neckar". A first word of three letters or less is no place
    /// name of its own — "Bad Cannstatt" against a theme about a bathroom.</summary>
    private static bool MentionsPlace(string theme, string place)
    {
        if (theme.Contains(place, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var first = place.Split(' ')[0];
        return first.Length > 3 && theme.Contains(first, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The chronologically first item of every thematic
    /// subgroup.</summary>
    internal static List<MediaItem> SelectSamples(MediaGroup group)
    {
        var sorted = group.Items.OrderBy(i => i.TakenLocal).ToList();

        var categorized = sorted.Where(i => !string.IsNullOrWhiteSpace(i.Category)).ToList();
        if (categorized.Count > 0)
        {
            return categorized
                .GroupBy(i => i.Category!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(i => i.TakenLocal).First())
                .OrderBy(i => i.TakenLocal)
                .ToList();
        }

        // Without model categories: chronological subclusters (gap > 2 h).
        return TimeClustering.SplitAtGaps(sorted, TimeSpan.FromHours(2))
            .Select(c => c[0])
            .ToList();
    }

    private string BuildContext(MediaGroup group, GroupContextInfo context)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        var sb = new StringBuilder();

        // The context class comes first — it decides the naming strategy.
        switch (context)
        {
            case { Kind: GroupContextKind.Home, ZoneName: { } zone }:
                sb.AppendLine($"KONTEXT: Diese Aufnahmen entstanden ZUHAUSE (\"{zone}\"). " +
                              "Verwende KEINEN Ortsnamen im Titel — benenne nach Personen, " +
                              "Aktivität oder Anlass (z.B. \"Mia spielt im Wohnzimmer\", " +
                              "\"Gartenarbeit\", \"Geburtstagsfeier\").");
                break;
            // A named zone beats the geocoded place: the user entered it
            // himself and means exactly that name. Without this, holiday and
            // other zones had no effect anywhere — they could be created, and
            // no folder ever mentioned them.
            case { Kind: GroupContextKind.Vacation, ZoneName: { } vz }:
                sb.AppendLine($"KONTEXT: Aufenthalt am selbst benannten Ort \"{vz}\" — " +
                              "sehr wahrscheinlich Urlaub oder Wochenendtrip. VERWENDE DIESEN NAMEN " +
                              $"im Titel, z.B. \"Urlaub {vz}\" oder genauer nach dem, was zu sehen ist.");
                break;
            case { Kind: GroupContextKind.DayTrip, ZoneName: { } dz }:
                sb.AppendLine($"KONTEXT: Ausflug zum selbst benannten Ort \"{dz}\". " +
                              $"VERWENDE DIESEN NAMEN im Titel, z.B. \"Besuch {dz}\" oder genauer.");
                break;
            case { Kind: GroupContextKind.Vacation, Place: { } vp }:
                sb.AppendLine($"KONTEXT: Mehrtägiger Aufenthalt fern von zuhause in/bei {vp.DisplayName} — " +
                              "sehr wahrscheinlich ein Urlaub oder Wochenendtrip. Bevorzugtes Titelmuster: " +
                              $"\"Urlaub {vp.Name}\" bzw. \"Wochenendtrip {vp.Name}\" oder spezifischer.");
                break;
            case { Kind: GroupContextKind.DayTrip, Place: { } dp }:
                sb.AppendLine($"KONTEXT: Tagesausflug in die Gegend von {dp.DisplayName}. " +
                              $"Bevorzugtes Titelmuster: \"Ausflug {dp.Name}\" oder spezifischer.");
                break;
            case { Place: { } up }:
                sb.AppendLine($"Nächstgelegener Ort laut Offline-Ortsdatenbank: {up.Name} ({up.CountryCode})");
                break;
        }

        // On a journey, name every station — it may be a round trip.
        if (context.Kind is GroupContextKind.Vacation or GroupContextKind.DayTrip)
        {
            var visited = Geo.PlaceResolver.ResolveVisitedPlaces(group.Items);
            if (visited.Count > 1)
            {
                sb.AppendLine("Besuchte Orte laut GPS (nach Häufigkeit): " +
                              string.Join(", ", visited.Select(p => p.DisplayName)));
            }
        }

        sb.AppendLine($"Zeitraum: {group.StartLocal.ToString("dddd, d. MMMM yyyy", de)}" +
                      (group.StartLocal.Date == group.EndLocal.Date
                          ? ""
                          : $" bis {group.EndLocal.ToString("dddd, d. MMMM yyyy", de)}"));
        sb.AppendLine($"Anzahl Aufnahmen: {group.Items.Count}");

        // People the face recognition is sure of are the strongest signal for
        // the title — by frequency, so that bystanders do not dominate.
        var people = group.Items
            .SelectMany(i => i.RecognizedPeople)
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Where(g => g.Count() * 4 >= group.Items.Count)
            .Select(g => $"{g.Key} (auf {g.Count()} von {group.Items.Count} Aufnahmen)")
            .Take(4)
            .ToList();
        if (people.Count > 0)
        {
            sb.AppendLine($"Sicher erkannte Personen: {string.Join(", ", people)}");
        }

        var notes = group.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.UserNote))
            .Select(i => i.UserNote!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        if (notes.Count > 0)
        {
            sb.AppendLine($"Nutzer-Notizen an Bildern dieser Gruppe: {string.Join(" | ", notes)}");
        }

        var categories = group.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Category))
            .GroupBy(i => i.Category!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .Take(6)
            .ToList();
        if (categories.Count > 0)
        {
            sb.AppendLine($"Erkannte Themen: {string.Join(", ", categories)}");
        }

        // MediaItem.LocationGuess is deliberately NOT offered here: it is the
        // place the model read off the picture, and the folder name would state
        // it as fact. Only the branches above name a place, and those come from
        // coordinates, a zone or the offline database.
        sb.AppendLine("Finde das Thema für den Ordnernamen.");
        return sb.ToString();
    }

    /// <summary>Makes a theme fit for the file system.</summary>
    public static string Sanitize(string theme)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(theme.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');

        if (cleaned.Length > MaxThemeLength)
        {
            cleaned = cleaned[..MaxThemeLength].Trim();
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "Aufnahmen" : cleaned;
    }

    /// <summary>Makes identical folder names (date + theme) unique.</summary>
    private static void EnsureUniqueFolderNames(List<MediaGroup> groups)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups.OrderBy(g => g.StartLocal))
        {
            if (group.IsDateless || seen.Add(group.FolderName))
            {
                continue;
            }

            var baseTheme = group.Theme;
            for (var n = 2; ; n++)
            {
                group.Theme = $"{baseTheme} {n}";
                if (seen.Add(group.FolderName))
                {
                    break;
                }
            }
        }
    }
}
