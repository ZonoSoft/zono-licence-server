using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ZonoLicence;

public sealed record Licence(
    string Id, string KeyHint, string BusinessName, string Email, string Plan, int TeamSeats, DateOnly? Expires,
    int MaxActivations, string Status, string Notes, DateTime CreatedUtc, string? AccountId = null);

public sealed record Activation(
    long Id, string LicenceId, string InstallId, string BusinessName, string DeviceName, string Platform, string AppVersion,
    DateTime FirstSeenUtc, DateTime LastSeenUtc, bool Released);

/// <summary>Licences and where they're activated, in SQLite. Licence keys are kept only as a hash.</summary>
public sealed class LicenceStore
{
    private readonly string _connection;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O, 1/I/L

    public LicenceStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var c = Open();
        Exec(c, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS licences (
                id TEXT PRIMARY KEY,
                key_hash TEXT NOT NULL UNIQUE,
                key_hint TEXT NOT NULL,
                business_name TEXT NOT NULL,
                email TEXT NOT NULL DEFAULT '',
                plan TEXT NOT NULL,
                team_seats INTEGER NOT NULL,
                expires TEXT NULL,
                max_activations INTEGER NOT NULL DEFAULT 1,
                status TEXT NOT NULL DEFAULT 'active',
                notes TEXT NOT NULL DEFAULT '',
                created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS activations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                licence_id TEXT NOT NULL REFERENCES licences(id),
                install_id TEXT NOT NULL,
                business_name TEXT NOT NULL DEFAULT '',
                device_name TEXT NOT NULL DEFAULT '',
                platform TEXT NOT NULL DEFAULT '',
                app_version TEXT NOT NULL DEFAULT '',
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                released INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_activations_licence ON activations(licence_id);
            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                at TEXT NOT NULL,
                licence_id TEXT NULL,
                what TEXT NOT NULL,
                detail TEXT NOT NULL DEFAULT '',
                ip TEXT NOT NULL DEFAULT '');
            CREATE INDEX IF NOT EXISTS ix_activations_install ON activations(install_id);
            """);
        // v2: licences belong to a customer account; the key is also kept encrypted so the customer can see it in the portal
        AddColumn(c, "licences", "account_id", "TEXT NULL");
        AddColumn(c, "licences", "key_enc", "TEXT NOT NULL DEFAULT ''");
    }

    private static void AddColumn(SqliteConnection c, string table, string column, string type)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        if ((long)cmd.ExecuteScalar()! == 0) Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} {type};");
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connection);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    // ---------- Keys ----------

    /// <summary>A new licence key like ZONO-7KQ2M-XW4PD-3H9TF-NB6RC (100 random bits).</summary>
    public static string NewKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        var chars = bytes.Select(b => Alphabet[b % Alphabet.Length]).ToArray();
        return "ZONO-" + string.Join('-', Enumerable.Range(0, 4).Select(i => new string(chars, i * 5, 5)));
    }

    /// <summary>The key as typed, tidied: capitals, no spaces or dashes, without the ZONO prefix.</summary>
    public static string Normalise(string key)
    {
        var s = new string((key ?? "").ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return s.StartsWith("ZONO", StringComparison.Ordinal) ? s[4..] : s;
    }

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("zono-licence:" + Normalise(key))));

    // ---------- Licences ----------

    /// <summary>Makes a licence. Returns it and its key (shown once; only the hash is kept).</summary>
    public (Licence Licence, string Key) Create(string businessName, string email, string plan, int teamSeats, DateOnly? expires, int maxActivations, string notes,
        string status = "active", string? accountId = null, Secrets? secrets = null)
    {
        var key = NewKey();
        var id = "L" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO licences(id, key_hash, key_hint, business_name, email, plan, team_seats, expires, max_activations, status, notes, created, account_id, key_enc)
            VALUES ($i,$h,$k,$b,$e,$p,$s,$x,$m,$st,$n,$c,$a,$ke);
            """;
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$h", Hash(key));
        cmd.Parameters.AddWithValue("$k", key[^5..]);
        cmd.Parameters.AddWithValue("$b", businessName.Trim());
        cmd.Parameters.AddWithValue("$e", email.Trim());
        cmd.Parameters.AddWithValue("$p", plan);
        cmd.Parameters.AddWithValue("$s", teamSeats);
        cmd.Parameters.AddWithValue("$x", expires is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$m", maxActivations);
        cmd.Parameters.AddWithValue("$n", notes.Trim());
        cmd.Parameters.AddWithValue("$c", Now());
        cmd.Parameters.AddWithValue("$st", status);
        cmd.Parameters.AddWithValue("$a", (object?)accountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ke", secrets?.Protect(key) ?? "");
        cmd.ExecuteNonQuery();
        return (Get(id)!, key);
    }

    public Licence? Get(string id) => Query("SELECT * FROM licences WHERE id = $v;", id).FirstOrDefault();
    public Licence? FindByKey(string key) => Query("SELECT * FROM licences WHERE key_hash = $v;", Hash(key)).FirstOrDefault();
    public List<Licence> All() => Query("SELECT * FROM licences ORDER BY created DESC;", null);
    public List<Licence> ForAccount(string accountId) => Query("SELECT * FROM licences WHERE account_id = $v ORDER BY created DESC;", accountId);

    /// <summary>The licence a business (install id) is activated on, if any.</summary>
    public Licence? ForInstall(string installId) => Query("""
        SELECT l.* FROM licences l JOIN activations a ON a.licence_id = l.id
        WHERE a.install_id = $v AND a.released = 0 ORDER BY a.last_seen DESC LIMIT 1;
        """, installId).FirstOrDefault();

    /// <summary>The licence key, readable (only for licences made since keys were kept encrypted).</summary>
    public string? KeyOf(string id, Secrets secrets)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key_enc FROM licences WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string { Length: > 0 } enc ? secrets.Unprotect(enc) : null;
    }

    public void SetAccount(string id, string accountId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE licences SET account_id = $a WHERE id = $id;";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private List<Licence> Query(string sql, string? value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (value is not null) cmd.Parameters.AddWithValue("$v", value);
        using var r = cmd.ExecuteReader();
        var list = new List<Licence>();
        while (r.Read())
            list.Add(new Licence(r.GetString(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("key_hint")), r.GetString(r.GetOrdinal("business_name")),
                r.GetString(r.GetOrdinal("email")), r.GetString(r.GetOrdinal("plan")), r.GetInt32(r.GetOrdinal("team_seats")),
                r.IsDBNull(r.GetOrdinal("expires")) ? null : DateOnly.ParseExact(r.GetString(r.GetOrdinal("expires")), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.GetInt32(r.GetOrdinal("max_activations")), r.GetString(r.GetOrdinal("status")), r.GetString(r.GetOrdinal("notes")),
                DateTime.Parse(r.GetString(r.GetOrdinal("created")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.IsDBNull(r.GetOrdinal("account_id")) ? null : r.GetString(r.GetOrdinal("account_id"))));
        return list;
    }

    public void Update(string id, string? businessName, string? email, string? plan, int? teamSeats, DateOnly? expires, bool clearExpiry,
        int? maxActivations, string? status, string? notes)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var sets = new List<string>();
        void Set(string col, object? value)
        {
            sets.Add($"{col} = ${col}");
            cmd.Parameters.AddWithValue("$" + col, value ?? DBNull.Value);
        }
        if (businessName is not null) Set("business_name", businessName.Trim());
        if (email is not null) Set("email", email.Trim());
        if (plan is not null) Set("plan", plan);
        if (teamSeats is not null) Set("team_seats", teamSeats);
        if (expires is { } d) Set("expires", d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        else if (clearExpiry) Set("expires", null);
        if (maxActivations is not null) Set("max_activations", maxActivations);
        if (status is not null) Set("status", status);
        if (notes is not null) Set("notes", notes.Trim());
        if (sets.Count == 0) return;
        cmd.CommandText = $"UPDATE licences SET {string.Join(", ", sets)} WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A new key for a licence (the old key stops working; activations stay).</summary>
    public string NewKeyFor(string id, Secrets? secrets = null)
    {
        var key = NewKey();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE licences SET key_hash = $h, key_hint = $k, key_enc = $ke WHERE id = $id;";
        cmd.Parameters.AddWithValue("$ke", secrets?.Protect(key) ?? "");
        cmd.Parameters.AddWithValue("$h", Hash(key));
        cmd.Parameters.AddWithValue("$k", key[^5..]);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        return key;
    }

    // ---------- Activations ----------

    public List<Activation> Activations(string licenceId, bool includeReleased = true)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM activations WHERE licence_id = $l" + (includeReleased ? "" : " AND released = 0") + " ORDER BY last_seen DESC;";
        cmd.Parameters.AddWithValue("$l", licenceId);
        using var r = cmd.ExecuteReader();
        var list = new List<Activation>();
        while (r.Read())
            list.Add(new Activation(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                DateTime.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTime.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetInt64(9) != 0));
        return list;
    }

    /// <summary>Records (or refreshes) an activation for an install. False when the licence has no free activations.</summary>
    public bool Activate(Licence licence, string installId, string businessName, string deviceName, string platform, string appVersion)
    {
        var current = Activations(licence.Id, includeReleased: false);
        var existing = current.FirstOrDefault(a => a.InstallId == installId);
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (existing is not null)
        {
            cmd.CommandText = "UPDATE activations SET last_seen = $t, business_name = $b, device_name = $d, platform = $p, app_version = $v WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", existing.Id);
        }
        else
        {
            if (current.Count >= licence.MaxActivations) return false;
            cmd.CommandText = """
                INSERT INTO activations(licence_id, install_id, business_name, device_name, platform, app_version, first_seen, last_seen, released)
                VALUES ($l,$i,$b,$d,$p,$v,$t,$t,0);
                """;
            cmd.Parameters.AddWithValue("$l", licence.Id);
            cmd.Parameters.AddWithValue("$i", installId);
        }
        cmd.Parameters.AddWithValue("$t", Now());
        cmd.Parameters.AddWithValue("$b", businessName);
        cmd.Parameters.AddWithValue("$d", deviceName);
        cmd.Parameters.AddWithValue("$p", platform);
        cmd.Parameters.AddWithValue("$v", appVersion);
        cmd.ExecuteNonQuery();
        return true;
    }

    public bool IsActive(string licenceId, string installId) => Activations(licenceId, includeReleased: false).Any(a => a.InstallId == installId);

    public void Release(string licenceId, string installId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE activations SET released = 1 WHERE licence_id = $l AND install_id = $i;";
        cmd.Parameters.AddWithValue("$l", licenceId);
        cmd.Parameters.AddWithValue("$i", installId);
        cmd.ExecuteNonQuery();
    }

    public void ReleaseById(long activationId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE activations SET released = 1 WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", activationId);
        cmd.ExecuteNonQuery();
    }

    // ---------- Log ----------

    public void Log(string? licenceId, string what, string detail, string ip)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO events(at, licence_id, what, detail, ip) VALUES ($a,$l,$w,$d,$i);";
        cmd.Parameters.AddWithValue("$a", Now());
        cmd.Parameters.AddWithValue("$l", (object?)licenceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$w", what);
        cmd.Parameters.AddWithValue("$d", detail);
        cmd.Parameters.AddWithValue("$i", ip);
        cmd.ExecuteNonQuery();
    }

    public List<(string At, string? LicenceId, string What, string Detail, string Ip)> Events(int limit = 200)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT at, licence_id, what, detail, ip FROM events ORDER BY id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string?, string, string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    /// <summary>A consistent copy of the database (for backups).</summary>
    public void BackupTo(string path)
    {
        using var c = Open();
        using var dest = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        dest.Open();
        c.BackupDatabase(dest);
    }
}
