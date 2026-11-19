using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShardForge.Server.Auth;
using ShardForge.Server.Data;

namespace ShardForge.Server.Maps;

public sealed class ItemException(string message) : Exception(message);

/// <summary>
/// Server items placed in Walk: doors, signs, teleporters, spawners, working items (forges, anvils…) and regions.
/// Unlike statics they become live objects on the shard, through the ServUO import script in the server package.
/// Each is a small JSON object with a client-made uid, so everyone can add, change and undo without id clashes.
/// </summary>
public sealed class ItemStore(Db db)
{
    public const int MaxItems = 200_000;
    public static readonly string[] Kinds = ["door", "sign", "item", "tele", "spawn", "region"];
    public static readonly string[] Facings = ["WestCW", "EastCCW", "WestCCW", "EastCW", "SouthCW", "NorthCCW", "SouthCCW", "NorthCW", "SouthSW", "SouthSE", "WestSS", "WestSN"];

    /// <summary>The ServUO map a facet number stands for (custom facets default to Felucca: edit the package's map line).</summary>
    public static string ServUOMap(MapInfo map) => map.Facet is >= 0 and <= 5 ? MapRegistry.FacetNames[map.Facet] : "Felucca";

    public IReadOnlyList<JsonObject> All(string mapId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM map_items WHERE map_id = $m ORDER BY uid";
        cmd.Parameters.AddWithValue("$m", mapId);
        var list = new List<JsonObject>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (JsonNode.Parse(r.GetString(0)) is JsonObject o) list.Add(o);
        return list;
    }

    /// <summary>What a change did, for the activity log.</summary>
    public sealed record ItemChange(string Op, string Kind, int X, int Y, string What);

    /// <summary>Kinds that shape the world rather than decorate it.</summary>
    public static readonly string[] WorldKinds = ["region", "tele"];

    /// <summary>
    /// Adds or replaces items and removes others, all at once, with the user's level enforced: decorators place
    /// doors, signs, items and spawners and may change or remove only their own; regions and teleporters, and
    /// anyone else's items, need a worldbuilder. Returns the cleaned items and what changed.
    /// </summary>
    public (List<JsonObject> Put, List<ItemChange> Changes) Apply(MapInfo map, User user, IEnumerable<JsonObject> put, IEnumerable<string> remove)
    {
        var clean = put.Select(o => Validate(o, map)).ToList();
        var gone = remove.Where(u => !string.IsNullOrEmpty(u) && u.Length <= 64).Distinct().ToList();
        bool world = user.Role >= Role.Worldbuilder;
        using var c = db.Open();
        using var t = c.BeginTransaction();
        (JsonObject Data, long? By)? Existing(string uid)
        {
            using var q = c.CreateCommand();
            q.Transaction = t;
            q.CommandText = "SELECT data, created_by FROM map_items WHERE map_id = $m AND uid = $u";
            q.Parameters.AddWithValue("$m", map.Id); q.Parameters.AddWithValue("$u", uid);
            using var r = q.ExecuteReader();
            return r.Read() && JsonNode.Parse(r.GetString(0)) is JsonObject o ? (o, r.IsDBNull(1) ? null : r.GetInt64(1)) : null;
        }
        var changes = new List<ItemChange>();
        foreach (var o in clean)
        {
            string uid = (string)o["uid"]!, kind = (string)o["kind"]!;
            var old = Existing(uid);
            if (!world && WorldKinds.Contains(kind)) throw new PermissionException($"Only worldbuilders can place or change {(kind == "tele" ? "teleporters" : "regions")}.");
            if (!world && old is { } e && e.By != user.Id) throw new PermissionException("Decorators can only change what they placed themselves. Ask a worldbuilder.");
            o["by"] = old?.Data["by"]?.DeepClone() ?? user.Username;
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "INSERT INTO map_items(map_id, uid, data, updated_by, updated, created_by) VALUES ($m, $u, $d, $by, $t, $by) ON CONFLICT(map_id, uid) DO UPDATE SET data = $d, updated_by = $by, updated = $t";
            cmd.Parameters.AddWithValue("$m", map.Id); cmd.Parameters.AddWithValue("$u", uid);
            cmd.Parameters.AddWithValue("$d", o.ToJsonString()); cmd.Parameters.AddWithValue("$by", user.Id); cmd.Parameters.AddWithValue("$t", Db.Now());
            cmd.ExecuteNonQuery();
            changes.Add(new ItemChange(old is null ? "placed" : "changed", kind, (int)o["x"]!, (int)o["y"]!, Describe(o)));
        }
        foreach (var u in gone)
        {
            var old = Existing(u);
            if (old is null) continue;
            if (!world && (old.Value.By != user.Id || WorldKinds.Contains(old.Value.Data["kind"]?.ToString())))
                throw new PermissionException("Decorators can only remove what they placed themselves. Ask a worldbuilder.");
            using var cmd = c.CreateCommand();
            cmd.Transaction = t;
            cmd.CommandText = "DELETE FROM map_items WHERE map_id = $m AND uid = $u";
            cmd.Parameters.AddWithValue("$m", map.Id); cmd.Parameters.AddWithValue("$u", u);
            cmd.ExecuteNonQuery();
            var d = old.Value.Data;
            changes.Add(new ItemChange("removed", d["kind"]?.ToString() ?? "?", N(d, "x"), N(d, "y"), Describe(d)));
        }
        using (var n = c.CreateCommand())
        {
            n.Transaction = t;
            n.CommandText = "SELECT COUNT(*) FROM map_items WHERE map_id = $m";
            n.Parameters.AddWithValue("$m", map.Id);
            if ((long)n.ExecuteScalar()! > MaxItems) throw new ItemException($"A map can hold up to {MaxItems:N0} items.");
        }
        t.Commit();
        return (clean, changes);
    }

    private static int N(JsonObject o, string k) => o[k] is null ? 0 : (int)double.Parse(o[k]!.ToJsonString(), CultureInfo.InvariantCulture);
    private static string S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>A short human description: "door (DarkWoodDoor)", "spawner: Orc:4,OrcishMage:1", "region Britain (guarded)".</summary>
    public static string Describe(JsonObject o) => S(o, "kind") switch
    {
        "door" => $"door ({S(o, "type")})",
        "sign" => $"sign \"{S(o, "name")}\"",
        "item" => $"item {S(o, "type")}",
        "tele" => $"teleporter to {N(o, "dx")}, {N(o, "dy")} {S(o, "dmap")}",
        "spawn" => $"spawner: {S(o, "creatures")}",
        "region" => $"region {S(o, "name")}{(o["guarded"]?.ToJsonString() == "true" ? " (guarded)" : "")} {N(o, "w")}x{N(o, "h")}",
        _ => S(o, "kind"),
    };

    private static string Str(JsonObject o, string k, int max, bool required = true)
    {
        var v = o[k] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s.Trim() : "";
        if (v.Length == 0 && required) throw new ItemException($"A {o["kind"]} needs {k}.");
        if (v.Length > max || v.Any(ch => ch is '\t' or '\n' or '\r')) throw new ItemException($"{k} is too long or has tabs or line breaks.");
        return v;
    }
    private static int Int(JsonObject o, string k, int min, int max, int def = 0)
    {
        int v = o[k] is JsonValue jv && jv.TryGetValue<double>(out var d) ? (int)Math.Round(d) : def;
        if (v < min || v > max) throw new ItemException($"{k} must be between {min} and {max}.");
        return v;
    }
    private static bool Ident(string s) => s.Length > 0 && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    public static JsonObject Validate(JsonObject o, MapInfo map)
    {
        string kind = Str(o, "kind", 10);
        if (!Kinds.Contains(kind)) throw new ItemException($"Unknown kind '{kind}'.");
        string uid = Str(o, "uid", 64);
        var r = new JsonObject { ["uid"] = uid, ["kind"] = kind, ["x"] = Int(o, "x", 0, map.Width - 1), ["y"] = Int(o, "y", 0, map.Height - 1), ["z"] = Int(o, "z", -128, 127) };
        switch (kind)
        {
            case "door":
                string type = Str(o, "type", 40), facing = Str(o, "facing", 10);
                if (!Ident(type)) throw new ItemException("A door type is a ServUO class name, like DarkWoodDoor.");
                if (!Facings.Contains(facing)) throw new ItemException($"Unknown door facing '{facing}'.");
                r["type"] = type; r["facing"] = facing; r["id"] = Int(o, "id", 0, 0xFFFF);
                break;
            case "sign":
                r["id"] = Int(o, "id", 1, 0xFFFF); r["hue"] = Int(o, "hue", 0, 3000); r["name"] = Str(o, "name", 120, false);
                break;
            case "item":
                string it = Str(o, "type", 60);
                if (!Ident(it)) throw new ItemException("An item type is a ServUO class name, like Forge.");
                r["type"] = it; r["id"] = Int(o, "id", 0, 0xFFFF); r["hue"] = Int(o, "hue", 0, 3000);
                break;
            case "tele":
                r["dx"] = Int(o, "dx", 0, 65535); r["dy"] = Int(o, "dy", 0, 65535); r["dz"] = Int(o, "dz", -128, 127);
                string dm = Str(o, "dmap", 20, false);
                r["dmap"] = dm.Length == 0 ? ServUOMap(map) : Ident(dm) ? dm : throw new ItemException("A map name has letters only.");
                break;
            case "spawn":
                // "Orc:4, OrcishMage:1", or plain names sharing the total count.
                var entries = Str(o, "creatures", 600).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(e => e.Split(':', 2, StringSplitOptions.TrimEntries)).ToList();
                if (entries.Count == 0 || entries.Count > 20 || !entries.All(e => Ident(e[0]) && (e.Length == 1 || int.TryParse(e[1], out var k) && k is >= 1 and <= 200)))
                    throw new ItemException("Creatures are ServUO class names, each with a count if you like: Orc:4, OrcishMage:1.");
                int total = Int(o, "count", 0, 200, 0), each = Math.Max(1, (total == 0 ? entries.Count : total) / entries.Count);
                var pairs = entries.Select(e => (Name: e[0], Count: e.Length > 1 ? int.Parse(e[1]) : each)).ToList();
                r["creatures"] = string.Join(",", pairs.Select(p => $"{p.Name}:{p.Count}")); r["count"] = Math.Min(200, pairs.Sum(p => p.Count)); r["range"] = Int(o, "range", 0, 100, 5);
                r["min"] = Int(o, "min", 0, 1440, 5); r["max"] = Int(o, "max", 0, 1440, 10);
                break;
            case "region":
                r["w"] = Int(o, "w", 1, 8192); r["h"] = Int(o, "h", 1, 8192); r["name"] = Str(o, "name", 60); r["guarded"] = o["guarded"] is JsonValue g && g.TryGetValue<bool>(out var b) && b;
                break;
        }
        return r;
    }

    /// <summary>The ServUO import file for a map's items (see servuo/ShardForgeImport.cs).</summary>
    public static string SfItems(string facetName, string mapName, IEnumerable<JsonObject> items)
    {
        var sb = new StringBuilder();
        sb.Append("# ShardForge items for ").Append(mapName).Append(". Load with [SFImport (see ShardForgeImport.cs).\n");
        sb.Append("map\t").Append(facetName).Append('\n');
        string I(JsonObject o, string k) => o[k] is null ? "0" : ((int)double.Parse(o[k]!.ToJsonString(), CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
        string S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        foreach (var o in items.OrderBy(o => S(o, "kind")).ThenBy(o => I(o, "y")).ThenBy(o => I(o, "x")))
        {
            string at = $"{I(o, "x")}\t{I(o, "y")}\t{I(o, "z")}";
            sb.Append(S(o, "kind") switch
            {
                "door" => $"door\t{at}\t{S(o, "type")}\t{S(o, "facing")}",
                "sign" => $"sign\t{at}\t{I(o, "id")}\t{I(o, "hue")}\t{S(o, "name")}",
                "item" => $"item\t{at}\t{S(o, "type")}\t{I(o, "id")}\t{I(o, "hue")}",
                "tele" => $"tele\t{at}\t{I(o, "dx")}\t{I(o, "dy")}\t{I(o, "dz")}\t{S(o, "dmap")}",
                "spawn" => $"spawn\t{at}\t{I(o, "count")}\t{I(o, "range")}\t{I(o, "min")}\t{I(o, "max")}\t{S(o, "creatures")}",
                "region" => $"region\t{I(o, "x")}\t{I(o, "y")}\t{I(o, "w")}\t{I(o, "h")}\t{(o["guarded"]?.ToJsonString() == "true" ? 1 : 0)}\t{S(o, "name")}",
                _ => "# ?",
            }).Append('\n');
        }
        return sb.ToString();
    }

    public static string ImportScript()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("servuo/ShardForgeImport.cs")!;
        return new StreamReader(s).ReadToEnd();
    }

    /// <summary>Everything a ServUO shard needs: the map files, the items and regions, the import script and steps.</summary>
    public static byte[] ServerPackage(MapInfo map, IEnumerable<(string Name, byte[] Bytes)> mapFiles, IReadOnlyList<JsonObject> items)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] bytes) { using var e = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); e.Write(bytes); }
            foreach (var (name, bytes) in mapFiles) Add("client/" + name, bytes);
            string slug = new string(map.Id.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray());
            Add($"ServUO/Data/ShardForge/{slug}.sfitems", Encoding.UTF8.GetBytes(SfItems(ServUOMap(map), map.Name, items)));
            Add("ServUO/Scripts/Custom/ShardForge/ShardForgeImport.cs", Encoding.UTF8.GetBytes(ImportScript()));
            int count(string k) => items.Count(o => o["kind"]?.ToJsonString() == $"\"{k}\"");
            Add("README.txt", Encoding.UTF8.GetBytes($"""
                ShardForge server package: {map.Name} ({ServUOMap(map)}, {map.Width}x{map.Height})

                client/   The map and statics files. Put them in the client folder ServUO reads (its DataPath)
                          and in every player's client (the ShardForge patcher does this).
                ServUO/   Copy into your ServUO folder: the import script and this map's items.

                Then start ServUO and, as an Administrator, type [SFImport
                It creates {count("door")} doors, {count("sign")} signs, {count("item")} working items, {count("tele")} teleporters
                and {count("spawn")} spawners, and registers {count("region")} regions (again at every start).
                [SFImport again replaces the earlier import; [SFClear removes it.

                """.Replace("\n", "\r\n")));
        }
        return ms.ToArray();
    }
}
