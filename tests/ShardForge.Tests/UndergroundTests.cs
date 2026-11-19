using ShardForge.Core.Map;
using ShardForge.Core.Project;
using Xunit;

namespace ShardForge.Tests;

public class UndergroundTests
{
    /// <summary>A 64x64 void with a cave (x 8-23) and a built dungeon room (x 40-51), both at z 0.</summary>
    static ShardProject Dungeons()
    {
        const int w = 64, h = 64;
        var terrain = new byte[w * h]; // 0 void
        for (int y = 20; y < 40; y++) for (int x = 8; x < 24; x++) terrain[x + y * w] = 1; // cave
        for (int y = 20; y < 30; y++) for (int x = 40; x < 52; x++) terrain[x + y * w] = 2; // dungeon
        return new ShardProject
        {
            Format = ShardProject.FormatName, Version = 1, Name = "Below", Width = w, Height = h, Seed = 5,
            Terrains =
            [
                new TerrainDef { Key = "void", Name = "Void", LandTiles = [0x244] },
                new TerrainDef { Key = "cave", Name = "Cave floor", LandTiles = [0x245, 0x246, 0x247, 0x248] },
                new TerrainDef { Key = "dungeon", Name = "Dungeon floor", LandTiles = [0x43E, 0x43F, 0x440, 0x441] },
            ],
            Layers = new ProjectLayers { Terrain = Convert.ToBase64String(terrain), Height = Convert.ToBase64String(new byte[w * h]), Density = Convert.ToBase64String(new byte[w * h]) },
        };
    }

    [Fact]
    public void CavesGetRisingRockWallsAndTheVoidStaysBlack()
    {
        var p = Dungeons();
        var f = p.Build(2, ShardForge.Core.Terrain.TransitionTable.BuiltIn);
        // Floor stays cave floor at z 0.
        Assert.InRange(f.GetLand(15, 30).TileId, 0x245, 0x248);
        Assert.Equal(0, f.GetLand(15, 30).Z);
        // West of the cave: walls facing east-west, rising; north: walls facing north-south.
        Assert.Equal(0x25A, f.GetLand(7, 30).TileId);
        Assert.Equal(0x266, f.GetLand(15, 19).TileId);
        Assert.InRange(f.GetLand(7, 30).Z, 5, 9);
        Assert.True(f.GetLand(6, 30).Z > f.GetLand(7, 30).Z);
        // Far away: plain void.
        Assert.Equal(0x244, f.GetLand(30, 5).TileId);
        Assert.True(p.LastUnderground!.WallCells > 100);
        // No blends were invented between cave floor and void.
        Assert.Equal(0, p.LastSoften!.Bridged);
    }

    [Fact]
    public void DungeonRoomsGetWallsLikeAHouse()
    {
        var p = Dungeons();
        var f = p.Build(2, ShardForge.Core.Terrain.TransitionTable.BuiltIn);
        StaticTile[] At(int x, int y) => f.Statics[f.BlockIndex(x >> 3, y >> 3)].Where(s => s.X == (x & 7) && s.Y == (y & 7)).ToArray();
        // North wall on the south edge of the row above, south wall on the last row; east-west pieces 0x241.
        Assert.Contains(At(45, 19), s => s.TileId == 0x241);
        Assert.Contains(At(45, 29), s => s.TileId == 0x241);
        // West wall on the column to the left, east wall on the last column; north-south pieces 0x242.
        Assert.Contains(At(39, 25), s => s.TileId == 0x242);
        Assert.Contains(At(51, 25), s => s.TileId == 0x242);
        // The south-east corner tile has both: the corner piece.
        Assert.Contains(At(51, 29), s => s.TileId == 0x243);
        // No rock cliffs around the room: the void there stays black and level.
        Assert.Equal(0x244, f.GetLand(39, 25).TileId);
        Assert.Equal(0, f.GetLand(38, 25).Z);
    }
}
