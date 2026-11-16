using System.Buffers.Binary;
using ShardForge.Core.Uop;

namespace ShardForge.Core.Art;

/// <summary>A decoded sprite: 32-bit BGRA pixels (alpha 0 = transparent).</summary>
public sealed record Sprite(int Width, int Height, uint[] Pixels);

/// <summary>
/// Reads tile art from artLegacyMUL.uop (or art.mul/artidx.mul). Land tiles are 44x44 diamonds stored
/// row by row; statics are run-length encoded sprites. Index 0..0x3FFF is land, 0x4000+ is statics.
/// </summary>
public sealed class ArtReader : IDisposable
{
    public const int LandSize = 44;
    private readonly UopFile? _uop;
    private readonly FileStream? _mul;
    private readonly byte[]? _idx;
    private readonly Dictionary<int, Sprite?> _cache = new();

    public ArtReader(string clientDir)
    {
        string uop = Path.Combine(clientDir, "artLegacyMUL.uop");
        if (File.Exists(uop)) { _uop = new UopFile(uop); return; }

        string mul = Path.Combine(clientDir, "art.mul"), idx = Path.Combine(clientDir, "artidx.mul");
        if (!File.Exists(mul) || !File.Exists(idx))
            throw new FileNotFoundException($"No art found in {clientDir} (artLegacyMUL.uop or art.mul + artidx.mul).");
        _mul = File.OpenRead(mul);
        _idx = File.ReadAllBytes(idx);
    }

    public Sprite? Land(int id) => Get(id & 0x3FFF, isLand: true);
    public Sprite? Static(int id) => Get(0x4000 + id, isLand: false);

    private Sprite? Get(int index, bool isLand)
    {
        if (_cache.TryGetValue(index, out var s)) return s;
        byte[]? raw = ReadRaw(index);
        s = raw is null || raw.Length < 8 ? null : isLand ? DecodeLand(raw) : DecodeStatic(raw);
        _cache[index] = s;
        return s;
    }

    private byte[]? ReadRaw(int index)
    {
        if (_uop is not null)
            return _uop.TryGetEntry($"build/artlegacymul/{index:D8}.tga", out var e) ? _uop.ReadData(e) : null;

        int at = index * 12;
        if (_idx is null || at + 12 > _idx.Length) return null;
        int offset = BinaryPrimitives.ReadInt32LittleEndian(_idx.AsSpan(at));
        int length = BinaryPrimitives.ReadInt32LittleEndian(_idx.AsSpan(at + 4));
        if (offset < 0 || length <= 0) return null;
        var buf = new byte[length];
        _mul!.Position = offset;
        _mul.ReadExactly(buf);
        return buf;
    }

    /// <summary>Average colour of a land tile's visible pixels, or null if it has no art.</summary>
    public (double R, double G, double B)? LandAverage(int id)
    {
        if (Land(id) is not { } s) return null;
        double r = 0, g = 0, b = 0; int n = 0;
        foreach (var p in s.Pixels)
        {
            if (p == 0) continue;
            r += (p >> 16) & 255; g += (p >> 8) & 255; b += p & 255; n++;
        }
        return n == 0 ? null : (r / n, g / n, b / n);
    }

    public static uint ToBgra(ushort c)
    {
        if (c == 0) return 0;
        static uint X(int v) => (uint)((v << 3) | (v >> 2));
        return 0xFF000000u | (X((c >> 10) & 0x1F) << 16) | (X((c >> 5) & 0x1F) << 8) | X(c & 0x1F);
    }

    /// <summary>Land: 22 growing rows (2,4,...,44 px) then 22 shrinking rows (44,...,2 px).</summary>
    public static Sprite DecodeLand(ReadOnlySpan<byte> raw)
    {
        var px = new uint[LandSize * LandSize];
        int p = 0;
        for (int row = 0; row < LandSize; row++)
        {
            int width = row < 22 ? 2 + row * 2 : 44 - (row - 22) * 2;
            int start = 22 - width / 2;
            for (int i = 0; i < width && p + 1 < raw.Length; i++, p += 2)
            {
                ushort c = BinaryPrimitives.ReadUInt16LittleEndian(raw[p..]);
                px[row * LandSize + start + i] = 0xFF000000u | ToBgra((ushort)(c | 0x8000));
            }
        }
        return new Sprite(LandSize, LandSize, px);
    }

    /// <summary>Static: int flag, ushort width, ushort height, row lookup table, then per row (xOffset, run, pixels...) until (0,0).</summary>
    public static Sprite? DecodeStatic(ReadOnlySpan<byte> raw)
    {
        int width = BinaryPrimitives.ReadUInt16LittleEndian(raw[4..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(raw[6..]);
        if (width is 0 or > 1024 || height is 0 or > 1024) return null;
        int lookupStart = 8, dataStart = lookupStart + height * 2;
        var px = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            int pos = dataStart + BinaryPrimitives.ReadUInt16LittleEndian(raw[(lookupStart + y * 2)..]) * 2;
            int x = 0;
            while (pos + 4 <= raw.Length)
            {
                int xOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw[pos..]);
                int run = BinaryPrimitives.ReadUInt16LittleEndian(raw[(pos + 2)..]);
                pos += 4;
                if (xOffset + run == 0) break;
                x += xOffset;
                for (int i = 0; i < run && pos + 1 < raw.Length; i++, pos += 2, x++)
                    if (x < width) px[y * width + x] = ToBgra((ushort)(BinaryPrimitives.ReadUInt16LittleEndian(raw[pos..]) | 0x8000));
            }
        }
        return new Sprite(width, height, px);
    }

    public void Dispose() { _uop?.Dispose(); _mul?.Dispose(); }
}
