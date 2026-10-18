namespace MediaOrganizer.Core.Services;

/// <summary>Abstraction over image preparation (decode and scale down to
/// JPEG) — implemented in the application layer with WPF imaging, so that the
/// core stays UI-free while HEIC and friends still go through the Windows
/// codecs.</summary>
public interface IImagePreprocessor
{
    /// <summary>The file as a scaled-down JPEG for model analysis, or null
    /// when the format cannot be decoded.</summary>
    byte[]? GetJpegForAnalysis(string imagePath, int maxDimension = 896);

    /// <summary>A single frame from a video through the Windows shell
    /// (thumbnail provider) — the fallback when ffmpeg is unavailable, so
    /// videos do not go without content analysis. Null when Windows cannot
    /// produce a preview.</summary>
    byte[]? GetVideoFrameJpeg(string videoPath, int maxDimension = 896);

    /// <summary>Writes the description losslessly into the image file's
    /// metadata, as XMP <c>dc:description</c> and nothing else. Returns false
    /// when the format does not allow it; the file is then left
    /// unchanged.</summary>
    bool TryWriteDescription(string imagePath, string description);
}
