using ShardForge.Core.Art;
using ShardForge.Core.Data;
using ShardForge.Core.Map;

namespace ShardForge.Core.Rendering;

/// <summary>
/// Draws part of a facet the way the client does: isometric 44x44 land diamonds raised by altitude
/// (4 px per z), stretched between corner altitudes, with statics sorted on top.
/// </summary>
public static class IsoRenderer
{
    public static (int Width, int Height, byte[] Rgb) Render(Facet facet, ArtReader art, TileData? tiles,
        int cx, int cy, int radius, bool statics = true, int maxZ = 1000)
    {
        int x0 = Math.Max(0, cx - radius), x1 = Math.Min(facet.Width - 1, cx + radius);
        int y0 = Math.Max(0, cy - radius), y1 = Math.Min(facet.Height - 1, cy + radius);

        // Screen bounds (generous vertical margin for altitude and tall statics).
        int span = (x1 - x0) + (y1 - y0) + 2;
        int width = span * 22 + 44, height = span * 22 + 44 + 600;
        int originX = (y1 - y0 + 1) * 22, originY = 400;
        var buf = new uint[width * height];
        Array.Fill(buf, 0xFF000000u);

        // Back to front: by x + y, then z.
        for (int sum = x0 + y0; sum <= x1 + y1; sum++)
        {
            for (int x = Math.Max(x0, sum - y1); x <= Math.Min(x1, sum - y0); x++)
            {
                int y = sum - x;
                int sx = originX + (x - x0 - (y - y0)) * 22;
                int sy = originY + (x - x0 + (y - y0)) * 22;

                var land = facet.GetLand(x, y);
                if (land.Z < maxZ && art.Land(land.TileId) is { } ls)
                {
                    int zE = facet.GetLand(Math.Min(x + 1, facet.Width - 1), y).Z;
                    int zS = facet.GetLand(x, Math.Min(y + 1, facet.Height - 1)).Z;
                    int zSE = facet.GetLand(Math.Min(x + 1, facet.Width - 1), Math.Min(y + 1, facet.Height - 1)).Z;
                    if (zE == land.Z && zS == land.Z && zSE == land.Z)
                        Blit(buf, width, height, ls, sx, sy - land.Z * 4);
                    else
                        BlitStretched(buf, width, height, ls, sx, sy, land.Z, zE, zSE, zS);
                }

                if (!statics) continue;
                var block = facet.GetBlockStatics(x >> 3, y >> 3);
                foreach (var s in block.Where(s => s.X == (x & 7) && s.Y == (y & 7))
                             .OrderBy(s => s.Z).ThenBy(s => tiles is null ? 0 : tiles.Statics[s.TileId].Height))
                {
                    if (s.Z >= maxZ || art.Static(s.TileId) is not { } ss) continue;
                    Blit(buf, width, height, ss, sx + 22 - ss.Width / 2, sy + 44 - ss.Height - s.Z * 4);
                }
            }
        }

        var rgb = new byte[width * height * 3];
        for (int i = 0; i < buf.Length; i++)
        {
            uint c = buf[i];
            rgb[i * 3] = (byte)(c >> 16); rgb[i * 3 + 1] = (byte)(c >> 8); rgb[i * 3 + 2] = (byte)c;
        }
        return (width, height, rgb);
    }

    /// <summary>
    /// Draws a land diamond whose four corners sit at different altitudes (top = this cell, right = east,
    /// bottom = south-east, left = south), like the client's stretched terrain. Forward-maps the source
    /// diamond at 2x density so the stretched surface has no holes.
    /// </summary>
    private static void BlitStretched(uint[] dst, int dw, int dh, Sprite s, int dx, int dy, int zTop, int zRight, int zBottom, int zLeft)
    {
        const int Steps = 88;
        // Light from the north-west, like the client: faces tilted toward the top-left of the screen are brighter.
        double slope = (zRight + zBottom) - (zTop + zLeft) + (zLeft - zRight) * 0.5;
        double light = Math.Clamp(1.0 + slope * 0.035, 0.45, 1.6);
        for (int ia = 0; ia <= Steps; ia++)
        for (int ib = 0; ib <= Steps; ib++)
        {
            double a = ia / (double)Steps, b = ib / (double)Steps; // a: top->right edge, b: top->left edge
            int srcX = (int)(22 + (a - b) * 22), srcY = (int)((a + b) * 22);
            if (srcX < 0 || srcX >= 44 || srcY < 0 || srcY >= 44) continue;
            uint c = s.Pixels[srcY * 44 + Math.Min(43, srcX)];
            if (c == 0) continue;
            double z = zTop * (1 - a) * (1 - b) + zRight * a * (1 - b) + zBottom * a * b + zLeft * (1 - a) * b;
            int tx = dx + (int)(22 + (a - b) * 22), ty = dy + (int)((a + b) * 22 - z * 4);
            if ((uint)tx < (uint)dw && (uint)ty < (uint)dh) dst[ty * dw + tx] = Shade(c, light);
        }
    }

    private static uint Shade(uint c, double f)
    {
        uint r = (uint)Math.Min(255, ((c >> 16) & 255) * f), g = (uint)Math.Min(255, ((c >> 8) & 255) * f), b = (uint)Math.Min(255, (c & 255) * f);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    private static void Blit(uint[] dst, int dw, int dh, Sprite s, int dx, int dy)
    {
        for (int y = 0; y < s.Height; y++)
        {
            int ty = dy + y;
            if ((uint)ty >= (uint)dh) continue;
            for (int x = 0; x < s.Width; x++)
            {
                uint c = s.Pixels[y * s.Width + x];
                int tx = dx + x;
                if (c == 0 || (uint)tx >= (uint)dw) continue;
                dst[ty * dw + tx] = c;
            }
        }
    }
}
