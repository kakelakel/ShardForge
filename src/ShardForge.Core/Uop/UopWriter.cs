namespace ShardForge.Core.Uop;

/// <summary>
/// Writes an uncompressed Mythic Package (.uop). Layout: header padded to 0x200, one or more entry tables
/// (capacity 1000 each, like the client's own files), then the file data. Entries carry no metadata header.
/// </summary>
public static class UopWriter
{
    public const int HeaderSize = 0x200;
    public const int TableCapacity = 1000;
    private const int EntrySize = 34;
    private const int TableHeaderSize = 12;

    public static void Write(string path, IReadOnlyList<(string Name, byte[] Data)> files)
    {
        int tables = Math.Max(1, (files.Count + TableCapacity - 1) / TableCapacity);
        long tableBytes = (long)tables * (TableHeaderSize + TableCapacity * EntrySize);

        using var w = new BinaryWriter(File.Create(path));
        w.Write(UopFile.Magic);
        w.Write(5u);           // version
        w.Write(0xFD23EC43u);  // signature
        w.Write((long)HeaderSize);
        w.Write((uint)TableCapacity);
        w.Write(files.Count);
        w.Write(new byte[HeaderSize - 28]);

        long dataOffset = HeaderSize + tableBytes;
        for (int t = 0; t < tables; t++)
        {
            int first = t * TableCapacity, count = Math.Min(TableCapacity, files.Count - first);
            long next = t + 1 < tables ? HeaderSize + (t + 1) * (TableHeaderSize + (long)TableCapacity * EntrySize) : 0;
            w.Write(count);
            w.Write(next);
            for (int i = 0; i < TableCapacity; i++)
            {
                if (i >= count) { w.Write(new byte[EntrySize]); continue; }
                var (name, data) = files[first + i];
                w.Write(dataOffset);
                w.Write(0);              // metadata header length
                w.Write(data.Length);    // compressed length
                w.Write(data.Length);    // decompressed length
                w.Write(UopHash.Hash(name.ToLowerInvariant()));
                w.Write(Adler32(data));
                w.Write((short)0);       // no compression
                dataOffset += data.Length;
            }
        }

        foreach (var (_, data) in files) w.Write(data);
    }

    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }
}
