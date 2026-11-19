using System.Text.Json;
using ShardForge.Server.Data;

namespace ShardForge.Server.Projects;

public sealed record Prefab(long Id, string Name, JsonElement Data, long? CreatedBy, string? CreatedByName, string Created, string Updated);

public sealed class PrefabException(string message) : Exception(message);

/// <summary>
/// The team's own houses and decorations, made in Walk's house designer or saved from a selection: a name and a
/// JSON body (a plan that stays editable, or a list of statics). Shared by everyone on the server.
/// </summary>
public sealed class PrefabStore(Db db)
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MaxCount = 5000;

    public IReadOnlyList<Prefab> All()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT p.id, p.name, p.data, p.created_by, u.username, p.created, p.updated FROM prefabs p LEFT JOIN users u ON u.id = p.created_by ORDER BY p.name COLLATE NOCASE";
        var list = new List<Prefab>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Prefab(r.GetInt64(0), r.GetString(1), JsonDocument.Parse(r.GetString(2)).RootElement.Clone(),
                r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6)));
        return list;
    }

    public Prefab? Get(long id) => All().FirstOrDefault(p => p.Id == id);

    /// <summary>Checks a body: a plan with storeys, or a list of statics [id, x, y, z, hue?].</summary>
    public static void Validate(string name, JsonElement data, int bytes)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) throw new PrefabException("Give it a name (up to 80 characters).");
        if (bytes > MaxBytes) throw new PrefabException("That design is too big (over 4 MB).");
        if (data.ValueKind != JsonValueKind.Object) throw new PrefabException("Send a plan or a list of statics.");
        bool plan = data.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("storeys", out var st) && st.ValueKind == JsonValueKind.Array;
        bool parts = data.TryGetProperty("parts", out var q) && q.ValueKind == JsonValueKind.Array && q.GetArrayLength() > 0;
        if (!plan && !parts) throw new PrefabException("A design needs a plan with storeys, or statics.");
        if (parts)
            foreach (var e in q.EnumerateArray())
                if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 4 || e.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number))
                    throw new PrefabException("Each static needs [id, x, y, z].");
    }

    public long Create(string name, JsonElement data, int bytes, long userId)
    {
        Validate(name, data, bytes);
        using var c = db.Open();
        using (var n = c.CreateCommand()) { n.CommandText = "SELECT COUNT(*) FROM prefabs"; if ((long)n.ExecuteScalar()! >= MaxCount) throw new PrefabException("The library is full."); }
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO prefabs(name, data, created_by, updated_by, created, updated) VALUES ($n, $d, $u, $u, $t, $t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name.Trim()); cmd.Parameters.AddWithValue("$d", data.GetRawText());
        cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$t", Db.Now());
        return (long)cmd.ExecuteScalar()!;
    }

    public void Update(long id, string name, JsonElement data, int bytes, long userId)
    {
        Validate(name, data, bytes);
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE prefabs SET name = $n, data = $d, updated_by = $u, updated = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$n", name.Trim()); cmd.Parameters.AddWithValue("$d", data.GetRawText());
        cmd.Parameters.AddWithValue("$u", userId); cmd.Parameters.AddWithValue("$t", Db.Now()); cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() == 0) throw new PrefabException("No such design.");
    }

    public bool Delete(long id)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM prefabs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}
