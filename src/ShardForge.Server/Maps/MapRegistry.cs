using System.Text.Json;
using ShardForge.Core.Map;

namespace ShardForge.Server.Maps;

/// <summary>A map the server offers: base files on disk plus shared statics edits in the database.</summary>
public sealed record MapInfo(string Id, string Name, int Facet, int Width, int Height, string Source, string Dir,
    IReadOnlyDictionary<string, long> Files)
{
    public FacetInfo FacetInfo => new(Facet, Name, Width, Height);
    public int BlockCount => Width / 8 * (Height / 8);
}

/// <summary>Metadata written next to maps the server builds from projects.</summary>
public sealed record BuiltMapMeta(string Name, int Facet, int Width, int Height, long ProjectId, int ProjectRev, string BuiltBy, string Built);

/// <summary>
/// Finds maps: the client's own facets, folders listed in settings, and maps built from projects
/// (data/maps/&lt;id&gt;/ with a map.json). Cheap enough to rescan on each request.
/// </summary>
public sealed class MapRegistry(ServerOptions options, string dataPath)
{
    public static readonly string[] FacetNames = ["Felucca", "Trammel", "Ilshenar", "Malas", "Tokuno", "TerMur"];

    public string BuiltMapsDir => Path.Combine(dataPath, "maps");

    public IReadOnlyList<MapInfo> All()
    {
        var list = new List<MapInfo>();
        if (Directory.Exists(options.ClientPath))
            for (int f = 0; f < FacetNames.Length; f++)
                if (Describe($"client-{f}", $"{FacetNames[f]} (client)", f, null, null, options.ClientPath, "client") is { } m) list.Add(m);

        foreach (var src in options.Maps)
            if (IsSafeId(src.Id) && Describe(src.Id, src.Name, src.Facet, src.Width, src.Height, src.Path, "folder") is { } m) list.Add(m);

        if (Directory.Exists(BuiltMapsDir))
            foreach (var dir in Directory.GetDirectories(BuiltMapsDir).Order())
            {
                string metaPath = Path.Combine(dir, "map.json");
                if (!File.Exists(metaPath)) continue;
                try
                {
                    var meta = JsonSerializer.Deserialize<BuiltMapMeta>(File.ReadAllText(metaPath));
                    if (meta is not null && Describe(Path.GetFileName(dir), meta.Name, meta.Facet, meta.Width, meta.Height, dir, "built") is { } m) list.Add(m);
                }
                catch (JsonException) { /* skip a broken folder */ }
            }
        return list;
    }

    public MapInfo? Get(string id) => All().FirstOrDefault(m => m.Id == id);

    public static bool IsSafeId(string id) => id.Length is > 0 and <= 64 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static MapInfo? Describe(string id, string name, int facet, int? width, int? height, string dir, string source)
    {
        if (!Directory.Exists(dir) || facet is < 0 or > 9) return null;
        var files = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in new[] { $"map{facet}LegacyMUL.uop", $"map{facet}.mul", $"staidx{facet}.mul", $"statics{facet}.mul" })
            if (FindFile(dir, n) is { } path) files[n.ToLowerInvariant()] = new FileInfo(path).Length;
        if (!files.ContainsKey($"map{facet}legacymul.uop") && !files.ContainsKey($"map{facet}.mul")) return null;
        // Towns and other named places from the project the map was built from (for the walk client's "Known places").
        if (source == "built" && FindFile(dir, "places.json") is { } places) files["places.json"] = new FileInfo(places).Length;

        if (width is null || height is null)
        {
            if (facet >= FacetInfo.Known.Count) return null;
            var known = FacetInfo.ForIndex(facet);
            width ??= known.Width; height ??= known.Height;
            // Old 6144-wide Felucca/Trammel: trust the statics index size.
            if (files.TryGetValue($"staidx{facet}.mul", out long idx) && facet is 0 or 1 && idx / 12 == 6144 / 8 * (4096 / 8)) width = 6144;
        }
        return new MapInfo(id, name, facet, width.Value, height.Value, source, dir, files);
    }

    /// <summary>Case-insensitive file lookup (client folders on Linux may differ in case).</summary>
    public static string? FindFile(string dir, string name)
    {
        string exact = Path.Combine(dir, name);
        if (File.Exists(exact)) return exact;
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
    }
}
