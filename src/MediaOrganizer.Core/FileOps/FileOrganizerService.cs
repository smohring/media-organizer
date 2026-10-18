using System.Security.Cryptography;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.FileOps;

/// <summary>Moves or copies files into target folders, with no way of losing
/// data:
/// <list type="bullet">
/// <item>Nothing is ever overwritten.</item>
/// <item>Name collision plus identical content (size and SHA-256): the file
/// is skipped and the source stays untouched, in move mode too.</item>
/// <item>Name collision with different content: a unique name, "name (2).ext"
/// and so on.</item>
/// </list></summary>
public sealed class FileOrganizerService
{
    /// <summary>The target folder according to the configured hierarchy. A
    /// dateless folder — the collecting folder, the everyday folder — sits
    /// directly in the base folder beside the year levels, unless it was split
    /// per period, in which case it sits inside the level of its own
    /// shots.</summary>
    public static string BuildTargetDirectory(string baseFolder, TargetHierarchy hierarchy, MediaGroup group)
    {
        if (group.IsDateless && !group.IsPeriodScoped)
        {
            return Path.Combine(baseFolder, group.FolderName);
        }

        var start = group.StartLocal;
        return hierarchy switch
        {
            TargetHierarchy.Year => Path.Combine(baseFolder, start.ToString("yyyy"), group.FolderName),
            TargetHierarchy.YearMonth => Path.Combine(baseFolder, start.ToString("yyyy"), start.ToString("MM"), group.FolderName),
            _ => Path.Combine(baseFolder, group.FolderName),
        };
    }

    /// <summary>How often a lost race for the same name is retried before the
    /// file is reported as failed. Only another program writing into the target
    /// folder at the same moment can cause one, and it cannot keep winning.</summary>
    private const int MaxAttempts = 5;

    /// <summary>Transfer of a single file — used by <c>PlanExecutor</c> and
    /// for corrections made afterwards from the result view.</summary>
    public static (string? FinalPath, FileOpStatus Status) TransferFile(
        string sourcePath,
        string targetDirectory,
        TransferMode mode)
    {
        Directory.CreateDirectory(targetDirectory);
        var fileName = Path.GetFileName(sourcePath);

        for (var attempt = 1; ; attempt++)
        {
            if (FindFreeName(sourcePath, targetDirectory, fileName) is not { } targetPath)
            {
                return (null, FileOpStatus.SkippedDuplicate);
            }

            try
            {
                if (mode == TransferMode.Move)
                {
                    File.Move(sourcePath, targetPath);
                    return (targetPath, FileOpStatus.Moved);
                }

                File.Copy(sourcePath, targetPath);
                return (targetPath, FileOpStatus.Copied);
            }
            catch (IOException) when (attempt < MaxAttempts && File.Exists(targetPath))
            {
                // Between choosing the free name and writing it, another
                // program created exactly that file. Neither Move nor Copy is
                // called with "overwrite", so .NET threw instead of destroying
                // it — that is the guarantee. Look for the next free name
                // rather than failing the file.
                //
                // A locked source or a full disk throws the same exception, but
                // then the target does not exist and the filter lets it through.
            }
        }
    }

    /// <summary>The path to write to: the file name itself when it is free,
    /// otherwise "name (2)", "name (3)" and so on. Null means the file is
    /// already in the target under one of those names, with the same content —
    /// then nothing is written at all.
    ///
    /// <para>Every occupied candidate is compared by content, not just the
    /// bare name. Comparing only the bare name meant that a target already
    /// holding a DIFFERENT file under that name sent two identical shots to
    /// "name (2)" and "name (3)": the second one never saw the copy of itself
    /// that the first one had just written.</para></summary>
    private static string? FindFreeName(string sourcePath, string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        for (var n = 1; ; n++)
        {
            var candidate = n == 1
                ? Path.Combine(directory, fileName)
                : Path.Combine(directory, $"{stem} ({n}){ext}");

            if (!File.Exists(candidate))
            {
                return candidate;
            }

            if (AreIdentical(sourcePath, candidate))
            {
                return null;
            }
        }
    }

    private static bool AreIdentical(string a, string b)
    {
        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length)
        {
            return false;
        }

        using var sha = SHA256.Create();
        using var streamA = File.OpenRead(a);
        var hashA = sha.ComputeHash(streamA);
        sha.Initialize();
        using var streamB = File.OpenRead(b);
        var hashB = sha.ComputeHash(streamB);
        return hashA.AsSpan().SequenceEqual(hashB);
    }
}
