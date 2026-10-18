using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;
using MediaOrganizer.Core.Video;

namespace MediaOrganizer.Core.Llm;

/// <summary>Produces one compact content description per medium through the
/// configured vision model (images directly, videos as a frame sequence) and
/// writes it into the <see cref="MediaItem"/>. Every medium is analysed on its
/// own — there is deliberately no inheritance of descriptions between "similar"
/// images any more (too error-prone on real photos). Optionally with a
/// persistent cache.</summary>
public sealed class MediaDescriber(
    OllamaClient ollama,
    string model,
    IImagePreprocessor imagePreprocessor,
    FfmpegService ffmpeg,
    DescriptionCache? cache = null,
    IReadOnlyList<string>? knowledgeFacts = null,
    Prompts? prompts = null)
{
    private readonly Prompts _prompts = prompts ?? Prompts.German;

    private readonly string _languageSalt = LanguageSalt(prompts);

    /// <summary>Language part of the cache key.
    ///
    /// <para>Empty for German on purpose. German is what every cache entry
    /// written before this setting existed was produced with; an unconditional
    /// suffix would invalidate all of them for nothing. Because the salt only
    /// grows for other languages, descriptions in both languages sit in the
    /// cache side by side and switching back finds the old ones.</para></summary>
    internal static string LanguageSalt(Prompts? prompts) =>
        prompts is null || ReferenceEquals(prompts, Prompts.German) ? "" : "|lang:en";

    /// <summary>Wissensbasis-Fakten im Vision-Prompt: Damit benennt das Modell
    /// bekannte Personen direkt beim Namen ("Mia hält eine Glasflasche"
    /// statt "Ein kleines Kind …"). Der System-Prompt enthält dazu eine
    /// strikte Anti-Halluzinations-Regel — verpflichtend, weil zu kleine
    /// Modelle daraus sonst Personen erfinden (daher auch erst ab 12B).</summary>
    private readonly string _systemPrompt =
        (prompts ?? Prompts.German).WithKnowledge(
            (prompts ?? Prompts.German).DescribeSystem, knowledgeFacts ?? []);

    /// <summary>Fakten-Anteil des Cache-Schlüssels. Bewusst ein stabiler Hash:
    /// <c>string.GetHashCode()</c> ist in .NET pro Prozess zufällig, der
    /// Schlüssel änderte sich damit bei jedem App-Start und der Cache traf nie.</summary>
    private readonly string _knowledgeSalt = knowledgeFacts is { Count: > 0 }
        ? "|k:" + StableHash(string.Join(";", knowledgeFacts))
        : "";

    /// <summary>Version des Beschreibungs-Prompts — Teil des Cache-Schlüssels,
    /// damit Prompt-Änderungen alte (ggf. fehlerhafte) Einträge invalidieren.
    /// v5: Namensnennung bei erkannten Personen ist jetzt Pflicht.
    /// v6: Ohne ausdrückliche Nennung darf KEIN Name verwendet werden — vorher
    /// setzte das Modell Namen aus den Hintergrund-Fakten ein, sobald jemand
    /// dazu zu passen schien. Alte Einträge können solche geratenen Namen
    /// enthalten und müssen deshalb verfallen.
    /// v7: Fakten über Gegenstände und Bauprojekte müssen jetzt auch auf
    /// Ausschnitte und Zwischenstände angewendet werden. Alte Einträge
    /// beschreiben genau diese Bilder noch allgemein ("eine Holzplatte") und
    /// müssen verfallen.</summary>
    private const string PromptVersion = "v7";

    private static string StableHash(string text)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes.AsSpan(0, 4));
    }

    /// <summary>Lädt das Analyse-Modell vor, damit der erste echte Aufruf
    /// nicht den Lade-Hit zahlt (keep_alive hält es danach im Speicher).</summary>
    public Task WarmupAsync(CancellationToken ct) =>
        ollama.ChatAsync(model, _prompts.WarmupSystem, "{}", numPredict: 5, ct: ct);

    /// <summary>Wendet eine bereits gespeicherte Beschreibung an, ohne das
    /// Modell zu fragen; false, wenn nichts gespeichert ist.
    ///
    /// <para>Eigenständig aufrufbar, damit die Serienerkennung zuerst den
    /// Cache abklopfen kann: Eine früher erzeugte, echte Beschreibung dieses
    /// Bildes ist immer besser als eine von der Nachbaraufnahme
    /// übernommene.</para></summary>
    public bool TryApplyCache(MediaItem item) => TryApplyCache(item, ComputeCacheKey(item));

    /// <summary>Fassung mit bereits berechnetem Schlüssel — sein Aufbau kostet
    /// einen Lesezugriff auf die Datei und soll sich je Aufnahme nicht
    /// wiederholen.</summary>
    private bool TryApplyCache(MediaItem item, string? cacheKey)
    {
        if (cacheKey is null || !cache!.TryApply(cacheKey, item))
        {
            return false;
        }

        RunLog.Log($"Analyse {item.FileName}: CACHE-Treffer -> \"{RunLog.Truncate(item.Description ?? "", 80)}\"");
        return true;
    }

    /// <summary>Schlüssel für den Beschreibungs-Cache; null ohne Cache oder
    /// wenn die Datei nicht lesbar ist.
    /// <para>Notiz und erkannte Personen gehören hinein: Beide verändern den
    /// Prompt, also muss sich auch der Schlüssel ändern.</para></summary>
    private string? ComputeCacheKey(MediaItem item)
    {
        if (cache is null)
        {
            return null;
        }

        var peopleSalt = item.RecognizedPeopleWithPosition.Count > 0
            ? $"|p{item.DetectedFaceCount}:" + string.Join(",", item.RecognizedPeopleWithPosition.OrderBy(p => p))
            : "";
        var cacheSalt = string.IsNullOrWhiteSpace(item.UserNote)
            ? $"{PromptVersion}|{model}{_languageSalt}{_knowledgeSalt}{peopleSalt}"
            : $"{PromptVersion}|{model}{_languageSalt}{_knowledgeSalt}{peopleSalt}|note:{item.UserNote.Trim()}";
        return DescriptionCache.ComputeKey(item.SourcePath, cacheSalt);
    }

    /// <summary>Beschreibt das Medium; liefert false, wenn keine Analyse möglich
    /// war (nicht dekodierbar, kein ffmpeg, LLM-Antwort unbrauchbar).</summary>
    public async Task<bool> DescribeAsync(MediaItem item, CancellationToken ct)
    {
        var cacheKey = ComputeCacheKey(item);
        if (TryApplyCache(item, cacheKey))
        {
            return true;
        }

        List<byte[]> images;
        string userPrompt;

        if (item.Kind == MediaKind.Image)
        {
            var jpeg = imagePreprocessor.GetJpegForAnalysis(item.SourcePath);
            if (jpeg is null || jpeg.Length == 0)
            {
                RunLog.Log($"Analyse {item.FileName}: Bild nicht dekodierbar — keine Analyse.");
                return false;
            }

            images = [jpeg];
            userPrompt = _prompts.DescribeUserImage;
        }
        else
        {
            images = await ffmpeg.ExtractFramesAsync(item.SourcePath, item.Duration, ct: ct)
                .ConfigureAwait(false);
            images = images.Where(f => f.Length > 0).ToList();

            if (images.Count == 0)
            {
                // Ohne ffmpeg: Einzelbild über den Windows-Thumbnail-Anbieter.
                // Besser eine Analyse aus einem Frame als gar keine — sonst
                // fehlt dem Video jedes inhaltliche Signal für die Gruppierung.
                if (imagePreprocessor.GetVideoFrameJpeg(item.SourcePath) is { Length: > 0 } shellFrame)
                {
                    images = [shellFrame];
                    RunLog.Log($"Analyse {item.FileName}: Video-Frame über Windows-Thumbnail (kein ffmpeg).");
                }
                else
                {
                    RunLog.Log($"Analyse {item.FileName}: kein Video-Frame verfügbar — keine Analyse.");
                    return false;
                }
            }

            userPrompt = _prompts.DescribeUserVideo(images.Count);
        }

        userPrompt = _prompts.WithRecognizedPeople(
            userPrompt, item.RecognizedPeopleWithPosition, item.UnidentifiedPositions);
        userPrompt = _prompts.WithUserNote(userPrompt, item.UserNote);
        var raw = await ollama
            .ChatAsync(model, _systemPrompt, userPrompt, images, numPredict: 220, ct: ct)
            .ConfigureAwait(false);

        using var json = LlmJson.TryParse(raw);
        if (json is null)
        {
            RunLog.Log($"Analyse {item.FileName}: unbrauchbare LLM-Antwort: {RunLog.Truncate(raw, 200)}");
            return false;
        }

        var root = json.RootElement;
        item.Description = LlmJson.GetString(root, "beschreibung")?.Trim();
        item.LocationGuess = LlmJson.GetString(root, "ort")?.Trim();
        item.Category = LlmJson.GetString(root, "kategorie")?.Trim();

        var usable = !string.IsNullOrWhiteSpace(item.Description) || !string.IsNullOrWhiteSpace(item.Category);
        RunLog.Log($"Analyse {item.FileName}: frisch ({images.Count} Bild(er), " +
                   $"{images.Sum(i => i.Length) / 1024} KB) -> Kat={item.Category} " +
                   $"\"{RunLog.Truncate(item.Description ?? "", 80)}\"");
        if (usable && cacheKey is not null)
        {
            cache!.Store(cacheKey, item);
        }

        return usable;
    }
}
