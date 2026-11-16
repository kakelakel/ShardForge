using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ShardForge.Core.Imaging;

/// <summary>Minimal dependency-free PNG encoder for 8-bit RGB images.</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static void WriteRgb(string path, int width, int height, ReadOnlySpan<byte> rgb)
    {
        using var fs = File.Create(path);
        WriteRgb(fs, width, height, rgb);
    }

    public static void WriteRgb(Stream output, int width, int height, ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length != width * height * 3)
            throw new ArgumentException("RGB buffer size does not match dimensions.", nameof(rgb));

        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type: truecolour
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(output, "IHDR", ihdr);

        using (var idat = new MemoryStream())
        {
            using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                int stride = width * 3;
                var row = new byte[stride + 1];
                var prev = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    // "Up" filter: cheap and compresses map imagery well.
                    var cur = rgb.Slice(y * stride, stride);
                    row[0] = 2;
                    for (int i = 0; i < stride; i++)
                        row[i + 1] = (byte)(cur[i] - prev[i]);
                    cur.CopyTo(prev);
                    z.Write(row);
                }
            }
            WriteChunk(output, "IDAT", idat.GetBuffer().AsSpan(0, (int)idat.Length));
        }

        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        s.Write(typeBytes);
        s.Write(data);

        uint crc = 0xFFFFFFFF;
        crc = UpdateCrc(crc, typeBytes);
        crc = UpdateCrc(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFF);
        s.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
