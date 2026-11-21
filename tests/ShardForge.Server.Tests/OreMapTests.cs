using System.Text.Json.Nodes;
using ShardForge.Core.Map;
using ShardForge.Server.Maps;

namespace ShardForge.Server.Tests;

public class OreMapTests
{
    [Fact]
    public void MountainsGetOreInServUOsSharesRarerFarFromTowns()
    {
        // Grass everywhere, a band of mountain rock (mineable) across the middle, a town at the west end.
        var facet = new Facet(new FacetInfo(0, "t", 512, 256));
        for (int y = 0; y < 256; y++)
        for (int x = 0; x < 512; x++)
            facet.SetLand(x, y, y is >= 96 and < 160 ? new LandCell(0x22C, 40) : new LandCell(0x03, 0));
        var items = new[] { new JsonObject { ["kind"] = "region", ["guarded"] = true, ["x"] = 0, ["y"] = 100, ["w"] = 40, ["h"] = 40, ["name"] = "Town" } };

        var rows = OreMap.Rows(facet, items, "test");

        Assert.All(rows, r => Assert.InRange(r.Y, 96 / 8, 159 / 8)); // only the mountain
        var all = string.Concat(rows.Select(r => r.Row)).Where(c => c != '.').ToList();
        Assert.Equal(64 * 8, all.Count); // every mountain bank
        Assert.InRange(all.Count(c => c == '0') * 100.0 / all.Count, 48, 51); // about half iron
        Assert.Contains('8', all); // some valorite
        // Near the town: iron mostly; the rare ores lie further east.
        double Mean(Func<int, bool> col) => rows.SelectMany(r => r.Row.Select((c, x) => (c, x))).Where(t => t.c != '.' && col(t.x)).Average(t => t.c - '0');
        Assert.True(Mean(x => x < 8) < Mean(x => x >= 40));

        var text = ItemStore.SfItems("Felucca", "t", [], rows);
        Assert.Contains("ore\t12\t", text);
    }
}
