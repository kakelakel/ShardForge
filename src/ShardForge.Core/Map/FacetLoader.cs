using System.Buffers.Binary;
using ShardForge.Core.Uop;

namespace ShardForge.Core.Map;

/// <summary>Loads a facet's land (map#.mul or map#LegacyMUL.uop) and statics (staidx#/statics#.mul).</summary>
public static class FacetLoader
{
    public const int LandBlockSize = 4 + 64 * 3; // header + 64 cells of (ushort id, sbyte z)
    public const int StaticIndexEntrySize = 12;  // int lookup, int length, int extra
    public const int StaticEntrySize = 7;        // ushort id, byte x, byte y, sbyte z, ushort hue

    public static string LandUopName(int facet, int chunk) => $"build/map{facet}legacymul/{chunk:D8}.dat";

    public static Facet Load(string clientDir, int facetIndex, FacetInfo? info = null)
    {
        info ??= InferInfo(clientDir, facetIndex);
        var facet = new Facet(info);

        byte[] land = ReadLandBytes(clientDir, facetIndex);
        DecodeLand(land, facet);

        string idx = System.IO.Path.Combine(clientDir, $"staidx{facetIndex}.mul");
        string sta = System.IO.Path.Combine(clientDir, $"statics{facetIndex}.mul");
        if (File.Exists(idx) && File.Exists(sta))
            DecodeStatics(File.ReadAllBytes(idx), File.ReadAllBytes(sta), facet);

        return facet;
    }

    /// <summary>Uses the known size table, cross-checked against staidx size when available.</summary>
    public static FacetInfo InferInfo(string clientDir, int facetIndex)
    {
        var known = FacetInfo.ForIndex(facetIndex);
        string idx = System.IO.Path.Combine(clientDir, $"staidx{facetIndex}.mul");
        if (File.Exists(idx))
        {
            long blocks = new FileInfo(idx).Length / StaticIndexEntrySize;
            if (blocks != known.BlockCount)
            {
                // Older clients had a 6144-wide Felucca/Trammel.
                if (facetIndex is 0 or 1 && blocks == (6144 / 8) * (4096 / 8))
                    return known with { Width = 6144 };
                throw new InvalidDataException(
                    $"staidx{facetIndex}.mul has {blocks} blocks but {known.Name} should have {known.BlockCount}. " +
                    "Custom map size? Pass explicit dimensions.");
            }
        }
        return known;
    }

    public static byte[] ReadLandBytes(string clientDir, int facetIndex)
    {
        string uop = System.IO.Path.Combine(clientDir, $"map{facetIndex}LegacyMUL.uop");
        if (File.Exists(uop))
        {
            using var file = new UopFile(uop);
            return file.ReadConcatenated(i => LandUopName(facetIndex, i));
        }

        string mul = System.IO.Path.Combine(clientDir, $"map{facetIndex}.mul");
        if (File.Exists(mul))
            return File.ReadAllBytes(mul);

        throw new FileNotFoundException($"No land file for facet {facetIndex} (looked for {uop} and {mul}).");
    }

    public static void DecodeLand(ReadOnlySpan<byte> data, Facet facet)
    {
        var info = facet.Info;
        long needed = (long)info.BlockCount * LandBlockSize;
        if (data.Length < needed)
            throw new InvalidDataException($"Land data is {data.Length} bytes; {info.Name} needs {needed}.");

        for (int bx = 0; bx < info.BlockWidth; bx++)
        for (int by = 0; by < info.BlockHeight; by++)
        {
            int block = facet.BlockIndex(bx, by);
            var span = data.Slice(block * LandBlockSize, LandBlockSize);
            facet.LandBlockHeaders[block] = BinaryPrimitives.ReadUInt32LittleEndian(span);
            int p = 4;
            for (int cy = 0; cy < 8; cy++)
            for (int cx = 0; cx < 8; cx++)
            {
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(span[p..]);
                sbyte z = (sbyte)span[p + 2];
                p += 3;
                facet.SetLand(bx * 8 + cx, by * 8 + cy, new LandCell(id, z));
            }
        }
    }

    public static void DecodeStatics(ReadOnlySpan<byte> index, ReadOnlySpan<byte> data, Facet facet)
    {
        int blocks = Math.Min(facet.Info.BlockCount, index.Length / StaticIndexEntrySize);
        for (int block = 0; block < blocks; block++)
        {
            var e = index.Slice(block * StaticIndexEntrySize, StaticIndexEntrySize);
            int lookup = BinaryPrimitives.ReadInt32LittleEndian(e);
            int length = BinaryPrimitives.ReadInt32LittleEndian(e[4..]);
            facet.StaticIndexExtra[block] = BinaryPrimitives.ReadInt32LittleEndian(e[8..]);

            if (lookup < 0 || length <= 0) continue;
            if ((long)lookup + length > data.Length)
                throw new InvalidDataException($"Statics block {block} points past end of statics file.");

            int count = length / StaticEntrySize;
            var tiles = new StaticTile[count];
            var s = data.Slice(lookup, count * StaticEntrySize);
            for (int i = 0; i < count; i++)
            {
                var t = s.Slice(i * StaticEntrySize, StaticEntrySize);
                tiles[i] = new StaticTile(
                    BinaryPrimitives.ReadUInt16LittleEndian(t),
                    t[2], t[3], (sbyte)t[4],
                    BinaryPrimitives.ReadUInt16LittleEndian(t[5..]));
            }
            facet.Statics[block] = tiles;
        }
    }
}
