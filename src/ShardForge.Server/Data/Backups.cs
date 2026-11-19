using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ShardForge.Server.Audit;
using ShardForge.Server.Auth;

namespace ShardForge.Server.Data;

public sealed record BackupInfo(string Name, string At, string Kind, string? By, long Bytes, bool Maps, Dictionary<string, long> Counts);

public sealed class BackupException(string message) : Exception(message);

/// <summary>
/// Backups of everything the team made: the database (projects with every revision, Walk edits with history, server
/// items, the house library, people and the activity log), the server settings and the built maps. Automatic
/// every few hours, kept as: the newest few, one a day for a week, one a week for a month. Restoring brings the
/// world back (all of it, one map, the projects or the library) but never the people or the activity log: those
/// stay as they are now, so a restore can't hide what happened, and it is logged itself.
/// </summary>
public sealed class Backups(Db db, ServerOptions options, string dataPath, AuditLog audit, ILogger<Backups> log)
{
    /// <summary>World content restored by scope; users and audit are never restored.</summary>
    private static readonly (string Table, string Scope, string? MapColumn)[] Content =
    [
        ("projects", "projects", null), ("project_revisions", "projects", null),
        ("map_state", "maps", "map_id"), ("map_blocks", "maps", "map_id"), ("map_block_history", "maps", "map_id"),
        ("map_land", "maps", "map_id"), ("map_land_history", "maps", "map_id"), ("map_items", "maps", "map_id"), ("static_owner", "maps", "map_id"),
        ("prefabs", "library", null),
    ];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Dir
    {
        get
        {
            string d = string.IsNullOrWhiteSpace(options.Backups.Path) ? Path.Combine(dataPath, "backups")
                : Path.GetFullPath(Path.IsPathRooted(options.Backups.Path) ? options.Backups.Path : Path.Combine(dataPath, options.Backups.Path));
            Directory.CreateDirectory(d);
            return d;
        }
    }
    private string MapsDir => Path.Combine(dataPath, "maps");

    public List<BackupInfo> List()
    {
        var list = new List<BackupInfo>();
        foreach (var f in Directory.GetFiles(Dir, "shardforge-*.zip"))
        {
            try
            {
                using var z = ZipFile.OpenRead(f);
                var meta = z.GetEntry("backup.json");
                if (meta is null) continue;
                using var s = meta.Open();
                var m = JsonSerializer.Deserialize<Meta>(s)!;
                list.Add(new BackupInfo(Path.GetFileName(f), m.At, m.Kind, m.By, new FileInfo(f).Length, z.Entries.Any(e => e.FullName.StartsWith("maps/")), m.Counts));
            }
            catch (Exception e) { log.LogWarning(e, "Unreadable backup {File}", f); }
        }
        return list.OrderByDescending(b => b.At, StringComparer.Ordinal).ToList();
    }

    private sealed record Meta(string At, string Kind, string? By, string Version, Dictionary<string, long> Counts);

    /// <summary>Makes a backup now. Kind: auto, manual or before-restore.</summary>
    public async Task<BackupInfo> Create(string kind, User? by)
    {
        await _gate.WaitAsync();
        try { return CreateLocked(kind, by); }
        finally { _gate.Release(); }
    }

    private BackupInfo CreateLocked(string kind, User? by)
    {
        var now = DateTime.UtcNow;
        string name = $"shardforge-{now:yyyyMMdd-HHmmss}-{kind}.zip", path = Path.Combine(Dir, name), tmpDb = Path.Combine(Dir, $".{name}.db"), part = path + ".part";
        try
        {
            // A consistent copy of the live database, even while people are editing.
            using (var src = db.Open())
            using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmpDb, Pooling = false }.ToString()))
            {
                dst.Open();
                src.BackupDatabase(dst);
                using var j = dst.CreateCommand(); j.CommandText = "PRAGMA journal_mode = DELETE"; j.ExecuteNonQuery();
            }
            var counts = Counts(tmpDb);
            using (var fs = File.Create(part))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tmpDb, "shardforge.db", CompressionLevel.Optimal);
                string settings = Path.Combine(dataPath, "settings.json");
                if (File.Exists(settings)) zip.CreateEntryFromFile(settings, "settings.json");
                if (options.Backups.IncludeMaps && Directory.Exists(MapsDir))
                    foreach (var f in Directory.GetFiles(MapsDir, "*", SearchOption.AllDirectories))
                        zip.CreateEntryFromFile(f, "maps/" + Path.GetRelativePath(MapsDir, f).Replace('\\', '/'), CompressionLevel.Fastest);
                var meta = zip.CreateEntry("backup.json");
                using var ms = meta.Open();
                JsonSerializer.Serialize(ms, new Meta(now.ToString("O"), kind, by?.Username, typeof(Backups).Assembly.GetName().Version?.ToString(3) ?? "", counts));
            }
            File.Move(part, path, overwrite: true);
            var info = new BackupInfo(name, now.ToString("O"), kind, by?.Username, new FileInfo(path).Length, options.Backups.IncludeMaps && Directory.Exists(MapsDir), counts);
            audit.Write(by, "backup.create", $"{(kind == "auto" ? "automatic" : kind)} backup {name} ({info.Bytes / 1024.0 / 1024:0.0} MB)", username: by is null ? "(server)" : null);
            Prune();
            return info;
        }
        finally
        {
            foreach (var f in new[] { tmpDb, tmpDb + "-wal", tmpDb + "-shm", part }) if (File.Exists(f)) File.Delete(f);
        }
    }

    private static Dictionary<string, long> Counts(string dbFile)
    {
        var d = new Dictionary<string, long>();
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        c.Open();
        foreach (var (key, sql) in new[] { ("projects", "SELECT COUNT(*) FROM projects"), ("editedBlocks", "SELECT COUNT(*) FROM map_blocks"), ("landBlocks", "SELECT COUNT(*) FROM map_land"),
            ("serverItems", "SELECT COUNT(*) FROM map_items"), ("libraryDesigns", "SELECT COUNT(*) FROM prefabs"), ("people", "SELECT COUNT(*) FROM users"), ("logEntries", "SELECT COUNT(*) FROM audit") })
        {
            try { using var cmd = c.CreateCommand(); cmd.CommandText = sql; d[key] = (long)cmd.ExecuteScalar()!; } catch (SqliteException) { }
        }
        return d;
    }

    /// <summary>Keeps the newest few, then one a day for a week and one a week for a month; manual ones for a while.</summary>
    public int Prune()
    {
        var o = options.Backups;
        var all = List();
        var keep = new HashSet<string>();
        var auto = all.Where(b => b.Kind == "auto").ToList();
        foreach (var b in auto.Take(Math.Max(1, o.KeepRecent))) keep.Add(b.Name);
        DateTime At(BackupInfo b) => DateTime.Parse(b.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var now = DateTime.UtcNow;
        foreach (var g in auto.Where(b => At(b) > now.AddDays(-o.KeepDaily)).GroupBy(b => At(b).Date)) keep.Add(g.First().Name);
        foreach (var g in auto.Where(b => At(b) > now.AddDays(-7 * o.KeepWeekly)).GroupBy(b => ISOWeek.GetYear(At(b)) * 100 + ISOWeek.GetWeekOfYear(At(b)))) keep.Add(g.First().Name);
        foreach (var b in all.Where(b => b.Kind != "auto" && At(b) > now.AddDays(-o.KeepManualDays))) keep.Add(b.Name);
        int n = 0;
        foreach (var b in all.Where(b => !keep.Contains(b.Name)))
        {
            try { File.Delete(Path.Combine(Dir, b.Name)); n++; } catch (IOException e) { log.LogWarning(e, "Couldn't remove old backup {Name}", b.Name); }
        }
        return n;
    }

    public string? PathOf(string name)
    {
        if (name.Contains('/') || name.Contains('\\') || !name.StartsWith("shardforge-") || !name.EndsWith(".zip")) return null;
        string p = Path.Combine(Dir, name);
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// Brings back the world as it was in a backup: "all", "map:{id}", "projects" or "library". A before-restore
    /// backup is made first. People and the activity log are never touched.
    /// </summary>
    public async Task<(BackupInfo Before, string Summary)> Restore(string name, string scope, User by)
    {
        string file = PathOf(name) ?? throw new BackupException("No such backup.");
        string? mapId = scope.StartsWith("map:") ? scope[4..] : null;
        if (scope is not ("all" or "projects" or "library") && mapId is null) throw new BackupException("Restore all, one map (map:id), the projects or the library.");
        await _gate.WaitAsync();
        try
        {
            var before = CreateLocked("before-restore", by);
            string tmpDb = Path.Combine(Dir, $".restore-{Guid.NewGuid():N}.db");
            try
            {
                using (var z = ZipFile.OpenRead(file))
                {
                    (z.GetEntry("shardforge.db") ?? throw new BackupException("That backup has no database.")).ExtractToFile(tmpDb);
                    if (mapId is not null || scope == "all") RestoreMaps(z, mapId);
                }
                var touched = new List<string>();
                using var c = db.Open();
                using (var fk = c.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys = OFF"; fk.ExecuteNonQuery(); }
                using (var at = c.CreateCommand()) { at.CommandText = "ATTACH DATABASE $f AS b"; at.Parameters.AddWithValue("$f", tmpDb); at.ExecuteNonQuery(); }
                using (var t = c.BeginTransaction())
                {
                    foreach (var (table, group, mapCol) in Content)
                    {
                        if (scope != "all" && !(mapId is not null && group == "maps") && scope != group) continue;
                        var cols = Columns(c, t, "main", table).Intersect(Columns(c, t, "b", table)).ToList();
                        if (cols.Count == 0) continue;
                        string list = string.Join(", ", cols.Select(x => $"\"{x}\"")), where = mapId is not null ? $" WHERE {mapCol} = $m" : "";
                        foreach (var sql in new[] { $"DELETE FROM main.{table}{where}", $"INSERT INTO main.{table}({list}) SELECT {list} FROM b.{table}{where}" })
                        {
                            using var cmd = c.CreateCommand();
                            cmd.Transaction = t; cmd.CommandText = sql;
                            if (mapId is not null) cmd.Parameters.AddWithValue("$m", mapId);
                            cmd.ExecuteNonQuery();
                        }
                        touched.Add(table);
                    }
                    t.Commit();
                }
                using (var de = c.CreateCommand()) { de.CommandText = "DETACH DATABASE b"; de.ExecuteNonQuery(); }
                var meta = List().First(b => b.Name == name);
                string what = scope == "all" ? "the whole world" : mapId is not null ? $"the map {mapId}" : scope == "projects" ? "all projects" : "the house library";
                string summary = $"restored {what} to the backup of {DateTime.Parse(meta.At, CultureInfo.InvariantCulture).ToLocalTime():yyyy-MM-dd HH:mm} ({name}); the state before is saved as {before.Name}";
                audit.Write(by, "backup.restore", summary, target: mapId is not null ? $"map:{mapId}" : null, details: new { backup = name, scope, before = before.Name, tables = touched });
                return (before, summary);
            }
            finally { SqliteConnection.ClearAllPools(); foreach (var f in new[] { tmpDb, tmpDb + "-wal", tmpDb + "-shm" }) if (File.Exists(f)) File.Delete(f); }
        }
        finally { _gate.Release(); }
    }

    private static List<string> Columns(SqliteConnection c, SqliteTransaction t, string schema, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = $"SELECT name FROM {schema}.pragma_table_info('{table}')";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Puts built map folders back as they were (one map, or all of them).</summary>
    private void RestoreMaps(ZipArchive z, string? mapId)
    {
        var entries = z.Entries.Where(e => e.FullName.StartsWith("maps/") && e.Length > 0 && (mapId is null || e.FullName.StartsWith($"maps/{mapId}/"))).ToList();
        if (entries.Count == 0) return;
        foreach (var dir in mapId is null ? entries.Select(e => e.FullName.Split('/')[1]).Distinct() : [mapId])
        {
            string target = Path.Combine(MapsDir, dir);
            if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(MapsDir))) continue;
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
        foreach (var e in entries)
        {
            string target = Path.GetFullPath(Path.Combine(MapsDir, e.FullName["maps/".Length..]));
            if (!target.StartsWith(Path.GetFullPath(MapsDir))) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, overwrite: true);
        }
    }
}

/// <summary>Runs the automatic backups: at start when the last one is older than the interval, then on schedule.</summary>
public sealed class BackupScheduler(Backups backups, ServerOptions options, ILogger<BackupScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var every = TimeSpan.FromHours(Math.Max(0.25, options.Backups.EveryHours));
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var last = backups.List().Where(b => b.Kind == "auto").Select(b => DateTime.Parse(b.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)).DefaultIfEmpty(DateTime.MinValue).Max();
                if (DateTime.UtcNow - last >= every) { var b = await backups.Create("auto", null); log.LogInformation("Backup {Name}", b.Name); }
            }
            catch (Exception e) { log.LogError(e, "Automatic backup failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stop); } catch (TaskCanceledException) { return; }
        }
    }
}
