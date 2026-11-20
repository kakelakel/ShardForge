using ShardForge.Core.Terrain;
using Xunit;

namespace ShardForge.Tests;

public class LiquidEdgesTests
{
    [Fact]
    public void LavaAndAcidPoolsGetRimsAllRound()
    {
        var table = LiquidEdges.BuiltIn;
        Assert.Contains("lava", table.Liquids.Keys);
        Assert.Contains("acid", table.Liquids.Keys);
        foreach (var key in new[] { "lava", "acid" })
        {
            // A 10x10 pool in the middle of a 30x30 cave.
            string? At(int x, int y) => x is >= 10 and < 20 && y is >= 10 and < 20 ? key : "cave";
            var rims = table.Rims(30, 30, At, (_, _) => 0, (x, y, k) => ((x * 73856093) ^ (y * 19349663) ^ (k * 83492791)) % 1000 / 1000.0 is var r && r < 0 ? -r : r);
            Assert.True(rims.Count >= 20, $"{key}: only {rims.Count} rim pieces round a 10x10 pool");
            // Only at the edge: on the pool's outer ring or the floor right beside it, never deep inside or far out.
            Assert.All(rims, p => Assert.True(p.X is >= 9 and <= 20 && p.Y is >= 9 and <= 20 && !(p.X is >= 11 and <= 18 && p.Y is >= 11 and <= 18)));
        }
    }
}
