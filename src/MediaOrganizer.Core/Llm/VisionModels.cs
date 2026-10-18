namespace MediaOrganizer.Core.Llm;

/// <summary>A selectable vision model, with a hint at what it needs in
/// memory.</summary>
public sealed record VisionModel(string Tag, string DisplayName, string RamHint)
{
    public string DisplayWithRam => $"{DisplayName}  ·  {RamHint}";
}

/// <summary>The catalogue of supported vision models. Every entry must be
/// multimodal (image input) and able to follow a JSON-structured prompt — the
/// same model does analysis, grouping and naming.
///
/// <para>Deliberately nothing below 12B. The small Gemma variants (E2B/E4B) are
/// multimodal too, but they describe real family and holiday photos wrongly —
/// "a pair of feet" for a portrait of a child. No sensible thematic grouping is
/// possible on descriptions like that, and they are not even faster at
/// producing them.</para></summary>
public static class VisionModels
{
    /// <summary>The 26B entry, as Unsloth's quantisation-aware build: it is the
    /// same Gemma 4 26B-A4B, but 15 GB instead of 19 GB, so it still fits a
    /// 16 GB card whole.</summary>
    public const string Gemma26BQat = "hf.co/unsloth/gemma-4-26B-A4B-it-qat-GGUF:UD-Q4_K_XL";

    /// <summary>The Ollama build the QAT one replaced. Kept only to translate
    /// stored settings — see <see cref="EnsureSupported"/>.</summary>
    private const string RetiredGemma26B = "gemma4:26b";

    public static IReadOnlyList<VisionModel> All { get; } =
    [
        new("gemma4:12b", "Gemma 4 12B", "~8 GB RAM"),
        new(Gemma26BQat, "Gemma 4 26B (MoE, QAT)", "~16 GB RAM"),
    ];

    /// <summary>Falls back to the smallest supported model when the settings
    /// name one that is no longer offered — which they do on installations
    /// configured before an entry was dropped. The retired 26B build is
    /// answered with the QAT build of the same model instead of a silent
    /// downgrade to 12B.</summary>
    public static string EnsureSupported(string? tag)
    {
        if (All.Any(m => m.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)))
        {
            return tag!;
        }

        return string.Equals(tag, RetiredGemma26B, StringComparison.OrdinalIgnoreCase)
            ? Gemma26BQat
            : All[0].Tag;
    }
}
