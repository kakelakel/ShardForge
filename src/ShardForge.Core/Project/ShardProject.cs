using System.Text.Json;
using System.Text.Json.Serialization;
using ShardForge.Core.Map;

namespace ShardForge.Core.Project;

/// <summary>A populate rule: on cells of this terrain, place one of <see cref="Items"/> with this probability.</summary>
public sealed class PopulateRule
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("density")] public double Density { get; set; }
    /// <summary>Each item is one or more static IDs placed on the same spot (e.g. trunk + leaves).</summary>
    [JsonPropertyName("items")] public int[][] Items { get; set; } = [];
}

public sealed class TerrainDef
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "";
    [JsonPropertyName("landTiles")] public int[] LandTiles { get; set; } = [];
    [JsonPropertyName("fixedZ")] public int? FixedZ { get; set; }
    [JsonPropertyName("populate")] public PopulateRule[] Populate { get; set; } = [];
}

public sealed class ProjectLayers
{
    [JsonPropertyName("terrain")] public string Terrain { get; set; } = "";
    [JsonPropertyName("height")] public string Height { get; set; } = "";
    [JsonPropertyName("density")] public string Density { get; set; } = "";
}

/// <summary>
/// A ShardForge editor project (.shardforge.json): painted terrain, altitude and vegetation density
/// layers plus the rules that turn them into UO land tiles and statics.
/// </summary>
public sealed class ShardProject
{
    public const string FormatName = "shardforge-project";

    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("seed")] public int Seed { get; set; }
    [JsonPropertyName("terrains")] public TerrainDef[] Terrains { get; set; } = [];
    [JsonPropertyName("layers")] public ProjectLayers Layers { get; set; } = new();

    public static ShardProject Load(string path)
    {
        using var fs = File.OpenRead(path);
        var p = JsonSerializer.Deserialize<ShardProject>(fs)
            ?? throw new InvalidDataException($"{path} is empty.");
        p.Validate();
        return p;
    }

    public void Validate()
    {
        if (Format != FormatName) throw new InvalidDataException("Not a ShardForge project (format field).");
        if (Version != 1) throw new InvalidDataException($"Unsupported project version {Version}.");
        if (Width <= 0 || Height <= 0 || Width % 8 != 0 || Height % 8 != 0)
            throw new InvalidDataException($"Map size {Width}x{Height} must be positive multiples of 8.");
        if (Terrains.Length == 0) throw new InvalidDataException("Project has no terrain definitions.");
        foreach (var t in Terrains)
            if (t.LandTiles.Length == 0) throw new InvalidDataException($"Terrain '{t.Key}' has no land tiles.");
    }

    /// <summary>Same hash the editor uses (lowbias32), so the preview matches the build exactly.</summary>
    public static double HRand(int x, int y, int seed, int k)
    {
        unchecked
        {
            uint h = (uint)seed ^ ((uint)x * 0x27d4eb2du) ^ ((uint)y * 0x165667b1u) ^ ((uint)k * 0x9e3779b9u);
            h ^= h >> 16; h *= 0x7feb352du;
            h ^= h >> 15; h *= 0x846ca68bu;
            h ^= h >> 16;
            return h / 4294967296.0;
        }
    }

    /// <summary>
    /// Bakes the painted layers into a facet: land tiles + altitude, then populated statics.
    /// With a transition table, cells on a border between two terrains get the matching edge or corner
    /// tile. A cell's corners are the terrains of itself and its east, south and south-east neighbours.
    /// </summary>
    public Facet Build(int facetIndex, ShardForge.Core.Terrain.TransitionTable? transitions = null)
    {
        byte[] terrain = Convert.FromBase64String(Layers.Terrain);
        byte[] height = Convert.FromBase64String(Layers.Height);
        byte[] density = Convert.FromBase64String(Layers.Density);
        int n = Width * Height;
        if (terrain.Length != n || height.Length != n || density.Length != n)
            throw new InvalidDataException("Layer sizes do not match the map size.");

        var info = new FacetInfo(facetIndex, Name, Width, Height);
        var facet = new Facet(info);
        var blockStatics = new List<StaticTile>[info.BlockCount];

        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = x + y * Width;
            int t = terrain[i];
            if (t >= Terrains.Length) throw new InvalidDataException($"Unknown terrain index {t} at {x},{y}.");
            var def = Terrains[t];

            ushort tile = (ushort)def.LandTiles[(int)(HRand(x, y, Seed, 0) * def.LandTiles.Length)];
            if (transitions is not null && TransitionTile(terrain, x, y, transitions) is int edge)
                tile = (ushort)edge;
            sbyte z = def.FixedZ is int fz ? (sbyte)fz : (sbyte)height[i];
            facet.SetLand(x, y, new LandCell(tile, z));

            double mult = density[i] / 128.0;
            for (int g = 0; g < def.Populate.Length; g++)
            {
                var rule = def.Populate[g];
                if (HRand(x, y, Seed, 1 + g) >= rule.Density * mult) continue;
                var item = rule.Items[(int)(HRand(x, y, Seed, 101 + g) * rule.Items.Length)];
                int block = facet.BlockIndex(x >> 3, y >> 3);
                var list = blockStatics[block] ??= [];
                foreach (int id in item)
                    list.Add(new StaticTile((ushort)id, (byte)(x & 7), (byte)(y & 7), z, 0));
                break; // at most one object per cell
            }
        }

        for (int b = 0; b < blockStatics.Length; b++)
            if (blockStatics[b] is { } list) facet.Statics[b] = list.ToArray();
        return facet;
    }

    private int? TransitionTile(byte[] terrain, int x, int y, ShardForge.Core.Terrain.TransitionTable table)
    {
        int x1 = Math.Min(x + 1, Width - 1), y1 = Math.Min(y + 1, Height - 1);
        Span<int> c = [terrain[x + y * Width], terrain[x1 + y * Width], terrain[x + y1 * Width], terrain[x1 + y1 * Width]];
        int own = c[0];
        if (c[1] == own && c[2] == own && c[3] == own) return null;

        // The other terrain: the most common non-own corner (three-way junctions collapse onto it).
        int other = -1, otherCount = 0;
        for (int i = 1; i < 4; i++)
        {
            if (c[i] == own) continue;
            int n = 0;
            for (int j = 1; j < 4; j++) if (c[j] == c[i]) n++;
            if (n > otherCount) { other = c[i]; otherCount = n; }
        }

        var corners = ShardForge.Core.Terrain.Corners.None;
        for (int i = 0; i < 4; i++)
            if (c[i] != own) corners |= (ShardForge.Core.Terrain.Corners)(1 << i);

        return table.Pick(Terrains[own].Key, Terrains[other].Key, corners, HRand(x, y, Seed, 50));
    }
}
