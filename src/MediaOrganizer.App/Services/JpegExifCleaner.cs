using System.IO;

namespace MediaOrganizer.App.Services;

/// <summary>Removes the two EXIF fields earlier versions wrote the description
/// into: <c>ImageDescription</c> (270) and <c>XPComment</c> (40092).
///
/// <para>Windows fills three rows of the properties dialog from them —
/// ImageDescription feeds both "Title" and "Subject", XPComment feeds
/// "Comments" — so a file described by an older version keeps showing the same
/// sentence three times. Writing goes to XMP alone now
/// (<see cref="JpegXmpWriter"/>); this takes the leftovers out again, on every
/// file that gets described anew.</para>
///
/// <para>The trick when rewriting: a new IFD0 is appended BEHIND the existing
/// TIFF data and the pointer in the TIFF header is bent to it. That way not a
/// single existing byte moves and every absolute offset stays valid — those in
/// vendor blocks (MakerNote) included, which a naive removal reliably
/// destroys. The old IFD0 stays behind as dead weight; that is worth a few
/// dozen bytes. The image data is taken over byte for byte, there is no
/// re-encoding.</para></summary>
internal static class JpegExifCleaner
{
    private static readonly ushort[] LegacyTags = [270, 40092];

    /// <summary>An APP1 segment holds at most 65535 bytes including the two
    /// length bytes; 6 of those go on "Exif\0\0".</summary>
    private const int MaxTiffLength = 65535 - 2 - 6;

    private static readonly byte[] ExifHeader = [0x45, 0x78, 0x69, 0x66, 0x00, 0x00];

    /// <summary>True when the file carried one of the fields and was rewritten.
    /// A file without them is left untouched.</summary>
    public static bool TryClean(string path)
    {
        try
        {
            var rebuilt = Rebuild(File.ReadAllBytes(path));
            return rebuilt is not null && AtomicFile.TryReplace(path, rebuilt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            return false;
        }
    }

    private static byte[]? Rebuild(byte[] data)
    {
        if (ReadSegments(data) is not { } parsed)
        {
            return null;
        }

        var (segments, tail) = parsed;

        var exifIndex = segments.FindIndex(s =>
            s.Marker == 0xE1 && StartsWithExifHeader(data, s.Start + 4, s.Length - 4));
        if (exifIndex < 0)
        {
            return null;
        }

        var start = segments[exifIndex].Start + 4 + ExifHeader.Length;
        var length = segments[exifIndex].Length - 4 - ExifHeader.Length;
        if (length <= 0)
        {
            return null;
        }

        if (WithoutLegacyTags(data[start..(start + length)]) is not { } tiff
            || BuildSegment(tiff) is not { } segment)
        {
            return null;
        }

        var output = new MemoryStream(data.Length + segment.Length + 64);
        output.Write(data, 0, 2);
        for (var i = 0; i < segments.Count; i++)
        {
            if (i == exifIndex)
            {
                output.Write(segment, 0, segment.Length);
            }
            else
            {
                output.Write(data, segments[i].Start, segments[i].Length);
            }
        }

        // From SOS on unchanged: the compressed image data.
        output.Write(data, tail, data.Length - tail);
        return output.ToArray();
    }

    /// <summary>Reads the marker segments up to the start of the image data
    /// (SOS). Null when the file is not a well-formed JPEG.</summary>
    private static (List<Segment> Segments, int Tail)? ReadSegments(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        var segments = new List<Segment>();
        var position = 2;
        while (position + 3 < data.Length)
        {
            if (data[position] != 0xFF)
            {
                break;
            }

            var marker = data[position + 1];
            if (marker == 0xDA)
            {
                break;
            }

            var length = (data[position + 2] << 8) | data[position + 3];
            if (length < 2 || position + 2 + length > data.Length)
            {
                return null;
            }

            segments.Add(new Segment(position, 2 + length, marker));
            position += 2 + length;
        }

        return (segments, position);
    }

    private static bool StartsWithExifHeader(byte[] data, int offset, int length)
    {
        if (length < ExifHeader.Length || offset + ExifHeader.Length > data.Length)
        {
            return false;
        }

        for (var i = 0; i < ExifHeader.Length; i++)
        {
            if (data[offset + i] != ExifHeader[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Appends an IFD0 without the two fields and bends the header
    /// pointer to it. Null when the block is unreadable, when it carries
    /// neither field — then nothing is rewritten — or when the result no
    /// longer fits into an APP1 segment.</summary>
    private static byte[]? WithoutLegacyTags(byte[] tiff)
    {
        if (tiff.Length < 8)
        {
            return null;
        }

        var little = tiff[0] == 0x49 && tiff[1] == 0x49;
        if (!little && (tiff[0] != 0x4D || tiff[1] != 0x4D))
        {
            return null;
        }

        if (ReadUInt16(tiff, 2, little) != 42)
        {
            return null;
        }

        var ifd0 = ReadUInt32(tiff, 4, little);
        if (ifd0 + 2 > (uint)tiff.Length)
        {
            return null;
        }

        var count = ReadUInt16(tiff, (int)ifd0, little);
        var entriesStart = (int)ifd0 + 2;
        if (entriesStart + (count * 12) + 4 > tiff.Length)
        {
            return null;
        }

        // The pointer to IFD1 (the preview image) is taken over unchanged: that
        // IFD stays where it is.
        var nextIfd = ReadUInt32(tiff, entriesStart + (count * 12), little);

        var kept = new List<byte[]>(count);
        var dropped = false;
        for (var i = 0; i < count; i++)
        {
            var offset = entriesStart + (i * 12);
            if (LegacyTags.Contains(ReadUInt16(tiff, offset, little)))
            {
                dropped = true;
                continue;
            }

            // Existing entries byte for byte: their value offsets keep pointing
            // at data that does not move.
            kept.Add(tiff[offset..(offset + 12)]);
        }

        if (!dropped)
        {
            return null;
        }

        var ifdStart = tiff.Length + (tiff.Length % 2);
        if (ifdStart + 2 + (kept.Count * 12) + 4 > MaxTiffLength)
        {
            return null;
        }

        var output = new MemoryStream(ifdStart + 2 + (kept.Count * 12) + 4);
        output.Write(tiff, 0, tiff.Length);
        if (tiff.Length % 2 == 1)
        {
            output.WriteByte(0);
        }

        var header = new byte[2];
        WriteUInt16(header, 0, (ushort)kept.Count, little);
        output.Write(header, 0, 2);
        foreach (var entry in kept)
        {
            output.Write(entry, 0, 12);
        }

        var next = new byte[4];
        WriteUInt32(next, 0, nextIfd, little);
        output.Write(next, 0, 4);

        var result = output.ToArray();
        WriteUInt32(result, 4, (uint)ifdStart, little);
        return result;
    }

    private static byte[]? BuildSegment(byte[] tiff)
    {
        var segmentLength = ExifHeader.Length + tiff.Length + 2;
        if (segmentLength > 0xFFFF)
        {
            return null;
        }

        var segment = new byte[segmentLength + 2];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        segment[2] = (byte)(segmentLength >> 8);
        segment[3] = (byte)(segmentLength & 0xFF);
        ExifHeader.CopyTo(segment, 4);
        tiff.CopyTo(segment, 4 + ExifHeader.Length);
        return segment;
    }

    private static ushort ReadUInt16(byte[] data, int offset, bool little) =>
        little
            ? (ushort)(data[offset] | (data[offset + 1] << 8))
            : (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(byte[] data, int offset, bool little) =>
        little
            ? (uint)(data[offset] | (data[offset + 1] << 8)
                                  | (data[offset + 2] << 16) | (data[offset + 3] << 24))
            : (uint)((data[offset] << 24) | (data[offset + 1] << 16)
                                          | (data[offset + 2] << 8) | data[offset + 3]);

    private static void WriteUInt16(byte[] data, int offset, ushort value, bool little)
    {
        data[offset] = little ? (byte)value : (byte)(value >> 8);
        data[offset + 1] = little ? (byte)(value >> 8) : (byte)value;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value, bool little)
    {
        for (var i = 0; i < 4; i++)
        {
            var shift = little ? i * 8 : (3 - i) * 8;
            data[offset + i] = (byte)(value >> shift);
        }
    }

    private readonly record struct Segment(int Start, int Length, byte Marker);
}
