using System.IO.Compression;
using Microsoft.Data.Sqlite;
using ShardForge.Core.Map;
using ShardForge.Server.Auth;
using ShardForge.Server.Data;

namespace ShardForge.Server.Maps;

/// <summary>A changed 8x8 block. Statics: its complete list in the 7-byte-per-static MUL encoding.
/// Land: its 64 cells (row by row) of ushort tile id and sbyte altitude, 192 bytes.</summary>
public sealed record BlockEdit(int Block, byte[] Data);

/// <param name="Cid">The live connection (browser tab) that made the change, so only that tab skips the echo.</param>
public sealed record BlockChange(string MapId, long Rev, long UserId, string Username, IReadOnlyList<BlockEdit> Blocks, IReadOnlyList<BlockEdit> Land, string? Cid = null);

public sealed class BlockException(string message) : Exception(message);

/// <summary>Not allowed at this user level (answered with 403).</summary>
public sealed class PermissionException(string message) : Exception(message);

/// <summary>What a save changed, for the activity log: statics added and removed, land blocks, and where.</summary>
public sealed record SaveResult(long Rev, int Added, int Removed, int StaticBlocks, int LandBlocks, int? X, int? Y, List<object> Samples);

/// <summary>What happened to a map's shared edits when it was rebuilt.</summary>
public sealed record RebaseResult(int StaticBlocks, int LandBlocks, int DroppedStatics);

/// <summary>
/// Shared edits made in Walk: statics and land, a whole 8x8 block at a time, last writer wins; every version is
/// kept in history. The base map files on disk are never modified. When a project is rebuilt, the edits are
/// rebased onto the new map (<see cref="Rebase"/>) so building and decorating can go back and forth.
/// </summary>
public sealed class BlockStore(Db db)
{
    public const int MaxBlocksPerChange = 4096;
    public const int MaxStaticsPerBlock = 1024;
    public const int LandBlockBytes = 64 * 3;

    public (long Rev, IReadOnlyList<BlockEdit> Blocks) GetAll(string mapId) => Get(mapId, "map_blocks");
    public (long Rev, IReadOnlyList<BlockEdit> Blocks) GetLand(string mapId) => Get(mapId, "map_land");

    private (long Rev, IReadOnlyList<BlockEdit> Blocks) Get(string mapId, string table)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT block, data FROM {table} WHERE map_id = $m ORDER BY block";
        cmd.Parameters.AddWithValue("$m", mapId);
        var list = new List<BlockEdit>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) list.Add(new BlockEdit(r.GetInt32(0), (byte[])r["data"]));
        return (Rev(c, mapId), list);
    }

    public static void Validate(MapInfo map, IReadOnlyList<BlockEdit> blocks, IReadOnlyList<BlockEdit>? land = null)
    {
        land ??= [];
        if (blocks.Count + land.Count is 0 || blocks.Count + land.Count > MaxBlocksPerChange) throw new BlockException($"Send between 1 and {MaxBlocksPerChange} blocks at a time.");
        foreach (var b in blocks)
        {
            if (b.Block < 0 || b.Block >= map.BlockCount) throw new BlockException($"Block {b.Block} is outside this map.");
            if (b.Data.Length % FacetLoader.StaticEntrySize != 0) throw new BlockException($"Block {b.Block} has a broken statics list.");
            if (b.Data.Length / FacetLoader.StaticEntrySize > MaxStaticsPerBlock) throw new BlockException($"Block {b.Block} has too many statics.");
            for (int i = 0; i < b.Data.Length; i += FacetLoader.StaticEntrySize)
                if (b.Data[i + 2] > 7 || b.Data[i + 3] > 7) throw new BlockException($"Block {b.Block} has a static outside its 8x8 cell range.");
        }
        foreach (var b in land)
        {
            if (b.Block < 0 || b.Block >= map.BlockCount) throw new BlockException($"Land block {b.Block} is outside this map.");
            if (b.Data.Length != LandBlockBytes) throw new BlockException($"Land block {b.Block} must be {LandBlockBytes} bytes.");
        }
    }

    public long Save(MapInfo map, long userId, IReadOnlyList<BlockEdit> blocks, IReadOnlyList<BlockEdit>? land = null)
    {
        land ??= [];
        Validate(map, blocks, land);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        long rev = NextRev(c, tx, map.Id);
        Write(c, tx, map.Id, "map_blocks", blocks, rev, userId);
        Write(c, tx, map.Id, "map_land", land, rev, userId);
        tx.Commit();
        return rev;
    }

    /// <summary>
    /// Saves with the user's level enforced: decorators may add statics, and remove or change only statics they
    /// placed themselves; land needs a worldbuilder. Who placed what is tracked in static_owner.
    /// </summary>
    public SaveResult Save(MapInfo map, User user, IReadOnlyList<BlockEdit> blocks, IReadOnlyList<BlockEdit>? land = null)
    {
        land ??= [];
        Validate(map, blocks, land);
        if (land.Count > 0 && user.Role < Role.Worldbuilder)
            throw new PermissionException("Decorators can't change the land. Ask a worldbuilder.");
        int bh = map.FacetInfo.BlockHeight, added = 0, removed = 0, foreign = 0;
        int? ax = null, ay = null;
        var samples = new List<object>();
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var diffs = new List<(int Block, List<StaticTile> Add, List<StaticTile> Del)>();
        foreach (var b in blocks)
        {
            var before = Current(c, tx, map, b.Block);
            var after = DecodeStatics(b.Data);
            var del = Subtract(before, after);
            var add = Subtract(after, before);
            if (del.Count == 0 && add.Count == 0) continue;
            int bx = b.Block / bh * 8, by = b.Block % bh * 8;
            if (user.Role < Role.Worldbuilder && del.Count > 0)
            {
                var mine = Owned(c, tx, map.Id, bx, by, user.Id);
                foreach (var s in del)
                {
                    var key = (bx + s.X, by + s.Y, (int)s.Z, (int)s.TileId, (int)s.Hue);
                    if (mine.TryGetValue(key, out int n) && n > 0) mine[key] = n - 1; else foreign++;
                }
            }
            diffs.Add((b.Block, add, del));
            var first = add.Count > 0 ? add[0] : del[0];
            ax ??= bx + first.X; ay ??= by + first.Y;
        }
        if (foreign > 0)
            throw new PermissionException($"Decorators can only remove or change what they placed themselves: {foreign} of these statics {(foreign == 1 ? "was" : "were")} placed by someone else or belong to the map. Ask a worldbuilder.");
        long rev = NextRev(c, tx, map.Id);
        Write(c, tx, map.Id, "map_blocks", blocks, rev, user.Id);
        Write(c, tx, map.Id, "map_land", land, rev, user.Id);
        foreach (var (block, add, del) in diffs)
        {
            int bx = block / bh * 8, by = block % bh * 8;
            foreach (var s in del)
            {
                Exec(c, tx, "DELETE FROM static_owner WHERE rowid = (SELECT rowid FROM static_owner WHERE map_id = $m AND x = $x AND y = $y AND z = $z AND tile = $t AND hue = $h ORDER BY user_id = $u DESC LIMIT 1)",
                    map.Id, bx + s.X, by + s.Y, s.Z, s.TileId, s.Hue, user.Id);
                if (samples.Count < 200) samples.Add(new { op = "removed", id = s.TileId, x = bx + s.X, y = by + s.Y, z = s.Z, hue = s.Hue });
            }
            foreach (var s in add)
            {
                Exec(c, tx, "INSERT INTO static_owner(map_id, x, y, z, tile, hue, user_id) VALUES ($m, $x, $y, $z, $t, $h, $u)",
                    map.Id, bx + s.X, by + s.Y, s.Z, s.TileId, s.Hue, user.Id);
                if (samples.Count < 200) samples.Add(new { op = "added", id = s.TileId, x = bx + s.X, y = by + s.Y, z = s.Z, hue = s.Hue });
            }
            added += add.Count; removed += del.Count;
        }
        if (ax is null && land.Count > 0) { int b0 = land[0].Block; ax = b0 / bh * 8 + 4; ay = b0 % bh * 8 + 4; }
        tx.Commit();
        return new SaveResult(rev, added, removed, diffs.Count, land.Count, ax, ay, samples);
    }

    private static void Exec(SqliteConnection c, SqliteTransaction tx, string sql, string map, int x, int y, int z, int tile, int hue, long user)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx; cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$m", map); cmd.Parameters.AddWithValue("$x", x); cmd.Parameters.AddWithValue("$y", y);
        cmd.Parameters.AddWithValue("$z", z); cmd.Parameters.AddWithValue("$t", tile); cmd.Parameters.AddWithValue("$h", hue); cmd.Parameters.AddWithValue("$u", user);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The statics a user placed in one 8x8 block, counted.</summary>
    private static Dictionary<(int, int, int, int, int), int> Owned(SqliteConnection c, SqliteTransaction tx, string mapId, int bx, int by, long userId)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT x, y, z, tile, hue FROM static_owner WHERE map_id = $m AND x >= $x0 AND x < $x0 + 8 AND y >= $y0 AND y < $y0 + 8 AND user_id = $u";
        cmd.Parameters.AddWithValue("$m", mapId); cmd.Parameters.AddWithValue("$x0", bx); cmd.Parameters.AddWithValue("$y0", by); cmd.Parameters.AddWithValue("$u", userId);
        var d = new Dictionary<(int, int, int, int, int), int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) { var k = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)); d[k] = d.GetValueOrDefault(k) + 1; }
        return d;
    }

    /// <summary>Statics this user placed on a map (for Walk, so decorators see what they may remove).</summary>
    public List<int[]> OwnedBy(string mapId, long userId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT x, y, z, tile, hue FROM static_owner WHERE map_id = $m AND user_id = $u";
        cmd.Parameters.AddWithValue("$m", mapId); cmd.Parameters.AddWithValue("$u", userId);
        var list = new List<int[]>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add([r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4)]);
        return list;
    }

    /// <summary>A block as it is now: the shared edit if there is one, else the map file's.</summary>
    private static List<StaticTile> Current(SqliteConnection c, SqliteTransaction tx, MapInfo map, int block)
    {
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT data FROM map_blocks WHERE map_id = $m AND block = $b";
            cmd.Parameters.AddWithValue("$m", map.Id); cmd.Parameters.AddWithValue("$b", block);
            if (cmd.ExecuteScalar() is byte[] data) return [.. DecodeStatics(data)];
        }
        string? idx = MapRegistry.FindFile(map.Dir, $"staidx{map.Facet}.mul"), mul = MapRegistry.FindFile(map.Dir, $"statics{map.Facet}.mul");
        if (idx is null || mul is null) return [];
        using var fi = File.OpenRead(idx);
        if ((long)block * 12 + 12 > fi.Length) return [];
        var e = new byte[12];
        fi.Position = (long)block * 12; fi.ReadExactly(e);
        int off = BitConverter.ToInt32(e, 0), len = BitConverter.ToInt32(e, 4);
        if (off < 0 || len <= 0) return [];
        using var fm = File.OpenRead(mul);
        if (off + (long)len > fm.Length) return [];
        var buf = new byte[len];
        fm.Position = off; fm.ReadExactly(buf);
        return [.. DecodeStatics(buf)];
    }

    private static long NextRev(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string mapId)
    {
        long rev = Rev(c, mapId) + 1;
        using var st = c.CreateCommand();
        st.Transaction = tx;
        st.CommandText = "INSERT INTO map_state(map_id, rev) VALUES ($m, $r) ON CONFLICT(map_id) DO UPDATE SET rev = $r";
        st.Parameters.AddWithValue("$m", mapId); st.Parameters.AddWithValue("$r", rev);
        st.ExecuteNonQuery();
        return rev;
    }

    private static void Write(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string mapId, string table,
        IReadOnlyList<BlockEdit> blocks, long rev, long? userId)
    {
        string now = Db.Now();
        string history = table == "map_blocks" ? "map_block_history" : "map_land_history";
        foreach (var b in blocks)
            foreach (var sql in new[]
            {
                $"INSERT INTO {table}(map_id, block, data, rev, user_id, updated) VALUES ($m, $b, $d, $r, $u, $t) ON CONFLICT(map_id, block) DO UPDATE SET data = $d, rev = $r, user_id = $u, updated = $t",
                $"INSERT INTO {history}(map_id, block, data, rev, user_id, created) VALUES ($m, $b, $d, $r, $u, $t)",
            })
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$m", mapId); cmd.Parameters.AddWithValue("$b", b.Block);
                cmd.Parameters.AddWithValue("$d", b.Data); cmd.Parameters.AddWithValue("$r", rev);
                cmd.Parameters.AddWithValue("$u", (object?)userId ?? DBNull.Value); cmd.Parameters.AddWithValue("$t", now);
                cmd.ExecuteNonQuery();
            }
    }

    /// <summary>Drops the current edits for a map (history is kept).</summary>
    public int Clear(string mapId)
    {
        using var c = db.Open();
        int n = 0;
        foreach (var table in new[] { "map_blocks", "map_land" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE map_id = $m";
            cmd.Parameters.AddWithValue("$m", mapId);
            n += cmd.ExecuteNonQuery();
        }
        return n;
    }

    /// <summary>
    /// Keeps the Walk edits when a map is rebuilt. For every edited statics block, what was added and removed
    /// relative to the old map is applied to the new one (a removed static that is gone anyway is ignored); for
    /// every edited land block, the cells changed in Walk keep their tile and altitude and the rest come from the
    /// new map. Edits that end up identical to the new map are dropped.
    /// </summary>
    public RebaseResult Rebase(string mapId, Facet oldBase, Facet newBase, long? userId)
    {
        var (_, statics) = GetAll(mapId);
        var (_, land) = GetLand(mapId);
        if (statics.Count == 0 && land.Count == 0) return new(0, 0, 0);
        int blocks = Math.Min(oldBase.Info.BlockCount, newBase.Info.BlockCount);
        var newStatics = new List<BlockEdit>();
        var newLand = new List<BlockEdit>();
        int dropped = 0;

        foreach (var e in statics)
        {
            if (e.Block >= blocks) continue;
            var edited = DecodeStatics(e.Data);
            var before = oldBase.Statics[e.Block] ?? [];
            var after = newBase.Statics[e.Block] ?? [];
            var added = Subtract(edited, before);
            var removed = Subtract(before, edited);
            var result = after.ToList();
            foreach (var s in removed) if (!result.Remove(s)) dropped++;
            result.AddRange(added);
            if (!result.SequenceEqual(after)) newStatics.Add(new BlockEdit(e.Block, EncodeStatics(result)));
        }
        foreach (var e in land)
        {
            if (e.Block >= blocks) continue;
            int bx = e.Block / newBase.Info.BlockHeight, by = e.Block % newBase.Info.BlockHeight;
            var data = new byte[LandBlockBytes];
            bool differs = false;
            for (int c = 0; c < 64; c++)
            {
                int x = bx * 8 + (c & 7), y = by * 8 + (c >> 3);
                var mine = new LandCell(BitConverter.ToUInt16(e.Data, c * 3), (sbyte)e.Data[c * 3 + 2]);
                var cell = mine != oldBase.GetLand(x, y) ? mine : newBase.GetLand(x, y);
                BitConverter.TryWriteBytes(data.AsSpan(c * 3), cell.TileId);
                data[c * 3 + 2] = (byte)cell.Z;
                differs |= cell != newBase.GetLand(x, y);
            }
            if (differs) newLand.Add(new BlockEdit(e.Block, data));
        }

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var table in new[] { "map_blocks", "map_land" })
        {
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {table} WHERE map_id = $m";
            del.Parameters.AddWithValue("$m", mapId);
            del.ExecuteNonQuery();
        }
        long rev = NextRev(conn, tx, mapId);
        Write(conn, tx, mapId, "map_blocks", newStatics, rev, userId);
        Write(conn, tx, mapId, "map_land", newLand, rev, userId);
        tx.Commit();
        return new(newStatics.Count, newLand.Count, dropped);
    }

    /// <summary>Items of <paramref name="a"/> not in <paramref name="b"/>, counting duplicates.</summary>
    private static List<StaticTile> Subtract(IEnumerable<StaticTile> a, IEnumerable<StaticTile> b)
    {
        var left = b.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        var outList = new List<StaticTile>();
        foreach (var s in a)
            if (left.TryGetValue(s, out int n) && n > 0) left[s] = n - 1;
            else outList.Add(s);
        return outList;
    }

    public static StaticTile[] DecodeStatics(byte[] data)
    {
        var tiles = new StaticTile[data.Length / FacetLoader.StaticEntrySize];
        for (int i = 0; i < tiles.Length; i++)
        {
            var s = data.AsSpan(i * FacetLoader.StaticEntrySize);
            tiles[i] = new StaticTile(BitConverter.ToUInt16(s), s[2], s[3], (sbyte)s[4], BitConverter.ToUInt16(s[5..]));
        }
        return tiles;
    }

    public static byte[] EncodeStatics(IReadOnlyList<StaticTile> tiles)
    {
        var data = new byte[tiles.Count * FacetLoader.StaticEntrySize];
        for (int i = 0; i < tiles.Count; i++)
        {
            var s = data.AsSpan(i * FacetLoader.StaticEntrySize);
            BitConverter.TryWriteBytes(s, tiles[i].TileId); s[2] = tiles[i].X; s[3] = tiles[i].Y; s[4] = (byte)tiles[i].Z;
            BitConverter.TryWriteBytes(s[5..], tiles[i].Hue);
        }
        return data;
    }

    /// <summary>The map from disk with the shared edits (land and statics) applied.</summary>
    public Facet Merged(MapInfo map)
    {
        var facet = FacetLoader.Load(map.Dir, map.Facet, map.FacetInfo);
        foreach (var b in GetAll(map.Id).Blocks)
            if (b.Block < facet.Statics.Length) facet.Statics[b.Block] = DecodeStatics(b.Data);
        foreach (var b in GetLand(map.Id).Blocks)
        {
            if (b.Block >= map.BlockCount) continue;
            int bx = b.Block / map.FacetInfo.BlockHeight, by = b.Block % map.FacetInfo.BlockHeight;
            for (int c = 0; c < 64; c++)
                facet.SetLand(bx * 8 + (c & 7), by * 8 + (c >> 3), new LandCell(BitConverter.ToUInt16(b.Data, c * 3), (sbyte)b.Data[c * 3 + 2]));
        }
        return facet;
    }

    public bool HasLandEdits(string mapId) => GetLand(mapId).Blocks.Count > 0;

    /// <summary>The map files with all shared edits: statics always, land too when land was edited.</summary>
    public IReadOnlyList<(string Name, byte[] Bytes)> MergedFiles(MapInfo map)
    {
        var facet = Merged(map);
        var (index, data) = FacetWriter.EncodeStatics(facet);
        var files = new List<(string, byte[])> { ($"staidx{map.Facet}.mul", index), ($"statics{map.Facet}.mul", data) };
        if (HasLandEdits(map.Id))
        {
            string tmp = Directory.CreateTempSubdirectory("sf-land-").FullName;
            try
            {
                foreach (var path in FacetWriter.WriteAll(facet, tmp, map.Facet))
                    if (Path.GetFileName(path).StartsWith("map", StringComparison.OrdinalIgnoreCase)) files.Add((Path.GetFileName(path), File.ReadAllBytes(path)));
            }
            finally { Directory.Delete(tmp, true); }
        }
        else if (map.Source == "built")
        {
            // A map built from a project has its own land: it goes along even when nobody edited it in Walk.
            foreach (var path in Directory.GetFiles(map.Dir))
            {
                string n = Path.GetFileName(path);
                if (n.StartsWith($"map{map.Facet}", StringComparison.OrdinalIgnoreCase) && (n.EndsWith(".uop", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".mul", StringComparison.OrdinalIgnoreCase)))
                    files.Add((n, File.ReadAllBytes(path)));
            }
        }
        return files;
    }

    /// <summary>Zip of <see cref="MergedFiles"/>.</summary>
    public byte[] ExportZip(MapInfo map)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, bytes) in MergedFiles(map))
            {
                using var e = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                e.Write(bytes);
            }
        return ms.ToArray();
    }

    private static long Rev(Microsoft.Data.Sqlite.SqliteConnection c, string mapId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rev FROM map_state WHERE map_id = $m";
        cmd.Parameters.AddWithValue("$m", mapId);
        return cmd.ExecuteScalar() is long r ? r : 0;
    }
}
