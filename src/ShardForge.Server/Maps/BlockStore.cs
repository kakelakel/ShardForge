using System.IO.Compression;
using ShardForge.Core.Map;
using ShardForge.Server.Data;

namespace ShardForge.Server.Maps;

/// <summary>A changed 8x8 block. Statics: its complete list in the 7-byte-per-static MUL encoding.
/// Land: its 64 cells (row by row) of ushort tile id and sbyte altitude, 192 bytes.</summary>
public sealed record BlockEdit(int Block, byte[] Data);

/// <param name="Cid">The live connection (browser tab) that made the change, so only that tab skips the echo.</param>
public sealed record BlockChange(string MapId, long Rev, long UserId, string Username, IReadOnlyList<BlockEdit> Blocks, IReadOnlyList<BlockEdit> Land, string? Cid = null);

public sealed class BlockException(string message) : Exception(message);

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
