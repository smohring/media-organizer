using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaOrganizer.Core.Llm;

/// <summary>Fortschritt eines Modell-Downloads (ollama pull).</summary>
public readonly record struct PullProgress(string Status, long Total, long Completed)
{
    public double? Fraction => Total > 0 ? (double)Completed / Total : null;
}

/// <summary>Schlanker Client für die lokale Ollama-REST-API.</summary>
public sealed class OllamaClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public OllamaClient(string baseUrl)
    {
        // Vision-Inferenz auf CPU kann Minuten dauern — kein globales HTTP-Timeout,
        // Abbruch läuft über CancellationTokens.
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Health-Check: Ist der Ollama-Dienst erreichbar?</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync("api/version", timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Namen aller lokal vorhandenen Modelle (z.B. "gemma4:e4b").</summary>
    public async Task<IReadOnlyList<string>> ListLocalModelsAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var response = await _http.GetFromJsonAsync<TagsResponse>("api/tags", JsonOptions, timeout.Token)
            .ConfigureAwait(false);
        return response?.Models?.Select(m => m.Name).ToList() ?? [];
    }

    /// <summary>Lädt ein Modell herunter und streamt den Fortschritt.</summary>
    public async IAsyncEnumerable<PullProgress> PullModelAsync(
        string model,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pull")
        {
            Content = JsonContent.Create(new { model, stream = true }, options: JsonOptions),
        };

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            PullStatusLine? status = null;
            try
            {
                status = JsonSerializer.Deserialize<PullStatusLine>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // Unerwartete Zeile ignorieren.
            }

            if (status is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(status.Error))
            {
                throw new InvalidOperationException($"Ollama-Pull fehlgeschlagen: {status.Error}");
            }

            yield return new PullProgress(status.Status ?? "", status.Total, status.Completed);
        }
    }

    /// <summary>Chat-Aufruf mit optionalen Bildern; erwartet und liefert die
    /// Antwort als Text (bei jsonMode als JSON-String).</summary>
    public async Task<string> ChatAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<byte[]>? images = null,
        bool jsonMode = true,
        int numPredict = 700,
        CancellationToken ct = default)
    {
        var request = new ChatRequest
        {
            Model = model,
            Stream = false,
            Format = jsonMode ? "json" : null,
            // Modell während eines Laufs im Speicher halten (kein Reload-Hit).
            KeepAlive = "30m",
            // Every model in the catalogue can "think" first, and does so by
            // default: invisible thinking tokens cost a multiple of the answer
            // time and can crowd out the answer itself when num_predict is
            // tight. Switched off for our structured JSON tasks.
            Think = false,
            // Temperature 0 (greedy): deterministische Antworten, damit z.B.
            // Wiederholungsläufe dieselben Ordnernamen erzeugen.
            Options = new ChatOptions { Temperature = 0.0, NumPredict = numPredict },
            Messages =
            [
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage
                {
                    Role = "user",
                    Content = userPrompt,
                    Images = images?.Select(Convert.ToBase64String).ToList(),
                },
            ],
        };

        using var response = await _http
            .PostAsJsonAsync("api/chat", request, JsonOptions, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Ältere Ollama-Versionen/Modelle kennen den think-Parameter nicht:
            // einmalig ohne ihn wiederholen.
            request.Think = null;
            using var retry = await _http
                .PostAsJsonAsync("api/chat", request, JsonOptions, ct)
                .ConfigureAwait(false);
            retry.EnsureSuccessStatusCode();
            var retryChat = await retry.Content
                .ReadFromJsonAsync<ChatResponse>(JsonOptions, ct)
                .ConfigureAwait(false);
            return retryChat?.Message?.Content ?? "";
        }

        response.EnsureSuccessStatusCode();

        var chat = await response.Content
            .ReadFromJsonAsync<ChatResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return chat?.Message?.Content ?? "";
    }

    public void Dispose() => _http.Dispose();

    // --- DTOs (snake_case über JsonOptions) ---

    private sealed class TagsResponse
    {
        public List<TagModel>? Models { get; set; }
    }

    private sealed class TagModel
    {
        public string Name { get; set; } = "";
    }

    private sealed class PullStatusLine
    {
        public string? Status { get; set; }

        public long Total { get; set; }

        public long Completed { get; set; }

        public string? Error { get; set; }
    }

    private sealed class ChatRequest
    {
        public required string Model { get; set; }

        public required List<ChatMessage> Messages { get; set; }

        public bool Stream { get; set; }

        public string? Format { get; set; }

        [JsonPropertyName("keep_alive")]
        public string? KeepAlive { get; set; }

        public bool? Think { get; set; }

        public ChatOptions? Options { get; set; }
    }

    private sealed class ChatMessage
    {
        public required string Role { get; set; }

        public required string Content { get; set; }

        public List<string>? Images { get; set; }
    }

    private sealed class ChatOptions
    {
        public double Temperature { get; set; }

        [JsonPropertyName("num_predict")]
        public int NumPredict { get; set; }
    }

    private sealed class ChatResponse
    {
        public ChatMessage? Message { get; set; }
    }
}
