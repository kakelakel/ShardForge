using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShardForge.Core.Architecture;

/// <summary>A wall material from the client's walls.txt (house customization sets).</summary>
public sealed class WallSet
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("category")] public int Category { get; set; }
    [JsonPropertyName("style")] public int Style { get; set; }
    /// <summary>Pieces running east-west (UO calls them "South" walls): straight variants.</summary>
    [JsonPropertyName("ew")] public int[] EW { get; set; } = [];
    /// <summary>Pieces running north-south ("East" walls).</summary>
    [JsonPropertyName("ns")] public int[] NS { get; set; } = [];
    [JsonPropertyName("corner")] public int Corner { get; set; }
    [JsonPropertyName("post")] public int Post { get; set; }
    [JsonPropertyName("windowEW")] public int[] WindowEW { get; set; } = [];
    [JsonPropertyName("windowNS")] public int[] WindowNS { get; set; } = [];
}

/// <summary>A pitched roof style from roof.txt.</summary>
public sealed class RoofSet
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("north")] public int North { get; set; }
    [JsonPropertyName("east")] public int East { get; set; }
    [JsonPropertyName("south")] public int South { get; set; }
    [JsonPropertyName("west")] public int West { get; set; }
    /// <summary>Ridge running north-south / east-west.</summary>
    [JsonPropertyName("ridgeNS")] public int RidgeNS { get; set; }
    [JsonPropertyName("ridgeEW")] public int RidgeEW { get; set; }
}

public sealed class FloorSet
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tiles")] public int[] Tiles { get; set; } = [];
}

/// <summary>House-customization component sets (from walls.txt, roof.txt, floors.txt), shipped with ShardForge.</summary>
public sealed class Kits
{
    [JsonPropertyName("walls")] public List<WallSet> Walls { get; set; } = new();
    [JsonPropertyName("roofs")] public List<RoofSet> Roofs { get; set; } = new();
    [JsonPropertyName("floors")] public List<FloorSet> Floors { get; set; } = new();

    public WallSet Wall(string name) => Walls.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"No wall set named '{name}'.");
    public RoofSet Roof(string name) => Roofs.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"No roof named '{name}'.");
    public FloorSet Floor(string name) => Floors.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"No floor named '{name}'.");

    /// <summary>Quarter-height walls of the same material (for gables and parapets), if the client has them.</summary>
    public WallSet? Low(WallSet full) => Walls.FirstOrDefault(w => w.Category == full.Category && w.Style > 0 && w.Name.Contains("Quarter", StringComparison.OrdinalIgnoreCase));

    private static Kits? _builtIn;
    public static Kits BuiltIn => _builtIn ??= Load();

    private static Kits Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ShardForge.Core.Architecture.kits.json")
            ?? throw new InvalidOperationException("kits.json is missing from ShardForge.Core.");
        return JsonSerializer.Deserialize<Kits>(s)!;
    }

    /// <summary>Reads the client's tab-separated component files (walls.txt, roof.txt, floors.txt).</summary>
    public static Kits FromClient(string clientDir)
    {
        var kits = new Kits();
        foreach (var r in Rows(Path.Combine(clientDir, "walls.txt")))
        {
            int[] Ids(params string[] cols) => cols.Select(c => r.GetValueOrDefault(c)).Where(v => v > 0).ToArray();
            var w = new WallSet
            {
                Name = r.Comment, Category = r.GetValueOrDefault("Category"), Style = r.GetValueOrDefault("Style"),
                EW = Ids("South1", "South2", "South3"), NS = Ids("East1", "East2", "East3"),
                Corner = r.GetValueOrDefault("Corner"), Post = r.GetValueOrDefault("Post"),
                WindowEW = Ids("WindowS", "AltWindowS", "SecondAltWindowS"), WindowNS = Ids("WindowE", "AltWindowE", "SecondAltWindowE"),
            };
            if (w.EW.Length > 0 && w.NS.Length > 0 && w.Corner > 0) kits.Walls.Add(w);
        }
        foreach (var r in Rows(Path.Combine(clientDir, "roof.txt")))
            kits.Roofs.Add(new RoofSet
            {
                Name = r.Comment, North = r.GetValueOrDefault("North"), East = r.GetValueOrDefault("East"), South = r.GetValueOrDefault("South"),
                West = r.GetValueOrDefault("West"), RidgeNS = r.GetValueOrDefault("NSCrosspiece"), RidgeEW = r.GetValueOrDefault("EWCrosspiece"),
            });
        foreach (var r in Rows(Path.Combine(clientDir, "floors.txt")))
        {
            var tiles = Enumerable.Range(1, 16).Select(i => r.GetValueOrDefault("F" + i)).Where(v => v > 0).ToArray();
            if (tiles.Length > 0) kits.Floors.Add(new FloorSet { Name = r.Comment, Tiles = tiles });
        }
        return kits;
    }

    private sealed class Row : Dictionary<string, int> { public string Comment = ""; }

    private static IEnumerable<Row> Rows(string path)
    {
        string[]? header = null;
        foreach (var line in File.ReadLines(path))
        {
            var cols = line.Split('\t');
            if (cols.Length > 1 && cols[0] == "Category") { header = cols; continue; }
            if (header is null || cols.Length < header.Length - 1 || !int.TryParse(cols[0], out _)) continue;
            var row = new Row();
            for (int i = 0; i < header.Length && i < cols.Length; i++)
            {
                if (header[i] == "Comment") row.Comment = cols[i].Trim();
                else if (int.TryParse(cols[i], out int v)) row[header[i]] = v;
            }
            yield return row;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });
}
