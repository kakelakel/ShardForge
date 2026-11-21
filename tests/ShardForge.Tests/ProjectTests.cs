using ShardForge.Core.Map;
using ShardForge.Core.Project;
using ShardForge.Core.Uop;

namespace ShardForge.Tests;

public class ProjectTests
{
    [Theory]
    // Reference values computed by the editor's JavaScript hrand(); the build must place
    // exactly what the editor previews, so these must never drift.
    [InlineData(0, 0, 1, 0, 0.40834908187389374)]
    [InlineData(5, 9, 12345, 1, 0.88084439607337117)]
    [InlineData(1447, 1023, 2147483000, 101, 0.34830219415016472)]
    [InlineData(300, 77, 80043619, 3, 0.54833856225013733)]
    public void HashMatchesEditor(int x, int y, int seed, int k, double expected) =>
        Assert.Equal(expected, ShardProject.HRand(x, y, seed, k), 15);

    private static ShardProject TinyProject()
    {
        const int w = 16, h = 16;
        var terrain = new byte[w * h];
        var height = new byte[w * h];
        var density = new byte[w * h];
        Array.Fill(density, (byte)255);
        for (int i = 0; i < terrain.Length; i++)
        {
            terrain[i] = (byte)(i % w < 8 ? 0 : 1);          // west half water, east half forest
            height[i] = unchecked((byte)(sbyte)(i % w < 8 ? -20 : 7));
        }
        return new ShardProject
        {
            Format = ShardProject.FormatName, Version = 1, Name = "tiny", Width = w, Height = h, Seed = 99,
            Terrains =
            [
                new TerrainDef { Key = "water", LandTiles = [0xA8, 0xA9], FixedZ = -5 },
                new TerrainDef { Key = "forest", LandTiles = [0xC4, 0xC5, 0xC6, 0xC7],
                    Populate = [new PopulateRule { Kind = "tree", Density = 0.5, Items = [[0x0CCD, 0x0CCE]] }] },
            ],
            Layers = new ProjectLayers
            {
                Terrain = Convert.ToBase64String(terrain),
                Height = Convert.ToBase64String(height),
                Density = Convert.ToBase64String(density),
            },
        };
    }

    [Fact]
    public void BuildsLandTilesHeightsAndPairedTreeStatics()
    {
        var facet = TinyProject().Build(7);

        Assert.Equal(new LandCell(facet.GetLand(0, 0).TileId, -5), facet.GetLand(0, 0)); // water uses fixed z
        Assert.Contains(facet.GetLand(0, 0).TileId, new ushort[] { 0xA8, 0xA9 });
        Assert.Equal(7, facet.GetLand(12, 3).Z);
        Assert.InRange(facet.GetLand(12, 3).TileId, (ushort)0xC4, (ushort)0xC7);

        // Open water gets nothing; the last three water cells before the forest are the shore: a sea bed at -15 under
        // flat water statics at sea level, as the client's own coasts are. Forest blocks get trunk + leaves pairs on
        // the same spot at z 7.
        var shore = facet.GetBlockStatics(0, 0);
        Assert.All(shore, s => { Assert.InRange(s.TileId, (ushort)0x1796, (ushort)0x17B2); Assert.Equal(-5, s.Z); Assert.True(s.X >= 5); });
        Assert.Equal(-15, facet.GetLand(7, 0).Z);
        Assert.DoesNotContain(shore, s => s.X < 5);
        var statics = facet.GetBlockStatics(1, 0);
        Assert.NotEmpty(statics);
        Assert.Equal(0, statics.Length % 2);
        Assert.All(statics, s => Assert.Equal(7, s.Z));
    }

    [Fact]
    public void WrittenFilesReadBackIdentically()
    {
        var facet = TinyProject().Build(7);
        string dir = Path.Combine(Path.GetTempPath(), "sf-" + Guid.NewGuid());
        try
        {
            FacetWriter.WriteAll(facet, dir, 7);
            var back = FacetLoader.Load(dir, 7, facet.Info); // land comes from the UOP we wrote

            Assert.True(back.Land.AsSpan().SequenceEqual(facet.Land));
            for (int b = 0; b < facet.Info.BlockCount; b++)
                Assert.True(back.Statics[b].AsSpan().SequenceEqual(facet.Statics[b]));

            using var uop = new UopFile(Path.Combine(dir, "map7LegacyMUL.uop"));
            Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "map7.mul")),
                uop.ReadConcatenated(i => FacetLoader.LandUopName(7, i)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RejectsMapSizesThatAreNotMultiplesOfEight()
    {
        var p = TinyProject();
        p.Width = 15;
        Assert.Throws<InvalidDataException>(p.Validate);
    }
}

public class LayerEncodingTests
{
    [Fact]
    public void DecodesGzipLayersFromTheEditor()
    {
        var data = Enumerable.Range(0, 4096).Select(i => (byte)(i % 7)).ToArray();
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data);
        var layers = new ProjectLayers { Encoding = "gzip-base64" };
        Assert.Equal(data, layers.Decode(Convert.ToBase64String(ms.ToArray())));
        Assert.Equal(data, new ProjectLayers().Decode(Convert.ToBase64String(data)));
    }
}
