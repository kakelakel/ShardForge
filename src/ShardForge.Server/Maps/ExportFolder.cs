using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShardForge.Server.Maps;

/// <summary>
/// The export folder next to the server (export/ in the ShardForge folder): everything the shard needs, laid out
/// so two copies install it. client/ goes into the client folder(s), ServUO/ over the ServUO folder. Each map takes
/// its facet's slot: exporting another map for the same facet replaces the files of the one before.
/// </summary>
public static class ExportFolder
{
    public sealed record Entry(string MapId, string Name, int Facet, string FacetName, int Width, int Height, int Items, string Exported, string By);

    public static string Dir(string contentRoot) => Path.Combine(contentRoot, "export");
    private static string Manifest(string dir) => Path.Combine(dir, "export.json");
    private static string Slug(string id) => new(id.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray());

    public static Dictionary<int, Entry> Read(string dir)
    {
        try { return JsonSerializer.Deserialize<Dictionary<int, Entry>>(File.ReadAllText(Manifest(dir))) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    public static Entry Write(string dir, MapInfo map, IEnumerable<(string Name, byte[] Bytes)> files, IReadOnlyList<JsonObject> items, string by)
    {
        string client = Path.Combine(dir, "client"), data = Path.Combine(dir, "ServUO", "Data", "ShardForge"), scripts = Path.Combine(dir, "ServUO", "Scripts", "Custom", "ShardForge");
        foreach (var d in new[] { client, data, scripts }) Directory.CreateDirectory(d);
        var all = Read(dir);
        int f = map.Facet;
        // This facet's earlier files out: the map files, and the previous map's items.
        foreach (var path in Directory.GetFiles(client))
        {
            string n = Path.GetFileName(path).ToLowerInvariant();
            if (n.StartsWith($"map{f}") || n == $"staidx{f}.mul" || n == $"statics{f}.mul") File.Delete(path);
        }
        if (all.TryGetValue(f, out var old)) File.Delete(Path.Combine(data, Slug(old.MapId) + ".sfitems"));
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(client, Path.GetFileName(name)), bytes);
        File.WriteAllText(Path.Combine(data, Slug(map.Id) + ".sfitems"), ItemStore.SfItems(ItemStore.ServUOMap(map), map.Name, items), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(scripts, "ShardForgeImport.cs"), ItemStore.ImportScript(), new UTF8Encoding(false));
        var entry = new Entry(map.Id, map.Name, f, ItemStore.ServUOMap(map), map.Width, map.Height, items.Count, ShardForge.Server.Data.Db.Now(), by);
        all[f] = entry;
        File.WriteAllText(Manifest(dir), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(dir, "README.txt"), Readme(all).Replace("\n", "\r\n"));
        return entry;
    }

    private static string Readme(Dictionary<int, Entry> all)
    {
        var sb = new StringBuilder();
        sb.Append("ShardForge export: everything your shard needs, ready to copy.\n\n");
        sb.Append("In this folder now:\n");
        foreach (var e in all.Values.OrderBy(e => e.Facet))
            sb.Append($"  facet {e.Facet} ({e.FacetName}): {e.Name}, {e.Width} x {e.Height}, {e.Items} server items, exported {e.Exported} by {e.By}\n");
        sb.Append("""

            To install (stop ServUO first, and keep a copy of its Saves folder):
              1. Copy everything in client\ into the client folder ServUO reads (Config\DataPath.cfg) and into
                 the client folder you play with. Overwrite.
              2. Copy the ServUO\ folder over your ServUO folder. It only adds Data\ShardForge\ and
                 Scripts\Custom\ShardForge\.
              3. Rebuild ServUO so it takes the script in: run "Compile.WIN - Release.bat" in the ServUO folder
                 (or "dotnet build -c Release"). Only the first time and when the script changes.
              4. Start ServUO, log in as an Administrator and type [SFImport

            [SFImport again replaces the earlier import; [SFClear removes it. Regions load at every start.
            If you copied a ServUO package (.zip) before, remove its .sfitems from Data\ShardForge\ so nothing is
            imported twice: this folder's Data\ShardForge\ is the whole set.

            """);
        return sb.ToString();
    }
}
