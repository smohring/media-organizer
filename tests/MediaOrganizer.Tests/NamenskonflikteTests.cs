using MediaOrganizer.Core.FileOps;
using MediaOrganizer.Core.Models;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Transferring a file into a target that already holds that name.
///
/// <para>The promise the whole application rests on: nothing is ever
/// overwritten and nothing is ever deleted. These tests run against real files
/// in a temporary folder — the interesting part is exactly the interplay with
/// the file system, and a stub would prove nothing.</para></summary>
public sealed class NamenskonflikteTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MediaOrganizerTests", Guid.NewGuid().ToString("N"));

    private string Source => Path.Combine(_root, "quelle");

    private string Target => Path.Combine(_root, "ziel");

    public NamenskonflikteTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder left behind is not worth failing a test over.
        }
    }

    private string WriteSource(string name, string content)
    {
        var path = Path.Combine(Source, name);
        File.WriteAllText(path, content);
        return path;
    }

    private void WriteTarget(string name, string content) =>
        File.WriteAllText(Path.Combine(Target, name), content);

    private (string? FinalPath, FileOpStatus Status) Transfer(
        string sourcePath, TransferMode mode = TransferMode.Copy) =>
        FileOrganizerService.TransferFile(sourcePath, Target, mode);

    private string[] TargetFiles() =>
        [.. Directory.GetFiles(Target).Select(Path.GetFileName).OrderBy(n => n)!];

    // --- The straightforward cases ---

    [Fact]
    public void AFreeNameIsUsedAsItIs()
    {
        var source = WriteSource("IMG_1.jpg", "A");

        var (finalPath, status) = Transfer(source);

        Assert.Equal(FileOpStatus.Copied, status);
        Assert.Equal(Path.Combine(Target, "IMG_1.jpg"), finalPath);
        Assert.True(File.Exists(source), "Copying must leave the source in place.");
    }

    [Fact]
    public void MovingTakesTheSourceWithIt()
    {
        var source = WriteSource("IMG_1.jpg", "A");

        var (_, status) = Transfer(source, TransferMode.Move);

        Assert.Equal(FileOpStatus.Moved, status);
        Assert.False(File.Exists(source));
    }

    // --- Name taken ---

    [Fact]
    public void ADifferentFileOfTheSameNameDodgesToTwo()
    {
        WriteTarget("IMG_1.jpg", "already here");
        var source = WriteSource("IMG_1.jpg", "something else");

        var (finalPath, status) = Transfer(source);

        Assert.Equal(FileOpStatus.Copied, status);
        Assert.Equal(Path.Combine(Target, "IMG_1 (2).jpg"), finalPath);
        Assert.Equal("already here", File.ReadAllText(Path.Combine(Target, "IMG_1.jpg")));
    }

    [Fact]
    public void AnIdenticalFileIsSkippedAndTheSourceKept()
    {
        WriteTarget("IMG_1.jpg", "same");
        var source = WriteSource("IMG_1.jpg", "same");

        var (finalPath, status) = Transfer(source, TransferMode.Move);

        Assert.Equal(FileOpStatus.SkippedDuplicate, status);
        Assert.Null(finalPath);
        Assert.True(File.Exists(source), "Nothing is deleted, not even in move mode.");
        Assert.Single(TargetFiles());
    }

    /// <summary>The reported gap. The target holds a different file under the
    /// name, so the first shot dodges to "(2)". The second shot is identical to
    /// the first — and used to compare itself only against the bare name, miss
    /// the copy of itself that had just been written, and land as "(3)".</summary>
    [Fact]
    public void ASecondIdenticalShotDoesNotBecomeAThirdCopy()
    {
        WriteTarget("IMG_1.jpg", "a stranger");
        var first = WriteSource("IMG_1.jpg", "the same shot");
        Directory.CreateDirectory(Path.Combine(Source, "zweites Geraet"));
        var second = Path.Combine(Source, "zweites Geraet", "IMG_1.jpg");
        File.WriteAllText(second, "the same shot");

        var firstResult = Transfer(first);
        var secondResult = Transfer(second);

        Assert.Equal(FileOpStatus.Copied, firstResult.Status);
        Assert.Equal(FileOpStatus.SkippedDuplicate, secondResult.Status);
        Assert.Equal(["IMG_1 (2).jpg", "IMG_1.jpg"], TargetFiles());
    }

    /// <summary>Three genuinely different files of the same name count up
    /// instead of piling onto one number.</summary>
    [Fact]
    public void DifferentFilesCountUp()
    {
        WriteTarget("IMG_1.jpg", "one");
        foreach (var content in new[] { "two", "three" })
        {
            var directory = Path.Combine(Source, content);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "IMG_1.jpg");
            File.WriteAllText(path, content);
            Transfer(path);
        }

        Assert.Equal(["IMG_1 (2).jpg", "IMG_1 (3).jpg", "IMG_1.jpg"], TargetFiles());
        Assert.Equal("two", File.ReadAllText(Path.Combine(Target, "IMG_1 (2).jpg")));
        Assert.Equal("three", File.ReadAllText(Path.Combine(Target, "IMG_1 (3).jpg")));
    }

    /// <summary>A file identical to a dodged version is recognised there too,
    /// however far down the numbering it sits.</summary>
    [Fact]
    public void AnIdenticalDodgedVersionIsFoundAsWell()
    {
        WriteTarget("IMG_1.jpg", "one");
        WriteTarget("IMG_1 (2).jpg", "two");
        WriteTarget("IMG_1 (3).jpg", "three");
        var source = WriteSource("IMG_1.jpg", "three");

        var (_, status) = Transfer(source);

        Assert.Equal(FileOpStatus.SkippedDuplicate, status);
        Assert.Equal(3, TargetFiles().Length);
    }

    /// <summary>Same size, different content — the case a length comparison
    /// alone would get wrong, and the reason the check hashes.</summary>
    [Fact]
    public void SameSizeIsNotTheSameFile()
    {
        WriteTarget("IMG_1.jpg", "AAAA");
        var source = WriteSource("IMG_1.jpg", "BBBB");

        var (finalPath, status) = Transfer(source);

        Assert.Equal(FileOpStatus.Copied, status);
        Assert.Equal(Path.Combine(Target, "IMG_1 (2).jpg"), finalPath);
    }

    [Fact]
    public void AFileWithoutAnExtensionIsHandledToo()
    {
        WriteTarget("SNAPSHOT", "one");
        var source = WriteSource("SNAPSHOT", "two");

        var (finalPath, _) = Transfer(source);

        Assert.Equal(Path.Combine(Target, "SNAPSHOT (2)"), finalPath);
    }

    /// <summary>Nothing in the target is ever changed, whatever happens.</summary>
    [Fact]
    public void TheExistingFileIsNeverTouched()
    {
        WriteTarget("IMG_1.jpg", "untouchable");
        var written = File.GetLastWriteTimeUtc(Path.Combine(Target, "IMG_1.jpg"));
        var source = WriteSource("IMG_1.jpg", "new content");

        Transfer(source, TransferMode.Move);

        Assert.Equal("untouchable", File.ReadAllText(Path.Combine(Target, "IMG_1.jpg")));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Path.Combine(Target, "IMG_1.jpg")));
    }
}
