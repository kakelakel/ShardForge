using System.Text;

namespace ShardForge.Core.Uop;

/// <summary>One file stored inside a UOP (Mythic Package) container.</summary>
public sealed record UopEntry(
    long Offset,
    int HeaderLength,
    int CompressedLength,
    int DecompressedLength,
    ulong Hash,
    uint DataHash,
    short Compression)
{
    public long DataOffset => Offset + HeaderLength;
}

/// <summary>
/// Reader for the Mythic Package (.uop) format used by modern Ultima Online clients.
/// Layout: 28-byte header ("MYP\0", version, signature, first table offset, table capacity, file count),
/// then linked tables of 34-byte entries pointing at (metadata header + data) blobs.
/// </summary>
public sealed class UopFile : IDisposable
{
    public const uint Magic = 0x0050594D; // "MYP\0"

    private readonly FileStream _stream;
    private readonly Dictionary<ulong, UopEntry> _byHash = new();

    public string Path { get; }
    public uint Version { get; }
    public uint Signature { get; }
    public uint TableCapacity { get; }
    public IReadOnlyList<UopEntry> Entries { get; }

    public UopFile(string path)
    {
        Path = path;
        _stream = File.OpenRead(path);
        using var r = new BinaryReader(_stream, Encoding.ASCII, leaveOpen: true);

        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException($"{path} is not a UOP file (bad magic).");

        Version = r.ReadUInt32();
        Signature = r.ReadUInt32();
        long nextTable = r.ReadInt64();
        TableCapacity = r.ReadUInt32();
        _ = r.ReadInt32(); // declared file count; the tables are authoritative

        var entries = new List<UopEntry>();
        var seenTables = new HashSet<long>();
        while (nextTable != 0)
        {
            if (!seenTables.Add(nextTable) || nextTable >= _stream.Length)
                throw new InvalidDataException($"{path}: corrupt UOP table chain at offset {nextTable}.");

            _stream.Position = nextTable;
            int count = r.ReadInt32();
            nextTable = r.ReadInt64();
            for (int i = 0; i < count; i++)
            {
                var e = new UopEntry(
                    Offset: r.ReadInt64(),
                    HeaderLength: r.ReadInt32(),
                    CompressedLength: r.ReadInt32(),
                    DecompressedLength: r.ReadInt32(),
                    Hash: r.ReadUInt64(),
                    DataHash: r.ReadUInt32(),
                    Compression: r.ReadInt16());
                if (e.Offset == 0) continue; // unused slot
                entries.Add(e);
                _byHash[e.Hash] = e;
            }
        }
        Entries = entries;
    }

    public bool TryGetEntry(string name, out UopEntry entry) =>
        _byHash.TryGetValue(UopHash.Hash(name.ToLowerInvariant()), out entry!);

    /// <summary>Reads an entry's (uncompressed) data into <paramref name="destination"/>.</summary>
    public void ReadData(UopEntry entry, Span<byte> destination)
    {
        if (entry.Compression != 0)
            throw new NotSupportedException($"Compressed UOP entries (flag {entry.Compression}) are not supported yet.");
        _stream.Position = entry.DataOffset;
        _stream.ReadExactly(destination[..entry.DecompressedLength]);
    }

    public byte[] ReadData(UopEntry entry)
    {
        var buffer = new byte[entry.DecompressedLength];
        ReadData(entry, buffer);
        return buffer;
    }

    /// <summary>
    /// Concatenates numbered entries (e.g. build/map0legacymul/00000000.dat, 00000001.dat, ...)
    /// back into the original flat MUL stream.
    /// </summary>
    public byte[] ReadConcatenated(Func<int, string> nameForIndex)
    {
        var parts = new List<UopEntry>();
        for (int i = 0; TryGetEntry(nameForIndex(i), out var e); i++)
            parts.Add(e);

        if (parts.Count == 0)
            throw new InvalidDataException($"{Path}: no entries named like '{nameForIndex(0)}'.");

        var result = new byte[parts.Sum(p => (long)p.DecompressedLength)];
        long pos = 0;
        foreach (var p in parts)
        {
            ReadData(p, result.AsSpan((int)pos, p.DecompressedLength));
            pos += p.DecompressedLength;
        }
        return result;
    }

    public void Dispose() => _stream.Dispose();
}
