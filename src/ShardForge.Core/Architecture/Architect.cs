using System.Text.Json.Serialization;

namespace ShardForge.Core.Architecture;

/// <summary>
/// A building to generate. (X, Y) is the north-west interior tile; W x H is the interior. The walls, roof
/// overhang and porch take one more tile on each side: the footprint is X-1..X+W, Y-1..Y+H.
/// </summary>
public sealed class BuildingSpec
{
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("w")] public int W { get; set; } = 6;
    [JsonPropertyName("h")] public int H { get; set; } = 6;
    [JsonPropertyName("z")] public int Z { get; set; }
    [JsonPropertyName("floors")] public int Floors { get; set; } = 1;
    /// <summary>A theme name (see <see cref="Architect.Themes"/>): picks walls, roof and floor.</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = "plaster";
    /// <summary>Wall with the doorway: N, S, E or W.</summary>
    [JsonPropertyName("door")] public string Door { get; set; } = "S";
    /// <summary>home, shop, tavern, smithy, farm, chapel, storage, guard, tower.</summary>
    [JsonPropertyName("use")] public string Use { get; set; } = "home";
    /// <summary>"pitched" (default), "flat" (with a parapet) or "none".</summary>
    [JsonPropertyName("roofType")] public string? RoofType { get; set; }
    [JsonPropertyName("walls")] public string? Walls { get; set; }
    [JsonPropertyName("roof")] public string? Roof { get; set; }
    [JsonPropertyName("floor")] public string? Floor { get; set; }
    [JsonPropertyName("seed")] public int Seed { get; set; }
}

public sealed record Theme(string Walls, string Roof, string Floor, string RoofType = "pitched");

/// <summary>A static the architect places: tile, absolute position and altitude.</summary>
public readonly record struct Placed(int Id, int X, int Y, int Z);

/// <summary>
/// Generates buildings from the client's house-customization pieces, following the rules of UO's own houses:
/// a wall piece sits on the south (east-west pieces) or east (north-south pieces) edge of its tile, a post caps
/// the north-west outer corner and a corner piece the south-east one; each storey is 20 high; pitched roofs rise
/// 3 per tile to a ridge along the longer side and overhang one tile to the south and east.
/// </summary>
public static class Architect
{
    public static readonly IReadOnlyDictionary<string, Theme> Themes = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase)
    {
        ["wood"] = new("Light Wood Standard Walls", "Wooden Shingles", "Light Wood 1"),
        ["darkwood"] = new("Dark Wood Standard Walls", "Wooden Shingles", "Dark Wood 1"),
        ["plaster"] = new("Wood and Plaster Plain Walls", "Tile Roof", "Dark Wood 1"),
        ["thatch"] = new("Wood and Plaster Plain Walls", "Thatch Roof", "Dark Wood 2"),
        ["log"] = new("Log Standard Walls", "Log Roof", "Dark Wood 2"),
        ["fieldstone"] = new("Fieldstone Standard Walls", "Slate Roof 1", "Grey Pavers and Flagstones"),
        ["brick"] = new("Grey Brick Standard Walls", "Tile Roof", "Red Brick"),
        ["redbrick"] = new("Light Brick Standard Walls", "Tile Roof", "Red Brick"),
        ["stone"] = new("Weathered Stone Standard Walls", "Stone Roof", "Grey Pavers and Flagstones", "flat"),
        ["sandstone"] = new("Sandstone Brick Fancy Walls", "Palm Fronds", "Sandstone Bricks", "flat"),
        ["marble"] = new("Tan Marble Fancy Walls", "Slate Roof 1", "Blue Tile"),
        ["whitemarble"] = new("White Marble Fancy Walls", "Slate Roof 1", "Misc Tile"),
        ["clay"] = new("Clay Standard Walls", "Orange Ceramic Tiles 1", "Red Tile"),
        ["hide"] = new("Hide Standard Walls", "Straw Roof 1", "Dirt and Cobblestones"),
    };

    public const int StoreyHeight = 20;

    // Furniture verified against the client's art: pieces for a north wall run along X, for a west wall along Y.
    private static readonly int[] ShelvesNorth = [0x0A97, 0x0A98, 0x0A9B, 0x0A9D], ShelvesWest = [0x0A99, 0x0A9A, 0x0A9C, 0x0A9E];
    private static readonly int[] ChestsNorth = [0x0E43, 0x0A2C], ChestsWest = [0x0E42];
    private const int Table = 0x0B73, ChairX = 0x0B52, ChairY = 0x0B53, Counter = 0x0B3F, Keg = 0x1AD7, Barrel = 0x0FAE,
        Forge = 0x0FB1, AnvilX = 0x0FB0, Candelabra = 0x0B26, BenchX = 0x0B2D, BenchY = 0x0B2C, Stool = 0x0A2A, LampPost = 0x0B24;
    private static readonly int[] Crates = [0x0E3C, 0x0E3D], Hay = [0x1036, 0x0F34], Sacks = [0x1045];

    /// <summary>All statics of one building. <paramref name="kits"/> defaults to the built-in sets.</summary>
    public static List<Placed> Generate(BuildingSpec s, Kits? kits = null)
    {
        kits ??= Kits.BuiltIn;
        if (s.W < 2 || s.H < 2 || s.W > 40 || s.H > 40) throw new InvalidDataException("Buildings need an interior between 2x2 and 40x40.");
        if (!Themes.TryGetValue(s.Theme, out var theme)) theme = Themes["plaster"];
        var walls = kits.Wall(s.Walls ?? theme.Walls);
        var roof = kits.Roof(s.Roof ?? theme.Roof);
        var floor = kits.Floor(s.Floor ?? theme.Floor);
        var low = kits.Low(walls);
        string roofType = s.RoofType ?? theme.RoofType;
        int floors = Math.Clamp(s.Floors, 1, 4);
        var rng = new Random(s.Seed);
        var outp = new List<Placed>();
        int x0 = s.X, y0 = s.Y, x1 = s.X + s.W - 1, y1 = s.Y + s.H - 1;

        // Doorway: one tile, away from the corners.
        string door = (s.Door ?? "S").ToUpperInvariant();
        int doorAt = door is "N" or "S" ? x0 + Math.Max(1, (s.W - 1) / 2) : y0 + Math.Max(1, (s.H - 1) / 2);
        if (door is "N" or "S") doorAt = Math.Min(doorAt, x1 - 1); else doorAt = Math.Min(doorAt, y1 - 1);

        for (int f = 0; f < floors; f++)
        {
            int z = s.Z + f * StoreyHeight;
            int floorTiles = Math.Min(4, floor.Tiles.Length);
            for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                outp.Add(new(floor.Tiles[rng.Next(floorTiles)], x, y, z));

            bool ground = f == 0;
            bool Opening(string side, int pos) => ground && side == door && pos == doorAt;
            bool Window(int i, int n) => i > 0 && i < n - 1 && i % 3 == 1 && rng.NextDouble() < 0.75;

            outp.Add(new(walls.Post, x0 - 1, y0 - 1, z));
            WallRun(outp, walls, z, ew: true, row: y0 - 1, from: x0, to: x1, opening: p => Opening("N", p), window: Window);
            WallRun(outp, walls, z, ew: false, row: x0 - 1, from: y0, to: y1, opening: p => Opening("W", p), window: Window);
            WallRun(outp, walls, z, ew: true, row: y1, from: x0, to: x1 - 1, opening: p => Opening("S", p), window: Window);
            WallRun(outp, walls, z, ew: false, row: x1, from: y0, to: y1 - 1, opening: p => Opening("E", p), window: Window);
            outp.Add(new(walls.Corner, x1, y1, z));
        }

        int top = s.Z + floors * StoreyHeight;
        if (roofType == "flat") FlatRoof(outp, floor, low ?? walls, top, x0, y0, x1, y1, rng);
        else if (roofType != "none") PitchedRoof(outp, roof, low, top, x0, y0, x1, y1);

        Furnish(outp, s, rng, x0, y0, x1, y1, door, doorAt);
        return outp;
    }

    /// <summary>A straight wall between openings: start, middle and end pieces, with windows.</summary>
    private static void WallRun(List<Placed> outp, WallSet w, int z, bool ew, int row, int from, int to, Func<int, bool> opening, Func<int, int, bool> window)
    {
        int[] pieces = ew ? w.EW : w.NS;
        int[] windows = ew ? w.WindowEW : w.WindowNS;
        int n = to - from + 1;
        for (int p = from; p <= to; p++)
        {
            if (opening(p)) continue;
            bool startOfRun = p == from || opening(p - 1), endOfRun = p == to || opening(p + 1);
            // EW sets list west, middle, east; NS sets list south, middle, north.
            int piece = pieces.Length < 3 ? pieces[0]
                : ew ? (startOfRun ? pieces[0] : endOfRun ? pieces[2] : pieces[1])
                : (startOfRun ? pieces[2] : endOfRun ? pieces[0] : pieces[1]);
            if (windows.Length > 0 && !startOfRun && !endOfRun && window(p - from, n)) piece = windows[0];
            outp.Add(ew ? new(piece, p, row, z) : new(piece, row, p, z));
        }
    }

    private static void PitchedRoof(List<Placed> outp, RoofSet r, WallSet? low, int top, int x0, int y0, int x1, int y1)
    {
        // The roof covers the interior plus one tile south and east (the overhang the walls' edges need).
        int rx0 = x0, ry0 = y0, rx1 = x1 + 1, ry1 = y1 + 1;
        bool ridgeNS = (rx1 - rx0) <= (ry1 - ry0); // ridge along the longer side
        int n = ridgeNS ? rx1 - rx0 + 1 : ry1 - ry0 + 1;
        for (int i = 0; i < n; i++)
        {
            int d = Math.Min(i, n - 1 - i), z = top + 3 * d;
            bool ridge = d == (n - 1) / 2 && (n % 2 == 1 || i == n / 2 - 1 || i == n / 2);
            int piece = ridgeNS ? (ridge ? r.RidgeNS : i < n / 2 ? r.West : r.East)
                                : (ridge ? r.RidgeEW : i < n / 2 ? r.North : r.South);
            if (ridgeNS) for (int y = ry0; y <= ry1; y++) outp.Add(new(piece, rx0 + i, y, z));
            else for (int x = rx0; x <= rx1; x++) outp.Add(new(piece, x, ry0 + i, z));
        }
        if (low is null) return;
        // Gables close the triangle under the roof at both ends.
        for (int k = 0; k < (n - 1) / 2; k++)
            for (int i = k + 1; i <= n - 2 - k; i++)
            {
                int z = top + 3 * k;
                if (ridgeNS)
                {
                    outp.Add(new(low.EW[Math.Min(1, low.EW.Length - 1)], rx0 + i - 1, y1, z));
                    outp.Add(new(low.EW[Math.Min(1, low.EW.Length - 1)], rx0 + i - 1, y0 - 1, z));
                }
                else
                {
                    outp.Add(new(low.NS[Math.Min(1, low.NS.Length - 1)], x1, ry0 + i - 1, z));
                    outp.Add(new(low.NS[Math.Min(1, low.NS.Length - 1)], x0 - 1, ry0 + i - 1, z));
                }
            }
    }

    private static void FlatRoof(List<Placed> outp, FloorSet floor, WallSet parapet, int top, int x0, int y0, int x1, int y1, Random rng)
    {
        int floorTiles = Math.Min(4, floor.Tiles.Length);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
            outp.Add(new(floor.Tiles[rng.Next(floorTiles)], x, y, top));
        // A low parapet around the edge, gaps every other tile for battlements.
        int ew = parapet.EW[Math.Min(1, parapet.EW.Length - 1)], ns = parapet.NS[Math.Min(1, parapet.NS.Length - 1)];
        outp.Add(new(parapet.Post, x0 - 1, y0 - 1, top));
        for (int x = x0; x <= x1; x++) { if ((x - x0) % 2 == 0) outp.Add(new(ew, x, y0 - 1, top)); if ((x - x0) % 2 == 0 && x < x1) outp.Add(new(ew, x, y1, top)); }
        for (int y = y0; y <= y1; y++) { if ((y - y0) % 2 == 0) outp.Add(new(ns, x0 - 1, y, top)); if ((y - y0) % 2 == 0 && y < y1) outp.Add(new(ns, x1, y, top)); }
        outp.Add(new(parapet.Corner, x1, y1, top));
    }

    /// <summary>Furniture for the ground floor by the building's use, kept off the path from the door.</summary>
    private static void Furnish(List<Placed> outp, BuildingSpec s, Random rng, int x0, int y0, int x1, int y1, string door, int doorAt)
    {
        int z = s.Z;
        var used = new HashSet<(int, int)>();
        // Keep the doorway and the two tiles inside it free.
        (int dx, int dy) inside = door switch { "N" => (doorAt, y0), "S" => (doorAt, y1), "E" => (x1, doorAt), _ => (x0, doorAt) };
        used.Add(inside);
        used.Add(door switch { "N" => (doorAt, y0 + 1), "S" => (doorAt, y1 - 1), "E" => (x1 - 1, doorAt), _ => (x0 + 1, doorAt) });
        bool Free(int x, int y) => x >= x0 && x <= x1 && y >= y0 && y <= y1 && !used.Contains((x, y));
        void Put(int id, int x, int y, int dz = 0) { if (!Free(x, y)) return; used.Add((x, y)); outp.Add(new(id, x, y, z + dz)); }
        T Pick<T>(T[] a) => a[rng.Next(a.Length)];

        // Against the north and west walls first (the walls players see from the front).
        var north = Enumerable.Range(x0, x1 - x0 + 1).Select(x => (x, y0)).ToList();
        var west = Enumerable.Range(y0 + 1, y1 - y0).Select(y => (x0, y)).ToList();
        int cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;

        switch (s.Use.ToLowerInvariant())
        {
            case "smithy":
                Put(Forge, x0, y0); Put(Forge, x0 + 1, y0); Put(AnvilX, x0 + 1, y0 + 2);
                foreach (var (x, y) in north.Skip(3)) if (rng.NextDouble() < 0.5) Put(Pick(Crates), x, y);
                Put(Barrel, x0, y1 - 1);
                break;
            case "tavern":
                foreach (var (x, y) in north) if (rng.NextDouble() < 0.6) Put(rng.NextDouble() < 0.5 ? Keg : Barrel, x, y);
                for (int y = y0 + 2; y <= y1 - 1; y += 3)
                for (int x = x0 + 2; x <= x1 - 1; x += 3)
                {
                    Put(Table, x, y); Put(ChairY, x - 1, y); Put(ChairX, x, y - 1); Put(ChairY, x + 1, y);
                }
                break;
            case "shop":
                for (int x = x0 + 1; x <= x1 - 1; x++) Put(Counter, x, cy);
                foreach (var (x, y) in north) if (rng.NextDouble() < 0.8) Put(Pick(ShelvesNorth), x, y);
                foreach (var (x, y) in west) if (rng.NextDouble() < 0.5) Put(Pick(Crates), x, y);
                break;
            case "farm":
            case "storage":
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (rng.NextDouble() < (s.Use == "farm" ? 0.35 : 0.5))
                        Put(s.Use == "farm" ? (rng.NextDouble() < 0.6 ? Pick(Hay) : Pick(Sacks)) : (rng.NextDouble() < 0.6 ? Pick(Crates) : rng.NextDouble() < 0.5 ? Barrel : Keg), x, y);
                break;
            case "chapel":
                Put(Candelabra, x0, y0); Put(Candelabra, x1, y0);
                for (int y = y0 + 2; y <= y1 - 1; y += 2)
                for (int x = x0 + 1; x <= x1 - 1; x++)
                    if (x != cx) Put(BenchX, x, y);
                break;
            case "guard":
            case "tower":
                foreach (var (x, y) in north) if (rng.NextDouble() < 0.4) Put(Pick(Crates), x, y);
                Put(Table, cx, cy); Put(Stool, cx - 1, cy);
                break;
            default: // home
                if (north.Count > 1) Put(Pick(ShelvesNorth), north[0].Item1, north[0].Item2);
                if (north.Count > 2) Put(Pick(ChestsNorth), north[^1].Item1, north[^1].Item2);
                if (west.Count > 1) Put(Pick(ShelvesWest), west[1].Item1, west[1].Item2);
                Put(Table, cx, cy); Put(ChairY, cx - 1, cy); Put(ChairX, cx, cy - 1);
                if (rng.NextDouble() < 0.6) Put(Barrel, x1, y0);
                break;
        }
        // A lamp post beside the door outside, sometimes.
        if (rng.NextDouble() < 0.5 && s.Theme is not ("hide" or "log") && s.Use is not ("storage" or "farm"))
        {
            var (lx, ly) = door switch { "N" => (doorAt + 1, y0 - 2), "S" => (doorAt + 1, y1 + 2), "E" => (x1 + 2, doorAt + 1), _ => (x0 - 2, doorAt + 1) };
            outp.Add(new(LampPost, lx, ly, z));
        }
    }

    /// <summary>Tiles a building covers (walls, roof overhang, porch): for planning and clearing.</summary>
    public static (int X0, int Y0, int X1, int Y1) Footprint(BuildingSpec s) => (s.X - 1, s.Y - 1, s.X + s.W, s.Y + s.H);
}
