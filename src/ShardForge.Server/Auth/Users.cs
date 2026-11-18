using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ShardForge.Server.Data;

namespace ShardForge.Server.Auth;

/// <summary>User levels. Higher includes everything below.</summary>
public enum Role
{
    /// <summary>Can look around: open maps and projects, walk, see others.</summary>
    Viewer = 1,
    /// <summary>Can change things: edit projects, place statics, build maps.</summary>
    Builder = 2,
    /// <summary>Can manage users, delete projects and publish.</summary>
    Admin = 3,
}

public sealed record User(long Id, string Username, Role Role, bool Disabled, string SecurityStamp, string Created, string? LastLogin);

public sealed class UserException(string message) : Exception(message);

/// <summary>Accounts with PBKDF2-SHA256 password hashes.</summary>
public sealed class UserStore(Db db, int iterations)
{
    public const int MinPasswordLength = 8;

    public int Count()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public User Create(string username, string password, Role role, bool onlyIfNoUsers = false)
    {
        username = ValidateUsername(username);
        ValidatePassword(password);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        if (onlyIfNoUsers)
        {
            using var count = c.CreateCommand();
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM users";
            if (Convert.ToInt32(count.ExecuteScalar()) > 0) throw new UserException("Setup has already been done.");
        }
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO users(username, password_hash, role, security_stamp, created) VALUES ($u, $p, $r, $s, $t) RETURNING id";
        cmd.Parameters.AddWithValue("$u", username);
        cmd.Parameters.AddWithValue("$p", Hash(password));
        cmd.Parameters.AddWithValue("$r", (int)role);
        cmd.Parameters.AddWithValue("$s", NewStamp());
        cmd.Parameters.AddWithValue("$t", Db.Now());
        long id;
        try { id = (long)cmd.ExecuteScalar()!; }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new UserException($"The name '{username}' is taken."); }
        tx.Commit();
        return Get(id)!;
    }

    public User? Get(long id) => Query("WHERE id = $v", id).FirstOrDefault();
    public User? Find(string username) => Query("WHERE username = $v", username.Trim()).FirstOrDefault();
    public IReadOnlyList<User> All() => Query("ORDER BY username COLLATE NOCASE", null);

    /// <summary>Checks a login. Returns null for unknown users, wrong passwords and disabled accounts alike.</summary>
    public User? Verify(string username, string password)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, password_hash FROM users WHERE username = $u AND disabled = 0";
        cmd.Parameters.AddWithValue("$u", username.Trim());
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            Hash(password); // same work either way, so timing doesn't reveal which names exist
            return null;
        }
        long id = r.GetInt64(0);
        if (!Check(password, r.GetString(1))) return null;
        r.Close();
        using var touch = c.CreateCommand();
        touch.CommandText = "UPDATE users SET last_login = $t WHERE id = $id";
        touch.Parameters.AddWithValue("$t", Db.Now());
        touch.Parameters.AddWithValue("$id", id);
        touch.ExecuteNonQuery();
        return Get(id);
    }

    /// <summary>Changes role, password or disabled state. Any change signs the user out everywhere.</summary>
    public User Update(long id, Role? role = null, string? password = null, bool? disabled = null)
    {
        var user = Get(id) ?? throw new UserException("No such user.");
        if (password is not null) ValidatePassword(password);
        if ((role is not null && role != Role.Admin || disabled == true) && user.Role == Role.Admin && !user.Disabled && ActiveAdmins() <= 1)
            throw new UserException("This is the last admin. Make someone else an admin first.");

        using var c = db.Open();
        using var cmd = c.CreateCommand();
        var sets = new List<string> { "security_stamp = $s" };
        cmd.Parameters.AddWithValue("$s", NewStamp());
        if (role is not null) { sets.Add("role = $r"); cmd.Parameters.AddWithValue("$r", (int)role); }
        if (password is not null) { sets.Add("password_hash = $p"); cmd.Parameters.AddWithValue("$p", Hash(password)); }
        if (disabled is not null) { sets.Add("disabled = $d"); cmd.Parameters.AddWithValue("$d", disabled.Value ? 1 : 0); }
        cmd.CommandText = $"UPDATE users SET {string.Join(", ", sets)} WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return Get(id)!;
    }

    public void Delete(long id)
    {
        var user = Get(id) ?? throw new UserException("No such user.");
        if (user.Role == Role.Admin && !user.Disabled && ActiveAdmins() <= 1)
            throw new UserException("This is the last admin. Make someone else an admin first.");
        // Keep history readable: the account is disabled and renamed rather than removed.
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET disabled = 1, username = $n, security_stamp = $s WHERE id = $id";
        cmd.Parameters.AddWithValue("$n", $"{user.Username} (deleted {id})");
        cmd.Parameters.AddWithValue("$s", NewStamp());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private int ActiveAdmins()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE role = 3 AND disabled = 0";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private List<User> Query(string where, object? value)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT id, username, role, disabled, security_stamp, created, last_login FROM users {where}";
        if (value is not null) cmd.Parameters.AddWithValue("$v", value);
        using var r = cmd.ExecuteReader();
        var list = new List<User>();
        while (r.Read())
            list.Add(new User(r.GetInt64(0), r.GetString(1), (Role)r.GetInt32(2), r.GetInt32(3) != 0, r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    public static string ValidateUsername(string username)
    {
        username = (username ?? "").Trim();
        if (username.Length is < 2 or > 32 || !username.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or ' '))
            throw new UserException("Names are 2-32 characters: letters, digits, spaces, _ - and . only.");
        return username;
    }

    public static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            throw new UserException($"Passwords need at least {MinPasswordLength} characters.");
        if (password.Length > 256) throw new UserException("That password is too long.");
    }

    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool Check(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out int iter)) return false;
        byte[] salt = Convert.FromBase64String(parts[2]), expected = Convert.FromBase64String(parts[3]);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
