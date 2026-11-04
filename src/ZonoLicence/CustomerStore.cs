using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ZonoLicence;

/// <summary>A customer's portal account. Personal details are stored encrypted (see <see cref="Secrets"/>).</summary>
public sealed record Account(
    string Id, string Username, string BusinessName, string ContactName, string Email, string PhoneCountry, string Phone,
    string PasswordHash, DateTime CreatedUtc, DateTime? LastLoginUtc, int FailedLogins, DateTime? LockedUntilUtc);

/// <summary>Customer accounts and portal sessions, in the licence database.</summary>
public sealed class CustomerStore
{
    private readonly string _connection;
    private readonly Secrets _secrets;

    public CustomerStore(string path, Secrets secrets)
    {
        _secrets = secrets;
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS accounts (
                id TEXT PRIMARY KEY,
                username_lookup TEXT NOT NULL UNIQUE,
                username TEXT NOT NULL,
                business_name TEXT NOT NULL,
                contact_name TEXT NOT NULL,
                email TEXT NOT NULL,
                phone_country TEXT NOT NULL,
                phone TEXT NOT NULL,
                password_hash TEXT NOT NULL,
                created TEXT NOT NULL,
                last_login TEXT NULL,
                failed_logins INTEGER NOT NULL DEFAULT 0,
                locked_until TEXT NULL);
            CREATE TABLE IF NOT EXISTS sessions (
                token_hash TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                created TEXT NOT NULL,
                expires TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        // Data minimisation (Nov 2026): phone numbers are no longer kept; clear any stored before
        using var wipe = c.CreateCommand();
        wipe.CommandText = "UPDATE accounts SET phone = '', phone_country = '' WHERE phone <> '' OR phone_country <> '';";
        wipe.ExecuteNonQuery();
        // No free trial (Nov 2026): devices without a licence aren't recorded. Remove the old trial records for good.
        using var trials = c.CreateCommand();
        trials.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'machines';";
        if (Convert.ToInt64(trials.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
        {
            trials.CommandText = "DROP TABLE machines;";
            trials.ExecuteNonQuery();
            trials.CommandText = "VACUUM;";
            trials.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connection);
        c.Open();
        return c;
    }

    private static string Iso(DateTime t) => t.ToString("O", CultureInfo.InvariantCulture);
    private static DateTime ParseTime(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] values)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    // ---------- Accounts ----------

    public bool UsernameTaken(string username)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT COUNT(*) FROM accounts WHERE username_lookup = $u;", ("$u", _secrets.Lookup(username)));
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public Account CreateAccount(string username, string password, string businessName, string contactName, string email, string phoneCountry, string phone)
    {
        var id = "A" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        using var c = Open();
        using var cmd = Command(c, """
            INSERT INTO accounts(id, username_lookup, username, business_name, contact_name, email, phone_country, phone, password_hash, created)
            VALUES ($id, $ul, $u, $b, $cn, $e, $pc, $p, $ph, $t);
            """, ("$id", id), ("$ul", _secrets.Lookup(username)), ("$u", _secrets.Protect(username.Trim())), ("$b", _secrets.Protect(businessName)),
            ("$cn", _secrets.Protect(contactName)), ("$e", _secrets.Protect(email)), ("$pc", phoneCountry), ("$p", _secrets.Protect(phone)),
            ("$ph", Secrets.HashPassword(password)), ("$t", Iso(DateTime.UtcNow)));
        cmd.ExecuteNonQuery();
        return GetAccount(id)!;
    }

    public Account? GetAccount(string id) => Accounts("SELECT * FROM accounts WHERE id = $v;", id).FirstOrDefault();
    public Account? FindByUsername(string username) => Accounts("SELECT * FROM accounts WHERE username_lookup = $v;", _secrets.Lookup(username)).FirstOrDefault();
    public List<Account> AllAccounts() => Accounts("SELECT * FROM accounts ORDER BY created DESC;", null);

    private List<Account> Accounts(string sql, string? value)
    {
        using var c = Open();
        using var cmd = Command(c, sql, ("$v", value));
        using var r = cmd.ExecuteReader();
        var list = new List<Account>();
        string Col(string name) => r.GetString(r.GetOrdinal(name));
        while (r.Read())
            list.Add(new Account(Col("id"), _secrets.Unprotect(Col("username")), _secrets.Unprotect(Col("business_name")),
                _secrets.Unprotect(Col("contact_name")), _secrets.Unprotect(Col("email")), Col("phone_country"), _secrets.Unprotect(Col("phone")),
                Col("password_hash"), ParseTime(Col("created")),
                r.IsDBNull(r.GetOrdinal("last_login")) ? null : ParseTime(Col("last_login")), r.GetInt32(r.GetOrdinal("failed_logins")),
                r.IsDBNull(r.GetOrdinal("locked_until")) ? null : ParseTime(Col("locked_until"))));
        return list;
    }

    public void UpdateDetails(string id, string businessName, string contactName, string email, string phoneCountry, string phone)
    {
        using var c = Open();
        using var cmd = Command(c, "UPDATE accounts SET business_name = $b, contact_name = $cn, email = $e, phone_country = $pc, phone = $p WHERE id = $id;",
            ("$b", _secrets.Protect(businessName)), ("$cn", _secrets.Protect(contactName)), ("$e", _secrets.Protect(email)),
            ("$pc", phoneCountry), ("$p", _secrets.Protect(phone)), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    public void SetPassword(string id, string password)
    {
        using var c = Open();
        using var cmd = Command(c, "UPDATE accounts SET password_hash = $ph, failed_logins = 0, locked_until = NULL WHERE id = $id;",
            ("$ph", Secrets.HashPassword(password)), ("$id", id));
        cmd.ExecuteNonQuery();
        EndSessions(id);
    }

    /// <summary>Records a sign-in attempt; 8 wrong passwords lock the account for 15 minutes.</summary>
    public void LoginResult(string id, bool ok)
    {
        using var c = Open();
        using var cmd = ok
            ? Command(c, "UPDATE accounts SET failed_logins = 0, locked_until = NULL, last_login = $t WHERE id = $id;", ("$t", Iso(DateTime.UtcNow)), ("$id", id))
            : Command(c, """
                UPDATE accounts SET
                    locked_until = CASE WHEN failed_logins + 1 >= 8 THEN $l ELSE locked_until END,
                    failed_logins = CASE WHEN failed_logins + 1 >= 8 THEN 0 ELSE failed_logins + 1 END
                WHERE id = $id;
                """, ("$l", Iso(DateTime.UtcNow.AddMinutes(15))), ("$id", id));
        cmd.ExecuteNonQuery();
    }

    // ---------- Portal sessions ----------

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public string StartSession(string accountId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var c = Open();
        using (var clean = Command(c, "DELETE FROM sessions WHERE expires < $t;", ("$t", Iso(DateTime.UtcNow)))) clean.ExecuteNonQuery();
        using var cmd = Command(c, "INSERT INTO sessions(token_hash, account_id, created, expires) VALUES ($h, $a, $t, $x);",
            ("$h", HashToken(token)), ("$a", accountId), ("$t", Iso(DateTime.UtcNow)), ("$x", Iso(DateTime.UtcNow.AddHours(12))));
        cmd.ExecuteNonQuery();
        return token;
    }

    public string? SessionAccount(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        using var c = Open();
        using var cmd = Command(c, "SELECT account_id FROM sessions WHERE token_hash = $h AND expires > $t;", ("$h", HashToken(token)), ("$t", Iso(DateTime.UtcNow)));
        return cmd.ExecuteScalar() as string;
    }

    public void EndSession(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        using var c = Open();
        using var cmd = Command(c, "DELETE FROM sessions WHERE token_hash = $h;", ("$h", HashToken(token)));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes expired portal sign-ins (daily).</summary>
    public int PruneSessions(DateTime nowUtc)
    {
        using var c = Open();
        using var cmd = Command(c, "DELETE FROM sessions WHERE expires < $t;", ("$t", Iso(nowUtc)));
        return cmd.ExecuteNonQuery();
    }

    public void EndSessions(string accountId)
    {
        using var c = Open();
        using var cmd = Command(c, "DELETE FROM sessions WHERE account_id = $a;", ("$a", accountId));
        cmd.ExecuteNonQuery();
    }
}
