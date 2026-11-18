using System.IO.Compression;
using ShardForge.Core.Map;
using ShardForge.Server.Data;

namespace ShardForge.Server.Maps;

/// <summary>A changed 8x8 block: its complete statics list in the 7-byte-per-static MUL encoding.</summary>
public sealed record BlockEdit(int Block, byte[] Data);

public sealed record BlockChange(string MapId, long Rev, long UserId, string Username, IReadOnlyList<BlockEdit> Blocks);

public sealed class BlockException(string message) : Exception(message);

/// <summary>
/// Shared statics edits. Each edit replaces a whole block, last writer wins; every version is kept in
/// map_block_history. The base map files on disk are never modified.
/// </summary>
public sealed class BlockStore(Db db)
{
    public const int MaxBlocksPerChange = 4096;
    public const int MaxStaticsPerBlock = 1024;

    public (long Rev, IReadOnlyList<BlockEdit> Blocks) GetAll(string mapId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT block, data FROM map_blocks WHERE map_id = $m ORDER BY block";
        cmd.Parameters.AddWithValue("$m", mapId);
        var list = new List<BlockEdit>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) list.Add(new BlockEdit(r.GetInt32(0), (byte[])r["data"]));
        return (Rev(c, mapId), list);
    }

    public static void Validate(MapInfo map, IReadOnlyList<BlockEdit> blocks)
    {
        if (blocks.Count is 0 or > MaxBlocksPerChange) throw new BlockException($"Send between 1 and {MaxBlocksPerChange} blocks at a time.");
        foreach (var b in blocks)
        {
            if (b.Block < 0 || b.Block >= map.BlockCount) throw new BlockException($"Block {b.Block} is outside this map.");
            if (b.Data.Length % FacetLoader.StaticEntrySize != 0) throw new BlockException($"Block {b.Block} has a broken statics list.");
            if (b.Data.Length / FacetLoader.StaticEntrySize > MaxStaticsPerBlock) throw new BlockException($"Block {b.Block} has too many statics.");
            for (int i = 0; i < b.Data.Length; i += FacetLoader.StaticEntrySize)
                if (b.Data[i + 2] > 7 || b.Data[i + 3] > 7) throw new BlockException($"Block {b.Block} has a static outside its 8x8 cell range.");
        }
    }

    public long Save(MapInfo map, long userId, IReadOnlyList<BlockEdit> blocks)
    {
        Validate(map, blocks);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        long rev = Rev(c, map.Id) + 1;
        string now = Db.Now();
        using (var st = c.CreateCommand())
        {
            st.Transaction = tx;
            st.CommandText = "INSERT INTO map_state(map_id, rev) VALUES ($m, $r) ON CONFLICT(map_id) DO UPDATE SET rev = $r";
            st.Parameters.AddWithValue("$m", map.Id); st.Parameters.AddWithValue("$r", rev);
            st.ExecuteNonQuery();
        }
        foreach (var b in blocks)
        {
            foreach (var sql in new[]
            {
                "INSERT INTO map_blocks(map_id, block, data, rev, user_id, updated) VALUES ($m, $b, $d, $r, $u, $t) ON CONFLICT(map_id, block) DO UPDATE SET data = $d, rev = $r, user_id = $u, updated = $t",
                "INSERT INTO map_block_history(map_id, block, data, rev, user_id, created) VALUES ($m, $b, $d, $r, $u, $t)",
            })
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("$m", map.Id); cmd.Parameters.AddWithValue("$b", b.Block);
                cmd.Parameters.AddWithValue("$d", b.Data); cmd.Parameters.AddWithValue("$r", rev);
                cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$t", now);
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return rev;
    }

    /// <summary>Drops the current edits for a map (history is kept). Used when a map is rebuilt.</summary>
    public int Clear(string mapId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM map_blocks WHERE map_id = $m";
        cmd.Parameters.AddWithValue("$m", mapId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Base statics from disk with the shared edits applied.</summary>
    public Facet Merged(MapInfo map)
    {
        var facet = new Facet(map.FacetInfo);
        string? idx = MapRegistry.FindFile(map.Dir, $"staidx{map.Facet}.mul"), sta = MapRegistry.FindFile(map.Dir, $"statics{map.Facet}.mul");
        if (idx is not null && sta is not null)
            FacetLoader.DecodeStatics(File.ReadAllBytes(idx), File.ReadAllBytes(sta), facet);
        foreach (var b in GetAll(map.Id).Blocks)
        {
            var tiles = new StaticTile[b.Data.Length / FacetLoader.StaticEntrySize];
            for (int i = 0; i < tiles.Length; i++)
            {
                var s = b.Data.AsSpan(i * FacetLoader.StaticEntrySize);
                tiles[i] = new StaticTile(BitConverter.ToUInt16(s), s[2], s[3], (sbyte)s[4], BitConverter.ToUInt16(s[5..]));
            }
            facet.Statics[b.Block] = tiles;
        }
        return facet;
    }

    /// <summary>Zip with staidx#.mul and statics#.mul including all shared edits.</summary>
    public byte[] ExportZip(MapInfo map)
    {
        var (index, data) = FacetWriter.EncodeStatics(Merged(map));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in new[] { ($"staidx{map.Facet}.mul", index), ($"statics{map.Facet}.mul", data) })
            {
                using var e = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                e.Write(bytes);
            }
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
