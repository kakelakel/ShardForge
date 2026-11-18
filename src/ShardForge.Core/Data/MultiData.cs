using System.Buffers.Binary;

namespace ShardForge.Core.Data;

/// <summary>One static of a multi (building), relative to the multi's centre.</summary>
public readonly record struct MultiComponent(ushort TileId, short X, short Y, short Z, uint Flags)
{
    /// <summary>Flag 0 marks invisible helper tiles (house bounds) that are not drawn.</summary>
    public bool Visible => Flags != 0;
}

public sealed record Multi(int Id, MultiComponent[] Components)
{
    public int MinX => Components.Length == 0 ? 0 : Components.Min(c => c.X);
    public int MinY => Components.Length == 0 ? 0 : Components.Min(c => c.Y);
    public int MaxX => Components.Length == 0 ? 0 : Components.Max(c => c.X);
    public int MaxY => Components.Length == 0 ? 0 : Components.Max(c => c.Y);
    public int Width => MaxX - MinX + 1;
    public int Height => MaxY - MinY + 1;
}

/// <summary>
/// multi.idx / multi.mul: buildings and boats as lists of statics. Entries are 16 bytes in modern clients
/// (ushort id, short x, short y, short z, uint flags, uint unknown) and 12 bytes in old ones.
/// </summary>
public sealed class MultiData
{
    private readonly byte[] _idx, _mul;
    private readonly Dictionary<int, Multi?> _cache = new();

    private MultiData(byte[] idx, byte[] mul) { _idx = idx; _mul = mul; }

    public static MultiData? TryLoad(string clientDir)
    {
        string idx = Path.Combine(clientDir, "multi.idx"), mul = Path.Combine(clientDir, "multi.mul");
        if (!File.Exists(idx)) idx = Directory.Exists(clientDir) ? Directory.EnumerateFiles(clientDir).FirstOrDefault(f => Path.GetFileName(f).Equals("multi.idx", StringComparison.OrdinalIgnoreCase)) ?? idx : idx;
        if (!File.Exists(mul)) mul = Directory.Exists(clientDir) ? Directory.EnumerateFiles(clientDir).FirstOrDefault(f => Path.GetFileName(f).Equals("multi.mul", StringComparison.OrdinalIgnoreCase)) ?? mul : mul;
        return File.Exists(idx) && File.Exists(mul) ? new MultiData(File.ReadAllBytes(idx), File.ReadAllBytes(mul)) : null;
    }

    public int Count => _idx.Length / 12;

    public Multi? Get(int id)
    {
        if (_cache.TryGetValue(id, out var m)) return m;
        m = null;
        if (id >= 0 && id < Count)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(_idx.AsSpan(id * 12));
            int length = BinaryPrimitives.ReadInt32LittleEndian(_idx.AsSpan(id * 12 + 4));
            if (offset >= 0 && length > 0 && (long)offset + length <= _mul.Length)
            {
                int size = length % 16 == 0 ? 16 : 12;
                var comps = new MultiComponent[length / size];
                for (int i = 0; i < comps.Length; i++)
                {
                    var e = _mul.AsSpan(offset + i * size, size);
                    comps[i] = new MultiComponent(BinaryPrimitives.ReadUInt16LittleEndian(e), BinaryPrimitives.ReadInt16LittleEndian(e[2..]),
                        BinaryPrimitives.ReadInt16LittleEndian(e[4..]), BinaryPrimitives.ReadInt16LittleEndian(e[6..]), BinaryPrimitives.ReadUInt32LittleEndian(e[8..]));
                }
                m = new Multi(id, comps);
            }
        }
        _cache[id] = m;
        return m;
    }
}
