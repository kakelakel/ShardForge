namespace ShardForge.Core.Terrain;

/// <summary>
/// Caves and dungeons the way the client's own are made (mined from the original Trammel dungeons):
/// <list type="bullet">
/// <item><b>void</b> (land 0x244) is the black nothing around everything underground;</item>
/// <item>where void meets cave floor (or any natural ground), the void turns into rock walls: the "cave" wall land
/// tiles 0x25A (facing east-west) and 0x266 (facing north-south), rising a few steps from the floor so the client
/// draws them as cliffs, then flat at the top so the black beyond lies level;</item>
/// <item>where void meets a built <b>dungeon</b> floor, it gets dungeon wall statics instead, laid like house walls
/// (east-west pieces on a tile's south edge, north-south on its east edge, corners, posts).</item>
/// </list>
/// </summary>
public static class Underground
{
    public const ushort VoidTile = 0x244;
    public const ushort WallEW = 0x25A, WallNS = 0x266;

    /// <summary>Dungeon wall sets: east-west run, north-south run, corner, post (from the client's dungeons).</summary>
    public static readonly (string Name, ushort EW, ushort NS, ushort Corner, ushort Post)[] DungeonWalls =
    [
        ("stone", 0x241, 0x242, 0x243, 0x244),
        ("dark stone", 0x2FE, 0x2FF, 0x2FD, 0x300),
        ("rough stone", 0x2FA, 0x2FB, 0x2F9, 0x2FC),
    ];

    public sealed record Plan(int[] Tile, sbyte?[] Z, List<(int X, int Y, ushort Id, sbyte Z)> Statics, int WallCells, int DungeonWalls);

    /// <param name="keys">Terrain keys by index.</param>
    /// <param name="rand">Deterministic per-cell random (x, y, k) in [0, 1).</param>
    public static Plan? Build(byte[] terrain, sbyte[] height, int width, int heightCells, IReadOnlyList<string> keys, Func<int, int, int, double> rand, int wallSet = 0)
    {
        int voidIdx = IndexOf(keys, "void"), dungIdx = IndexOf(keys, "dungeon");
        if (voidIdx < 0) return null;
        int n = width * heightCells;
        var tile = new int[n]; Array.Fill(tile, -1);
        var z = new sbyte?[n];
        var statics = new List<(int, int, ushort, sbyte)>();
        bool Void(int i) => terrain[i] == voidIdx;
        bool Natural(int i) => terrain[i] != voidIdx && terrain[i] != dungIdx;

        // Distance (8-neighbour steps) from natural floor into the void, remembering the nearest floor cell.
        const int Thick = 3;
        var dist = new byte[n]; var from = new int[n];
        var q = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            from[i] = -1;
            if (!Void(i)) continue;
            int x = i % width, y = i / width;
            // Straight neighbours first, so a wall faces the floor it's beside rather than one off a corner.
            foreach (var (dx, dy) in Around)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= heightCells) continue;
                int j = nx + ny * width;
                if (Natural(j)) { dist[i] = 1; from[i] = j; q.Enqueue(i); break; }
            }
        }
        while (q.Count > 0)
        {
            int i = q.Dequeue(), x = i % width, y = i / width;
            if (dist[i] >= 250) continue; // every void cell learns its nearest floor: the void lies level with the wall tops
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= width || ny >= heightCells) continue;
                int j = nx + ny * width;
                if (!Void(j) || from[j] >= 0) continue;
                dist[j] = (byte)(dist[i] + 1); from[j] = from[i]; q.Enqueue(j);
            }
        }

        // The same from built dungeon floors: void nearer a dungeon than a cave lies level with the dungeon floor.
        var ddist = new int[n]; var dfrom = new int[n];
        Array.Fill(ddist, int.MaxValue); Array.Fill(dfrom, -1);
        if (dungIdx >= 0)
        {
            var dq = new Queue<int>();
            for (int i = 0; i < n; i++) if (terrain[i] == dungIdx) { ddist[i] = 0; dfrom[i] = i; dq.Enqueue(i); }
            while (dq.Count > 0)
            {
                int i = dq.Dequeue(), x = i % width, y = i / width;
                foreach (var (dx, dy) in Around)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= heightCells) continue;
                    int j = nx + ny * width;
                    if (!Void(j) || ddist[j] != int.MaxValue) continue;
                    ddist[j] = ddist[i] + 1; dfrom[j] = dfrom[i]; dq.Enqueue(j);
                }
            }
        }

        double Smooth(int x, int y, double scale, int k) => SmoothNoise(rand, x / scale, y / scale, k);
        int walls = 0;
        for (int i = 0; i < n; i++)
        {
            if (!Void(i)) continue;
            int x = i % width, y = i / width;
            if (from[i] < 0 || (dfrom[i] >= 0 && ddist[i] < dist[i] && dist[i] > 1))
            {
                tile[i] = VoidTile;
                if (dfrom[i] >= 0) z[i] = height[dfrom[i]];
                continue;
            }
            int f = from[i], fx = f % width, fy = f / width, d = dist[i];
            int baseZ = height[f];
            // Walls are 2-3 tiles thick (varying), rising about 7 a step, then the void lies flat at the top.
            // Thickness varies smoothly along the wall (2 or 3), so its top edge doesn't saw back and forth.
            int thick = 2 + (Smooth(x, y, 6, 900) > 0.5 ? 1 : 0);
            // About 7 a step, as in the client's caves; a little unevenness, never jagged.
            int Rise(int k) => k * 7 + (int)Math.Round(rand(x, y, 901) * 2);
            if (d <= thick)
            {
                int ddx = Math.Abs(fx - x), ddy = Math.Abs(fy - y);
                ushort id = ddy > ddx ? WallNS : ddx > ddy ? WallEW : (rand(x, y, 902) < 0.5 ? WallNS : WallEW);
                tile[i] = id;
                z[i] = (sbyte)Math.Clamp(baseZ + Rise(d), -128, 127);
                walls++;
            }
            else
            {
                tile[i] = VoidTile;
                z[i] = (sbyte)Math.Clamp(baseZ + 3 * 7 + 1, -128, 127);
            }
        }

        // Built dungeon floors: walls of statics where they meet the void, like a house's outer walls.
        int dw = 0;
        if (dungIdx >= 0)
        {
            var set = DungeonWalls[Math.Clamp(wallSet, 0, DungeonWalls.Length - 1)];
            bool Floor(int x, int y) => x >= 0 && y >= 0 && x < width && y < heightCells && terrain[x + y * width] != voidIdx;
            var ew = new HashSet<(int, int)>(); var ns = new HashSet<(int, int)>();
            for (int y = 0; y < heightCells; y++)
            for (int x = 0; x < width; x++)
            {
                if (terrain[x + y * width] != dungIdx) continue;
                if (!Floor(x, y - 1)) ew.Add((x, y - 1));
                if (!Floor(x, y + 1)) ew.Add((x, y));
                if (!Floor(x - 1, y)) ns.Add((x - 1, y));
                if (!Floor(x + 1, y)) ns.Add((x, y));
            }
            var all = new HashSet<(int, int)>(ew); all.UnionWith(ns);
            sbyte Zat(int x, int y)
            {
                // The floor the wall stands by.
                foreach (var (ax, ay) in new[] { (x, y), (x, y + 1), (x + 1, y), (x + 1, y + 1), (x - 1, y), (x, y - 1) })
                    if (ax >= 0 && ay >= 0 && ax < width && ay < heightCells && terrain[ax + ay * width] == dungIdx) return height[ax + ay * width];
                return 0;
            }
            foreach (var (x, y) in all)
            {
                if (x < 0 || y < 0 || x >= width || y >= heightCells) continue;
                bool e = ew.Contains((x, y)), s = ns.Contains((x, y));
                statics.Add((x, y, e && s ? set.Corner : e ? set.EW : set.NS, Zat(x, y))); dw++;
            }
            // Posts close the corner north-west of where a run starts.
            var posts = new HashSet<(int, int)>();
            foreach (var (x, y) in ew) if (!all.Contains((x - 1, y))) posts.Add((x - 1, y));
            foreach (var (x, y) in ns) if (!all.Contains((x, y - 1))) posts.Add((x, y - 1));
            foreach (var (x, y) in posts)
                if (x >= 0 && y >= 0 && x < width && y < heightCells && terrain[x + y * width] == voidIdx) statics.Add((x, y, set.Post, Zat(x, y)));
        }
        return new Plan(tile, z, statics, walls, dw);
    }

    /// <summary>Smooth value noise from the per-cell random, at the given scale.</summary>
    static double SmoothNoise(Func<int, int, int, double> r, double x, double y, int k)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        double u = x - xi, v = y - yi; u = u * u * (3 - 2 * u); v = v * v * (3 - 2 * v);
        double a = r(xi, yi, k), b = r(xi + 1, yi, k), c = r(xi, yi + 1, k), d = r(xi + 1, yi + 1, k);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    static readonly (int, int)[] Around = [(0, -1), (1, 0), (0, 1), (-1, 0), (-1, -1), (1, -1), (1, 1), (-1, 1)];

    static int IndexOf(IReadOnlyList<string> keys, string k) { for (int i = 0; i < keys.Count; i++) if (keys[i] == k) return i; return -1; }
}
