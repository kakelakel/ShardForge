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
    /// <summary>
    /// Craggy relief added at build time, in altitude units (UO mountains are rough: neighbouring rock tiles
    /// typically differ by 4 and often by 12+). Ramped in from the terrain's border. Null = default for the key.
    /// </summary>
    [JsonPropertyName("crag")] public int? Crag { get; set; }

    public int EffectiveCrag => Crag ?? Key switch { "rock" => 30, "snow" => 16, _ => 0 };
    [JsonPropertyName("populate")] public PopulateRule[] Populate { get; set; } = [];
}

/// <summary>Placed things: buildings (multis) and individual statics, with explicit altitude.</summary>
public sealed class ProjectObjects
{
    /// <summary>[multiId, x, y, z]: a building from multi.mul, its centre at x,y.</summary>
    [JsonPropertyName("multis")] public int[][] Multis { get; set; } = [];
    /// <summary>[tileId, x, y, z] or [tileId, x, y, z, hue].</summary>
    [JsonPropertyName("statics")] public int[][] Statics { get; set; } = [];
    /// <summary>Procedurally generated buildings (walls, roofs, floors and furniture made at build time).</summary>
    [JsonPropertyName("buildings")] public ShardForge.Core.Architecture.BuildingSpec[] Buildings { get; set; } = [];
    /// <summary>Building plots laid out by the settlement planner: levelled, cleared ground to build on by hand.</summary>
    [JsonPropertyName("lots")] public ProjectLot[] Lots { get; set; } = [];
    /// <summary>
    /// Server items the generator placed (traps, spawners, braziers that burn...), in the same shape Walk's server
    /// items have ({"kind": "item", "type": "SpikeTrap", "x", "y", "z", ...}). Building the project puts them on
    /// the map, replacing the ones the previous build put there; items placed by hand stay.
    /// </summary>
    [JsonPropertyName("serverItems")] public System.Text.Json.Nodes.JsonObject[] ServerItems { get; set; } = [];
    /// <summary>
    /// [landTileId, x, y, z]: land tiles set by hand over what the terrain would give (cave mouths in a mountain
    /// side use the client's own mouth tiles). Natural statics on those cells are cleared.
    /// </summary>
    [JsonPropertyName("land")] public int[][] Land { get; set; } = [];
}

/// <summary>A building plot: x, y is the north-west tile; <see cref="Face"/> is the side that faces its road.</summary>
public sealed class ProjectLot
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; }
    [JsonPropertyName("h")] public int H { get; set; }
    [JsonPropertyName("z")] public int? Z { get; set; }
    [JsonPropertyName("face")] public string Face { get; set; } = "S";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "home";
}

/// <summary>A named place (town, village, camp...). Used for labels now, ServUO regions later.</summary>
public sealed class ProjectPlace
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("radius")] public int Radius { get; set; }
}

public sealed class ProjectLayers
{
    /// <summary>"base64" (default, raw bytes) or "gzip-base64" (gzip-compressed, then base64).</summary>
    [JsonPropertyName("encoding")] public string? Encoding { get; set; }

    public byte[] Decode(string layer)
    {
        byte[] raw = Convert.FromBase64String(layer);
        if (Encoding is null or "base64") return raw;
        if (Encoding != "gzip-base64") throw new InvalidDataException($"Unknown layer encoding '{Encoding}'.");
        using var gz = new System.IO.Compression.GZipStream(new MemoryStream(raw), System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

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
    [JsonPropertyName("objects")] public ProjectObjects? Objects { get; set; }
    [JsonPropertyName("places")] public ProjectPlace[]? Places { get; set; }

    public bool HasBuildings => Objects?.Multis.Length > 0;

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
    public const string LayoutFile = "layout.json";

    /// <summary>
    /// Writes layout.json next to built map files: the project's named places (towns, camps, ruins...) and building
    /// plots, for Walk's "Known places" list and plot overlay. Removes a stale one when there is nothing to write.
    /// </summary>
    public void WriteLayout(string dir)
    {
        string path = Path.Combine(dir, LayoutFile);
        string[] order = ["city", "town", "village", "hamlet", "camp", "ruins", "shrine", "dungeon", "castle", "cave", "fort", "tower", "lighthouse", "mill", "wreck", "oasis", "stones"];
        var places = (Places ?? []).Where(p => order.Contains(p.Kind))
            .OrderBy(p => Array.IndexOf(order, p.Kind)).ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new LayoutPlace(p.Name, p.Kind, p.X, p.Y)).ToArray();
        var lots = Objects?.Lots ?? [];
        if (places.Length == 0 && lots.Length == 0) { File.Delete(path); return; }
        File.WriteAllText(path, JsonSerializer.Serialize(new Layout(places, lots), LayoutJson.Options));
    }
    private sealed record LayoutPlace([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("x")] int X, [property: JsonPropertyName("y")] int Y);
    private sealed record Layout([property: JsonPropertyName("places")] LayoutPlace[] Places, [property: JsonPropertyName("lots")] ProjectLot[] Lots);
    private static class LayoutJson { public static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }; }

    /// <summary>What the soft-edge pass changed in the last <see cref="Build"/> (null without transitions).</summary>
    [JsonIgnore] public ShardForge.Core.Terrain.SoftenReport? LastSoften { get; private set; }

    /// <summary>What the underground pass made in the last <see cref="Build"/> (null when the project has no void).</summary>
    [JsonIgnore] public ShardForge.Core.Terrain.Underground.Plan? LastUnderground { get; private set; }
    [JsonIgnore] public int LastRims { get; private set; }
    /// <summary>Water statics laid along the shores in the last build.</summary>
    [JsonIgnore] public int LastShore { get; private set; }

    private bool NextToShore(byte[] shoreDist, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            int X = x + dx, Y = y + dy;
            if (X >= 0 && Y >= 0 && X < Width && Y < Height && shoreDist[X + Y * Width] == 1) return true;
        }
        return false;
    }

    /// <summary>Which dungeon wall set built dungeon floors get (see Underground.DungeonWalls).</summary>
    [JsonPropertyName("dungeonWalls")] public int DungeonWallSet { get; set; }

    public Facet Build(int facetIndex, ShardForge.Core.Terrain.TransitionTable? transitions = null, ShardForge.Core.Data.MultiData? multis = null)
    {
        if (HasBuildings && multis is null)
            throw new InvalidDataException("This project has buildings. Building it needs the client's multi.mul (pass the client folder).");
        byte[] terrain = Layers.Decode(Layers.Terrain);
        byte[] height = Layers.Decode(Layers.Height);
        byte[] density = Layers.Decode(Layers.Density);
        int n = Width * Height;
        LastShore = 0;
        if (terrain.Length != n || height.Length != n || density.Length != n)
            throw new InvalidDataException("Layer sizes do not match the map size.");

        // Every land border must blend: bridge or reshape corners the client has no blend tile for.
        LastSoften = transitions is null ? null
            : ShardForge.Core.Terrain.EdgeSoftener.Soften(terrain, Width, Height, Terrains.Select(t => t.Key).ToArray(), transitions);
        height = ApplyCrags(terrain, height);
        // Underground: void, cave walls rising from the floor, dungeon walls around built floors.
        var keys = Terrains.Select(t => t.Key).ToArray();
        var under = ShardForge.Core.Terrain.Underground.Build(terrain, Array.ConvertAll(height, b => (sbyte)b), Width, Height, keys,
            (x, y, k) => HRand(x, y, Seed, k), DungeonWallSet);
        LastUnderground = under;

        // Shores: water near land becomes flat water statics over a low sea bed that the beach slopes into (WaterEdges).
        int waterIdx = Array.IndexOf(keys, "water");
        byte[] look = terrain;           // the terrain whose tiles a cell shows (a shore cell shows the beach's)
        byte[]? shoreDist = null;        // 1..Band for water cells that far from dry ground, else 0
        if (waterIdx >= 0)
        {
            shoreDist = new byte[n];
            look = (byte[])terrain.Clone();
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
                if (terrain[i] != waterIdx) queue.Enqueue(i);
            var d = new byte[n];
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(), x = i % Width, y = i / Width;
                if (d[i] >= ShardForge.Core.Terrain.WaterEdges.Band) continue;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int X = x + dx, Y = y + dy;
                    if (X < 0 || Y < 0 || X >= Width || Y >= Height) continue;
                    int q = X + Y * Width;
                    if (terrain[q] != waterIdx || d[q] != 0) continue;
                    if (under is not null && under.Tile[q] >= 0) continue;
                    d[q] = (byte)(d[i] + 1);
                    look[q] = look[i];
                    queue.Enqueue(q);
                }
            }
            shoreDist = d;
        }

        var info = new FacetInfo(facetIndex, Name, Width, Height);
        var facet = new Facet(info);
        var blockStatics = new List<StaticTile>[info.BlockCount];

        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int i = x + y * Width;
            int t = look[i];
            if (t >= Terrains.Length || terrain[i] >= Terrains.Length) throw new InvalidDataException($"Unknown terrain index {terrain[i]} at {x},{y}.");
            var def = Terrains[t];
            bool shore = shoreDist is not null && shoreDist[i] > 0;

            ushort tile = (ushort)def.LandTiles[(int)(HRand(x, y, Seed, 0) * def.LandTiles.Length)];
            if (transitions is not null && TransitionTile(look, x, y, transitions) is int edge)
                tile = (ushort)edge;
            // The sea bed is low near the shore and back at sea level on the band's outer row, so the open water's
            // land tiles beyond it meet it level instead of sagging toward it.
            sbyte z = shore ? (sbyte)(shoreDist![i] < ShardForge.Core.Terrain.WaterEdges.Band ? ShardForge.Core.Terrain.WaterEdges.SeaBedZ : Terrains[waterIdx].FixedZ ?? -5)
                : def.FixedZ is int fz ? (sbyte)fz : (sbyte)height[i];
            // Dry ground beside the shore stays above the water, so the beach slopes down into it.
            if (!shore && shoreDist is not null && terrain[i] != waterIdx && z < -2 && NextToShore(shoreDist, x, y)) z = -2;
            if (under is not null && under.Tile[i] >= 0) { tile = (ushort)under.Tile[i]; if (under.Z[i] is sbyte uz) z = uz; }
            facet.SetLand(x, y, new LandCell(tile, z));

            if (shore)
            {
                // Flat water on top: open water out from the edge, the shaped and foamed edge pieces along it.
                var mask = ShardForge.Core.Terrain.LiquidEdges.Mask((X, Y) => X < 0 || Y < 0 || X >= Width || Y >= Height || terrain[X + Y * Width] == waterIdx, x, y);
                var piece = ShardForge.Core.Terrain.WaterEdges.BuiltIn.Piece(mask, HRand(x, y, Seed, 71));
                (blockStatics[facet.BlockIndex(x >> 3, y >> 3)] ??= []).Add(new StaticTile(piece, (byte)(x & 7), (byte)(y & 7),
                    (sbyte)(Terrains[waterIdx].FixedZ ?? -5), 0));
                LastShore++;
                continue; // nothing grows under water
            }

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

        // Lava and acid get the crusted, slimy rims the client's own dungeons have (LiquidEdges).
        var rimKeys = Terrains.Select(t => t.Key).ToArray();
        LastRims = 0;
        if (rimKeys.Any(k => k is "lava" or "acid"))
            foreach (var (rx, ry, id, rz) in ShardForge.Core.Terrain.LiquidEdges.BuiltIn.Rims(Width, Height,
                         (x, y) => rimKeys[terrain[x + y * Width]], (x, y) => facet.GetLand(x, y).Z, (x, y, k) => HRand(x, y, Seed, k)))
            {
                (blockStatics[facet.BlockIndex(rx >> 3, ry >> 3)] ??= []).Add(new StaticTile(id, (byte)(rx & 7), (byte)(ry & 7), rz, 0));
                LastRims++;
            }

        if (under is not null)
            foreach (var (sx, sy, id, sz) in under.Statics)
                (blockStatics[facet.BlockIndex(sx >> 3, sy >> 3)] ??= []).Add(new StaticTile(id, (byte)(sx & 7), (byte)(sy & 7), sz, 0));
        StampObjects(facet, blockStatics, multis);
        for (int b = 0; b < blockStatics.Length; b++)
            if (blockStatics[b] is { } list) facet.Statics[b] = list.ToArray();
        return facet;
    }

    /// <summary>Adds placed buildings and statics. Natural populate statics under a building's footprint are removed first.</summary>
    private void StampObjects(Facet facet, List<StaticTile>[] blockStatics, ShardForge.Core.Data.MultiData? multis)
    {
        if (Objects is null) return;
        void Add(int id, int x, int y, int z, int hue = 0)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height || id is < 0 or > 0xFFFF) return;
            var list = blockStatics[facet.BlockIndex(x >> 3, y >> 3)] ??= [];
            list.Add(new StaticTile((ushort)id, (byte)(x & 7), (byte)(y & 7), (sbyte)Math.Clamp(z, -128, 127), (ushort)Math.Clamp(hue, 0, 0xFFFF)));
        }
        foreach (var l in Objects.Land)
        {
            if (l.Length < 4) throw new InvalidDataException("Each land override needs [tileId, x, y, z].");
            int x = l[1], y = l[2];
            if (x < 0 || y < 0 || x >= Width || y >= Height || l[0] is < 0 or > 0x3FFF) continue;
            facet.SetLand(x, y, new LandCell((ushort)l[0], (sbyte)Math.Clamp(l[3], -128, 127)));
            blockStatics[facet.BlockIndex(x >> 3, y >> 3)]?.RemoveAll(s => s.X == (x & 7) && s.Y == (y & 7));
        }
        foreach (var m in Objects.Multis)
        {
            if (m.Length < 4) throw new InvalidDataException("Each building needs [multiId, x, y, z].");
            var multi = multis!.Get(m[0]) ?? throw new InvalidDataException($"The client has no building (multi) 0x{m[0]:X}.");
            // Clear trees and grass inside the footprint so they don't grow through floors.
            for (int y = m[2] + multi.MinY; y <= m[2] + multi.MaxY; y++)
            for (int x = m[1] + multi.MinX; x <= m[1] + multi.MaxX; x++)
            {
                if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                var list = blockStatics[facet.BlockIndex(x >> 3, y >> 3)];
                list?.RemoveAll(s => s.X == (x & 7) && s.Y == (y & 7) && s.TileId is < 0x1796 or > 0x17B2);
            }
            foreach (var c in multi.Components)
                if (c.Visible) Add(c.TileId, m[1] + c.X, m[2] + c.Y, m[3] + c.Z);
        }
        foreach (var b in Objects.Buildings)
        {
            var (fx0, fy0, fx1, fy1) = ShardForge.Core.Architecture.Architect.Footprint(b);
            for (int y = fy0; y <= fy1; y++)
            for (int x = fx0; x <= fx1; x++)
            {
                if (x < 0 || y < 0 || x >= Width || y >= Height) continue;
                blockStatics[facet.BlockIndex(x >> 3, y >> 3)]?.RemoveAll(s => s.X == (x & 7) && s.Y == (y & 7));
            }
            foreach (var p in ShardForge.Core.Architecture.Architect.Generate(b)) Add(p.Id, p.X, p.Y, p.Z);
        }
        foreach (var s in Objects.Statics)
        {
            if (s.Length < 4) throw new InvalidDataException("Each static needs [tileId, x, y, z].");
            Add(s[0], s[1], s[2], s[3], s.Length > 4 ? s[4] : 0);
        }
    }

    /// <summary>Roughens craggy terrains (rock, snow): smooth noise plus per-tile jitter, only ever upward,
    /// faded in over the first few tiles from the terrain's edge so the mountain foot meets its neighbours.</summary>
    private byte[] ApplyCrags(byte[] terrain, byte[] height)
    {
        var crag = Terrains.Select(t => t.EffectiveCrag).ToArray();
        if (crag.All(c => c == 0)) return height;

        const int Ramp = 3;
        int n = Width * Height;
        // Building plots stay flat whatever the ground (a winter town on snow): no crags on them or right around them.
        var flat = new bool[n];
        foreach (var l in Objects?.Lots ?? [])
            for (int y = Math.Max(0, l.Y - 1); y <= Math.Min(Height - 1, l.Y + l.H); y++)
            for (int x = Math.Max(0, l.X - 1); x <= Math.Min(Width - 1, l.X + l.W); x++) flat[x + y * Width] = true;
        bool Crag(int i) => !flat[i] && terrain[i] < crag.Length && crag[terrain[i]] != 0;
        var dist = new byte[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (!Crag(i)) continue;
            int x = i % Width, y = i / Width;
            bool edge = x == 0 || y == 0 || x == Width - 1 || y == Height - 1
                || !Crag(i - 1) || !Crag(i + 1) || !Crag(i - Width) || !Crag(i + Width);
            if (edge) { dist[i] = 1; queue.Enqueue(i); } else dist[i] = Ramp + 1;
        }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % Width, y = i / Width;
            if (dist[i] >= Ramp) continue;
            foreach (int j in (ReadOnlySpan<int>)[x > 0 ? i - 1 : -1, x < Width - 1 ? i + 1 : -1, y > 0 ? i - Width : -1, y < Height - 1 ? i + Width : -1])
                if (j >= 0 && dist[j] > dist[i] + 1) { dist[j] = (byte)(dist[i] + 1); queue.Enqueue(j); }
        }

        var result = (byte[])height.Clone();
        for (int i = 0; i < n; i++)
        {
            if (!Crag(i)) continue;
            int t = terrain[i];
            int x = i % Width, y = i / Width;
            double ramp = Math.Min(1.0, (dist[i] - 1) / (double)Ramp);
            double rough = 0.35 * ValueNoise(x / 7.0, y / 7.0, Seed + 300) + 0.30 * ValueNoise(x / 2.0, y / 2.0, Seed + 301)
                + 0.35 * HRand(x, y, Seed, 302);
            int z = (sbyte)height[i] + (int)Math.Round(crag[t] * ramp * rough);
            result[i] = (byte)(sbyte)Math.Clamp(z, -128, 127);
        }
        return result;
    }

    private static double ValueNoise(double x, double y, int seed)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double xf = x - xi, yf = y - yi;
        double u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
        double a = HRand(xi, yi, seed, 7), b = HRand(xi + 1, yi, seed, 7), c = HRand(xi, yi + 1, seed, 7), d = HRand(xi + 1, yi + 1, seed, 7);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    private int? TransitionTile(byte[] terrain, int x, int y, ShardForge.Core.Terrain.TransitionTable table)
    {
        int x1 = Math.Min(x + 1, Width - 1), y1 = Math.Min(y + 1, Height - 1);
        Span<int> c = [terrain[x + y * Width], terrain[x1 + y * Width], terrain[x + y1 * Width], terrain[x1 + y1 * Width]];
        int own = c[0];
        if (c[1] == own && c[2] == own && c[3] == own) return null;
        // Underground edges are walls, not blends.
        foreach (int t in c) if (Terrains[t].Key is "void" or "dungeon") return null;

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

        string ka = Terrains[own].Key, kb = Terrains[other].Key;
        var tile = table.PickNearest(ka, kb, corners, HRand(x, y, Seed, 50), out bool exact);
        if (LastSoften is { } r && ka != "water" && kb != "water")
        {
            if (tile is null) { string key = ShardForge.Core.Terrain.TransitionTable.PairKey(ka, kb); r.Hard[key] = r.Hard.GetValueOrDefault(key) + 1; }
            else if (exact) r.Exact++; else r.Near++;
        }
        return tile;
    }
}
