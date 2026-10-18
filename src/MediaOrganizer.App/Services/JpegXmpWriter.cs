using System.IO;
using System.Text;

namespace MediaOrganizer.App.Services;

/// <summary>Writes a description losslessly into a JPEG file as an XMP block
/// (<c>dc:description</c>) — the one and only place the application puts it.
///
/// The file is read at marker level; an existing XMP APP1 block is replaced,
/// otherwise a new one is inserted right after the EXIF/JFIF blocks. Every
/// other segment — EXIF (date, GPS) above all, and the compressed image data
/// — is taken over byte for byte. There is NO re-encoding, the image quality
/// is untouched.
///
/// Replacing goes through <see cref="AtomicFile"/> so that breaking off never
/// damages the original.</summary>
internal static class JpegXmpWriter
{
    private const string XmpNamespace = "http://ns.adobe.com/xap/1.0/\0";

    public static bool TryWrite(string path, string description)
    {
        try
        {
            var rebuilt = Rebuild(File.ReadAllBytes(path), description);
            return rebuilt is not null && AtomicFile.TryReplace(path, rebuilt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            return false;
        }
    }

    private static byte[]? Rebuild(byte[] data, string description)
    {
        // A JPEG starts with SOI (FFD8).
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        var output = new MemoryStream(data.Length + 4096);
        output.Write(data, 0, 2);

        var xmpSegment = BuildXmpSegment(description);
        var xmpWritten = false;
        var position = 2;

        while (position + 3 < data.Length)
        {
            if (data[position] != 0xFF)
            {
                break;
            }

            var marker = data[position + 1];

            // SOS (FFDA) opens the image data: everything from here is left as is.
            if (marker == 0xDA)
            {
                break;
            }

            var length = (data[position + 2] << 8) | data[position + 3];
            if (length < 2 || position + 2 + length > data.Length)
            {
                return null;
            }

            var isXmp = marker == 0xE1 && IsXmpSegment(data, position + 4, length - 2);
            var isAppSegment = marker >= 0xE0 && marker <= 0xEF;

            // Insert the XMP block before the first non-APP segment.
            if (!isAppSegment && !xmpWritten)
            {
                output.Write(xmpSegment, 0, xmpSegment.Length);
                xmpWritten = true;
            }

            if (isXmp)
            {
                // Do NOT throw away existing XMP (from the camera app, say);
                // only add or replace dc:description inside it.
                var existing = Encoding.UTF8.GetString(
                    data, position + 4 + XmpNamespace.Length,
                    length - 2 - XmpNamespace.Length);
                var merged = MergeDescription(existing, description);
                if (merged is null)
                {
                    return null;
                }

                var mergedSegment = BuildSegment(merged);
                output.Write(mergedSegment, 0, mergedSegment.Length);
                xmpWritten = true;
            }
            else
            {
                output.Write(data, position, 2 + length);
            }

            position += 2 + length;
        }

        if (!xmpWritten)
        {
            output.Write(xmpSegment, 0, xmpSegment.Length);
        }

        // Append the rest (the image data) unchanged.
        output.Write(data, position, data.Length - position);
        return output.ToArray();
    }

    private static bool IsXmpSegment(byte[] data, int offset, int length)
    {
        if (length < XmpNamespace.Length)
        {
            return false;
        }

        for (var i = 0; i < XmpNamespace.Length; i++)
        {
            if (data[offset + i] != (byte)XmpNamespace[i])
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] BuildXmpSegment(string description)
    {
        var escaped = System.Security.SecurityElement.Escape(description) ?? "";
        var xmp =
            "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            $"<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">{escaped}</rdf:li></rdf:Alt></dc:description>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta>" +
            "<?xpacket end=\"w\"?>";
        return BuildSegment(xmp);
    }

    /// <summary>Adds or replaces dc:description in existing XMP without losing
    /// the other entries (camera-specific blocks, for instance). Null when the
    /// existing XMP cannot be read as XML.</summary>
    private static string? MergeDescription(string existingXmp, string description)
    {
        try
        {
            // The xpacket header is a processing instruction outside the root
            // element — cut it off for the XML step.
            var start = existingXmp.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
            var endTag = existingXmp.IndexOf("</x:xmpmeta>", StringComparison.Ordinal);
            if (start < 0 || endTag < 0)
            {
                return null;
            }

            var core = existingXmp[start..(endTag + "</x:xmpmeta>".Length)];
            var doc = System.Xml.Linq.XDocument.Parse(core);

            System.Xml.Linq.XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
            System.Xml.Linq.XNamespace dc = "http://purl.org/dc/elements/1.1/";
            System.Xml.Linq.XNamespace xml = "http://www.w3.org/XML/1998/namespace";

            var rdfRoot = doc.Descendants(rdf + "RDF").FirstOrDefault();
            if (rdfRoot is null)
            {
                return null;
            }

            var target = rdfRoot.Elements(rdf + "Description").FirstOrDefault();
            if (target is null)
            {
                target = new System.Xml.Linq.XElement(rdf + "Description",
                    new System.Xml.Linq.XAttribute(rdf + "about", ""));
                rdfRoot.Add(target);
            }

            // Declare the prefix on the element the entry is added to.
            // Without it XDocument serialises the namespace as the default one
            // — <description xmlns="…"> instead of <dc:description>, which is
            // the same thing to a namespace-aware reader and unrecognisable to
            // everything else.
            target.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "dc", dc.NamespaceName);

            System.Xml.Linq.Extensions.Remove(target.Elements(dc + "description"));
            target.Add(new System.Xml.Linq.XElement(dc + "description",
                new System.Xml.Linq.XElement(rdf + "Alt",
                    new System.Xml.Linq.XElement(rdf + "li",
                        new System.Xml.Linq.XAttribute(xml + "lang", "x-default"),
                        description))));

            return "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
                   doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting) +
                   "<?xpacket end=\"w\"?>";
        }
        catch
        {
            return null;
        }
    }

    private static byte[] BuildSegment(string xmp)
    {
        var payload = Encoding.UTF8.GetBytes(XmpNamespace + xmp);
        var segmentLength = payload.Length + 2;

        var segment = new byte[payload.Length + 4];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        segment[2] = (byte)(segmentLength >> 8);
        segment[3] = (byte)(segmentLength & 0xFF);
        payload.CopyTo(segment, 4);
        return segment;
    }
}
