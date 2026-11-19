namespace ShardForge.Core.Terrain;

/// <summary>What <see cref="EdgeSoftener.Soften"/> changed, and how the land tiles were chosen afterwards.</summary>
public sealed class SoftenReport
{
    /// <summary>Single speckles merged into the terrain around them.</summary>
    public int Smoothed { get; set; }
    /// <summary>Cells turned into a bridging terrain between two terrains that don't blend.</summary>
    public int Bridged { get; set; }
    /// <summary>Edge tiles drawn with the exact blend.</summary>
    public int Exact { get; set; }
    /// <summary>Edge tiles drawn with the nearest blend (one corner differs), because the exact one doesn't exist.</summary>
    public int Near { get; set; }
    /// <summary>Edge tiles with no blend at all (a hard edge), by terrain pair. Water is excluded.</summary>
    public Dictionary<string, int> Hard { get; } = new();
    public int HardTotal => Hard.Values.Sum();
}

/// <summary>
/// Makes borders between land terrains blend. A land tile's art is chosen from the terrains at its four corners
/// (itself, east, south and south-east). Before tiles are chosen, the terrain layer is prepared:
/// <list type="bullet">
/// <item>speckles (a cell almost surrounded by one other terrain) join their surroundings;</item>
/// <item>two terrains that hardly blend (cobblestone and grass, forest and sand) get a one-tile strip of a terrain
///   that blends with both (dirt, grass) on the softer side.</item>
/// </list>
/// Tile choice then falls back to the nearest blend when a corner shape has no exact tile (see
/// <see cref="TransitionTable.PickNearest"/>). Roads and fields keep their shape; grass and forest give way
/// (<see cref="Priority"/>). Water is left alone: UO coasts are edges between land and water tiles.
/// </summary>
public static class EdgeSoftener
{
    /// <summary>Higher keeps its shape; lower gives way.</summary>
    public static int Priority(string key) => key switch
    {
        "water" => 100, "cobblestone" => 90, "lava" => 85, "farmland" => 70, "cave" => 60, "dirt" => 50,
        "rock" => 40, "snow" => 35, "sand" => 30, "jungle" => 20, "forest" => 15, "grass" => 10, _ => 0,
    };

    public static SoftenReport Soften(byte[] terrain, int width, int height, IReadOnlyList<string> keys, TransitionTable table)
    {
        int k = keys.Count;
        var report = new SoftenReport();
        var prio = keys.Select(Priority).ToArray();
        int water = keys.ToList().IndexOf("water");
        // Underground edges become walls, not blends: void and dungeon floor are left alone, like water.
        var fixedSet = new bool[Math.Max(k, 1)];
        for (int i = 0; i < k; i++) fixedSet[i] = keys[i] is "water" or "void" or "dungeon";

        // How many of the 12 corner shapes each pair can draw (exactly or with the nearest-blend fallback).
        var cov = new int[k, k];
        for (int a = 0; a < k; a++)
        for (int b = a + 1; b < k; b++)
        {
            int n = table.Coverage(keys[a], keys[b]);
            cov[a, b] = cov[b, a] = n;
        }
        // Bridge for pairs that hardly blend: the terrain that blends best with both (ties: the softer one).
        var bridge = new int[k, k];
        for (int a = 0; a < k; a++)
        for (int b = 0; b < k; b++)
        {
            bridge[a, b] = -1;
            if (a == b || fixedSet[a] || fixedSet[b] || cov[a, b] >= 12) continue;
            int best = -1, bestCov = cov[a, b];
            for (int c = 0; c < k; c++)
            {
                if (c == a || c == b || fixedSet[c]) continue;
                int m = Math.Min(cov[a, c], cov[c, b]);
                if (m > bestCov || (m == bestCov && best >= 0 && prio[c] < prio[best])) { best = c; bestCov = m; }
            }
            bridge[a, b] = best;
        }

        // 1. Speckles.
        Span<int> tally = stackalloc int[Math.Max(k, 1)];
        for (int it = 0; it < 2; it++)
        {
            int before = report.Smoothed;
            for (int y = 1; y < height - 1; y++)
            for (int x = 1; x < width - 1; x++)
            {
                int i = x + y * width, t = terrain[i];
                if (t >= k || fixedSet[t] || prio[t] > 40) continue;
                tally.Clear();
                int same = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx | dy) == 0) continue;
                    int n = terrain[i + dx + dy * width];
                    if (n == t) same++; else if (n < k) tally[n]++;
                }
                if (same > 1) continue;
                int top = -1;
                for (int q = 0; q < k; q++) if (q != t && (top < 0 || tally[q] > tally[top])) top = q;
                if (top >= 0 && !fixedSet[top] && tally[top] >= 6) { terrain[i] = (byte)top; report.Smoothed++; }
            }
            if (report.Smoothed == before) break;
        }

        // 2. Bridges: where the softer side of a hard pair touches the firmer one, that cell becomes a terrain that
        //    blends fully with all of its neighbours (dirt between cobblestone and grass); if no terrain does, the
        //    cell joins its firmest neighbour.
        Span<bool> near = stackalloc bool[Math.Max(k, 1)];
        for (int round = 0; round < 16; round++)
        {
            int bridgedBefore = report.Bridged;
            var src = (byte[])terrain.Clone();
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = x + y * width, t = src[i];
                if (t >= k || fixedSet[t]) continue;
                near.Clear();
                bool needs = false;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int o = src[nx + ny * width];
                    if (o >= k || fixedSet[o]) continue;
                    near[o] = true; // its own terrain too: the bridge must blend with every neighbour
                    if (o == t) continue;
                    if (cov[t, o] < 12 && (prio[t] < prio[o] || (prio[t] == prio[o] && t > o))) needs = true;
                }
                if (!needs) continue;
                int best = -1, bestMin = -1, firm = -1;
                for (int c = 0; c < k; c++)
                {
                    if (near[c] && (firm < 0 || prio[c] > prio[firm])) firm = c;
                    if (fixedSet[c]) continue;
                    int m = 12;
                    for (int o = 0; o < k; o++) if (near[o] && o != c) m = Math.Min(m, cov[c, o]);
                    if (m > bestMin || (m == bestMin && best >= 0 && prio[c] < prio[best])) { best = c; bestMin = m; }
                }
                int to = bestMin >= 12 ? best : firm;
                if (to < 0 || to == t) continue;
                terrain[i] = (byte)to;
                report.Bridged++;
            }
            if (report.Bridged == bridgedBefore) break;
        }
        return report;
    }
}
