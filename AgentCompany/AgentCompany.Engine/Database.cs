using System.IO;
using Microsoft.Data.Sqlite;

namespace AgentCompany.Desktop.Services;

/// <summary>
/// SQLite 状态库：%APPDATA%\AgentCompany\state.db。
/// 轮 1 只负责建库建表与设置读写；tasks / approvals / sessions 的业务读写由后续轮次填充。
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    /// <summary>状态库文件完整路径。</summary>
    public string DbPath { get; }

    public Database(string? dbPath = null)
    {
        if (dbPath is not null)
        {
            // 单元测试用临时库
            DbPath = dbPath;
        }
        else
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AgentCompany");
            Directory.CreateDirectory(dir);
            DbPath = Path.Combine(dir, "state.db");
        }
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
    }

    /// <summary>首次启动自动建表（幂等）。</summary>
    public void EnsureCreated()
    {
        var ddl = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS tasks (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                title      TEXT    NOT NULL,
                detail     TEXT,
                status     TEXT    NOT NULL DEFAULT 'todo',
                assignee   TEXT,
                created_at TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
                updated_at TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS approval_records (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                agent      TEXT    NOT NULL,
                action     TEXT    NOT NULL,
                detail     TEXT,
                risk       TEXT,
                status     TEXT    NOT NULL DEFAULT 'pending',
                created_at TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
                decided_at TEXT
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS agent_sessions (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                agent      TEXT    NOT NULL,
                pid        INTEGER,
                started_at TEXT,
                ended_at   TEXT,
                note       TEXT
            )
            """,
            """
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )
            """,
            // 轮 5：PID 记账——子进程启动即写、正常结束标 cleaned=1；启动清扫的杀树依据
            """
            CREATE TABLE IF NOT EXISTS process_journal (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id    INTEGER,
                seq        INTEGER,
                agent      TEXT    NOT NULL,
                pid        INTEGER NOT NULL,
                started_at TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
                cleaned    INTEGER NOT NULL DEFAULT 0
            )
            """,
            // 轮 5：events 黑匣子——任务状态变更/审批决定/启动清扫/优雅退出/崩溃中断
            """
            CREATE TABLE IF NOT EXISTS events (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT    NOT NULL DEFAULT (datetime('now','localtime')),
                type       TEXT    NOT NULL,
                subject    TEXT,
                detail     TEXT
            )
            """,
            // 轮 6：预算台账——每次 CLI 调用（含 CEO）的 token 入账，预算总览页矩阵的数据源
            """
            CREATE TABLE IF NOT EXISTS budget_ledger (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                task_id    INTEGER NOT NULL,
                agent      TEXT    NOT NULL,
                role       TEXT,
                tokens     INTEGER NOT NULL,
                created_at TEXT    NOT NULL DEFAULT (datetime('now','localtime'))
            )
            """,
        };

        using var conn = Open();
        foreach (var sql in ddl)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        MigrateAgentSessions(conn);
        MigrateApprovalRecords(conn);
    }

    /// <summary>
    /// 轮 2 迁移：轮 1 的 agent_sessions 只有 pid/ended_at/note，
    /// 补齐 session_id / model / finished_at / token 计量 / exit_code / status 列（幂等）。
    /// </summary>
    private static void MigrateAgentSessions(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(agent_sessions)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                existing.Add(r.GetString(1));
        }

        var columns = new (string Name, string Ddl)[]
        {
            ("session_id",        "TEXT"),
            ("model",             "TEXT"),
            ("finished_at",       "TEXT"),
            ("input_tokens",      "INTEGER NOT NULL DEFAULT 0"),
            ("output_tokens",     "INTEGER NOT NULL DEFAULT 0"),
            ("cache_read_tokens", "INTEGER NOT NULL DEFAULT 0"),
            ("exit_code",         "INTEGER"),
            ("status",            "TEXT NOT NULL DEFAULT 'ok'"),
        };

        foreach (var (name, columnDdl) in columns)
        {
            if (existing.Contains(name))
                continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE agent_sessions ADD COLUMN {name} {columnDdl}";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 轮 3 迁移：轮 1 的 approval_records 只有 action/detail/status/created_at，
    /// 补齐 spec 命名的 command / decision（approved|denied|timeout）/ requested_at 列（幂等）。
    /// </summary>
    private static void MigrateApprovalRecords(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(approval_records)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                existing.Add(r.GetString(1));
        }

        var columns = new (string Name, string Ddl)[]
        {
            ("command",      "TEXT"),
            ("decision",     "TEXT"),
            ("requested_at", "TEXT"),
        };

        foreach (var (name, columnDdl) in columns)
        {
            if (existing.Contains(name))
                continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE approval_records ADD COLUMN {name} {columnDdl}";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>审批落库（轮 3：请求被决定后调用）。</summary>
    public void InsertApprovalRecord(ApprovalRecord r)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO approval_records
                (agent, action, detail, status, created_at, decided_at, command, decision, requested_at)
            VALUES
                ($agent, 'terminal', $command, $decision, $requested, $decided, $command, $decision, $requested)
            """;
        cmd.Parameters.AddWithValue("$agent", r.Agent);
        cmd.Parameters.AddWithValue("$command", r.Command);
        cmd.Parameters.AddWithValue("$decision", r.Decision);
        cmd.Parameters.AddWithValue("$requested", r.RequestedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$decided", r.DecidedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>审批历史（倒序，供审批中心页与验收 ⑦ 用）。</summary>
    public List<ApprovalRecord> GetApprovalHistory(int limit = 200)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, agent, COALESCE(command, detail), COALESCE(decision, status),
                   COALESCE(requested_at, created_at), decided_at
            FROM approval_records ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<ApprovalRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            DateTime.TryParse(r.IsDBNull(4) ? null : r.GetString(4), out var requested);
            DateTime.TryParse(r.IsDBNull(5) ? null : r.GetString(5), out var decided);
            list.Add(new ApprovalRecord(
                Id: r.GetInt32(0),
                Agent: r.GetString(1),
                Command: r.IsDBNull(2) ? "" : r.GetString(2),
                Decision: r.IsDBNull(3) ? "" : r.GetString(3),
                RequestedAt: requested,
                DecidedAt: decided));
        }
        return list;
    }

    /// <summary>会话归档（轮 2 起，每次任务结束调用）。</summary>
    public void InsertAgentSession(AgentSessionRecord s)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_sessions
                (agent, session_id, model, started_at, finished_at,
                 input_tokens, output_tokens, cache_read_tokens, exit_code, status)
            VALUES
                ($agent, $sid, $model, $started, $finished,
                 $inTok, $outTok, $cacheTok, $exit, $status)
            """;
        cmd.Parameters.AddWithValue("$agent", s.Agent);
        cmd.Parameters.AddWithValue("$sid", (object?)s.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)s.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$started", s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$finished", s.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$inTok", s.InputTokens);
        cmd.Parameters.AddWithValue("$outTok", s.OutputTokens);
        cmd.Parameters.AddWithValue("$cacheTok", s.CacheReadTokens);
        cmd.Parameters.AddWithValue("$exit", s.ExitCode);
        cmd.Parameters.AddWithValue("$status", s.Status);
        cmd.ExecuteNonQuery();
    }

    /// <summary>历史会话总数。</summary>
    public int CountAgentSessions()
    {
        using var conn = Open();
        return (int)Scalar(conn, "SELECT COUNT(*) FROM agent_sessions");
    }

    /// <summary>三张业务表的行数（运行监视页状态行用）。</summary>
    public (int Tasks, int Approvals, int Sessions) CountAll()
    {
        using var conn = Open();
        return ((int)Scalar(conn, "SELECT COUNT(*) FROM tasks"),
                (int)Scalar(conn, "SELECT COUNT(*) FROM approval_records"),
                (int)Scalar(conn, "SELECT COUNT(*) FROM agent_sessions"));
    }

    /// <summary>状态行文案：tasks n / approvals n / sessions n。</summary>
    public string StatusText()
    {
        var (t, a, s) = CountAll();
        return $"tasks {t} / approvals {a} / sessions {s}";
    }

    /// <summary>读取设置；不存在返回 null。</summary>
    public string? GetSetting(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>写入设置（upsert）。</summary>
    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 轮 8：一致性快照备份（SQLite VACUUM INTO——连接开着也能拷出完整可用库）。
    /// 目标文件已存在会抛 SqliteException，由调用方先选唯一文件名。
    /// </summary>
    public void BackupTo(string destPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"VACUUM INTO '{destPath.Replace("'", "''")}'";
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    // ---- process_journal（轮 5：PID 记账） ----

    /// <summary>子进程启动即记账，返回行 id（结束时标 cleaned 用）。</summary>
    public long InsertJournal(int? taskId, int? seq, string agent, int pid)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO process_journal (task_id, seq, agent, pid)
            VALUES ($task, $seq, $agent, $pid);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$seq", (object?)seq ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$agent", agent);
        cmd.Parameters.AddWithValue("$pid", pid);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>正常结束销账（宿主崩溃时不会走到这里——留给下次启动清扫）。</summary>
    public void MarkJournalCleaned(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE process_journal SET cleaned = 1 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>未销账的行（启动清扫的输入）。</summary>
    public List<JournalRow> LoadUncleanedJournal(int limit = 200)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, task_id, seq, agent, pid, started_at
            FROM process_journal WHERE cleaned = 0 ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<JournalRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new JournalRow(
                Id: r.GetInt64(0),
                TaskId: r.IsDBNull(1) ? null : r.GetInt32(1),
                Seq: r.IsDBNull(2) ? null : r.GetInt32(2),
                Agent: r.GetString(3),
                Pid: r.GetInt32(4),
                StartedAt: r.IsDBNull(5) ? DateTime.Now : ParseTime(r.GetString(5))));
        }
        return list;
    }

    // ---- events 黑匣子（轮 5） ----

    /// <summary>写一条黑匣子事件。</summary>
    public void InsertEvent(string type, string subject, string detail)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO events (created_at, type, subject, detail)
            VALUES (datetime('now','localtime'), $type, $subject, $detail)
            """;
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$subject", (object?)subject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>最近事件（倒序，最新在前；诊断日志区回填与筛选用）。</summary>
    public List<EventRow> LoadEvents(int limit = 200)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, created_at, type, subject, detail
            FROM events ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<EventRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new EventRow(
                Id: r.GetInt64(0),
                Ts: ParseTime(r.GetString(1)),
                Type: r.GetString(2),
                Subject: r.IsDBNull(3) ? "" : r.GetString(3),
                Detail: r.IsDBNull(4) ? "" : r.GetString(4)));
        }
        return list;
    }

    private static DateTime ParseTime(string s) =>
        DateTime.TryParse(s, out var t) ? t : DateTime.Now;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}

/// <summary>approval_records 落库记录（轮 3）。</summary>
public sealed record ApprovalRecord(
    int Id,
    string Agent,
    string Command,
    string Decision, // approved / denied / timeout
    DateTime RequestedAt,
    DateTime DecidedAt);

/// <summary>process_journal 行（轮 5：PID 记账）。</summary>
public sealed record JournalRow(
    long Id,
    int? TaskId,
    int? Seq,
    string Agent,
    int Pid,
    DateTime StartedAt);

/// <summary>events 黑匣子行（轮 5）。</summary>
public sealed record EventRow(
    long Id,
    DateTime Ts,
    string Type,
    string Subject,
    string Detail);
