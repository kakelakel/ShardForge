using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShardForge.Core.Terrain;

/// <summary>
/// Shores the way the client's own coasts are made. Land water tiles meeting the ground at a different height
/// stretch into a sawtooth, so near the shore the original maps lay the sea bed low (z -15), let the beach slope down
/// into it, and put flat water statics on top at sea level; the cells along the edge get water pieces with foam
/// and a shaped edge, chosen by which of the eight neighbours are water. The table is mined from Trammel.
/// </summary>
public sealed class WaterEdges
{
    public sealed class Pattern
    {
        [JsonPropertyName("seen")] public int Seen { get; set; }
        /// <summary>[tileId, count].</summary>
        [JsonPropertyName("pieces")] public List<int[]> Pieces { get; set; } = new();
    }

    [JsonPropertyName("source")] public string Source { get; set; } = "";
    /// <summary>Pattern (8 chars, N NE E SE S SW W NW, 1 = water) → the water statics used there.</summary>
    [JsonPropertyName("patterns")] public Dictionary<string, Pattern> Patterns { get; set; } = new();

    /// <summary>Plain open water statics.</summary>
    public static readonly ushort[] Open = [0x1797, 0x1798, 0x1799, 0x179A, 0x179B, 0x179C];
    /// <summary>How far out from the shore the water is statics over a low sea bed.</summary>
    public const int Band = 3;
    public const int SeaBedZ = -15;

    private static WaterEdges? _builtIn;
    public static WaterEdges BuiltIn => _builtIn ??= Load();
    private static WaterEdges Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ShardForge.Core.Terrain.water-edges.json");
        return s is null ? new WaterEdges() : JsonSerializer.Deserialize<WaterEdges>(s) ?? new WaterEdges();
    }

    /// <summary>The water static for a cell with this neighbour mask: the pattern's pieces as often as the
    /// originals use them, the nearest pattern seen often enough when this one is rare, else open water.</summary>
    public ushort Piece(string mask, double roll)
    {
        Pattern? p = Patterns.TryGetValue(mask, out var exact) && exact.Seen >= 20 ? exact : null;
        if (p is null)
        {
            int bestD = int.MaxValue;
            foreach (var (m, q) in Patterns)
            {
                if (q.Seen < 20) continue;
                int d = 0;
                for (int i = 0; i < 8; i++) if (m[i] != mask[i]) d += i % 2 == 0 ? 3 : 1;
                if (d < bestD || (d == bestD && q.Seen > p!.Seen)) { bestD = d; p = q; }
            }
            if (bestD > 2) p = exact; // nothing close: the rare exact one, or open water
        }
        if (p is null || p.Pieces.Count == 0) return Open[(int)(roll * Open.Length) % Open.Length];
        int total = p.Pieces.Sum(q => q[1]);
        double r = roll * total;
        foreach (var q in p.Pieces) { r -= q[1]; if (r < 0) return (ushort)q[0]; }
        return (ushort)p.Pieces[^1][0];
    }
}
