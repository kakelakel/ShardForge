using System.Buffers.Binary;
using ShardForge.Core.Uop;

namespace ShardForge.Core.Map;

/// <summary>Writes a facet back to client/server files: map#.mul or map#LegacyMUL.uop, plus staidx#/statics#.mul.</summary>
public static class FacetWriter
{
    /// <summary>Blocks per UOP chunk in map#LegacyMUL.uop (0xC4000 bytes each, as in the client).</summary>
    public const int BlocksPerUopChunk = 4096;

    public static byte[] EncodeLand(Facet facet)
    {
        var info = facet.Info;
        var data = new byte[(long)info.BlockCount * FacetLoader.LandBlockSize];
        for (int bx = 0; bx < info.BlockWidth; bx++)
        for (int by = 0; by < info.BlockHeight; by++)
        {
            int block = facet.BlockIndex(bx, by);
            var span = data.AsSpan(block * FacetLoader.LandBlockSize, FacetLoader.LandBlockSize);
            BinaryPrimitives.WriteUInt32LittleEndian(span, facet.LandBlockHeaders[block]);
            int p = 4;
            for (int cy = 0; cy < 8; cy++)
            for (int cx = 0; cx < 8; cx++)
            {
                var c = facet.GetLand(bx * 8 + cx, by * 8 + cy);
                BinaryPrimitives.WriteUInt16LittleEndian(span[p..], c.TileId);
                span[p + 2] = (byte)c.Z;
                p += 3;
            }
        }
        return data;
    }

    public static (byte[] Index, byte[] Data) EncodeStatics(Facet facet)
    {
        var index = new byte[facet.Info.BlockCount * FacetLoader.StaticIndexEntrySize];
        using var data = new MemoryStream();
        Span<byte> entry = stackalloc byte[FacetLoader.StaticEntrySize];
        for (int block = 0; block < facet.Info.BlockCount; block++)
        {
            var e = index.AsSpan(block * FacetLoader.StaticIndexEntrySize);
            var tiles = facet.Statics[block];
            if (tiles.Length == 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(e, -1);
                BinaryPrimitives.WriteInt32LittleEndian(e[4..], -1);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(e, (int)data.Position);
                BinaryPrimitives.WriteInt32LittleEndian(e[4..], tiles.Length * FacetLoader.StaticEntrySize);
                foreach (var t in tiles)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(entry, t.TileId);
                    entry[2] = t.X; entry[3] = t.Y; entry[4] = (byte)t.Z;
                    BinaryPrimitives.WriteUInt16LittleEndian(entry[5..], t.Hue);
                    data.Write(entry);
                }
            }
            BinaryPrimitives.WriteInt32LittleEndian(e[8..], facet.StaticIndexExtra[block]);
        }
        return (index, data.ToArray());
    }

    /// <summary>Writes map#.mul, map#LegacyMUL.uop, staidx#.mul and statics#.mul into <paramref name="dir"/>.</summary>
    public static IReadOnlyList<string> WriteAll(Facet facet, string dir, int facetIndex, bool uop = true, bool mul = true)
    {
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        byte[] land = EncodeLand(facet);

        if (mul)
        {
            string p = Path.Combine(dir, $"map{facetIndex}.mul");
            File.WriteAllBytes(p, land);
            written.Add(p);
        }
        if (uop)
        {
            string p = Path.Combine(dir, $"map{facetIndex}LegacyMUL.uop");
            int chunk = BlocksPerUopChunk * FacetLoader.LandBlockSize;
            var files = new List<(string, byte[])>();
            for (int i = 0, off = 0; off < land.Length; i++, off += chunk)
                files.Add((FacetLoader.LandUopName(facetIndex, i), land.AsSpan(off, Math.Min(chunk, land.Length - off)).ToArray()));
            UopWriter.Write(p, files);
            written.Add(p);
        }

        var (index, statics) = EncodeStatics(facet);
        string ip = Path.Combine(dir, $"staidx{facetIndex}.mul"), sp = Path.Combine(dir, $"statics{facetIndex}.mul");
        File.WriteAllBytes(ip, index);
        File.WriteAllBytes(sp, statics);
        written.Add(ip); written.Add(sp);
        return written;
    }
}
