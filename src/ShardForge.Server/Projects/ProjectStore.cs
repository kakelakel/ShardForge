using System.Text;
using System.Text.Json;
using ShardForge.Core.Project;
using ShardForge.Server.Data;

namespace ShardForge.Server.Projects;

public sealed record ProjectSummary(long Id, string Name, int Rev, string Updated, string? UpdatedBy, int Width, int Height, long Size);
public sealed record ProjectRevision(int Rev, string Created, string? User, long Size);

public sealed class ProjectException(string message) : Exception(message);

/// <summary>Raised when someone saved a newer revision since the caller loaded theirs.</summary>
public sealed class ProjectConflictException(int latest, string? by, string at) : Exception("This project was saved by someone else meanwhile.")
{
    public int Latest { get; } = latest;
    public string? By { get; } = by;
    public string At { get; } = at;
}

/// <summary>Editor projects (.shardforge.json) with every saved revision kept.</summary>
public sealed class ProjectStore(Db db)
{
    public const int MaxProjectBytes = 64 * 1024 * 1024;

    /// <summary>Checks that the bytes are a ShardForge project and returns its parsed form.</summary>
    public static ShardProject Parse(byte[] json)
    {
        if (json.Length == 0 || json.Length > MaxProjectBytes) throw new ProjectException("Projects must be between 1 byte and 64 MB.");
        try
        {
            var p = JsonSerializer.Deserialize<ShardProject>(json) ?? throw new ProjectException("The project is empty.");
            p.Validate();
            return p;
        }
        catch (JsonException e) { throw new ProjectException("That isn't valid project JSON: " + e.Message); }
        catch (InvalidDataException e) { throw new ProjectException(e.Message); }
    }

    public IReadOnlyList<ProjectSummary> List()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT p.id, p.name, p.latest_rev, r.created, u.username, r.size, r.data
            FROM projects p JOIN project_revisions r ON r.project_id = p.id AND r.rev = p.latest_rev
            LEFT JOIN users u ON u.id = r.user_id
            WHERE p.deleted = 0 ORDER BY r.created DESC
            """;
        using var rd = cmd.ExecuteReader();
        var list = new List<ProjectSummary>();
        while (rd.Read())
        {
            var (w, h) = Dimensions((byte[])rd["data"]);
            list.Add(new ProjectSummary(rd.GetInt64(0), rd.GetString(1), rd.GetInt32(2), rd.GetString(3), rd.IsDBNull(4) ? null : rd.GetString(4), w, h, rd.GetInt64(5)));
        }
        return list;
    }

    public long Create(byte[] json, long userId)
    {
        var p = Parse(json);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO projects(name, created_by, created, latest_rev) VALUES ($n, $u, $t, 1) RETURNING id";
        cmd.Parameters.AddWithValue("$n", string.IsNullOrWhiteSpace(p.Name) ? "Untitled" : p.Name.Trim()[..Math.Min(p.Name.Trim().Length, 120)]);
        cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$t", Db.Now());
        long id = (long)cmd.ExecuteScalar()!;
        AddRevision(c, tx, id, 1, userId, json);
        tx.Commit();
        return id;
    }

    /// <summary>Saves a new revision if <paramref name="baseRev"/> is still the latest.</summary>
    public int Save(long id, int baseRev, byte[] json, long userId)
    {
        var p = Parse(json);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var (latest, deleted) = Latest(c, tx, id);
        if (latest == 0 || deleted) throw new ProjectException("No such project.");
        if (baseRev != latest)
        {
            var info = RevisionInfo(c, tx, id, latest);
            throw new ProjectConflictException(latest, info.User, info.Created);
        }
        int rev = latest + 1;
        AddRevision(c, tx, id, rev, userId, json);
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE projects SET latest_rev = $r, name = $n WHERE id = $id";
        cmd.Parameters.AddWithValue("$r", rev);
        cmd.Parameters.AddWithValue("$n", string.IsNullOrWhiteSpace(p.Name) ? "Untitled" : p.Name.Trim()[..Math.Min(p.Name.Trim().Length, 120)]);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return rev;
    }

    /// <summary>The project JSON at a revision (latest when null), or null.</summary>
    public (byte[] Json, int Rev)? Get(long id, int? rev = null)
    {
        using var c = db.Open();
        var (latest, deleted) = Latest(c, null, id);
        if (latest == 0 || deleted) return null;
        int want = rev ?? latest;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM project_revisions WHERE project_id = $id AND rev = $r";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$r", want);
        return cmd.ExecuteScalar() is byte[] data ? (data, want) : null;
    }

    public IReadOnlyList<ProjectRevision> History(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT r.rev, r.created, u.username, r.size FROM project_revisions r LEFT JOIN users u ON u.id = r.user_id
            WHERE r.project_id = $id ORDER BY r.rev DESC
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var rd = cmd.ExecuteReader();
        var list = new List<ProjectRevision>();
        while (rd.Read()) list.Add(new ProjectRevision(rd.GetInt32(0), rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.GetInt64(3)));
        return list;
    }

    public bool Delete(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE projects SET deleted = 1 WHERE id = $id AND deleted = 0";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static void AddRevision(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, long id, int rev, long userId, byte[] json)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO project_revisions(project_id, rev, user_id, created, size, data) VALUES ($id, $r, $u, $t, $s, $d)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$r", rev); cmd.Parameters.AddWithValue("$u", userId);
        cmd.Parameters.AddWithValue("$t", Db.Now()); cmd.Parameters.AddWithValue("$s", json.Length); cmd.Parameters.AddWithValue("$d", json);
        cmd.ExecuteNonQuery();
    }

    private static (int Latest, bool Deleted) Latest(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction? tx, long id)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT latest_rev, deleted FROM projects WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1) != 0) : (0, false);
    }

    private static (string Created, string? User) RevisionInfo(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, long id, int rev)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT r.created, u.username FROM project_revisions r LEFT JOIN users u ON u.id = r.user_id WHERE r.project_id = $id AND r.rev = $r";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$r", rev);
        using var rd = cmd.ExecuteReader();
        return rd.Read() ? (rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1)) : ("", null);
    }

    /// <summary>Reads width/height from the start of the JSON without parsing the (large) layers.</summary>
    private static (int, int) Dimensions(byte[] json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            int w = 0, h = 0, depth = 0;
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) { depth++; continue; }
                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) { depth--; continue; }
                if (depth != 1 || reader.TokenType != JsonTokenType.PropertyName) continue;
                string name = reader.GetString()!;
                if (name is "width" or "height") { reader.Read(); if (reader.TokenType == JsonTokenType.Number) { if (name == "width") w = reader.GetInt32(); else h = reader.GetInt32(); } }
                else reader.Skip();
                if (w > 0 && h > 0) break;
            }
            return (w, h);
        }
        catch (JsonException) { return (0, 0); }
    }
}
