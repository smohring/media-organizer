using System.Text;
using System.Text.Json;
using MediaOrganizer.Core.Knowledge;

namespace MediaOrganizer.Core.Llm;

/// <summary>Distils the user's new image notes into lasting knowledge base
/// facts through the model — "my daughter Mia playing" becomes "Mia is the
/// user's daughter". Runs once per run, and only over notes not yet
/// processed.</summary>
public sealed class KnowledgeDistiller(OllamaClient ollama, string model, Prompts? prompts = null)
{
    private readonly Prompts _prompts = prompts ?? Prompts.German;

    public async Task DistillAsync(KnowledgeStore store, CancellationToken ct)
    {
        var pending = store.UndistilledNotes;
        if (pending.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        if (store.Facts.Count > 0)
        {
            sb.AppendLine(_prompts.DistillKnownFactsHeading);
            foreach (var fact in store.Facts)
            {
                sb.AppendLine($"- {fact}");
            }

            sb.AppendLine();
        }

        sb.AppendLine(_prompts.DistillNewNotesHeading);
        foreach (var (_, note, fileName) in pending)
        {
            sb.AppendLine($"- \"{note}\" {_prompts.DistillNoteSource(fileName)}");
        }

        string raw;
        try
        {
            raw = await ollama.ChatAsync(model, _prompts.DistillSystem, sb.ToString(), ct: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Distillation is a convenience — the notes stay unprocessed and
            // are tried again on the next run.
            return;
        }

        using var json = LlmJson.TryParse(raw);
        if (json is null
            || !json.RootElement.TryGetProperty("fakten", out var fakten)
            || fakten.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var fact in fakten.EnumerateArray())
        {
            if (fact.ValueKind == JsonValueKind.String && fact.GetString() is { } text)
            {
                store.AddFact(text);
            }
        }

        // Even with an empty list of facts the notes count as processed — an
        // empty result is a good result, not a failure to retry forever.
        store.MarkDistilled(pending.Select(p => p.Key));
        store.Save();
    }
}
