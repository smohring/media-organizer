using System.IO;

namespace MediaOrganizer.App.Services;

/// <summary>Writes descriptions losslessly into JPEG metadata, as XMP
/// <c>dc:description</c> and nothing else.
///
/// <para>It used to write the two EXIF fields <c>ImageDescription</c> and
/// <c>XPComment</c> as well, on the assumption that Explorer reads no XMP.
/// Measured, that is wrong, and the three sinks showed up as three filled
/// rows in the properties dialog; XMP alone fills just one.
/// <see cref="JpegExifCleaner"/> takes those two out of files an older
/// version wrote.</para>
///
/// <para><see cref="JpegXmpWriter"/> works at JPEG segment level: the
/// compressed image data is taken over byte for byte, there is NO
/// re-encoding. WPF's <c>InPlaceBitmapMetadataWriter</c>, used earlier,
/// failed on camera JPEGs because those carry no metadata padding.</para></summary>
internal static class MetadataWriter
{
    public static bool TryWrite(string imagePath, string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        var extension = Path.GetExtension(imagePath);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            // JPEG only: any other format would have to be re-encoded, and
            // that is data loss.
            return false;
        }

        // Both steps regardless of each other: a file described by an older
        // version carries the two EXIF fields, and they are what fills the
        // other two rows of the properties dialog.
        JpegExifCleaner.TryClean(imagePath);
        return JpegXmpWriter.TryWrite(imagePath, description);
    }
}
