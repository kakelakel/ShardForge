using System.Text.Json;
using ShardForge.Server.Auth;
using ShardForge.Server.Data;

namespace ShardForge.Server.Audit;

public sealed record AuditEntry(long Id, string At, long? UserId, string Username, string Role, string Action, string? Target,
    int? X, int? Y, string Summary, JsonElement? Details, string? Ip);

/// <summary>
/// Who did what, when and where: every change, sign-in, save and export, per person. Admins read it on the Admin
/// page; entries with a place link straight to it in Walk. Nothing is ever deleted from it by the app.
/// </summary>
public sealed class AuditLog(Db db)
{
    public void Write(User? user, string action, string summary, string? target = null, int? x = null, int? y = null,
        object? details = null, string? ip = null, string? username = null)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO audit(at, user_id, username, role, action, target, x, y, summary, details, ip) VALUES ($at, $u, $n, $r, $a, $t, $x, $y, $s, $d, $ip)";
        cmd.Parameters.AddWithValue("$at", Db.Now());
        cmd.Parameters.AddWithValue("$u", (object?)user?.Id ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$n", user?.Username ?? username ?? "(nobody)");
        cmd.Parameters.AddWithValue("$r", user?.Role.ToString() ?? "");
        cmd.Parameters.AddWithValue("$a", action);
        cmd.Parameters.AddWithValue("$t", (object?)target ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$x", (object?)x ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$y", (object?)y ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", summary.Length > 500 ? summary[..500] : summary);
        cmd.Parameters.AddWithValue("$d", details is null ? DBNull.Value : JsonSerializer.Serialize(details));
        cmd.Parameters.AddWithValue("$ip", (object?)ip ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Newest first, filtered; <paramref name="before"/> pages back by id.</summary>
    public List<AuditEntry> Query(long? userId = null, string? action = null, string? target = null, string? since = null,
        string? until = null, string? text = null, long? before = null, int limit = 100)
    {
        var where = new List<string>();
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        if (userId is not null) { where.Add("user_id = $u"); cmd.Parameters.AddWithValue("$u", userId); }
        if (!string.IsNullOrEmpty(action)) { where.Add("(action = $a OR action LIKE $ap)"); cmd.Parameters.AddWithValue("$a", action); cmd.Parameters.AddWithValue("$ap", action + ".%"); }
        if (!string.IsNullOrEmpty(target)) { where.Add("target = $t"); cmd.Parameters.AddWithValue("$t", target); }
        if (!string.IsNullOrEmpty(since)) { where.Add("at >= $since"); cmd.Parameters.AddWithValue("$since", since); }
        if (!string.IsNullOrEmpty(until)) { where.Add("at < $until"); cmd.Parameters.AddWithValue("$until", until); }
        if (!string.IsNullOrEmpty(text)) { where.Add("(summary LIKE $q OR username LIKE $q OR details LIKE $q)"); cmd.Parameters.AddWithValue("$q", "%" + text + "%"); }
        if (before is not null) { where.Add("id < $b"); cmd.Parameters.AddWithValue("$b", before); }
        cmd.CommandText = $"SELECT id, at, user_id, username, role, action, target, x, y, summary, details, ip FROM audit {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")} ORDER BY id DESC LIMIT {Math.Clamp(limit, 1, 1000)}";
        var list = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AuditEntry(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8), r.GetString(9),
                r.IsDBNull(10) ? null : JsonDocument.Parse(r.GetString(10)).RootElement.Clone(), r.IsDBNull(11) ? null : r.GetString(11)));
        return list;
    }

    /// <summary>Per person: how many entries of each kind and when they were last active.</summary>
    public List<object> People()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, username, COUNT(*), MAX(at),
              SUM(action LIKE 'statics%'), SUM(action LIKE 'land%'), SUM(action LIKE 'items%'), SUM(action LIKE 'export%' OR action LIKE 'publish%')
            FROM audit WHERE user_id IS NOT NULL GROUP BY user_id ORDER BY MAX(at) DESC
            """;
        var list = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new { userId = r.GetInt64(0), username = r.GetString(1), entries = r.GetInt64(2), last = r.GetString(3),
                statics = r.GetInt64(4), land = r.GetInt64(5), items = r.GetInt64(6), exports = r.GetInt64(7) });
        return list;
    }
}
