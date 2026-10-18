using System.IO;

namespace MediaOrganizer.App.Services;

/// <summary>Replaces a file only after the new content has been written in
/// full — through a temporary file and
/// <see cref="File.Replace(string,string,string)"/> with a backup copy. If
/// anything breaks off, the original is left untouched.</summary>
internal static class AtomicFile
{
    private const string TempSuffix = ".moTmp";
    private const string BackupSuffix = ".moBak";

    public static bool TryReplace(string path, byte[] content)
    {
        var temp = path + TempSuffix;
        var backup = path + BackupSuffix;
        try
        {
            File.WriteAllBytes(temp, content);
            File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            File.Delete(backup);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            Cleanup(temp, backup);
            return false;
        }
    }

    private static void Cleanup(params string[] leftovers)
    {
        foreach (var leftover in leftovers)
        {
            try
            {
                if (File.Exists(leftover))
                {
                    File.Delete(leftover);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cleaning up is best effort.
            }
        }
    }
}
