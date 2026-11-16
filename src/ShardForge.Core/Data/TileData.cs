using System.Buffers.Binary;
using System.Text;

namespace ShardForge.Core.Data;

[Flags]
public enum TileFlag : ulong
{
    None = 0,
    Background = 1UL << 0,
    Weapon = 1UL << 1,
    Transparent = 1UL << 2,
    Translucent = 1UL << 3,
    Wall = 1UL << 4,
    Damaging = 1UL << 5,
    Impassable = 1UL << 6,
    Wet = 1UL << 7,
    Surface = 1UL << 9,
    Bridge = 1UL << 10,
    Generic = 1UL << 11,
    Window = 1UL << 12,
    NoShoot = 1UL << 13,
    ArticleA = 1UL << 14,
    ArticleAn = 1UL << 15,
    Foliage = 1UL << 17,
    PartialHue = 1UL << 18,
    Map = 1UL << 20,
    Container = 1UL << 21,
    Wearable = 1UL << 22,
    LightSource = 1UL << 23,
    Animation = 1UL << 24,
    NoDiagonal = 1UL << 25,
    Armor = 1UL << 27,
    Roof = 1UL << 28,
    Door = 1UL << 29,
    StairBack = 1UL << 30,
    StairRight = 1UL << 31,
}

public readonly record struct LandTileData(string Name, TileFlag Flags, ushort TextureId);

public readonly record struct StaticTileData(string Name, TileFlag Flags, byte Weight, byte Quality,
    ushort Animation, byte Hue, byte Height, byte Value);

/// <summary>
/// tiledata.mul: names, flags and heights for every land and static tile.
/// Supports the 64-bit-flag layout (High Seas 7.0.9+) and the older 32-bit layout.
/// </summary>
public sealed class TileData
{
    public const int LandCount = 0x4000;

    public LandTileData[] Land { get; }
    public StaticTileData[] Statics { get; }
    public bool LongFlags { get; }

    private TileData(LandTileData[] land, StaticTileData[] statics, bool longFlags)
    {
        Land = land; Statics = statics; LongFlags = longFlags;
    }

    public static TileData Load(string path) => Parse(File.ReadAllBytes(path));

    public static TileData Parse(ReadOnlySpan<byte> data)
    {
        // Land section: 512 groups of (int header + 32 entries). Entry = flags + ushort tex + char[20].
        // Static section fills the rest: groups of (int header + 32 entries).
        const int groups = LandCount / 32;
        bool longFlags = data.Length % 2 == 0 && IsLongLayout(data.Length);
        int flagSize = longFlags ? 8 : 4;
        int landEntry = flagSize + 2 + 20;
        int staticEntry = flagSize + 1 + 1 + 2 + 1 + 1 + 2 + 1 + 1 + 1 + 1 + 1 + 20;

        var land = new LandTileData[LandCount];
        int p = 0;
        for (int g = 0; g < groups; g++)
        {
            p += 4;
            for (int i = 0; i < 32; i++, p += landEntry)
            {
                var e = data.Slice(p, landEntry);
                ulong flags = longFlags ? BinaryPrimitives.ReadUInt64LittleEndian(e) : BinaryPrimitives.ReadUInt32LittleEndian(e);
                ushort tex = BinaryPrimitives.ReadUInt16LittleEndian(e[flagSize..]);
                land[g * 32 + i] = new LandTileData(ReadName(e.Slice(flagSize + 2, 20)), (TileFlag)flags, tex);
            }
        }

        int staticGroupSize = 4 + 32 * staticEntry;
        int staticGroups = (data.Length - p) / staticGroupSize;
        var statics = new StaticTileData[staticGroups * 32];
        for (int g = 0; g < staticGroups; g++)
        {
            p += 4;
            for (int i = 0; i < 32; i++, p += staticEntry)
            {
                var e = data.Slice(p, staticEntry);
                ulong flags = longFlags ? BinaryPrimitives.ReadUInt64LittleEndian(e) : BinaryPrimitives.ReadUInt32LittleEndian(e);
                var r = e[flagSize..];
                // weight, quality, misc(2), unk2, quantity, anim(2), unk3, hue, stackOffset, value, height, name
                statics[g * 32 + i] = new StaticTileData(
                    Name: ReadName(r.Slice(13, 20)),
                    Flags: (TileFlag)flags,
                    Weight: r[0],
                    Quality: r[1],
                    Animation: BinaryPrimitives.ReadUInt16LittleEndian(r[6..]),
                    Hue: r[9],
                    Height: r[12],
                    Value: r[11]);
            }
        }

        return new TileData(land, statics, longFlags);
    }

    private static bool IsLongLayout(int length)
    {
        const int landLong = 512 * (4 + 32 * 30);
        const int staticLongGroup = 4 + 32 * 41;
        return length > landLong && (length - landLong) % staticLongGroup == 0;
    }

    private static string ReadName(ReadOnlySpan<byte> raw)
    {
        int end = raw.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? raw : raw[..end]).Trim();
    }
}
