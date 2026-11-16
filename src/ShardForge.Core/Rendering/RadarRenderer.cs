using ShardForge.Core.Data;
using ShardForge.Core.Map;

namespace ShardForge.Core.Rendering;

public sealed record RadarRenderOptions
{
    /// <summary>Draw statics (trees, buildings...) on top of land.</summary>
    public bool Statics { get; init; } = true;

    /// <summary>Hill-shade from altitude so terrain relief is visible.</summary>
    public bool Shade { get; init; } = true;

    /// <summary>Downscale factor (1 = one pixel per tile). Pixels are box-averaged.</summary>
    public int Scale { get; init; } = 1;
}

/// <summary>Renders a facet top-down using the client's own radar (minimap) colours.</summary>
public static class RadarRenderer
{
    public static (int Width, int Height, byte[] Rgb) Render(
        Facet facet, RadarColors colors, TileData? tiles, RadarRenderOptions? options = null)
    {
        options ??= new RadarRenderOptions();
        int w = facet.Width, h = facet.Height;

        // Pass 1: topmost colour and surface altitude per cell.
        var color = new ushort[w * h];
        var surfaceZ = new sbyte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            var land = facet.Land[i];
            color[i] = colors.Land(land.TileId);
            surfaceZ[i] = land.Z;
        }

        if (options.Statics)
        {
            var bestKey = new int[w * h];
            for (int i = 0; i < bestKey.Length; i++) bestKey[i] = surfaceZ[i] * 256;

            for (int bx = 0; bx < facet.Info.BlockWidth; bx++)
            for (int by = 0; by < facet.Info.BlockHeight; by++)
            {
                foreach (var s in facet.GetBlockStatics(bx, by))
                {
                    ushort c = colors.Static(s.TileId);
                    if (c == 0) continue; // invisible / no radar colour

                    int x = bx * 8 + s.X, y = by * 8 + s.Y;
                    if (x >= w || y >= h) continue;
                    int i = x + y * w;
                    int height = tiles is not null && s.TileId < tiles.Statics.Length ? tiles.Statics[s.TileId].Height : 0;
                    int key = s.Z * 256 + height;
                    if (key >= bestKey[i])
                    {
                        bestKey[i] = key;
                        color[i] = c;
                        surfaceZ[i] = s.Z;
                    }
                }
            }
        }

        // Pass 2: to RGB, with optional hill shading from the north-west neighbour.
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = x + y * w;
            var (r, g, b) = RadarColors.ToRgb(color[i]);
            if (options.Shade)
            {
                int nw = surfaceZ[Math.Max(0, x - 1) + Math.Max(0, y - 1) * w];
                float f = Math.Clamp(1f + (surfaceZ[i] - nw) * 0.035f, 0.55f, 1.45f);
                r = (byte)Math.Min(255, r * f);
                g = (byte)Math.Min(255, g * f);
                b = (byte)Math.Min(255, b * f);
            }
            rgb[i * 3] = r; rgb[i * 3 + 1] = g; rgb[i * 3 + 2] = b;
        }

        return options.Scale <= 1 ? (w, h, rgb) : Downscale(w, h, rgb, options.Scale);
    }

    private static (int, int, byte[]) Downscale(int w, int h, byte[] rgb, int s)
    {
        int ow = w / s, oh = h / s;
        var output = new byte[ow * oh * 3];
        int n = s * s;
        for (int oy = 0; oy < oh; oy++)
        for (int ox = 0; ox < ow; ox++)
        {
            int r = 0, g = 0, b = 0;
            for (int dy = 0; dy < s; dy++)
            {
                int row = ((oy * s + dy) * w + ox * s) * 3;
                for (int dx = 0; dx < s; dx++)
                {
                    r += rgb[row + dx * 3];
                    g += rgb[row + dx * 3 + 1];
                    b += rgb[row + dx * 3 + 2];
                }
            }
            int o = (ox + oy * ow) * 3;
            output[o] = (byte)(r / n); output[o + 1] = (byte)(g / n); output[o + 2] = (byte)(b / n);
        }
        return (ow, oh, output);
    }
}
