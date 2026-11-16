using System.Buffers.Binary;
using ShardForge.Core.Data;
using ShardForge.Core.Imaging;
using ShardForge.Core.Map;
using ShardForge.Core.Uop;

namespace ShardForge.Tests;

public class UopHashTests
{
    [Theory]
    // Values observed in a real client's map4LegacyMUL.uop (client 7.0.x / Version.txt 1.25.35).
    [InlineData("build/map4legacymul/00000000.dat", 16042838398401671548UL)]
    [InlineData("build/map4legacymul/00000007.dat", 3648891388799047255UL)]
    public void MatchesClientHashes(string name, ulong expected) =>
        Assert.Equal(expected, UopHash.Hash(name));
}

public class UopFileTests
{
    [Fact]
    public void ReadsConcatenatedEntriesInIndexOrderRegardlessOfTableOrder()
    {
        var parts = new[] { "AAAA"u8.ToArray(), "BB"u8.ToArray(), "CCC"u8.ToArray() };
        string path = Path.GetTempFileName();
        try
        {
            // Store entries in a scrambled order, each with a small metadata header, like the real client.
            WriteUop(path, [(2, parts[2]), (0, parts[0]), (1, parts[1])], i => $"build/test/{i:D8}.dat");
            using var uop = new UopFile(path);
            Assert.Equal(3, uop.Entries.Count);
            Assert.Equal("AAAABBCCC"u8.ToArray(), uop.ReadConcatenated(i => $"build/test/{i:D8}.dat"));
        }
        finally { File.Delete(path); }
    }

    internal static void WriteUop(string path, (int Index, byte[] Data)[] files, Func<int, string> name)
    {
        const int headerLen = 8;
        using var w = new BinaryWriter(File.Create(path));
        long tableOffset = 0x200;
        w.Write(UopFile.Magic); w.Write(5u); w.Write(0xFD23EC43u); w.Write(tableOffset); w.Write(100u); w.Write(files.Length);
        w.BaseStream.Position = tableOffset;
        w.Write(files.Length); w.Write(0L);
        long dataStart = tableOffset + 12 + files.Length * 34;
        long pos = dataStart;
        foreach (var (index, data) in files)
        {
            w.Write(pos); w.Write(headerLen); w.Write(data.Length); w.Write(data.Length);
            w.Write(UopHash.Hash(name(index))); w.Write(0u); w.Write((short)0);
            pos += headerLen + data.Length;
        }
        foreach (var (_, data) in files) { w.Write(new byte[headerLen]); w.Write(data); }
    }
}

public class FacetTests
{
    [Fact]
    public void DecodesLandAndStaticsWithColumnMajorBlocks()
    {
        var info = new FacetInfo(99, "Test", 16, 16); // 2 x 2 blocks
        var land = new byte[info.BlockCount * FacetLoader.LandBlockSize];
        // Block (bx=1, by=0) is block index 1*2+0 = 2. Put tile 0x0003 z=5 at its cell (2,3).
        int cellOffset = 2 * FacetLoader.LandBlockSize + 4 + (3 * 8 + 2) * 3;
        BinaryPrimitives.WriteUInt16LittleEndian(land.AsSpan(cellOffset), 0x0003);
        land[cellOffset + 2] = 5;

        var statics = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(statics, 0x0CE6);
        statics[2] = 4; statics[3] = 6; statics[4] = unchecked((byte)-3);
        var index = new byte[info.BlockCount * 12];
        for (int b = 0; b < info.BlockCount; b++)
            BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(b * 12), -1);
        // Block (bx=0, by=1) is index 1.
        BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(12), 0);
        BinaryPrimitives.WriteInt32LittleEndian(index.AsSpan(16), 7);

        var facet = new Facet(info);
        FacetLoader.DecodeLand(land, facet);
        FacetLoader.DecodeStatics(index, statics, facet);

        Assert.Equal(new LandCell(0x0003, 5), facet.GetLand(8 + 2, 3));
        var s = Assert.Single(facet.GetBlockStatics(0, 1));
        Assert.Equal(new StaticTile(0x0CE6, 4, 6, -3, 0), s);
        Assert.Equal(1, facet.StaticCount);
    }
}

public class ImagingTests
{
    [Fact]
    public void WritesValidPngSignatureAndChunks()
    {
        using var ms = new MemoryStream();
        PngWriter.WriteRgb(ms, 2, 2, new byte[12]);
        var bytes = ms.ToArray();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes[..8]);
        Assert.Equal("IHDR"u8.ToArray(), bytes[12..16]);
        Assert.Equal("IEND"u8.ToArray(), bytes[^8..^4]);
    }

    [Fact]
    public void ExpandsRadarColours()
    {
        Assert.Equal(((byte)255, (byte)255, (byte)255), RadarColors.ToRgb(0x7FFF));
        Assert.Equal(((byte)255, (byte)0, (byte)0), RadarColors.ToRgb(0x7C00));
    }
}
