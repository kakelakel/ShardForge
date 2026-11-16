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
  shardforge build  <project.shardforge.json> <outDir> [--facet N] [--client <clientDir>] [--no-transitions]
  shardforge iso    <mapDir> <facet> <x> <y> <radius> <out.png> [--client <clientDir>] [--size W H] [--no-statics]
  shardforge mine-transitions <clientDir> <out.json> <facet,facet,...>
  shardforge tiles  <clientDir> <facet> <name,name,...>
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
        ["build", var project, var outDir, .. var rest] => Build(project, outDir, rest),
        ["iso", var mapDir, var f, var cx, var cy, var r, var output, .. var rest] =>
            Iso(mapDir, int.Parse(f), int.Parse(cx), int.Parse(cy), int.Parse(r), output, rest),
        ["mine-transitions", var dir, var output, var facets] => MineTransitions(dir, output, facets),
        ["tiles", var dir, var f, var filter] => TileStats(dir, int.Parse(f), filter),
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

static int Build(string projectPath, string outDir, string[] rest)
{
    int facetIndex = 0;
    string? client = null;
    bool transitions = true;
    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--facet" when i + 1 < rest.Length: facetIndex = int.Parse(rest[++i]); break;
            case "--client" when i + 1 < rest.Length: client = rest[++i]; break;
            case "--no-transitions": transitions = false; break;
            default: return Fail($"Unknown option '{rest[i]}'.\n\n{Usage}");
        }
    }

    var sw = Stopwatch.StartNew();
    var project = ShardForge.Core.Project.ShardProject.Load(projectPath);
    var facet = project.Build(facetIndex, transitions ? ShardForge.Core.Terrain.TransitionTable.BuiltIn : null);
    var files = FacetWriter.WriteAll(facet, outDir, facetIndex);
    Console.WriteLine($"Built '{project.Name}' ({facet.Width} x {facet.Height}, {facet.StaticCount:N0} statics) as facet {facetIndex} in {sw.ElapsedMilliseconds} ms:");
    foreach (var f in files) Console.WriteLine($"  {f} ({new FileInfo(f).Length:N0} bytes)");

    // Verify: read the files back (UOP land + MUL statics) and compare with what we built.
    var reread = FacetLoader.Load(outDir, facetIndex, facet.Info);
    bool landOk = reread.Land.AsSpan().SequenceEqual(facet.Land);
    bool staticsOk = Enumerable.Range(0, facet.Info.BlockCount).All(b => reread.Statics[b].AsSpan().SequenceEqual(facet.Statics[b]));
    bool mulOk = File.ReadAllBytes(Path.Combine(outDir, $"map{facetIndex}.mul")).AsSpan()
        .SequenceEqual(FacetWriter.EncodeLand(reread));
    Console.WriteLine($"Verify: land {(landOk ? "OK" : "MISMATCH")}, statics {(staticsOk ? "OK" : "MISMATCH")}, UOP == MUL {(mulOk ? "OK" : "MISMATCH")}");

    if (client is not null)
    {
        var colors = RadarColors.Load(Path.Combine(client, "radarcol.mul"));
        var tiles = TileData.Load(Path.Combine(client, "tiledata.mul"));
        var (w, h, rgb) = RadarRenderer.Render(reread, colors, tiles);
        string png = Path.Combine(outDir, "preview.png");
        PngWriter.WriteRgb(png, w, h, rgb);
        Console.WriteLine($"Preview with the client's radar colours: {png}");
    }
    return landOk && staticsOk && mulOk ? 0 : 2;
}

static int Iso(string mapDir, int facetIndex, int cx, int cy, int radius, string output, string[] rest)
{
    string client = mapDir; bool statics = true; int? w = null, h = null;
    for (int i = 0; i < rest.Length; i++)
    {
        switch (rest[i])
        {
            case "--client" when i + 1 < rest.Length: client = rest[++i]; break;
            case "--size" when i + 2 < rest.Length: w = int.Parse(rest[++i]); h = int.Parse(rest[++i]); break;
            case "--no-statics": statics = false; break;
            default: return Fail($"Unknown option '{rest[i]}'.\n\n{Usage}");
        }
    }
    var info = w is int ww && h is int hh ? new FacetInfo(facetIndex, $"facet {facetIndex}", ww, hh) : null;
    var facet = FacetLoader.Load(mapDir, facetIndex, info);
    using var art = new ShardForge.Core.Art.ArtReader(client);
    var tiles = TileData.Load(Path.Combine(client, "tiledata.mul"));
    var (iw, ih, rgb) = IsoRenderer.Render(facet, art, tiles, cx, cy, radius, statics);
    PngWriter.WriteRgb(output, iw, ih, rgb);
    Console.WriteLine($"Rendered {radius * 2 + 1} x {radius * 2 + 1} tiles around {cx},{cy} to {output} ({iw} x {ih})");
    return 0;
}

static int MineTransitions(string dir, string output, string facetList)
{
    var facets = facetList.Split(',').Select(f => FacetLoader.Load(dir, int.Parse(f))).ToList();
    using var art = File.Exists(Path.Combine(dir, "artLegacyMUL.uop")) || File.Exists(Path.Combine(dir, "art.mul"))
        ? new ShardForge.Core.Art.ArtReader(dir) : null;
    var table = ShardForge.Core.Terrain.TransitionMiner.Mine(facets, averageColor: art is null ? null : art.LandAverage);
    table.Save(output);
    var tiles = TileData.Load(Path.Combine(dir, "tiledata.mul"));
    Console.WriteLine($"{table.Source}: {table.Pairs.Count} terrain pairs");
    foreach (var (pair, byPattern) in table.Pairs.OrderByDescending(p => p.Value.Values.Sum(l => l.Sum(c => c.Count))))
    {
        int total = byPattern.Values.Sum(l => l.Sum(c => c.Count));
        var patterns = string.Join(" ", Enumerable.Range(1, 14).Select(p => byPattern.ContainsKey(p.ToString()) ? p.ToString("X") : "."));
        Console.WriteLine($"  {pair,-20} {total,8:N0} uses   patterns [{patterns}]");
    }
    return 0;
}

static int TileStats(string dir, int facetIndex, string filter)
{
    var facet = FacetLoader.Load(dir, facetIndex);
    var tiles = TileData.Load(Path.Combine(dir, "tiledata.mul"));
    var counts = new int[TileData.LandCount];
    foreach (var l in facet.Land) counts[l.TileId & 0x3FFF]++;

    foreach (var name in filter.Split(','))
    {
        var top = Enumerable.Range(0, counts.Length)
            .Where(i => counts[i] > 0 && tiles.Land[i].Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => counts[i]).Take(8)
            .Select(i => $"0x{i:X4}:{tiles.Land[i].Name}:{counts[i]}");
        Console.WriteLine($"{name,-10} {string.Join("  ", top)}");
    }

    var sCounts = new int[tiles.Statics.Length];
    foreach (var s in facet.Statics.SelectMany(b => b))
        if (s.TileId < sCounts.Length) sCounts[s.TileId]++;
    Console.WriteLine("-- statics --");
    foreach (var name in filter.Split(','))
    {
        var top = Enumerable.Range(0, sCounts.Length)
            .Where(i => sCounts[i] > 0 && tiles.Statics[i].Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => sCounts[i]).Take(10)
            .Select(i => $"0x{i:X4}:{tiles.Statics[i].Name}:{sCounts[i]}");
        Console.WriteLine($"{name,-10} {string.Join("  ", top)}");
    }
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
