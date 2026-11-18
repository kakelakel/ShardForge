using ShardForge.Core.Terrain;
namespace ShardForge.Tests;

public class BridgeTests
{
    [Theory]
    [InlineData("forest", "snow")]
    [InlineData("grass", "snow")]
    [InlineData("grass", "cobblestone")]
    public void HardPairsGetAThinStripInsteadOfOneEatingTheOther(string top, string bottom)
    {
        string[] keys = ["water", "grass", "forest", "snow", "dirt", "rock", "cobblestone"];
        int w = 20, h = 20, a = Array.IndexOf(keys, top), b = Array.IndexOf(keys, bottom);
        var terrain = new byte[w * h];
        for (int i = 0; i < terrain.Length; i++) terrain[i] = (byte)(i / w < 10 ? a : b);
        EdgeSoftener.Soften(terrain, w, h, keys, TransitionTable.BuiltIn);
        var column = Enumerable.Range(0, h).Select(y => keys[terrain[5 + y * w]]).ToArray();
        // Both terrains survive almost whole; between them a strip of something that blends with both.
        Assert.True(column.Take(8).All(k => k == top), string.Join(" ", column));
        Assert.True(column.Skip(12).All(k => k == bottom), string.Join(" ", column));
        Assert.Contains(column.Skip(8).Take(4), k => k != top && k != bottom);
    }
}
