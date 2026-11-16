using System.Diagnostics;
using ShardForge.Core.Data;
using ShardForge.Core.Imaging;
using ShardForge.Core.Map;
using ShardForge.Core.Rendering;
using ShardForge.Core.Uop;

const string Usage = """
ShardForge - forge your own shard.

Usage:
  shardforge info   <clientDir> <facet>
  shardforge render <clientDir> <facet> <out.png> [--scale N] [--no-statics] [--no-shade]
  shardforge uop    <file.uop>

Facets: 0 Felucca, 1 Trammel, 2 Ilshenar, 3 Malas, 4 Tokuno, 5 TerMur
""";

try
{
    return args switch
    {
        ["info", var dir, var f] => Info(dir, int.Parse(f)),
        ["render", var dir, var f, var output, .. var rest] => Render(dir, int.Parse(f), output, rest),
        ["uop", var path] => DumpUop(path),
        _ => Fail(Usage),
    };
}
catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FormatException or NotSupportedException)
{
    return Fail($"error: {ex.Message}");
}

static int Info(string dir, int facetIndex)
{
    var sw = Stopwatch.StartNew();
    var facet = FacetLoader.Load(dir, facetIndex);
    var tiles = TileData.Load(Path.Combine(dir, "tiledata.mul"));
    Console.WriteLine($"{facet.Info.Name}: {facet.Width} x {facet.Height} ({facet.Info.BlockCount} blocks), loaded in {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"Statics: {facet.StaticCount:N0}");

    var z = facet.Land.Select(l => (int)l.Z).ToArray();
    Console.WriteLine($"Land altitude: min {z.Min()}, max {z.Max()}, mean {z.Average():F1}");

    Console.WriteLine("Most common land tiles:");
    foreach (var g in facet.Land.GroupBy(l => l.TileId).OrderByDescending(g => g.Count()).Take(12))
        Console.WriteLine($"  0x{g.Key:X4} {tiles.Land[g.Key].Name,-20} {g.Count() * 100.0 / facet.Land.Length,6:F2}%");

    Console.WriteLine("Most common statics:");
    foreach (var g in facet.Statics.SelectMany(s => s).GroupBy(s => s.TileId).OrderByDescending(g => g.Count()).Take(12))
        Console.WriteLine($"  0x{g.Key:X4} {tiles.Statics[g.Key].Name,-20} {g.Count(),9:N0}");
    return 0;
}

static int Render(string dir, int facetIndex, string output, string[] rest)
{
    int scale = 1;
    bool statics = true, shade = true;
    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--scale" when i + 1 < rest.Length: scale = int.Parse(rest[++i]); break;
            case "--no-statics": statics = false; break;
            case "--no-shade": shade = false; break;
            default: return Fail($"Unknown option '{rest[i]}'.\n\n{Usage}");
        }
    }

    var sw = Stopwatch.StartNew();
    var facet = FacetLoader.Load(dir, facetIndex);
    var colors = RadarColors.Load(Path.Combine(dir, "radarcol.mul"));
    var tilesPath = Path.Combine(dir, "tiledata.mul");
    var tiles = File.Exists(tilesPath) ? TileData.Load(tilesPath) : null;

    var (w, h, rgb) = RadarRenderer.Render(facet, colors, tiles,
        new RadarRenderOptions { Scale = scale, Statics = statics, Shade = shade });
    PngWriter.WriteRgb(output, w, h, rgb);
    Console.WriteLine($"Rendered {facet.Info.Name} to {output} ({w} x {h}) in {sw.ElapsedMilliseconds} ms");
    return 0;
}

static int DumpUop(string path)
{
    using var uop = new UopFile(path);
    Console.WriteLine($"{path}: version {uop.Version}, {uop.Entries.Count} entries, table capacity {uop.TableCapacity}");
    foreach (var e in uop.Entries.Take(20))
        Console.WriteLine($"  hash {e.Hash:X16} @ {e.Offset,10} header {e.HeaderLength,4} size {e.DecompressedLength,9} compression {e.Compression}");
    if (uop.Entries.Count > 20) Console.WriteLine($"  ... {uop.Entries.Count - 20} more");
    return 0;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
