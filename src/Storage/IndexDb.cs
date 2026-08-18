using Microsoft.Data.Sqlite;
using CozyHarness.Domain;

namespace CozyHarness.Storage;

/// <summary>
/// Derived index. Holds only what grep cannot do: recency/salience scoring,
/// fast goal checks for the pulse, message state. Rebuildable from the tree at
/// any time — if it corrupts, replay.
/// </summary>
public sealed class IndexDb : IDisposable
{
    private readonly SqliteConnection _db;

    public IndexDb(string path)
    {
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL;");
        CreateSchema();
        EnsureColumn("messages", "contact_id", "TEXT");
        Exec("CREATE INDEX IF NOT EXISTS idx_msg_contact ON messages(contact_id);");
    }

    private void CreateSchema() => Exec("""
        CREATE TABLE IF NOT EXISTS episodes (
          path TEXT PRIMARY KEY, ts TEXT NOT NULL, tick_type TEXT NOT NULL,
          summary TEXT NOT NULL, did_nothing INT DEFAULT 0, goal TEXT,
          salience REAL DEFAULT 0.5, person TEXT, sensitive INT DEFAULT 0,
          commit_sha TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_ep_ts ON episodes(ts);
        CREATE INDEX IF NOT EXISTS idx_ep_goal ON episodes(goal);

        CREATE TABLE IF NOT EXISTS goals (
          id TEXT PRIMARY KEY, path TEXT NOT NULL, title TEXT NOT NULL,
          state TEXT NOT NULL, kind TEXT NOT NULL, created_ts TEXT,
          renew_by TEXT, last_touched TEXT, closed_why TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_goal_state ON goals(state);

        CREATE TABLE IF NOT EXISTS facts (
          id INTEGER PRIMARY KEY, person TEXT NOT NULL, source_class TEXT NOT NULL,
          derived_from_private INT DEFAULT 0, sensitive INT DEFAULT 0,
          path TEXT NOT NULL, ts TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_fact_person ON facts(person);

        CREATE TABLE IF NOT EXISTS messages (
          id INTEGER PRIMARY KEY, ts TEXT NOT NULL, direction TEXT NOT NULL,
          person TEXT NOT NULL, content TEXT NOT NULL, handled INT DEFAULT 0,
          episode_path TEXT, contact_id TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_msg_handled ON messages(handled);

        CREATE TABLE IF NOT EXISTS observations (
          id INTEGER PRIMARY KEY, ts TEXT NOT NULL, source TEXT NOT NULL,
          ref TEXT, content TEXT NOT NULL, consumed INT DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_obs_consumed ON observations(consumed);

        CREATE TABLE IF NOT EXISTS chores (
          id TEXT PRIMARY KEY, path TEXT NOT NULL, title TEXT NOT NULL,
          state TEXT NOT NULL, due_by TEXT NOT NULL, created_ts TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_chore_state ON chores(state);
        """);

    /// <summary>
    /// CREATE TABLE IF NOT EXISTS above is a no-op against a messages table
    /// that already existed before contact_id did — this box has been
    /// running since before per-sender conversation scoping, so the live
    /// table needs a real migration, not just a schema-string edit. Runs
    /// after CreateSchema so a fresh table already has the column and this
    /// (and the index below) are no-ops; on an existing table, adds it.
    /// </summary>
    private void EnsureColumn(string table, string column, string sqlType)
    {
        try { Exec($"ALTER TABLE {table} ADD COLUMN {column} {sqlType};"); }
        // SQLite has no ADD COLUMN IF NOT EXISTS — "duplicate column name" is
        // the standard, stable error text for exactly this case.
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column name")) { }
    }

    // ── writes ──────────────────────────────────────────────────────────

    public void UpsertEpisode(Episode e, string relPath, string? sha)
    {
        using var c = _db.CreateCommand();
        c.CommandText = """
            INSERT INTO episodes (path, ts, tick_type, summary, did_nothing, goal, salience, person, sensitive, commit_sha)
            VALUES ($p,$ts,$t,$s,$dn,$g,$sal,$per,$sen,$sha)
            ON CONFLICT(path) DO UPDATE SET commit_sha=excluded.commit_sha;
            """;
        c.Parameters.AddWithValue("$p", relPath);
        c.Parameters.AddWithValue("$ts", e.Timestamp.ToString("o"));
        c.Parameters.AddWithValue("$t", e.Type.ToString().ToLowerInvariant());
        c.Parameters.AddWithValue("$s", e.Summary);
        c.Parameters.AddWithValue("$dn", e.DidNothing ? 1 : 0);
        c.Parameters.AddWithValue("$g", (object?)e.GoalId ?? DBNull.Value);
        c.Parameters.AddWithValue("$sal", e.Salience);
        c.Parameters.AddWithValue("$per", (object?)e.Person ?? DBNull.Value);
        c.Parameters.AddWithValue("$sen", e.Sensitive ? 1 : 0);
        c.Parameters.AddWithValue("$sha", (object?)sha ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    public void UpsertGoal(Goal g)
    {
        using var c = _db.CreateCommand();
        c.CommandText = """
            INSERT INTO goals (id, path, title, state, kind, created_ts, renew_by, last_touched, closed_why)
            VALUES ($id,$p,$t,$s,$k,$c,$r,$lt,$cw)
            ON CONFLICT(id) DO UPDATE SET
              path=excluded.path, title=excluded.title, state=excluded.state,
              kind=excluded.kind, renew_by=excluded.renew_by,
              last_touched=excluded.last_touched, closed_why=excluded.closed_why;
            """;
        c.Parameters.AddWithValue("$id", g.Id);
        c.Parameters.AddWithValue("$p", g.RelativePath);
        c.Parameters.AddWithValue("$t", g.Title);
        c.Parameters.AddWithValue("$s", g.State.ToString().ToLowerInvariant());
        c.Parameters.AddWithValue("$k", g.Kind.ToString().ToLowerInvariant());
        c.Parameters.AddWithValue("$c", g.Created.ToString("o"));
        c.Parameters.AddWithValue("$r", (object?)g.RenewBy?.ToString("o") ?? DBNull.Value);
        c.Parameters.AddWithValue("$lt", (object?)g.LastTouched?.ToString("o") ?? DBNull.Value);
        c.Parameters.AddWithValue("$cw", (object?)g.ClosedWhy ?? DBNull.Value);
        c.ExecuteNonQuery();
    }

    public void UpsertChore(Chore c)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO chores (id, path, title, state, due_by, created_ts)
            VALUES ($id,$p,$t,$s,$d,$c)
            ON CONFLICT(id) DO UPDATE SET
              path=excluded.path, title=excluded.title, state=excluded.state, due_by=excluded.due_by;
            """;
        cmd.Parameters.AddWithValue("$id", c.Id);
        cmd.Parameters.AddWithValue("$p", c.RelativePath);
        cmd.Parameters.AddWithValue("$t", c.Title);
        cmd.Parameters.AddWithValue("$s", c.State.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$d", c.DueBy.ToString("o"));
        cmd.Parameters.AddWithValue("$c", c.Created.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public long AddObservation(string source, string? reference, string content)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT INTO observations (ts, source, ref, content) VALUES ($ts,$s,$r,$c); SELECT last_insert_rowid();";
        c.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("o"));
        c.Parameters.AddWithValue("$s", source);
        c.Parameters.AddWithValue("$r", (object?)reference ?? DBNull.Value);
        c.Parameters.AddWithValue("$c", content);
        return (long)(c.ExecuteScalar() ?? 0L);
    }

    public void MarkObservationsConsumed(IEnumerable<long> ids)
    {
        foreach (var id in ids)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "UPDATE observations SET consumed=1 WHERE id=$id";
            c.Parameters.AddWithValue("$id", id);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// contactId scopes conversation history (PendingInbound, RecentConversation)
    /// to one sender — the Discord user id as a string, in or out doesn't
    /// matter, it's "who this exchange is with" either way. person is
    /// unrelated and unchanged by any of this — still the display label used
    /// for logging (see ReplyTick), not a scoping key.
    /// </summary>
    public long AddMessage(string direction, string person, string content, string? contactId = null)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "INSERT INTO messages (ts, direction, person, content, contact_id) VALUES ($ts,$d,$p,$c,$cid); SELECT last_insert_rowid();";
        c.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("o"));
        c.Parameters.AddWithValue("$d", direction);
        c.Parameters.AddWithValue("$p", person);
        c.Parameters.AddWithValue("$c", content);
        c.Parameters.AddWithValue("$cid", (object?)contactId ?? DBNull.Value);
        return (long)(c.ExecuteScalar() ?? 0L);
    }

    public void MarkMessageHandled(long id, string episodePath)
    {
        using var c = _db.CreateCommand();
        c.CommandText = "UPDATE messages SET handled=1, episode_path=$e WHERE id=$id";
        c.Parameters.AddWithValue("$id", id);
        c.Parameters.AddWithValue("$e", episodePath);
        c.ExecuteNonQuery();
    }

    public void AddFact(PersonFact f, string path)
    {
        using var c = _db.CreateCommand();
        c.CommandText = """
            INSERT INTO facts (person, source_class, derived_from_private, sensitive, path, ts)
            VALUES ($p,$s,$d,$sen,$path,$ts)
            """;
        c.Parameters.AddWithValue("$p", f.PersonSlug);
        c.Parameters.AddWithValue("$s", f.Source.ToString().ToLowerInvariant());
        c.Parameters.AddWithValue("$d", f.DerivedFromPrivate ? 1 : 0);
        c.Parameters.AddWithValue("$sen", f.Sensitive ? 1 : 0);
        c.Parameters.AddWithValue("$path", path);
        c.Parameters.AddWithValue("$ts", f.Recorded.ToString("o"));
        c.ExecuteNonQuery();
    }

    // ── reads used by the pulse (must be cheap) ──────────────────────────

    public int UnconsumedObservationCount() =>
        (int)Scalar<long>("SELECT COUNT(*) FROM observations WHERE consumed=0");

    public int UnhandledMessageCount() =>
        (int)Scalar<long>("SELECT COUNT(*) FROM messages WHERE handled=0 AND direction='in'");

    public int WorkTicksToday()
    {
        var since = DateTimeOffset.UtcNow.Date.ToString("o");
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM episodes WHERE tick_type='work' AND ts >= $s";
        c.Parameters.AddWithValue("$s", since);
        return Convert.ToInt32(c.ExecuteScalar());
    }

    public int ChoreTicksToday()
    {
        var since = DateTimeOffset.UtcNow.Date.ToString("o");
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM episodes WHERE tick_type='chore' AND ts >= $s";
        c.Parameters.AddWithValue("$s", since);
        return Convert.ToInt32(c.ExecuteScalar());
    }

    /// <summary>Active chores whose interval has elapsed, oldest-due first — same rotation-over-priority idea as WorkTick's goal pick.</summary>
    public List<string> DueChores(DateTimeOffset now)
    {
        var list = new List<string>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id FROM chores WHERE state='active' AND due_by <= $now ORDER BY due_by ASC";
        c.Parameters.AddWithValue("$now", now.ToString("o"));
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public int OutboundMessagesToday()
    {
        var since = DateTimeOffset.UtcNow.Date.ToString("o");
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM messages WHERE direction='out' AND ts >= $s";
        c.Parameters.AddWithValue("$s", since);
        return Convert.ToInt32(c.ExecuteScalar());
    }

    /// <summary>contactId scopes this to one sender's unhandled messages — see AddMessage.</summary>
    public List<(long Id, string Content)> PendingInbound(string contactId, int limit = 5)
    {
        var list = new List<(long, string)>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id, content FROM messages WHERE handled=0 AND direction='in' AND contact_id=$cid ORDER BY ts LIMIT $l";
        c.Parameters.AddWithValue("$cid", contactId);
        c.Parameters.AddWithValue("$l", limit);
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    /// <summary>
    /// The messages that make up "the current conversation" with one contact
    /// (see AddMessage): scans back from the most recent message with them
    /// (either direction, handled or not — a reply tick marks its inputs
    /// handled immediately, so restricting to unhandled would mean every
    /// reply after the first sees none of what came before it) and stops at
    /// the first gap longer than gapMinutes. Everything before that gap is a
    /// different conversation and is left out. Returned oldest-first, ready
    /// to render as a transcript. Scoped to contactId so two different
    /// people talking to it around the same time never see each other's
    /// messages woven into their own history.
    /// </summary>
    public List<(string Direction, string Content, string Ts)> RecentConversation(string contactId, int gapMinutes, int scanLimit = 40)
    {
        var rows = new List<(string Direction, string Content, string Ts)>();
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "SELECT direction, content, ts FROM messages WHERE contact_id=$cid ORDER BY ts DESC LIMIT $l";
            c.Parameters.AddWithValue("$cid", contactId);
            c.Parameters.AddWithValue("$l", scanLimit);
            using var r = c.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }

        var gap = TimeSpan.FromMinutes(gapMinutes);
        var conversation = new List<(string Direction, string Content, string Ts)>();
        DateTimeOffset? previous = null;
        foreach (var row in rows)   // newest first
        {
            if (!DateTimeOffset.TryParse(row.Ts, out var ts)) continue;
            if (previous is { } prev && prev - ts > gap) break;
            conversation.Add(row);
            previous = ts;
        }

        conversation.Reverse();   // oldest first
        return conversation;
    }

    public List<(long Id, string Source, string Content)> UnconsumedObservations(int limit = 40)
    {
        var list = new List<(long, string, string)>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id, source, content FROM observations WHERE consumed=0 ORDER BY ts LIMIT $l";
        c.Parameters.AddWithValue("$l", limit);
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public List<string> GoalsPastRenewal()
    {
        var list = new List<string>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id FROM goals WHERE state='active' AND renew_by IS NOT NULL AND renew_by < $now";
        c.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    public List<(string Id, string Title, string Kind, string? LastTouched)> ActiveGoals()
    {
        var list = new List<(string, string, string, string?)>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id, title, kind, last_touched FROM goals WHERE state='active' ORDER BY COALESCE(last_touched,'') ASC";
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    public int CountGoals(string state, string? kind = null)
    {
        using var c = _db.CreateCommand();
        c.CommandText = kind is null
            ? "SELECT COUNT(*) FROM goals WHERE state=$s"
            : "SELECT COUNT(*) FROM goals WHERE state=$s AND kind=$k";
        c.Parameters.AddWithValue("$s", state);
        if (kind is not null) c.Parameters.AddWithValue("$k", kind);
        return Convert.ToInt32(c.ExecuteScalar());
    }

    /// <summary>Recency + salience weighted episode summaries. Keyword overlap added by the caller.</summary>
    public List<(string Path, string Ts, string Summary, double Salience)> RecentEpisodes(int limit, string? goalId = null)
    {
        var list = new List<(string, string, string, double)>();
        using var c = _db.CreateCommand();
        c.CommandText = goalId is null
            ? "SELECT path, ts, summary, salience FROM episodes ORDER BY ts DESC LIMIT $l"
            : "SELECT path, ts, summary, salience FROM episodes WHERE goal=$g ORDER BY ts DESC LIMIT $l";
        c.Parameters.AddWithValue("$l", limit);
        if (goalId is not null) c.Parameters.AddWithValue("$g", goalId);
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3)));
        return list;
    }

    public List<(string Path, string Summary)> EpisodesBetween(DateTimeOffset from, DateTimeOffset to, int limit)
    {
        var list = new List<(string, string)>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT path, summary FROM episodes WHERE ts >= $f AND ts < $t ORDER BY salience DESC, ts DESC LIMIT $l";
        c.Parameters.AddWithValue("$f", from.ToString("o"));
        c.Parameters.AddWithValue("$t", to.ToString("o"));
        c.Parameters.AddWithValue("$l", limit);
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Beliefs traceable to a single external source — surfaced in weekly reflect (social capture check).</summary>
    public List<(string Person, int Count)> SingleSourceBeliefCandidates()
    {
        var list = new List<(string, int)>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT person, COUNT(*) n FROM facts WHERE source_class='learned' GROUP BY person HAVING n > 5 ORDER BY n DESC";
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetInt32(1)));
        return list;
    }

    public void Clear(string table) => Exec($"DELETE FROM {table};");

    private T Scalar<T>(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        var o = c.ExecuteScalar();
        return o is null ? default! : (T)Convert.ChangeType(o, typeof(T));
    }

    private void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
