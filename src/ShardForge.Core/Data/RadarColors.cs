using System.Buffers.Binary;

namespace ShardForge.Core.Data;

/// <summary>radarcol.mul: one 16-bit (ARGB1555) minimap colour per land tile, then per static tile.</summary>
public sealed class RadarColors
{
    private readonly ushort[] _colors;

    private RadarColors(ushort[] colors) => _colors = colors;

    public static RadarColors Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var colors = new ushort[bytes.Length / 2];
        for (int i = 0; i < colors.Length; i++)
            colors[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2));
        return new RadarColors(colors);
    }

    public ushort Land(int id) => Get(id);
    public ushort Static(int id) => Get(TileData.LandCount + id);

    private ushort Get(int i) => (uint)i < (uint)_colors.Length ? _colors[i] : (ushort)0;

    /// <summary>Expands a 5-bit-per-channel colour to 8-bit RGB.</summary>
    public static (byte R, byte G, byte B) ToRgb(ushort c)
    {
        static byte Expand(int v) => (byte)((v << 3) | (v >> 2));
        return (Expand((c >> 10) & 0x1F), Expand((c >> 5) & 0x1F), Expand(c & 0x1F));
    }
}
