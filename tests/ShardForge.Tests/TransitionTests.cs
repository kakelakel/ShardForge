using ShardForge.Core.Project;
using ShardForge.Core.Terrain;

namespace ShardForge.Tests;

public class TransitionTests
{
    [Fact]
    public void BuiltInTableHasTheTwelveClassicPatternsForGrassAndSand()
    {
        var table = TransitionTable.BuiltIn;
        int[] classic = [1, 2, 3, 4, 5, 7, 8, 10, 11, 12, 13, 14]; // edges, outer and inner corners
        foreach (var p in classic)
            Assert.NotNull(table.Find("grass", "sand", (Corners)p));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.999)]
    public void PicksTheSameTileFromEitherSideOfTheBorder(double roll)
    {
        var table = TransitionTable.BuiltIn;
        // Grass cell with sand only in its bottom-right corner == sand cell with grass in the other three.
        int? fromGrass = table.Pick("grass", "sand", Corners.BottomRight, roll);
        int? fromSand = table.Pick("sand", "grass", Corners.TopLeft | Corners.TopRight | Corners.BottomLeft, roll);
        Assert.NotNull(fromGrass);
        Assert.Equal(fromGrass, fromSand);
    }

    [Fact]
    public void SkipsCandidatesWhoseArtIsNotABlend()
    {
        // 0x0467 sits in grass/dirt borders on real maps but is a bright special tile, not a blend.
        var table = TransitionTable.BuiltIn;
        for (double roll = 0; roll < 1; roll += 0.01)
            Assert.NotEqual(0x0467, table.Pick("grass", "dirt", (Corners)7 /* TL TR BL dirt */, roll));
    }

    [Fact]
    public void BuildPlacesEdgeTilesOnlyAlongTheBorder()
    {
        const int w = 16, h = 16;
        var terrain = new byte[w * h];
        for (int i = 0; i < terrain.Length; i++) terrain[i] = (byte)(i % w < 8 ? 0 : 1); // grass | sand
        var p = new ShardProject
        {
            Format = ShardProject.FormatName, Version = 1, Name = "edge", Width = w, Height = h, Seed = 5,
            Terrains =
            [
                new TerrainDef { Key = "grass", LandTiles = [0x03, 0x04, 0x05, 0x06] },
                new TerrainDef { Key = "sand", LandTiles = [0x16, 0x17, 0x18, 0x19] },
            ],
            Layers = new ProjectLayers
            {
                Terrain = Convert.ToBase64String(terrain),
                Height = Convert.ToBase64String(new byte[w * h]),
                Density = Convert.ToBase64String(new byte[w * h]),
            },
        };

        var plain = p.Build(0);
        var blended = p.Build(0, TransitionTable.BuiltIn);
        var edge = TransitionTable.BuiltIn.Find("grass", "sand", Corners.TopRight | Corners.BottomRight)!.Select(c => c.Tile).ToHashSet();

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (x == 7) Assert.Contains(blended.GetLand(x, y).TileId, edge);           // last grass column
            else Assert.Equal(plain.GetLand(x, y).TileId, blended.GetLand(x, y).TileId); // untouched
        }
    }
}
