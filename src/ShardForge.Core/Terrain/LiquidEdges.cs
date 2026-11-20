using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShardForge.Core.Data;
using ShardForge.Core.Map;

namespace ShardForge.Core.Terrain;

/// <summary>
/// The rims of lava and acid, the way the client's own dungeons have them. Lava and acid are land tiles; the
/// glowing crust or slimy lip where they meet the floor is statics, laid on the floor tiles beside them and on the
/// outermost liquid tiles. Which piece goes where depends on which of the eight neighbours are liquid, so the
/// table is mined from the original facets: for every neighbour pattern, the pieces used there (with how often)
/// and how often a piece is there at all.
/// </summary>
public sealed class LiquidEdges
{
    public sealed class Liquid
    {
        /// <summary>The land tiles that are this liquid.</summary>
        [JsonPropertyName("land")] public int[] Land { get; set; } = [];
        /// <summary>The static name of its rim pieces (as in tiledata).</summary>
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        /// <summary>On liquid tiles: pattern (8 chars, N NE E SE S SW W NW, 1 = liquid) → pieces.</summary>
        [JsonPropertyName("in")] public Dictionary<string, Pattern> In { get; set; } = new();
        /// <summary>On floor tiles next to the liquid.</summary>
        [JsonPropertyName("out")] public Dictionary<string, Pattern> Out { get; set; } = new();
    }

    public sealed class Pattern
    {
        /// <summary>How many tiles with this pattern were seen, with or without a piece.</summary>
        [JsonPropertyName("seen")] public int Seen { get; set; }
        /// <summary>[tileId, count, z above the land].</summary>
        [JsonPropertyName("pieces")] public List<int[]> Pieces { get; set; } = new();
        [JsonIgnore] public int Used => Pieces.Sum(p => p[1]);
    }

    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("liquids")] public Dictionary<string, Liquid> Liquids { get; set; } = new();

    private static readonly (int Dx, int Dy)[] Dirs = [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];

    public static string Mask(Func<int, int, bool> liquid, int x, int y)
    {
        Span<char> m = stackalloc char[8];
        for (int d = 0; d < 8; d++) m[d] = liquid(x + Dirs[d].Dx, y + Dirs[d].Dy) ? '1' : '0';
        return new string(m);
    }

    /// <summary>The pattern to use for a mask: the same one, or the nearest seen often enough (sides count double).</summary>
    public static Pattern? Nearest(Dictionary<string, Pattern> table, string mask, int minSeen = 6)
    {
        if (table.TryGetValue(mask, out var exact) && exact.Seen >= 3 && exact.Pieces.Count > 0) return exact;
        Pattern? best = null; int bestD = int.MaxValue;
        foreach (var (m, p) in table)
        {
            if (p.Seen < minSeen || p.Pieces.Count == 0) continue;
            int d = 0;
            for (int i = 0; i < 8; i++) if (m[i] != mask[i]) d += i % 2 == 0 ? 2 : 1;
            if (d < bestD) { bestD = d; best = p; }
        }
        return bestD <= 1 ? best : null; // only a corner off: anything further would put a rim facing the wrong way
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false }));

    private static LiquidEdges? _builtIn;
    /// <summary>The table shipped with ShardForge (mined from the client's own dungeons).</summary>
    public static LiquidEdges BuiltIn => _builtIn ??= Load();
    private static LiquidEdges Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ShardForge.Core.Terrain.liquid-edges.json");
        return s is null ? new LiquidEdges() : JsonSerializer.Deserialize<LiquidEdges>(s) ?? new LiquidEdges();
    }

    /// <summary>Learns the rims from facets.</summary>
    public static LiquidEdges Mine(IEnumerable<Facet> facets, TileData tiles, IReadOnlyDictionary<string, (int[] Land, string Name)> liquids)
    {
        var result = new LiquidEdges();
        var names = new List<string>();
        foreach (var (key, (land, name)) in liquids) result.Liquids[key] = new Liquid { Land = land, Name = name };
        foreach (var facet in facets)
        {
            names.Add(facet.Info.Name);
            int w = facet.Width, h = facet.Height;
            // statics by tile, only the rim pieces
            var at = new Dictionary<int, List<StaticTile>>();
            for (int bx = 0; bx < facet.Info.BlockWidth; bx++)
            for (int by = 0; by < facet.Info.BlockHeight; by++)
                if (facet.Statics[facet.BlockIndex(bx, by)] is { } list)
                    foreach (var s in list)
                    {
                        string n = (tiles.Statics[s.TileId].Name ?? "").Trim().ToLowerInvariant();
                        if (!liquids.Values.Any(l => l.Name == n)) continue;
                        int k = (bx * 8 + s.X) + (by * 8 + s.Y) * w;
                        (at.TryGetValue(k, out var l2) ? l2 : at[k] = new()).Add(s);
                    }
            foreach (var (key, liq) in result.Liquids)
            {
                var set = new HashSet<int>(liq.Land);
                bool Liquid(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && set.Contains(facet.Land[x + y * w].TileId);
                for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    bool on = Liquid(x, y);
                    string mask = Mask(Liquid, x, y);
                    if (on ? mask == "11111111" : mask == "00000000") continue; // not at an edge
                    var table = on ? liq.In : liq.Out;
                    var p = table.TryGetValue(mask, out var pp) ? pp : table[mask] = new Pattern();
                    p.Seen++;
                    if (!at.TryGetValue(x + y * w, out var here)) continue;
                    int landZ = facet.Land[x + y * w].Z;
                    foreach (var s in here)
                    {
                        if ((tiles.Statics[s.TileId].Name ?? "").Trim().ToLowerInvariant() != liq.Name) continue;
                        int dz = s.Z - landZ;
                        var e = p.Pieces.FirstOrDefault(q => q[0] == s.TileId && q[2] == dz);
                        if (e is null) p.Pieces.Add([s.TileId, 1, dz]); else e[1]++;
                        break; // one piece per tile
                    }
                }
            }
        }
        foreach (var liq in result.Liquids.Values)
            foreach (var t in new[] { liq.In, liq.Out })
                foreach (var p in t.Values) p.Pieces = p.Pieces.Where(q => q[1] >= 2 && Math.Abs(q[2]) <= 2).OrderByDescending(q => q[1]).ToList();
        result.Source = "mined from " + string.Join(", ", names);
        return result;
    }

    /// <summary>
    /// Rim pieces for a built map: on every edge tile of each liquid, a piece the client's dungeons use for that
    /// pattern, about as often as they use one there. <paramref name="keyAt"/> gives a tile's terrain key.
    /// </summary>
    public List<(int X, int Y, ushort Id, sbyte Z)> Rims(int w, int h, Func<int, int, string?> keyAt, Func<int, int, sbyte> landZ, Func<int, int, int, double> rand)
    {
        var out_ = new List<(int, int, ushort, sbyte)>();
        foreach (var (key, liq) in Liquids)
        {
            bool Liquid(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && keyAt(x, y) == key;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool on = Liquid(x, y);
                if (!on)
                {
                    // Floor beside it only: not the void, walls or water.
                    string? k = keyAt(x, y);
                    if (k is null or "void" or "water" or "lava" or "acid") continue;
                }
                string mask = Mask(Liquid, x, y);
                if (on ? mask == "11111111" : mask == "00000000") continue;
                var p = Nearest(on ? liq.In : liq.Out, mask);
                if (p is null) continue;
                // As often as the originals have a piece there (but rims read better a little fuller).
                double chance = Math.Min(1, (double)p.Used / Math.Max(1, p.Seen) * 2.6);
                if (rand(x, y, 61) >= chance) continue;
                double roll = rand(x, y, 62) * p.Used;
                foreach (var piece in p.Pieces)
                {
                    roll -= piece[1];
                    if (roll > 0) continue;
                    out_.Add((x, y, (ushort)piece[0], (sbyte)Math.Clamp(landZ(x, y) + piece[2], -128, 127)));
                    break;
                }
            }
        }
        return out_;
    }
}
