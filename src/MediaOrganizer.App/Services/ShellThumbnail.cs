using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace MediaOrganizer.App.Services;

/// <summary>Vorschaubilder über den Windows-Shell-Thumbnail-Anbieter
/// (<c>IShellItemImageFactory</c>) — liefert echte Einzelbilder aus Videos,
/// ohne dass ffmpeg installiert sein muss. Damit bekommen Videos auch auf
/// Systemen ohne ffmpeg eine Inhaltsanalyse (und ein Thumbnail in der
/// Ergebnisansicht).</summary>
internal static class ShellThumbnail
{
    public static BitmapSource? Get(string path, int maxDimension)
    {
        IShellItemImageFactory? factory = null;
        var hBitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);

            var size = new NativeSize { Width = maxDimension, Height = maxDimension };
            // THUMBNAILONLY: niemals auf ein generisches Datei-Icon ausweichen —
            // ein Icon wäre für die Bildanalyse wertlos.
            factory.GetImage(size, SIIGBF.ResizeToFit | SIIGBF.ThumbnailOnly, out hBitmap);
            if (hBitmap == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            // Kein Thumbnail-Anbieter für dieses Format o.ä.
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }

            if (factory is not null)
            {
                Marshal.ReleaseComObject(factory);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingNameRaw(
        string path, IntPtr bindingContext, ref Guid riid, out IntPtr item);

    private static void SHCreateItemFromParsingName(
        string path, IntPtr bindingContext, ref Guid riid, out IShellItemImageFactory factory)
    {
        Marshal.ThrowExceptionForHR(
            SHCreateItemFromParsingNameRaw(path, bindingContext, ref riid, out var raw));
        factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(raw);
        Marshal.Release(raw);
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;

        public int Height;
    }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(NativeSize size, SIIGBF flags, out IntPtr phbm);
    }
}
