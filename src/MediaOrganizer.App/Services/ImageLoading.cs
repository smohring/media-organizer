using System.IO;
using System.Windows.Media.Imaging;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.App.Services;

/// <summary>Bild-Dekodierung über WPF/Windows-Codecs (inkl. HEIC, sofern die
/// HEIF-Erweiterung installiert ist) mit EXIF-Orientierungskorrektur.
/// Thread-sicher: alle gelieferten Quellen sind eingefroren.</summary>
public static class ImageLoading
{
    public static BitmapSource? LoadScaled(string path, int maxDimension)
    {
        try
        {
            var (pixelWidth, pixelHeight, rotation) = ReadHeader(path);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // IgnoreImageCache: Die App verschiebt Dateien — WPFs URI-Cache
            // könnte sonst veraltete Bilder für wiederverwendete Pfade liefern.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
            if (pixelWidth >= pixelHeight)
            {
                bmp.DecodePixelWidth = Math.Min(maxDimension, Math.Max(1, pixelWidth));
            }
            else
            {
                bmp.DecodePixelHeight = Math.Min(maxDimension, Math.Max(1, pixelHeight));
            }

            bmp.Rotation = rotation;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource? FromBytes(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = stream;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static byte[] EncodeJpeg(BitmapSource source, int quality = 82)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static (int Width, int Height, Rotation Rotation) ReadHeader(string path)
    {
        using var fs = File.OpenRead(path);
        var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;

        var rotation = Rotation.Rotate0;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata
                && metadata.GetQuery("System.Photo.Orientation") is ushort orientation)
            {
                rotation = orientation switch
                {
                    6 => Rotation.Rotate90,
                    3 => Rotation.Rotate180,
                    8 => Rotation.Rotate270,
                    _ => Rotation.Rotate0,
                };
            }
        }
        catch
        {
            // Formate ohne EXIF-Metadaten-Query.
        }

        return (width, height, rotation);
    }
}

/// <summary>IImagePreprocessor-Implementierung für den Core: dekodiert und
/// verkleinert Bilder zu JPEG für die LLM-Analyse.</summary>
public sealed class WpfImagePreprocessor : IImagePreprocessor
{
    public byte[]? GetJpegForAnalysis(string imagePath, int maxDimension = 896)
    {
        var source = ImageLoading.LoadScaled(imagePath, maxDimension);
        return source is null ? null : ImageLoading.EncodeJpeg(source);
    }

    public byte[]? GetVideoFrameJpeg(string videoPath, int maxDimension = 896)
    {
        var source = ShellThumbnail.Get(videoPath, maxDimension);
        return source is null ? null : ImageLoading.EncodeJpeg(source);
    }

    public bool TryWriteDescription(string imagePath, string description) =>
        MetadataWriter.TryWrite(imagePath, description);
}
