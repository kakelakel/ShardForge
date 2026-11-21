using System.Text.Json.Nodes;
using ShardForge.Core.Map;

namespace ShardForge.Server.Maps;

/// <summary>
/// Which ore each patch of mountain gives in ServUO. ServUO deals out ore by 8x8 "banks", by chance; on a ShardForge
/// map the import script sets every bank instead, from this map: iron near the towns and on low hills, the rare ores
/// deep in the mountains, high up and far from any town, each kind in its own ranges (so a range is known for its
/// copper, say), in the same shares as ServUO's own chances (half iron, valorite rarest).
/// </summary>
public static class OreMap
{
    public const int Bank = 8;

    // What ServUO's Mining can dig: mountain and cave land, and cave floor statics.
    private static readonly (int A, int B)[] MineLand =
    [
        (0xDC, 0xE7), (0xEC, 0xF7), (0xFC, 0x107), (0x10C, 0x117), (0x11E, 0x126), (0x128, 0x129), (0x141, 0x144), (0x1D3, 0x1DA),
        (0x1DC, 0x1E7), (0x1EC, 0x1EF), (0x21F, 0x243), (0x245, 0x259), (0x262, 0x265), (0x3F2, 0x3F2), (0x6CD, 0x6DD), (0x6EB, 0x6FE),
        (0x709, 0x711), (0x713, 0x720), (0x727, 0x73E), (0x745, 0x75C), (0x7BD, 0x7D4), (0x7EC, 0x7F1), (0x834, 0x839),
    ];
    private static bool Mineable(int land) => MineLand.Any(r => land >= r.A && land <= r.B);
    private static bool MineableStatic(int id) => id is >= 0x53B and <= 0x54F;

    /// <summary>ServUO's ore veins in order (Iron … Valorite) and their shares in percent.</summary>
    public static readonly string[] Names = ["Iron", "Dull Copper", "Shadow Iron", "Copper", "Bronze", "Gold", "Agapite", "Verite", "Valorite"];
    private static readonly double[] Shares = [49.6, 11.2, 9.8, 8.4, 7.0, 5.6, 4.2, 2.8, 1.4];

    /// <summary>One row per bank row with something to mine: a character per bank, '0'-'8' the vein, '.' nothing.</summary>
    public static List<(int Y, string Row)> Rows(Facet facet, IEnumerable<JsonObject> items, string seed)
    {
        int bw = facet.Width / Bank, bh = facet.Height / Bank;
        var towns = new List<(double X, double Y, double R)>();
        foreach (var o in items)
        {
            if (o["kind"]?.GetValue<string>() != "region" || o["guarded"]?.ToJsonString() != "true") continue;
            double x = N(o, "x"), y = N(o, "y"), w = N(o, "w"), h = N(o, "h");
            towns.Add((x + w / 2, y + h / 2, Math.Max(w, h) / 2));
        }
        int s = seed.Aggregate(17, (a, ch) => a * 31 + ch);
        var score = new double[bw * bh];
        var mine = new bool[bw * bh];
        var list = new List<int>();
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            int n = 0; double z = 0;
            for (int y = by * Bank; y < by * Bank + Bank; y++)
            for (int x = bx * Bank; x < bx * Bank + Bank; x++)
            {
                var l = facet.GetLand(x, y);
                if (Mineable(l.TileId)) { n++; z += l.Z; }
            }
            foreach (var st in facet.GetBlockStatics(bx, by)) if (MineableStatic(st.TileId)) { n++; z += 40; }
            if (n < 3) continue;
            int i = bx + by * bw;
            mine[i] = true; list.Add(i);
            double cx = bx * Bank + 4, cy = by * Bank + 4;
            double town = towns.Count == 0 ? 400 : towns.Min(t => Math.Max(0, Math.Sqrt((t.X - cx) * (t.X - cx) + (t.Y - cy) * (t.Y - cy)) - t.R));
            double far = Math.Clamp(town / 450.0, 0, 1), high = Math.Clamp((z / n - 10) / 80.0, 0, 1);
            // Ranges: smooth noise over about 80 tiles decides which ranges are rich.
            double range = Noise(cx / 80.0, cy / 80.0, s), spot = Hash(bx, by, s + 7);
            score[i] = 0.45 * far + 0.25 * high + 0.6 * (range - 0.5) + 0.12 * spot;
        }
        // The richest banks get the rarest ore, in ServUO's shares.
        var vein = new char[bw * bh];
        Array.Fill(vein, '.');
        list.Sort((a, b) => score[a].CompareTo(score[b]));
        double total = Shares.Sum(), acc = 0; int k = 0;
        for (int v = 0; v < Shares.Length; v++)
        {
            acc += Shares[v];
            int upTo = v == Shares.Length - 1 ? list.Count : (int)Math.Round(list.Count * acc / total);
            for (; k < upTo; k++) vein[list[k]] = (char)('0' + v);
        }
        var rows = new List<(int, string)>();
        for (int by = 0; by < bh; by++)
        {
            var row = new string(vein, by * bw, bw);
            if (row.Any(c => c != '.')) rows.Add((by, row.TrimEnd('.')));
        }
        return rows;
    }

    private static double N(JsonObject o, string k) => o[k] is null ? 0 : double.Parse(o[k]!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

    private static double Hash(int x, int y, int s)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + s * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (double)0x1000000;
        }
    }

    private static double Noise(double x, double y, int s)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        double a = Hash(x0, y0, s), b = Hash(x0 + 1, y0, s), c = Hash(x0, y0 + 1, s), d = Hash(x0 + 1, y0 + 1, s);
        return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
    }
}
