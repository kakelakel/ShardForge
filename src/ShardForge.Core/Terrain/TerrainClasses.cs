namespace ShardForge.Core.Terrain;

/// <summary>
/// The "pure" land tiles of each terrain: the plain, repeating ground of a region (the client's own most-used
/// variants). Everything else on a real map is a transition, a detail tile, or something hand-placed.
/// Keys match the editor's terrain keys.
/// </summary>
public static class TerrainClasses
{
    public static readonly IReadOnlyDictionary<string, ushort[]> Pure = new Dictionary<string, ushort[]>
    {
        ["water"] = [0xA8, 0xA9, 0xAA, 0xAB],
        ["grass"] = [0x03, 0x04, 0x05, 0x06],
        ["forest"] = [0xC4, 0xC5, 0xC6, 0xC7],
        ["jungle"] = [0xAC, 0xAD, 0xAE, 0xAF],
        ["sand"] = [0x16, 0x17, 0x18, 0x19],
        ["dirt"] = [0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78],
        ["rock"] = [0x22C, 0x22D, 0x22E, 0x22F],
        ["snow"] = [0x11A, 0x11B, 0x11C, 0x11D],
        ["lava"] = [0x1F4, 0x1F5, 0x1F6, 0x1F7],
        ["cobblestone"] = [0x3E9, 0x3EA, 0x3EB, 0x3EC],
        ["farmland"] = [0x09],
        ["cave"] = [0x245, 0x246, 0x247, 0x248],
    };

    public static Dictionary<ushort, string> ByTile()
    {
        var map = new Dictionary<ushort, string>();
        foreach (var (key, tiles) in Pure)
            foreach (var t in tiles) map[t] = key;
        return map;
    }
}
