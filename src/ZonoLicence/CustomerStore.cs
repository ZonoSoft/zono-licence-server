using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ZonoLicence;

/// <summary>A PC or phone running Zono (its id is a one-way hash made on the device, so it identifies nothing by itself).</summary>
public sealed record Machine(
    string Id, string Product, string Platform, string AppVersion, string InstallId, string BusinessName,
    DateTime FirstSeenUtc, DateTime LastSeenUtc, DateOnly TrialStarted, string? AccountId, int Launches);

/// <summary>A customer's portal account. Personal details are stored encrypted (see <see cref="Secrets"/>).</summary>
public sealed record Account(
    string Id, string Username, string BusinessName, string ContactName, string Email, string PhoneCountry, string Phone,
    string PasswordHash, DateTime CreatedUtc, DateTime? LastLoginUtc, int FailedLogins, DateTime? LockedUntilUtc);

/// <summary>Machines (for the free trial), customer accounts and portal sessions, in the licence database.</summary>
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
            CREATE TABLE IF NOT EXISTS machines (
                id TEXT PRIMARY KEY,
                product TEXT NOT NULL DEFAULT '',
                platform TEXT NOT NULL DEFAULT '',
                app_version TEXT NOT NULL DEFAULT '',
                install_id TEXT NOT NULL DEFAULT '',
                business_name TEXT NOT NULL DEFAULT '',
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                trial_started TEXT NOT NULL,
                account_id TEXT NULL,
                launches INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_machines_install ON machines(install_id);
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

    // ---------- Machines ----------

    /// <summary>Records a launch. The trial starts the first time a machine is seen and never restarts.</summary>
    public Machine Seen(string id, string product, string platform, string appVersion, string installId, string businessName, DateTime nowUtc)
    {
        using var c = Open();
        using (var cmd = Command(c, """
            INSERT INTO machines(id, product, platform, app_version, install_id, business_name, first_seen, last_seen, trial_started, launches)
            VALUES ($id, $pr, $pl, $v, $i, $b, $t, $t, $d, 1)
            ON CONFLICT(id) DO UPDATE SET product = $pr, platform = $pl, app_version = $v,
                install_id = CASE WHEN $i = '' THEN install_id ELSE $i END,
                business_name = CASE WHEN $b = '' THEN business_name ELSE $b END,
                last_seen = $t, launches = launches + 1;
            """, ("$id", id), ("$pr", product), ("$pl", platform), ("$v", appVersion), ("$i", installId), ("$b", businessName),
            ("$t", Iso(nowUtc)), ("$d", DateOnly.FromDateTime(nowUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))
            cmd.ExecuteNonQuery();
        return GetMachine(id)!;
    }

    public Machine? GetMachine(string id) => Machines("SELECT * FROM machines WHERE id = $v;", id).FirstOrDefault();
    public List<Machine> MachinesForInstall(string installId) => Machines("SELECT * FROM machines WHERE install_id = $v;", installId);
    public List<Machine> AllMachines(int limit = 500) => Machines($"SELECT * FROM machines ORDER BY last_seen DESC LIMIT {limit};", null);

    private List<Machine> Machines(string sql, string? value)
    {
        using var c = Open();
        using var cmd = Command(c, sql, ("$v", value));
        using var r = cmd.ExecuteReader();
        var list = new List<Machine>();
        while (r.Read())
            list.Add(new Machine(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                ParseTime(r.GetString(6)), ParseTime(r.GetString(7)),
                DateOnly.ParseExact(r.GetString(8), "yyyy-MM-dd", CultureInfo.InvariantCulture), r.IsDBNull(9) ? null : r.GetString(9), r.GetInt32(10)));
        return list;
    }

    public void LinkMachine(string machineId, string accountId)
    {
        using var c = Open();
        using var cmd = Command(c, "UPDATE machines SET account_id = $a WHERE id = $id;", ("$a", accountId), ("$id", machineId));
        cmd.ExecuteNonQuery();
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

    public void EndSessions(string accountId)
    {
        using var c = Open();
        using var cmd = Command(c, "DELETE FROM sessions WHERE account_id = $a;", ("$a", accountId));
        cmd.ExecuteNonQuery();
    }
}
