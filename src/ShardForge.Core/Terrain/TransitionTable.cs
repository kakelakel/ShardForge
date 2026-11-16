using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShardForge.Core.Map;

namespace ShardForge.Core.Terrain;

/// <summary>
/// Corner patterns for a cell: bit set = that corner belongs to the pair's second terrain.
/// TL=1, TR=2, BL=4, BR=8 in map coordinates (x east, y south). 0 and 15 are pure.
/// </summary>
[Flags]
public enum Corners { None = 0, TopLeft = 1, TopRight = 2, BottomLeft = 4, BottomRight = 8, All = 15 }

public sealed class TransitionCandidate
{
    [JsonPropertyName("tile")] public int Tile { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    /// <summary>Weakest corner's agreement (0-1) when this tile was classified.</summary>
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    /// <summary>
    /// Distance (RGB, 0-441) between this tile's average colour and the expected blend of its two terrains.
    /// Null when mined without art. Large values flag decorated or special tiles that aren't true blends.
    /// </summary>
    [JsonPropertyName("colorError")] public double? ColorError { get; set; }
}

/// <summary>
/// Transition tiles learned from real maps: for a pair of terrains ("grass|sand", alphabetical) and a corner
/// pattern, the land tiles the original map makers used, with how often.
/// </summary>
public sealed class TransitionTable
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("pairs")] public Dictionary<string, Dictionary<string, List<TransitionCandidate>>> Pairs { get; set; } = new();

    /// <summary>Extra plain tiles per terrain found while mining (art variants of the same ground).</summary>
    [JsonPropertyName("variants")] public Dictionary<string, List<int>> Variants { get; set; } = new();

    public static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    /// <summary>
    /// Candidates for corners described from <paramref name="a"/>'s and <paramref name="b"/>'s point of view,
    /// where <paramref name="bCorners"/> marks the corners that are terrain <paramref name="b"/>.
    /// </summary>
    public IReadOnlyList<TransitionCandidate>? Find(string a, string b, Corners bCorners)
    {
        // Normalise so the bits refer to the alphabetically second terrain.
        var pattern = string.CompareOrdinal(a, b) <= 0 ? bCorners : (Corners)(15 - (int)bCorners);
        return Pairs.TryGetValue(PairKey(a, b), out var byPattern)
            && byPattern.TryGetValue(((int)pattern).ToString(), out var list) && list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Picks a tile for the pattern. Only clearly classified candidates are used (confidence within 0.15 of
    /// the best and at least 0.75); among those, choice is weighted by how often the original maps used them.
    /// <paramref name="roll"/> is a 0-1 value from the caller's deterministic hash.
    /// </summary>
    public const double MaxColorError = 25;

    public int? Pick(string a, string b, Corners bCorners, double roll)
    {
        var list = Find(a, b, bCorners);
        if (list is null) return null;
        var usable = list.Where(c => c.ColorError is null or <= MaxColorError).ToList();
        if (usable.Count == 0) return null;
        double best = usable.Max(c => c.Confidence);
        double floor = Math.Max(0.75, best - 0.15);
        usable.RemoveAll(c => c.Confidence < floor);
        if (usable.Count == 0) return null;
        double target = roll * usable.Sum(c => (double)c.Count);
        foreach (var c in usable)
        {
            target -= c.Count;
            if (target < 0) return c.Tile;
        }
        return usable[^1].Tile;
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    public static TransitionTable Load(string path) =>
        JsonSerializer.Deserialize<TransitionTable>(File.ReadAllText(path)) ?? new TransitionTable();

    private static TransitionTable? _builtIn;

    /// <summary>The table shipped with ShardForge (mined from the standard facets).</summary>
    public static TransitionTable BuiltIn => _builtIn ??= LoadEmbedded();

    private static TransitionTable LoadEmbedded()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ShardForge.Core.Terrain.transitions.json");
        return s is null ? new TransitionTable() : JsonSerializer.Deserialize<TransitionTable>(s) ?? new TransitionTable();
    }
}

/// <summary>Learns <see cref="TransitionTable"/> entries from existing facets.</summary>
public static class TransitionMiner
{
    private static readonly (int Dx, int Dy)[] Dirs = [(0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)];
    // Indices into Dirs: N=0 NE=1 E=2 SE=3 S=4 SW=5 W=6 NW=7. Each corner: diagonal (weight 2) + two sides.
    private static readonly (int Diag, int A, int B)[] CornerDirs = [(7, 0, 6), (1, 0, 2), (5, 4, 6), (3, 4, 2)]; // TL TR BL BR

    public sealed class Options
    {
        public int MinSamples { get; init; } = 12;
        public double MinCornerConfidence { get; init; } = 0.6;
        public int MaxCandidates { get; init; } = 6;
    }

    /// <param name="averageColor">Optional: average RGB of a land tile's art, used to score how well a
    /// candidate blends its two terrains.</param>
    public static TransitionTable Mine(IEnumerable<Facet> facets, Options? options = null,
        Func<int, (double R, double G, double B)?>? averageColor = null)
    {
        options ??= new Options();
        var classOf = TerrainClasses.ByTile();
        var keys = TerrainClasses.Pure.Keys.ToArray();
        var keyIndex = keys.Select((k, i) => (k, i)).ToDictionary(t => t.k, t => t.i);
        int nk = keys.Length;

        // votes[tile] = float[4 corners * nk classes]; samples[tile]
        var votes = new Dictionary<ushort, double[]>();
        var samples = new Dictionary<ushort, int>();
        var names = new List<string>();

        var facetList = facets.ToList();

        // Pass 1: find plain variants, tiles that mostly sit fully inside one terrain (e.g. extra sand art).
        // They must count as that terrain, or they'd be mistaken for transitions.
        var occurrences = new Dictionary<ushort, int>();
        var enclosed = new Dictionary<ushort, int[]>();
        foreach (var facet in facetList)
        {
            int w = facet.Width, h = facet.Height;
            for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                ushort tile = facet.Land[x + y * w].TileId;
                if (classOf.ContainsKey(tile)) continue;
                occurrences[tile] = occurrences.GetValueOrDefault(tile) + 1;
                // "Inside one terrain": at least 5 neighbours are plain tiles and all of those agree.
                // (Neighbours may be other variants we don't know yet.)
                string? only = null; bool uniform = true; int known = 0;
                foreach (var (dx, dy) in Dirs)
                {
                    if (!classOf.TryGetValue(facet.Land[x + dx + (y + dy) * w].TileId, out var k)) continue;
                    known++;
                    if ((only ??= k) != k) { uniform = false; break; }
                }
                if (!uniform || known < 5) continue;
                if (!enclosed.TryGetValue(tile, out var arr)) enclosed[tile] = arr = new int[nk];
                arr[keyIndex[only!]]++;
            }
        }
        var variants = new Dictionary<string, List<ushort>>();
        foreach (var (tile, arr) in enclosed)
        {
            int best = arr.Max();
            if (occurrences[tile] >= 50 && best >= occurrences[tile] * 0.2)
            {
                string k = keys[Array.IndexOf(arr, best)];
                classOf[tile] = k;
                (variants.TryGetValue(k, out var l) ? l : variants[k] = new()).Add(tile);
            }
        }

        // Pass 2: vote on the corners of every remaining non-plain tile.
        foreach (var facet in facetList)
        {
            names.Add(facet.Info.Name);
            int w = facet.Width, h = facet.Height;
            var cls = new sbyte[w * h];
            for (int i = 0; i < cls.Length; i++)
                cls[i] = classOf.TryGetValue(facet.Land[i].TileId, out var k) ? (sbyte)keyIndex[k] : (sbyte)-1;

            for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = x + y * w;
                if (cls[i] >= 0) continue; // pure tile, nothing to learn
                ushort tile = facet.Land[i].TileId;

                if (!votes.TryGetValue(tile, out var v)) { v = new double[4 * nk]; votes[tile] = v; }
                bool any = false;
                for (int c = 0; c < 4; c++)
                {
                    var (d, a, b) = CornerDirs[c];
                    Add(v, c, nk, Neighbor(d), 2); Add(v, c, nk, Neighbor(a), 1); Add(v, c, nk, Neighbor(b), 1);
                }
                samples[tile] = samples.GetValueOrDefault(tile) + (any ? 1 : 0);

                int Neighbor(int dir) => cls[x + Dirs[dir].Dx + (y + Dirs[dir].Dy) * w];
                void Add(double[] arr, int corner, int n, int k, double weight)
                {
                    if (k < 0) return;
                    arr[corner * n + k] += weight; any = true;
                }
            }
        }

        var table = new TransitionTable
        {
            Source = "mined from " + string.Join(", ", names),
            Variants = variants.ToDictionary(v => v.Key, v => v.Value.Order().Select(t => (int)t).ToList()),
        };
        foreach (var (tile, v) in votes)
        {
            if (samples.GetValueOrDefault(tile) < options.MinSamples) continue;

            var corner = new int[4];
            bool confident = true;
            double weakest = 1;
            for (int c = 0; c < 4 && confident; c++)
            {
                double total = 0, best = 0; int bestK = -1;
                for (int k = 0; k < nk; k++)
                {
                    total += v[c * nk + k];
                    if (v[c * nk + k] > best) { best = v[c * nk + k]; bestK = k; }
                }
                confident = total > 0 && best / total >= options.MinCornerConfidence;
                if (total > 0) weakest = Math.Min(weakest, best / total);
                corner[c] = bestK;
            }
            if (!confident) continue;

            var distinct = corner.Distinct().ToArray();
            if (distinct.Length != 2) continue; // pure-looking variant or a 3-way tile: skip

            string a = keys[distinct[0]], b = keys[distinct[1]];
            string first = string.CompareOrdinal(a, b) <= 0 ? a : b;
            int pattern = 0;
            for (int c = 0; c < 4; c++) if (keys[corner[c]] != first) pattern |= 1 << c;
            if (pattern is 6 or 9) continue; // diagonal checkerboards: too rare in real maps to trust

            string pair = TransitionTable.PairKey(a, b);
            if (!table.Pairs.TryGetValue(pair, out var byPattern)) table.Pairs[pair] = byPattern = new();
            if (!byPattern.TryGetValue(pattern.ToString(), out var list)) byPattern[pattern.ToString()] = list = new();
            double? colorError = null;
            if (averageColor is not null && averageColor(tile) is { } tc
                && BaseColor(a, averageColor) is { } ca && BaseColor(b, averageColor) is { } cb)
            {
                // Expected blend: share of corners belonging to each terrain.
                double fb = 0; for (int c = 0; c < 4; c++) if (keys[corner[c]] == b) fb += 0.25;
                double er = tc.R - (ca.R * (1 - fb) + cb.R * fb), eg = tc.G - (ca.G * (1 - fb) + cb.G * fb), eb = tc.B - (ca.B * (1 - fb) + cb.B * fb);
                colorError = Math.Round(Math.Sqrt(er * er + eg * eg + eb * eb), 1);
            }
            list.Add(new TransitionCandidate { Tile = tile, Count = samples[tile], Confidence = Math.Round(weakest, 3), ColorError = colorError });
        }

        static (double R, double G, double B)? BaseColor(string key, Func<int, (double R, double G, double B)?> avg)
        {
            var cols = TerrainClasses.Pure[key].Select(t => avg(t)).Where(c => c is not null).Select(c => c!.Value).ToList();
            return cols.Count == 0 ? null : (cols.Average(c => c.R), cols.Average(c => c.G), cols.Average(c => c.B));
        }

        foreach (var byPattern in table.Pairs.Values)
            foreach (var key in byPattern.Keys.ToArray())
                byPattern[key] = byPattern[key].OrderByDescending(c => c.Count).Take(options.MaxCandidates).ToList();
        return table;
    }
}
