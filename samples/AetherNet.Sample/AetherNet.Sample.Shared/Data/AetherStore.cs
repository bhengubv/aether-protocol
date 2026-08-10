// SPDX-License-Identifier: MIT

using Microsoft.Data.Sqlite;

namespace AetherNet.Sample.Shared.Data;

/// <summary>Someone this device knows, and how the relationship stands.</summary>
/// <param name="Tag">Their AetherTag — the primary key; this is who they are.</param>
/// <param name="DisplayName">A local nickname. Yours to set; never sent anywhere.</param>
/// <param name="PublicKey">Their Ed25519 public key, verified to match <paramref name="Tag"/>.</param>
/// <param name="AddedByMe">You have added them.</param>
/// <param name="AddedByThem">They have added you.</param>
/// <param name="AddedVia">How the tag arrived: qr · typed · radio · invite.</param>
public sealed record ContactRecord(
    string Tag,
    string DisplayName,
    byte[]? PublicKey,
    bool AddedByMe,
    bool AddedByThem,
    string AddedVia,
    long FirstSeenMs,
    long LastSeenMs)
{
    /// <summary>Both sides have added each other — the BBM handshake is complete.</summary>
    public bool IsMutual => AddedByMe && AddedByThem;

    /// <summary>They added you and are waiting on you.</summary>
    public bool IsIncoming => AddedByThem && !AddedByMe;

    /// <summary>You added them and are waiting on them.</summary>
    public bool IsPending => AddedByMe && !AddedByThem;
}

/// <summary>One message in a conversation, as this device stored it.</summary>
/// <param name="PeerTag">The other person's AetherTag — the conversation this belongs to.</param>
/// <param name="Body">The plaintext. It is only ever plaintext here, on your own device.</param>
/// <param name="Mine">You wrote it.</param>
/// <param name="State">pending (waiting for a secure session) · sent · received.</param>
public sealed record ChatMessage(string Id, string PeerTag, string Body, bool Mine, string State, long SentMs)
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Received = "received";
}

/// <summary>
/// This device's own database — identity, the people it knows, and app settings. Everything here is
/// local and stays local: there is no server copy and no central directory, so the address book is
/// yours in the same sense the keys are (see IDENTITY_AND_DATA_SOVEREIGNTY — "no central list"
/// governs the network, not your own phone).
///
/// Same shape as <c>AetherNet.Map.Sqlite</c> / <c>AetherNet.Content.Sqlite</c>: one long-lived
/// connection behind a lock, WAL, busy-timeout — reliable single-writer on-device use.
/// </summary>
public sealed class AetherStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public AetherStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(databasePath);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA busy_timeout=5000;");
        Exec("PRAGMA synchronous=NORMAL;");
        EnsureSchema();
    }

    /// <summary>A private in-memory database — for tests.</summary>
    public static AetherStore InMemory() => new(":memory:");

    private void EnsureSchema()
    {
        Exec("""
            CREATE TABLE IF NOT EXISTS identity (
                id          INTEGER PRIMARY KEY CHECK (id = 1),
                tag         TEXT NOT NULL,
                public_key  BLOB NOT NULL,
                created_ms  INTEGER NOT NULL
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS contacts (
                tag            TEXT PRIMARY KEY NOT NULL,
                display_name   TEXT NOT NULL DEFAULT '',
                public_key     BLOB,
                added_by_me    INTEGER NOT NULL DEFAULT 0,
                added_by_them  INTEGER NOT NULL DEFAULT 0,
                added_via      TEXT NOT NULL DEFAULT 'typed',
                first_seen_ms  INTEGER NOT NULL,
                last_seen_ms   INTEGER NOT NULL
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS messages (
                id        TEXT PRIMARY KEY NOT NULL,
                peer_tag  TEXT NOT NULL,
                body      TEXT NOT NULL,
                mine      INTEGER NOT NULL,
                state     TEXT NOT NULL,
                sent_ms   INTEGER NOT NULL
            );
            """);
        Exec("CREATE INDEX IF NOT EXISTS ix_contacts_last_seen ON contacts(last_seen_ms);");
        Exec("CREATE INDEX IF NOT EXISTS ix_messages_peer ON messages(peer_tag, sent_ms);");
        Exec("CREATE INDEX IF NOT EXISTS ix_messages_state ON messages(state);");
    }

    // ── Identity ────────────────────────────────────────────────────────────────

    /// <summary>The identity this device has already established, or null on a first run.</summary>
    public (string Tag, byte[] PublicKey)? GetIdentity()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT tag, public_key FROM identity WHERE id = 1;";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return (reader.GetString(0), (byte[])reader[1]);
        }
    }

    /// <summary>Record the identity this device generated. Written once, on first run.</summary>
    public void SaveIdentity(string tag, byte[] publicKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentNullException.ThrowIfNull(publicKey);

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO identity (id, tag, public_key, created_ms) VALUES (1, @tag, @key, @ms)
                ON CONFLICT(id) DO UPDATE SET tag=@tag, public_key=@key;
                """;
            cmd.Parameters.AddWithValue("@tag", tag);
            cmd.Parameters.AddWithValue("@key", publicKey);
            cmd.Parameters.AddWithValue("@ms", Now());
            cmd.ExecuteNonQuery();
        }
    }

    // ── Contacts ────────────────────────────────────────────────────────────────

    public IReadOnlyList<ContactRecord> GetContacts()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT tag, display_name, public_key, added_by_me, added_by_them, added_via, first_seen_ms, last_seen_ms
                FROM contacts ORDER BY last_seen_ms DESC;
                """;
            var list = new List<ContactRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(Read(reader));
            return list;
        }
    }

    public ContactRecord? GetContact(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return null;
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT tag, display_name, public_key, added_by_me, added_by_them, added_via, first_seen_ms, last_seen_ms
                FROM contacts WHERE tag = @tag;
                """;
            cmd.Parameters.AddWithValue("@tag", tag);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    /// <summary>
    /// Record one side of the BBM handshake. <paramref name="byMe"/> / <paramref name="byThem"/> are
    /// sticky — passing false never un-adds, so an inbound request can land before or after your own
    /// add and the pair still converges on mutual.
    /// </summary>
    public void UpsertContact(string tag, byte[]? publicKey, bool byMe, bool byThem, string via, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO contacts (tag, display_name, public_key, added_by_me, added_by_them, added_via, first_seen_ms, last_seen_ms)
                VALUES (@tag, @name, @key, @me, @them, @via, @ms, @ms)
                ON CONFLICT(tag) DO UPDATE SET
                    display_name  = CASE WHEN @name <> '' THEN @name ELSE contacts.display_name END,
                    public_key    = COALESCE(@key, contacts.public_key),
                    added_by_me   = MAX(contacts.added_by_me, @me),
                    added_by_them = MAX(contacts.added_by_them, @them),
                    last_seen_ms  = @ms;
                """;
            cmd.Parameters.AddWithValue("@tag", tag);
            cmd.Parameters.AddWithValue("@name", displayName ?? string.Empty);
            cmd.Parameters.AddWithValue("@key", (object?)publicKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@me", byMe ? 1 : 0);
            cmd.Parameters.AddWithValue("@them", byThem ? 1 : 0);
            cmd.Parameters.AddWithValue("@via", via);
            cmd.Parameters.AddWithValue("@ms", Now());
            cmd.ExecuteNonQuery();
        }
    }

    public void TouchContact(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return;
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE contacts SET last_seen_ms = @ms WHERE tag = @tag;";
            cmd.Parameters.AddWithValue("@tag", tag);
            cmd.Parameters.AddWithValue("@ms", Now());
            cmd.ExecuteNonQuery();
        }
    }

    public bool RemoveContact(string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM contacts WHERE tag = @tag;";
            cmd.Parameters.AddWithValue("@tag", tag);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ── Messages ────────────────────────────────────────────────────────────────

    /// <summary>The conversation with one peer, oldest first.</summary>
    public IReadOnlyList<ChatMessage> GetMessages(string peerTag, int limit = 500)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerTag);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, peer_tag, body, mine, state, sent_ms FROM messages
                WHERE peer_tag = @tag ORDER BY sent_ms DESC LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@tag", peerTag);
            cmd.Parameters.AddWithValue("@limit", limit);
            var list = new List<ChatMessage>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadMessage(reader));
            list.Reverse();          // read newest-first for the LIMIT, show oldest-first
            return list;
        }
    }

    /// <summary>The most recent message with each peer — what the chat list shows.</summary>
    public IReadOnlyList<ChatMessage> GetLatestPerPeer()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, peer_tag, body, mine, state, sent_ms FROM messages
                WHERE sent_ms = (SELECT MAX(sent_ms) FROM messages m2 WHERE m2.peer_tag = messages.peer_tag)
                GROUP BY peer_tag ORDER BY sent_ms DESC;
                """;
            var list = new List<ChatMessage>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadMessage(reader));
            return list;
        }
    }

    /// <summary>Messages still waiting for a secure session before they can go out.</summary>
    public IReadOnlyList<ChatMessage> GetPendingMessages(string peerTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerTag);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, peer_tag, body, mine, state, sent_ms FROM messages
                WHERE peer_tag = @tag AND state = 'pending' ORDER BY sent_ms;
                """;
            cmd.Parameters.AddWithValue("@tag", peerTag);
            var list = new List<ChatMessage>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadMessage(reader));
            return list;
        }
    }

    public void SaveMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO messages (id, peer_tag, body, mine, state, sent_ms)
                VALUES (@id, @tag, @body, @mine, @state, @ms)
                ON CONFLICT(id) DO UPDATE SET state=@state;
                """;
            cmd.Parameters.AddWithValue("@id", message.Id);
            cmd.Parameters.AddWithValue("@tag", message.PeerTag);
            cmd.Parameters.AddWithValue("@body", message.Body);
            cmd.Parameters.AddWithValue("@mine", message.Mine ? 1 : 0);
            cmd.Parameters.AddWithValue("@state", message.State);
            cmd.Parameters.AddWithValue("@ms", message.SentMs);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetMessageState(string id, string state)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET state = @state WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@state", state);
            cmd.ExecuteNonQuery();
        }
    }

    private static ChatMessage ReadMessage(SqliteDataReader r) => new(
        Id: r.GetString(0),
        PeerTag: r.GetString(1),
        Body: r.GetString(2),
        Mine: r.GetInt32(3) != 0,
        State: r.GetString(4),
        SentMs: r.GetInt64(5));

    // ── Settings ────────────────────────────────────────────────────────────────

    public string? GetSetting(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = @key;";
            cmd.Parameters.AddWithValue("@key", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetSetting(string key, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings (key, value) VALUES (@key, @value)
                ON CONFLICT(key) DO UPDATE SET value=@value;
                """;
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }
    }

    public bool GetFlag(string key) => GetSetting(key) == "1";

    public void SetFlag(string key, bool value) => SetSetting(key, value ? "1" : "0");

    // ── Internals ───────────────────────────────────────────────────────────────

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static ContactRecord Read(SqliteDataReader r) => new(
        Tag: r.GetString(0),
        DisplayName: r.GetString(1),
        PublicKey: r.IsDBNull(2) ? null : (byte[])r[2],
        AddedByMe: r.GetInt32(3) != 0,
        AddedByThem: r.GetInt32(4) != 0,
        AddedVia: r.GetString(5),
        FirstSeenMs: r.GetInt64(6),
        LastSeenMs: r.GetInt64(7));

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
