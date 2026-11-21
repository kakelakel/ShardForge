using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShardForge.Server.Maps;

/// <summary>
/// Cave mouths the world generator made lead into areas on another map (a dungeon facet). The generator only knows
/// the area's name; at build time this finds it in that map's layout, points the mouth's teleporters at its way in and
/// makes the teleporters back out, placed on the dungeon map.
/// </summary>
public static class DungeonLinks
{
    public sealed record Result(List<JsonObject> Items, Dictionary<string, List<JsonObject>> Returns, List<string> Unlinked);

    public static Result Resolve(MapInfo here, IEnumerable<JsonObject> items, MapRegistry maps, BlockStore blocks)
    {
        var outp = new List<JsonObject>();
        var returns = new Dictionary<string, List<JsonObject>>();
        var unlinked = new List<string>();
        var layouts = new Dictionary<string, (MapInfo Map, JsonArray Places, ShardForge.Core.Map.Facet Facet)?>();
        var backDone = new HashSet<string>();
        foreach (var o in items)
        {
            if (o["link"] is not JsonObject link) { outp.Add(o); continue; }
            string mid = link["map"]?.GetValue<string>() ?? "", place = link["place"]?.GetValue<string>() ?? "";
            if (!layouts.TryGetValue(mid, out var target))
            {
                target = null;
                if (maps.Get(mid) is { } m && MapRegistry.FindFile(m.Dir, "layout.json") is { } path
                    && JsonNode.Parse(File.ReadAllText(path))?["places"] is JsonArray places)
                    target = (m, places, blocks.Merged(m));
                layouts[mid] = target;
            }
            // By name; when several areas share it ("Cave"), the one at the position the editor saw.
            int? lx = link["x"] is JsonValue vx && vx.TryGetValue<int>(out var ix) ? ix : null, ly = link["y"] is JsonValue vy && vy.TryGetValue<int>(out var iy) ? iy : null;
            var p = target?.Places.OfType<JsonObject>().Where(q => q["name"]?.GetValue<string>() == place)
                .OrderBy(q => lx is null || ly is null ? 0 : Math.Abs(q["x"]!.GetValue<int>() - lx.Value) + Math.Abs(q["y"]!.GetValue<int>() - ly.Value)).FirstOrDefault();
            if (target is not { } t || p is null) { unlinked.Add(place.Length > 0 ? place : "?"); continue; }
            int px = p["x"]!.GetValue<int>(), py = p["y"]!.GetValue<int>();
            var (ax, ay) = Arrival(t.Facet, px, py);
            var c = (JsonObject)o.DeepClone();
            c.Remove("link"); var back = c["back"] as JsonArray; c.Remove("back");
            c["dx"] = ax; c["dy"] = ay; c["dz"] = (int)t.Facet.GetLand(ax, ay).Z;
            c["dmap"] = ItemStore.ServUOMap(t.Map); c["dmapId"] = t.Map.Id;
            outp.Add(c);
            if (back is { Count: 3 } && backDone.Add($"{mid}|{px},{py}"))
            {
                var list = returns.TryGetValue(mid, out var l) ? l : returns[mid] = [];
                list.Add(new JsonObject
                {
                    ["uid"] = $"link-{here.Id}-{list.Count:D4}", ["kind"] = "tele", ["x"] = px, ["y"] = py, ["z"] = (int)t.Facet.GetLand(px, py).Z,
                    ["dx"] = back[0]!.GetValue<int>(), ["dy"] = back[1]!.GetValue<int>(), ["dz"] = back[2]!.GetValue<int>(),
                    ["dmap"] = ItemStore.ServUOMap(here), ["dmapId"] = here.Id,
                });
            }
        }
        return new Result(outp, returns, unlinked);
    }

    /// <summary>A floor tile a couple of steps in from the way in, so arriving doesn't land on the way out.</summary>
    private static (int, int) Arrival(ShardForge.Core.Map.Facet f, int x, int y)
    {
        int z0 = f.GetLand(x, y).Z;
        // Floor: not the void, and level with the way in (cave walls are raised land).
        bool Floor(int ax, int ay) => ax > 0 && ay > 0 && ax < f.Width - 1 && ay < f.Height - 1 && f.GetLand(ax, ay).TileId is not (0x244 or 0x2)
            && Math.Abs(f.GetLand(ax, ay).Z - z0) <= 3;
        for (int r = 2; r <= 4; r++)
        {
            (int, int)? best = null; int bestN = -1;
            for (int dy = -r; dy <= r; dy++) for (int dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r || !Floor(x + dx, y + dy)) continue;
                int n = 0; for (int k = -1; k <= 1; k++) for (int j = -1; j <= 1; j++) if (Floor(x + dx + j, y + dy + k)) n++;
                if (n > bestN) { bestN = n; best = (x + dx, y + dy); }
            }
            if (best is { } b && bestN >= 6) return b;
        }
        return (x, y);
    }
}
