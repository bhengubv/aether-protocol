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

/// <summary>
/// A group conversation: a chat with more than one person in it.
/// </summary>
/// <param name="Id">
/// The group's own id, which takes the place of a person's tag everywhere a conversation is named.
/// It is generated once by whoever creates the group and never changes, so every member agrees on
/// which conversation a message belongs to without anyone coordinating.
/// </param>
/// <param name="AdminTag">Whoever created it — the one who may rename it and add or remove people.</param>
public sealed record GroupRecord(string Id, string Name, string AdminTag, long CreatedMs);

/// <summary>One message in a conversation, as this device stored it.</summary>
/// <param name="PeerTag">The other person's AetherTag — the conversation this belongs to.</param>
/// <param name="Body">The plaintext. It is only ever plaintext here, on your own device.</param>
/// <param name="Mine">You wrote it.</param>
/// <param name="State">pending · sent · delivered · failed · received.</param>
/// <param name="SenderTag">
/// Who wrote it. In a one-to-one chat this is the same as <paramref name="PeerTag"/> and carries no
/// extra information, but in a group the thread is the group and the author is someone in it — so
/// the author has to be stored separately or a group conversation cannot say who is speaking.
/// </param>
public sealed record ChatMessage(
    string Id, string PeerTag, string Body, bool Mine, string State, long SentMs, string? SenderTag = null)
{
    /// <summary>Still on this phone — no secure session yet, so it has not gone out.</summary>
    public const string Pending = "pending";

    /// <summary>Handed to the radio. Says nothing about whether the other phone got it.</summary>
    public const string Sent = "sent";

    /// <summary>The other phone confirmed it. This is the only state that means "they have it".</summary>
    public const string Delivered = "delivered";

    /// <summary>Went out but was never confirmed — treat it as lost, and retry when we can.</summary>
    public const string Failed = "failed";

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
        // A group is a chat. It gets its own identity and membership, but its messages live in the
        // same table as everything else, keyed by the group's id where a one-to-one chat uses the
        // other person's tag — so the chat list, the conversation screen and delivery receipts all
        // work on a group without knowing it is one.
        Exec("""
            CREATE TABLE IF NOT EXISTS groups (
                id         TEXT PRIMARY KEY NOT NULL,
                name       TEXT NOT NULL,
                admin_tag  TEXT NOT NULL,
                created_ms INTEGER NOT NULL
            );
            """);
        Exec("""
            CREATE TABLE IF NOT EXISTS group_members (
                group_id TEXT NOT NULL,
                tag      TEXT NOT NULL,
                added_ms INTEGER NOT NULL,
                PRIMARY KEY (group_id, tag)
            );
            """);
        // Receipts this phone owes and could not send. A receipt only travels inside a secure session,
        // so a message can arrive during a session rebuild, be read and saved, and have no way to be
        // confirmed at that moment. Forgetting it strands the sender on a failure for a message that is
        // sitting on this phone — and the message will not arrive again to prompt a second attempt.
        Exec("""
            CREATE TABLE IF NOT EXISTS owed_receipts (
                message_id TEXT PRIMARY KEY NOT NULL,
                peer_tag   TEXT NOT NULL,
                owed_ms    INTEGER NOT NULL
            );
            """);
        AddColumnIfMissing("messages", "sender_tag", "TEXT");
        Exec("CREATE INDEX IF NOT EXISTS ix_contacts_last_seen ON contacts(last_seen_ms);");
        Exec("CREATE INDEX IF NOT EXISTS ix_messages_peer ON messages(peer_tag, sent_ms);");
        Exec("CREATE INDEX IF NOT EXISTS ix_messages_state ON messages(state);");
    }

    /// <summary>
    /// Add a column to an existing table if it is not already there — phones in the field already
    /// have a messages table, and a schema change must not cost anyone their conversations.
    /// </summary>
    private void AddColumnIfMissing(string table, string column, string type)
    {
        using var check = _conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @c;";
        check.Parameters.AddWithValue("@c", column);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        Exec($"ALTER TABLE {table} ADD COLUMN {column} {type};");
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
                SELECT id, peer_tag, body, mine, state, sent_ms, sender_tag FROM messages
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
                SELECT id, peer_tag, body, mine, state, sent_ms, sender_tag FROM messages
                WHERE sent_ms = (SELECT MAX(sent_ms) FROM messages m2 WHERE m2.peer_tag = messages.peer_tag)
                GROUP BY peer_tag ORDER BY sent_ms DESC;
                """;
            var list = new List<ChatMessage>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadMessage(reader));
            return list;
        }
    }

    /// <summary>
    /// Messages that are not in the other person's hands yet — they never went out, they went out and
    /// were never confirmed, or we gave up on them. All get another try the moment the peer is
    /// reachable, because only a receipt proves anything and everything short of one is still owed.
    /// </summary>
    public IReadOnlyList<ChatMessage> GetUnsentMessages(string peerTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerTag);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, peer_tag, body, mine, state, sent_ms, sender_tag FROM messages
                WHERE peer_tag = @tag AND mine = 1 AND state <> 'delivered' ORDER BY sent_ms;
                """;
            cmd.Parameters.AddWithValue("@tag", peerTag);
            var list = new List<ChatMessage>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(ReadMessage(reader));
            return list;
        }
    }

    /// <summary>
    /// Note that a message was read and saved but could not be confirmed yet. Kept on disk rather than
    /// in memory: the sender is already waiting, and losing the debt to a restart means their message
    /// shows as failed forever.
    /// </summary>
    public void RememberOwedReceipt(string peerTag, string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerTag);
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO owed_receipts (message_id, peer_tag, owed_ms) VALUES (@id, @tag, @ms)
                ON CONFLICT(message_id) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("@id", messageId);
            cmd.Parameters.AddWithValue("@tag", peerTag);
            cmd.Parameters.AddWithValue("@ms", Now());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Receipts still owed to one person, oldest first.</summary>
    public IReadOnlyList<string> GetOwedReceipts(string peerTag)
    {
        ArgumentException.ThrowIfNullOrEmpty(peerTag);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT message_id FROM owed_receipts WHERE peer_tag = @tag ORDER BY owed_ms;";
            cmd.Parameters.AddWithValue("@tag", peerTag);
            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        }
    }

    /// <summary>The receipt went out; the debt is settled.</summary>
    public void ForgetOwedReceipt(string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM owed_receipts WHERE message_id = @id;";
            cmd.Parameters.AddWithValue("@id", messageId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Everyone this phone still owes a message to. A radio link coming up is the moment to try them
    /// all: the radio can only say it is linked to <i>something</i> — the wire address it saw is not a
    /// person and nothing is filed under it — so who to talk to has to come from what is waiting.
    /// </summary>
    public IReadOnlyList<string> GetPeersWithUnsentMessages()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            // Same rule as GetUnsentMessages, deliberately: this decides whose conversation a new link
            // is worth reviving, and a narrower rule here means a phone with messages waiting is told
            // it owes nobody, never starts a session, and sits behind "setting up encryption…" forever.
            cmd.CommandText = """
                SELECT DISTINCT peer_tag FROM messages
                WHERE mine = 1 AND state <> 'delivered';
                """;
            var list = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) list.Add(reader.GetString(0));
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
                INSERT INTO messages (id, peer_tag, body, mine, state, sent_ms, sender_tag)
                VALUES (@id, @tag, @body, @mine, @state, @ms, @sender)
                ON CONFLICT(id) DO UPDATE SET state=@state;
                """;
            cmd.Parameters.AddWithValue("@id", message.Id);
            cmd.Parameters.AddWithValue("@tag", message.PeerTag);
            cmd.Parameters.AddWithValue("@body", message.Body);
            cmd.Parameters.AddWithValue("@mine", message.Mine ? 1 : 0);
            cmd.Parameters.AddWithValue("@state", message.State);
            cmd.Parameters.AddWithValue("@ms", message.SentMs);
            cmd.Parameters.AddWithValue("@sender", (object?)message.SenderTag ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Move a message to a new state only if it is still in the one we expect.
    /// <para>
    /// A receipt can arrive before the send call that triggered it has even returned — on a fast
    /// link the peer answers while we are still inside our own send. An unconditional write then
    /// puts "sent" back over a "delivered" that already landed, and the message never recovers,
    /// because nothing will confirm it a second time.
    /// </para>
    /// </summary>
    public void SetMessageStateIf(string id, string expected, string next)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET state = @next WHERE id = @id AND state = @expected;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@expected", expected);
            cmd.Parameters.AddWithValue("@next", next);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Move a message on unless the other phone has already confirmed it.
    /// <para>
    /// Confirmation is the end of the road, and the only state a message must never come back from —
    /// but everything before it may move forward again. A message that was given up on and then re-sent
    /// over a new link has genuinely gone out a second time, and has to stop showing as a failure or
    /// the person is looking at a red mark on a message that is on its way.
    /// </para>
    /// </summary>
    public void SetMessageStateUnlessDelivered(string id, string next)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE messages SET state = @next WHERE id = @id AND state <> @delivered;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@next", next);
            cmd.Parameters.AddWithValue("@delivered", ChatMessage.Delivered);
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
        SentMs: r.GetInt64(5),
        SenderTag: r.IsDBNull(6) ? null : r.GetString(6));

    // ── Groups ──────────────────────────────────────────────────────────────────

    public void SaveGroup(GroupRecord group)
    {
        ArgumentNullException.ThrowIfNull(group);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO groups (id, name, admin_tag, created_ms)
                VALUES (@id, @name, @admin, @ms)
                ON CONFLICT(id) DO UPDATE SET name = @name;
                """;
            cmd.Parameters.AddWithValue("@id", group.Id);
            cmd.Parameters.AddWithValue("@name", group.Name);
            cmd.Parameters.AddWithValue("@admin", group.AdminTag);
            cmd.Parameters.AddWithValue("@ms", group.CreatedMs);
            cmd.ExecuteNonQuery();
        }
    }

    public void AddGroupMember(string groupId, string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        ArgumentException.ThrowIfNullOrEmpty(tag);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO group_members (group_id, tag, added_ms) VALUES (@g, @t, @ms)
                ON CONFLICT(group_id, tag) DO NOTHING;
                """;
            cmd.Parameters.AddWithValue("@g", groupId);
            cmd.Parameters.AddWithValue("@t", tag);
            cmd.Parameters.AddWithValue("@ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveGroupMember(string groupId, string tag)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM group_members WHERE group_id = @g AND tag = @t;";
            cmd.Parameters.AddWithValue("@g", groupId);
            cmd.Parameters.AddWithValue("@t", tag);
            cmd.ExecuteNonQuery();
        }
    }

    public GroupRecord? GetGroup(string groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return null;
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, admin_tag, created_ms FROM groups WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", groupId);
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new GroupRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3))
                : null;
        }
    }

    public IReadOnlyList<GroupRecord> GetGroups()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, admin_tag, created_ms FROM groups ORDER BY created_ms;";
            var list = new List<GroupRecord>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new GroupRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
            return list;
        }
    }

    public IReadOnlyList<string> GetGroupMembers(string groupId)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT tag FROM group_members WHERE group_id = @g ORDER BY added_ms;";
            cmd.Parameters.AddWithValue("@g", groupId);
            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

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
