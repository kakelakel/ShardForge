using ShardForge.Core.Architecture;
using ShardForge.Core.Project;

namespace ShardForge.Tests;

public class ArchitectTests
{
    public static TheoryData<string> AllThemes()
    {
        var d = new TheoryData<string>();
        foreach (var t in Architect.Themes.Keys) d.Add(t);
        return d;
    }

    [Theory]
    [MemberData(nameof(AllThemes))]
    public void EveryThemeBuildsWithinItsFootprintAndRoofOverhang(string theme)
    {
        var spec = new BuildingSpec { X = 100, Y = 200, W = 8, H = 6, Z = 10, Floors = 2, Theme = theme, Door = "S", Use = "tavern", Seed = 3 };
        var placed = Architect.Generate(spec);

        Assert.NotEmpty(placed);
        // Walls, roof and furniture stay on the footprint (plus the one-tile roof overhang and an optional lamp post).
        Assert.All(placed, p =>
        {
            Assert.InRange(p.X, spec.X - 2, spec.X + spec.W + 2);
            Assert.InRange(p.Y, spec.Y - 2, spec.Y + spec.H + 2);
            Assert.InRange(p.Z, spec.Z, spec.Z + 20 * spec.Floors + 40);
        });
        // Two storeys: something stands on the upper floor.
        Assert.Contains(placed, p => p.Z == spec.Z + 20);
    }

    [Theory]
    [InlineData("N")]
    [InlineData("S")]
    [InlineData("E")]
    [InlineData("W")]
    public void LeavesADoorwayInTheChosenWall(string door)
    {
        var spec = new BuildingSpec { X = 50, Y = 50, W = 6, H = 6, Z = 0, Theme = "plaster", Door = door, Use = "storage", Seed = 1 };
        var ground = Architect.Generate(spec).Where(p => p.Z == 0).ToList();
        var kits = Kits.BuiltIn;
        var wallIds = kits.Walls.SelectMany(w => w.EW.Concat(w.NS).Concat(w.WindowEW).Concat(w.WindowNS).Append(w.Corner).Append(w.Post)).ToHashSet();

        // The wall line holding the door, as the architect lays it out (see Architect's class remarks).
        var (line, onX) = door switch { "N" => (spec.Y - 1, true), "S" => (spec.Y + spec.H - 1, true), "E" => (spec.X + spec.W - 1, false), _ => (spec.X - 1, false) };
        var walls = ground.Where(p => wallIds.Contains(p.Id) && (onX ? p.Y == line : p.X == line)).Select(p => onX ? p.X : p.Y).ToHashSet();
        var span = onX ? Enumerable.Range(spec.X, spec.W - 1) : Enumerable.Range(spec.Y, spec.H - 1);
        Assert.Contains(span, i => !walls.Contains(i));
    }

    [Fact]
    public void SameSeedSameBuilding()
    {
        BuildingSpec Spec(int seed) => new() { X = 10, Y = 10, W = 10, H = 8, Z = 5, Floors = 2, Theme = "brick", Use = "shop", Seed = seed };
        Assert.Equal(Architect.Generate(Spec(42)), Architect.Generate(Spec(42)));
    }

    [Fact]
    public void BuiltInKitsAreLoaded()
    {
        var kits = Kits.BuiltIn;
        Assert.True(kits.Walls.Count > 100);
        Assert.True(kits.Roofs.Count > 20);
        Assert.True(kits.Floors.Count > 20);
        foreach (var t in Architect.Themes.Values)
            Assert.Contains(kits.Walls, w => w.Name == t.Walls);
    }

    [Fact]
    public void ProjectBuildStampsBuildingsAndStaticsAndClearsTrees()
    {
        const int w = 32, h = 32;
        var terrain = new byte[w * h];
        var height = new byte[w * h];
        var density = Enumerable.Repeat((byte)255, w * h).ToArray();
        Array.Fill(height, (byte)10);
        var project = new ShardProject
        {
            Format = ShardProject.FormatName, Version = 1, Name = "town", Width = w, Height = h, Seed = 5,
            Terrains =
            [
                new TerrainDef { Key = "forest", LandTiles = [0xC4, 0xC5, 0xC6, 0xC7],
                    Populate = [new PopulateRule { Kind = "tree", Density = 0.9, Items = [[0x0CCD, 0x0CCE]] }] },
            ],
            Layers = new ProjectLayers { Terrain = Convert.ToBase64String(terrain), Height = Convert.ToBase64String(height), Density = Convert.ToBase64String(density) },
            Objects = new ProjectObjects
            {
                Buildings = [new BuildingSpec { X = 9, Y = 9, W = 6, H = 6, Z = 10, Theme = "wood", Door = "S", Use = "home", Seed = 7 }],
                Statics = [[0x0B24, 2, 2, 10], [0x0E3C, 3, 2, 10, 0x21]],
            },
        };
        var facet = project.Build(7);
        var all = Enumerable.Range(0, 4).SelectMany(by => Enumerable.Range(0, 4).SelectMany(bx =>
            facet.GetBlockStatics(bx, by).Select(s => (s.TileId, X: bx * 8 + s.X, Y: by * 8 + s.Y, s.Z, s.Hue)))).ToList();

        var expected = Architect.Generate(project.Objects.Buildings[0]);
        foreach (var p in expected) Assert.Contains(all, s => s.TileId == p.Id && s.X == p.X && s.Y == p.Y && s.Z == p.Z);
        Assert.Contains(all, s => s.TileId == 0x0B24 && s.X == 2 && s.Y == 2);
        Assert.Contains(all, s => s.TileId == 0x0E3C && s.Hue == 0x21);
        // No trees inside the building.
        var (x0, y0, x1, y1) = Architect.Footprint(project.Objects.Buildings[0]);
        Assert.DoesNotContain(all, s => s.TileId is 0x0CCD or 0x0CCE && s.X >= x0 && s.X <= x1 && s.Y >= y0 && s.Y <= y1);
        // Trees elsewhere still grow.
        Assert.Contains(all, s => s.TileId == 0x0CCD);
    }
}

public class SoftEdgeTests
{
    [Fact]
    public void BuildLeavesNoHardLandEdges()
    {
        const int w = 64, h = 64;
        string[] keys = ["grass", "forest", "cobblestone", "sand", "dirt", "rock", "jungle", "farmland"];
        var terrain = new byte[w * h];
        var rnd = new Random(3);
        // Blobs of every terrain over grass, with ragged edges and speckles: the worst case for blending.
        for (int n = 0; n < 40; n++)
        {
            int cx = rnd.Next(w), cy = rnd.Next(h), r = rnd.Next(2, 9), t = rnd.Next(keys.Length);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r + rnd.Next(-3, 4)) terrain[x + y * w] = (byte)t;
        }
        var project = new ShardProject
        {
            Format = ShardProject.FormatName, Version = 1, Name = "soft", Width = w, Height = h, Seed = 1,
            Terrains = keys.Select(k => new TerrainDef { Key = k, LandTiles = ShardForge.Core.Terrain.TerrainClasses.Pure[k].Select(t => (int)t).ToArray() }).ToArray(),
            Layers = new ProjectLayers
            {
                Terrain = Convert.ToBase64String(terrain),
                Height = Convert.ToBase64String(new byte[w * h]),
                Density = Convert.ToBase64String(new byte[w * h]),
            },
        };
        project.Build(7, ShardForge.Core.Terrain.TransitionTable.BuiltIn);
        var report = project.LastSoften!;
        Assert.True(report.Exact > 0);
        Assert.True(report.Bridged > 0);         // cobblestone next to grass got a dirt strip
        Assert.True(report.HardTotal == 0, string.Join(", ", report.Hard.Select(p => $"{p.Key} {p.Value}")));
    }
}

public class LayoutTests
{
    [Fact]
    public void WritesPlacesAndPlotsForWalk()
    {
        var project = new ShardProject
        {
            Places = [new ProjectPlace { Name = "Camp", Kind = "camp", X = 5, Y = 6 }, new ProjectPlace { Name = "Ash", Kind = "town", X = 1, Y = 2 },
                      new ProjectPlace { Name = "Graveyard", Kind = "graveyard", X = 3, Y = 3 }],
            Objects = new ProjectObjects { Lots = [new ProjectLot { X = 10, Y = 11, W = 8, H = 6, Face = "E", Kind = "inn" }] },
        };
        string dir = Directory.CreateTempSubdirectory().FullName;
        project.WriteLayout(dir);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, ShardProject.LayoutFile)));
        var places = doc.RootElement.GetProperty("places").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
        Assert.Equal(["Ash", "Camp"], places);                     // towns first; graveyards aren't listed
        var lot = doc.RootElement.GetProperty("lots")[0];
        Assert.Equal(8, lot.GetProperty("w").GetInt32());
        Assert.Equal("E", lot.GetProperty("face").GetString());

        new ShardProject().WriteLayout(dir);                        // nothing to write: the stale file goes
        Assert.False(File.Exists(Path.Combine(dir, ShardProject.LayoutFile)));
    }
}
