using System.Buffers.Binary;

namespace Konnyaku.Content.Domain;

/// <summary>
/// Removes metadata from uploaded images (docs/xu-ly-du-lieu-ca-nhan.md): EXIF (GPS position, camera serial, date),
/// XMP, IPTC and text chunks. Pixels are not decoded or re-encoded. A JPEG keeps only its EXIF orientation, so phone
/// photos still display upright.
/// </summary>
public static class ImageMetadata
{
    /// <summary>The image without metadata, or null when it is not a well-formed JPEG/PNG/WebP. GIF is returned as is.</summary>
    public static byte[]? Strip(byte[] image, string normalizedType) => normalizedType switch
    {
        "image/jpeg" => StripJpeg(image),
        "image/png" => StripPng(image),
        "image/webp" => StripWebp(image),
        "image/gif" => image,
        _ => image,
    };

    public static bool Supports(string normalizedType) => normalizedType is "image/jpeg" or "image/png" or "image/webp";

    private static byte[]? StripJpeg(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 2);
        var pos = 2;
        var orientationWritten = false;
        while (pos < data.Length)
        {
            if (data[pos] != 0xFF)
            {
                return null;
            }

            // Fill bytes before a marker.
            while (pos < data.Length && data[pos] == 0xFF)
            {
                pos++;
            }

            if (pos >= data.Length)
            {
                return null;
            }

            var marker = data[pos];
            pos++;
            if (marker == 0xD9)
            {
                output.Write([0xFF, 0xD9]);
                break;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                output.Write([0xFF, marker]);
                continue;
            }

            if (pos + 2 > data.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
            if (length < 2 || pos + length > data.Length)
            {
                return null;
            }

            var segment = data.AsSpan(pos + 2, length - 2);
            if (marker == 0xDA)
            {
                // Start of scan: the compressed image data and everything after it are copied unchanged.
                output.Write([0xFF, marker]);
                output.Write(data, pos, data.Length - pos);
                return output.ToArray();
            }

            // APP1 (EXIF, XMP), APP13 (IPTC/Photoshop) and comments are dropped; JFIF, ICC profile (APP2), Adobe (APP14) stay.
            var drop = marker is 0xE1 or 0xED or 0xFE or (>= 0xE3 and <= 0xEC) or 0xEF;
            if (marker == 0xE1 && !orientationWritten && ReadExifOrientation(segment) is { } orientation and > 1)
            {
                output.Write(MinimalExif(orientation));
                orientationWritten = true;
            }

            if (!drop)
            {
                output.Write([0xFF, marker]);
                output.Write(data, pos, length);
            }

            pos += length;
        }

        return output.ToArray();
    }

    /// <summary>The orientation tag (0x0112) of an APP1 "Exif" segment, if any.</summary>
    public static ushort? ReadExifOrientation(ReadOnlySpan<byte> segment)
    {
        if (segment.Length < 14 || !segment.StartsWith("Exif\0\0"u8))
        {
            return null;
        }

        var tiff = segment[6..];
        bool little;
        if (tiff.StartsWith("II"u8))
        {
            little = true;
        }
        else if (tiff.StartsWith("MM"u8))
        {
            little = false;
        }
        else
        {
            return null;
        }

        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

        var ifd = U32(tiff.Slice(4, 4));
        if (ifd + 2 > tiff.Length)
        {
            return null;
        }

        var count = U16(tiff.Slice((int)ifd, 2));
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
            {
                return null;
            }

            if (U16(tiff.Slice(entry, 2)) == 0x0112)
            {
                var value = U16(tiff.Slice(entry + 8, 2));
                return value is >= 1 and <= 8 ? value : null;
            }
        }

        return null;
    }

    /// <summary>An APP1 segment holding only the orientation tag (big-endian TIFF, one IFD entry).</summary>
    private static byte[] MinimalExif(ushort orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, // header, IFD0 at 8
            0x00, 0x01, // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00, // orientation SHORT
            0x00, 0x00, 0x00, 0x00, // no next IFD
        ];
        var length = 2 + 6 + tiff.Length;
        var segment = new byte[2 + length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)length);
        "Exif\0\0"u8.CopyTo(segment.AsSpan(4));
        tiff.CopyTo(segment.AsSpan(10));
        return segment;
    }

    private static byte[]? StripPng(byte[] data)
    {
        if (data.Length < 8 || FileSignature.Detect(data) != FileSignature.Png)
        {
            return null;
        }

        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 8);
        var pos = 8;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            if (length > int.MaxValue || pos + 12 + (long)length > data.Length)
            {
                return null;
            }

            var type = data.AsSpan(pos + 4, 4);
            var total = 12 + (int)length;
            var drop = type.SequenceEqual("eXIf"u8) || type.SequenceEqual("tEXt"u8) || type.SequenceEqual("zTXt"u8)
                       || type.SequenceEqual("iTXt"u8) || type.SequenceEqual("tIME"u8);
            if (!drop)
            {
                output.Write(data, pos, total);
            }

            pos += total;
            if (type.SequenceEqual("IEND"u8))
            {
                return output.ToArray();
            }
        }

        return null;
    }

    private static byte[]? StripWebp(byte[] data)
    {
        if (data.Length < 12 || FileSignature.Detect(data) != FileSignature.Webp)
        {
            return null;
        }

        using var output = new MemoryStream(data.Length);
        output.Write(data, 0, 12);
        var pos = 12;
        while (pos + 8 <= data.Length)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
            var padded = (long)size + (size & 1);
            if (pos + 8 + padded > data.Length && pos + 8 + size != data.Length)
            {
                return null;
            }

            var chunk = (int)Math.Min(8 + padded, data.Length - pos);
            var fourCc = data.AsSpan(pos, 4);
            if (fourCc.SequenceEqual("VP8X"u8) && size >= 10)
            {
                var copy = data.AsSpan(pos, chunk).ToArray();
                copy[8] &= unchecked((byte)~0x0C); // EXIF (0x08) and XMP (0x04) flags
                output.Write(copy);
            }
            else if (!fourCc.SequenceEqual("EXIF"u8) && !fourCc.SequenceEqual("XMP "u8))
            {
                output.Write(data, pos, chunk);
            }

            pos += chunk;
        }

        var result = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)(result.Length - 8));
        return result;
    }
}
