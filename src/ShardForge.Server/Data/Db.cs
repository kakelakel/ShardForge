using Microsoft.Data.Sqlite;

namespace ShardForge.Server.Data;

/// <summary>SQLite storage. One connection per operation; the schema is created on startup.</summary>
public sealed class Db
{
    private readonly string _connectionString;

    public Db(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataPath, "shardforge.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using (var wal = c.CreateCommand()) { wal.CommandText = "PRAGMA journal_mode = WAL;"; wal.ExecuteNonQuery(); }
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users(
              id INTEGER PRIMARY KEY,
              username TEXT NOT NULL UNIQUE COLLATE NOCASE,
              password_hash TEXT NOT NULL,
              role INTEGER NOT NULL,
              disabled INTEGER NOT NULL DEFAULT 0,
              security_stamp TEXT NOT NULL,
              created TEXT NOT NULL,
              last_login TEXT);
            CREATE TABLE IF NOT EXISTS projects(
              id INTEGER PRIMARY KEY,
              name TEXT NOT NULL,
              created_by INTEGER REFERENCES users(id),
              created TEXT NOT NULL,
              latest_rev INTEGER NOT NULL DEFAULT 0,
              deleted INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS project_revisions(
              project_id INTEGER NOT NULL REFERENCES projects(id),
              rev INTEGER NOT NULL,
              user_id INTEGER REFERENCES users(id),
              created TEXT NOT NULL,
              size INTEGER NOT NULL,
              data BLOB NOT NULL,
              PRIMARY KEY(project_id, rev));
            CREATE TABLE IF NOT EXISTS map_state(
              map_id TEXT PRIMARY KEY,
              rev INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS map_blocks(
              map_id TEXT NOT NULL,
              block INTEGER NOT NULL,
              data BLOB NOT NULL,
              rev INTEGER NOT NULL,
              user_id INTEGER REFERENCES users(id),
              updated TEXT NOT NULL,
              PRIMARY KEY(map_id, block));
            CREATE TABLE IF NOT EXISTS map_block_history(
              id INTEGER PRIMARY KEY,
              map_id TEXT NOT NULL,
              block INTEGER NOT NULL,
              data BLOB NOT NULL,
              rev INTEGER NOT NULL,
              user_id INTEGER REFERENCES users(id),
              created TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_block_history ON map_block_history(map_id, rev);
            CREATE TABLE IF NOT EXISTS map_land(
              map_id TEXT NOT NULL,
              block INTEGER NOT NULL,
              data BLOB NOT NULL,
              rev INTEGER NOT NULL,
              user_id INTEGER REFERENCES users(id),
              updated TEXT NOT NULL,
              PRIMARY KEY(map_id, block));
            CREATE TABLE IF NOT EXISTS map_land_history(
              id INTEGER PRIMARY KEY,
              map_id TEXT NOT NULL,
              block INTEGER NOT NULL,
              data BLOB NOT NULL,
              rev INTEGER NOT NULL,
              user_id INTEGER REFERENCES users(id),
              created TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_land_history ON map_land_history(map_id, rev);
            CREATE TABLE IF NOT EXISTS map_items(
              map_id TEXT NOT NULL,
              uid TEXT NOT NULL,
              data TEXT NOT NULL,
              updated_by INTEGER REFERENCES users(id),
              updated TEXT NOT NULL,
              PRIMARY KEY(map_id, uid));
            CREATE TABLE IF NOT EXISTS prefabs(
              id INTEGER PRIMARY KEY,
              name TEXT NOT NULL,
              data TEXT NOT NULL,
              created_by INTEGER REFERENCES users(id),
              updated_by INTEGER REFERENCES users(id),
              created TEXT NOT NULL,
              updated TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public static string Now() => DateTime.UtcNow.ToString("O");
}
