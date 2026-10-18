using MediaOrganizer.Core.Metadata;
using Xunit;

namespace MediaOrganizer.Tests;

/// <summary>Reading the capture date out of the file name.
///
/// <para>Reported case: 4867 of 16 176 files had no capture metadata, and the
/// file system timestamp put them on the day of the backup — thousands of
/// messenger images and screenshots landed in whatever event happened to be
/// near that date. Their name held the real date all along.</para>
///
/// <para>The risk runs the other way too: a serial number is eight digits like
/// a date, and a wrong date is worse than none. Half of these tests are about
/// what must NOT be read as a timestamp.</para></summary>
public class FileNameTimestampTests
{
    [Theory]
    // WhatsApp: date only, no time of day anywhere in the name.
    [InlineData("IMG-20250910-WA0001.jpg", "2025-09-10 00:00:00")]
    [InlineData("VID-20260308-WA0002.mp4", "2026-03-08 00:00:00")]
    // Android camera and screenshots.
    [InlineData("IMG_20260114_100607.jpg", "2026-01-14 10:06:07")]
    [InlineData("Screenshot_20251105-172535.png", "2025-11-05 17:25:35")]
    // Xiaomi, with milliseconds and the app name glued on.
    [InlineData("Screenshot_2026-01-14-10-05-23-511_com.google.android.apps.docs.jpg", "2026-01-14 10:05:23")]
    // Pixel, milliseconds without a separator.
    [InlineData("PXL_20260509_135347641.PORTRAIT.jpg", "2026-05-09 13:53:47")]
    // Desktop exports.
    [InlineData("2026-01-14 10.05.23.jpg", "2026-01-14 10:05:23")]
    [InlineData("signal-2026-03-08-101530.jpg", "2026-03-08 10:15:30")]
    public void TheDateInTheNameIsRead(string fileName, string expected) =>
        Assert.Equal(
            DateTime.Parse(expected),
            MediaMetadataReader.TryReadTimestampFromFileName(fileName));

    [Theory]
    [InlineData("IMG_1234.jpg")]                 // four digits, no date
    [InlineData("DSC00042.JPG")]                 // camera counter
    [InlineData("IMG_12345678.jpg")]             // eight digits, month 56
    [InlineData("20260231_Ausflug.jpg")]         // 31 February
    [InlineData("19700101_alt.jpg")]             // before the plausible range
    [InlineData("Urlaub 2019.jpg")]              // a year is not a date
    [InlineData("100_0234.jpg")]
    [InlineData("Foto.jpg")]
    public void NonsenseIsNotReadAsADate(string fileName) =>
        Assert.Null(MediaMetadataReader.TryReadTimestampFromFileName(fileName));

    /// <summary>A photo cannot have been taken next year. Eight digits that
    /// happen to form a valid future date are a serial number, not a
    /// timestamp.</summary>
    [Fact]
    public void ADateInTheFutureIsRejected()
    {
        var nextYear = DateTime.Now.AddYears(1);

        Assert.Null(MediaMetadataReader.TryReadTimestampFromFileName($"IMG_{nextYear:yyyyMMdd}.jpg"));
    }

    /// <summary>An impossible time of day must not fall back to midnight of
    /// that day through the date-only pattern — the name is evidently not what
    /// it looks like.</summary>
    [Fact]
    public void AnImpossibleTimeFallsBackToTheDate()
    {
        Assert.Equal(
            new DateTime(2026, 1, 14),
            MediaMetadataReader.TryReadTimestampFromFileName("IMG_20260114_996060.jpg"));
    }

    // --- Companion files ---

    [Theory]
    [InlineData("IMG_1234.AAE")]        // Apple edit sidecar, 699 in one collection
    [InlineData(".nomedia")]
    [InlineData("photo.jpg.json")]      // Google Takeout
    [InlineData("clip.THM")]
    [InlineData("Thumbs.db")]
    [InlineData("desktop.ini")]
    public void CompanionFilesAreRecognised(string fileName) =>
        Assert.True(MediaMetadataReader.IsCompanionFile(fileName));

    [Theory]
    [InlineData("IMG_1234.jpg")]
    [InlineData("clip.mp4")]
    [InlineData("scan.psd")]            // unsupported, but a real file — must be reported
    public void RealFilesAreNotCompanions(string fileName) =>
        Assert.False(MediaMetadataReader.IsCompanionFile(fileName));
}
