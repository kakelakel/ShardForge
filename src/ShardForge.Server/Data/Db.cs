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
            CREATE TABLE IF NOT EXISTS audit(
              id INTEGER PRIMARY KEY,
              at TEXT NOT NULL,
              user_id INTEGER,
              username TEXT NOT NULL,
              role TEXT NOT NULL,
              action TEXT NOT NULL,
              target TEXT,
              x INTEGER,
              y INTEGER,
              summary TEXT NOT NULL,
              details TEXT,
              ip TEXT);
            CREATE INDEX IF NOT EXISTS ix_audit_user ON audit(user_id, id);
            CREATE INDEX IF NOT EXISTS ix_audit_action ON audit(action, id);
            CREATE INDEX IF NOT EXISTS ix_audit_target ON audit(target, id);
            CREATE TABLE IF NOT EXISTS static_owner(
              map_id TEXT NOT NULL,
              x INTEGER NOT NULL,
              y INTEGER NOT NULL,
              z INTEGER NOT NULL,
              tile INTEGER NOT NULL,
              hue INTEGER NOT NULL,
              user_id INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_static_owner ON static_owner(map_id, x, y);
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
        Upgrade(c);
    }

    /// <summary>Step-by-step changes to existing databases, tracked in user_version.</summary>
    private static void Upgrade(SqliteConnection c)
    {
        long version;
        using (var v = c.CreateCommand()) { v.CommandText = "PRAGMA user_version"; version = (long)v.ExecuteScalar()!; }
        void Run(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        if (version < 1)
        {
            // Four levels instead of three: Builder (2) could remove things, so it becomes Worldbuilder (3); Admin 3 -> 4.
            using var t = c.BeginTransaction();
            using (var cmd = c.CreateCommand()) { cmd.Transaction = t; cmd.CommandText = "UPDATE users SET role = CASE role WHEN 3 THEN 4 WHEN 2 THEN 3 ELSE role END"; cmd.ExecuteNonQuery(); }
            using (var cmd = c.CreateCommand()) { cmd.Transaction = t; cmd.CommandText = "PRAGMA user_version = 1"; cmd.ExecuteNonQuery(); }
            t.Commit();
        }
        if (version < 2)
        {
            bool has = false;
            using (var info = c.CreateCommand()) { info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('map_items') WHERE name = 'created_by'"; has = (long)info.ExecuteScalar()! > 0; }
            if (!has) Run("ALTER TABLE map_items ADD COLUMN created_by INTEGER REFERENCES users(id)");
            Run("UPDATE map_items SET created_by = updated_by WHERE created_by IS NULL");
            Run("PRAGMA user_version = 2");
        }
    }

    public static string Now() => DateTime.UtcNow.ToString("O");
}
