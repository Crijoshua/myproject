using System.IO;
using Microsoft.Data.Sqlite;

namespace AgentCompany.Desktop.Services;

/// <summary>
/// 工作区布局：workspace\claude、workspace\pipeline\task-N（轮 4 起流水线共享工作区）。
/// 轮 8 可移植：根 = 设置覆盖项（settings 表 workspace_root）优先，默认主程序 exe 旁 workspace\
/// （AppContext.BaseDirectory，不再向上找源码仓库标记——整个 publish 文件夹拷到哪，工作区跟到哪）。
/// 只影响新任务：历史任务记录里的旧路径不迁移不改写。
/// </summary>
public static class WorkspaceLayout
{
    /// <summary>设置键：工作区根覆盖项（空 = 默认 exe 旁）。</summary>
    public const string SettingKey = "workspace_root";

    private static Func<string?> _rootProvider = () => null;

    /// <summary>App 启动时注入覆盖项读取器（每次调用实时读库，设置页改完即生效）。</summary>
    public static void Init(Func<string?> provider) => _rootProvider = provider;

    /// <summary>还原默认（单测隔离用）。</summary>
    public static void Reset() => _rootProvider = () => null;

    /// <summary>工作区根：覆盖项优先；默认主程序 exe 所在目录\workspace。</summary>
    public static string Root()
    {
        var o = _rootProvider()?.Trim();
        if (!string.IsNullOrEmpty(o))
            return o;
        return Path.Combine(AppContext.BaseDirectory, "workspace");
    }

    /// <summary>员工默认工作区（监视页直发任务用）。</summary>
    public static string AgentWorkspace(string agentFolder) =>
        Path.Combine(Root(), agentFolder);

    /// <summary>流水线任务工作区：两步共用 cwd + 沙箱根，B 天然读到 A 的产物。</summary>
    public static string PipelineWorkspace(int taskId) =>
        Path.Combine(Root(), "pipeline", $"task-{taskId}");

    /// <summary>公司记忆（轮 6）：任务成功后追加一行，CEO 下次拆解时读入 stdin。</summary>
    public static string CompanyMemoryPath() =>
        Path.Combine(Root(), "MEMORY.md");
}

/// <summary>task_steps 行。</summary>
public sealed record TaskStepRecord(
    int Seq,
    string Agent,
    string Prompt,
    string Status,      // pending / running / done / failed / cancelled / interrupted
    string? SessionId,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? Error,
    DateTime? InterruptedAt = null,
    string? Role = null,                       // 轮 6：'ceo' = CEO 拆解调用（账目行，不进节点列表）
    string? NodeId = null,                     // 轮 6：DAG 节点 id（n1/n2/…）
    IReadOnlyList<string>? WriteTargets = null, // 轮 6：本节点独占的写目标（相对路径）
    IReadOnlyList<string>? DependsOn = null);   // 轮 6：前置节点 id

/// <summary>tasks 行（含步骤）。</summary>
public sealed record TaskRecord(
    int Id,
    string Title,
    string Status,      // pending / planning / preview / parse_failed / working / completed / failed / cancelled / interrupted
    int CurrentStep,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<string> Artifacts,
    IReadOnlyList<TaskStepRecord> Steps,
    DateTime? InterruptedAt = null,
    string Kind = "pipeline",          // 轮 6：pipeline（轮 4 手工流水线）/ dag（CEO 拆解）
    string? Demand = null,             // 轮 6：一句话需求原文
    long? BudgetTokens = null,         // 轮 6：三上限（null = 用 BudgetService 默认）
    int? BudgetWallSeconds = null,
    int? BudgetReruns = null,
    string? FailReason = null,         // 轮 6：失败/拆解失败原因（预算超限红标用）
    int RerunsUsed = 0);               // 轮 6：人工重跑已用次数

/// <summary>
/// 任务持久化（轮 4）：tasks + task_steps 两表，A2A 任务模型全部经 SQLite 落库，
/// 重启后看板从这里恢复历史状态与产物清单。状态每次变更由调用方（A2aGateway）发事件总线。
/// </summary>
public sealed class TaskStore
{
    private readonly string _connectionString;

    public TaskStore(Database db)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = db.DbPath }.ToString();
        EnsureCreated();
    }

    private void EnsureCreated()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS task_steps (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id     INTEGER NOT NULL,
                    seq         INTEGER NOT NULL,
                    agent       TEXT    NOT NULL,
                    prompt      TEXT    NOT NULL,
                    status      TEXT    NOT NULL DEFAULT 'pending',
                    session_id  TEXT,
                    started_at  TEXT,
                    finished_at TEXT,
                    error       TEXT
                )
                """;
            cmd.ExecuteNonQuery();
        }

        // 轮 1 的 tasks 表只有 title/detail/status/assignee/时间戳：补 current_step / artifacts（幂等）；
        // 轮 5 补 interrupted_at（崩溃中断记账，卡片"已中断（含时间）"用）；
        // 轮 6 补 kind/demand/三上限/fail_reason/reruns_used（CEO 拆解 + 预算熔断）
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(tasks)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                existing.Add(r.GetString(1));
        }
        foreach (var (name, ddl) in new[]
                 {
                     ("current_step", "INTEGER NOT NULL DEFAULT 0"),
                     ("artifacts", "TEXT"),
                     ("interrupted_at", "TEXT"),
                     ("kind", "TEXT NOT NULL DEFAULT 'pipeline'"),
                     ("demand", "TEXT"),
                     ("budget_tokens", "INTEGER"),
                     ("budget_wall_seconds", "INTEGER"),
                     ("budget_reruns", "INTEGER"),
                     ("fail_reason", "TEXT"),
                     ("reruns_used", "INTEGER NOT NULL DEFAULT 0"),
                 })
        {
            if (existing.Contains(name))
                continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE tasks ADD COLUMN {name} {ddl}";
            cmd.ExecuteNonQuery();
        }

        // task_steps 同样补 interrupted_at（轮 5）与 DAG 节点四列（轮 6，幂等）
        var stepColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(task_steps)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                stepColumns.Add(r.GetString(1));
        }
        foreach (var (name, ddl) in new[]
                 {
                     ("interrupted_at", "TEXT"),
                     ("role", "TEXT"),
                     ("node_id", "TEXT"),
                     ("write_targets", "TEXT"),
                     ("depends_on", "TEXT"),
                 })
        {
            if (stepColumns.Contains(name))
                continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE task_steps ADD COLUMN {name} {ddl}";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>建任务（pending）+ 步骤行（pending），返回任务 id。</summary>
    public int CreatePipeline(string title, IReadOnlyList<(string Agent, string Prompt)> steps)
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tasks (title, status, current_step, created_at, updated_at)
                VALUES ($title, 'pending', 0, datetime('now','localtime'), datetime('now','localtime'));
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$title", title);
            var id = (long)cmd.ExecuteScalar()!;
            using (var step = conn.CreateCommand())
            {
                step.CommandText = """
                    INSERT INTO task_steps (task_id, seq, agent, prompt, status)
                    VALUES ($task, $seq, $agent, $prompt, 'pending')
                    """;
                var pTask = step.Parameters.Add("$task", SqliteType.Integer);
                var pSeq = step.Parameters.Add("$seq", SqliteType.Integer);
                var pAgent = step.Parameters.Add("$agent", SqliteType.Text);
                var pPrompt = step.Parameters.Add("$prompt", SqliteType.Text);
                for (var i = 0; i < steps.Count; i++)
                {
                    pTask.Value = id;
                    pSeq.Value = i + 1;
                    pAgent.Value = steps[i].Agent;
                    pPrompt.Value = steps[i].Prompt;
                    step.ExecuteNonQuery();
                }
            }
            return (int)id;
        }
    }

    // ---- CEO 拆解与 DAG（轮 6） ----

    /// <summary>建项目任务（kind=dag，status=planning），返回任务 id；预算 null = 默认三上限。</summary>
    public int CreateProject(string title, string demand, long? budgetTokens, int? budgetWallSeconds, int? budgetReruns)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tasks (title, status, kind, demand, budget_tokens, budget_wall_seconds, budget_reruns,
                               created_at, updated_at)
            VALUES ($title, 'planning', 'dag', $demand, $tok, $wall, $rerun, datetime('now','localtime'), datetime('now','localtime'));
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$demand", demand);
        cmd.Parameters.AddWithValue("$tok", (object?)budgetTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$wall", (object?)budgetWallSeconds ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rerun", (object?)budgetReruns ?? DBNull.Value);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>CEO 拆解调用记账行（seq=0, agent=claude, role=ceo；每次调用 upsert 同一行的时间）。</summary>
    public void UpsertCeoStep(int taskId, string demand)
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO task_steps (task_id, seq, agent, prompt, status, role, started_at)
                SELECT $task, 0, 'claude', $demand, 'running', 'ceo', datetime('now','localtime')
                WHERE NOT EXISTS (SELECT 1 FROM task_steps WHERE task_id = $task AND role = 'ceo')
                """;
            cmd.Parameters.AddWithValue("$task", taskId);
            cmd.Parameters.AddWithValue("$demand", demand.Length <= 200 ? demand : demand[..200]);
            cmd.ExecuteNonQuery();
        }
        // 重新拆解：行已存在时回 running（上一轮拆解已收口成 done/failed）
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE task_steps SET status = 'running', started_at = datetime('now','localtime'), finished_at = NULL,
                                       error = NULL
                WHERE task_id = $task AND role = 'ceo'
                """;
            cmd.Parameters.AddWithValue("$task", taskId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>CEO 拆解调用收口（done / failed；记账行不再永久停在 running）。</summary>
    public void SetCeoStepFinished(int taskId, string status, string? error)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE task_steps SET status = $status, finished_at = datetime('now','localtime'), error = $err
            WHERE task_id = $task AND role = 'ceo'
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>CEO 拆解成功：写入节点步骤行（先清旧节点），任务转 preview（待确认）。</summary>
    public void SetPlanPreview(int taskId, IReadOnlyList<DagNode> nodes)
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM task_steps WHERE task_id = $task AND role IS NULL";
            cmd.Parameters.AddWithValue("$task", taskId);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO task_steps (task_id, seq, agent, prompt, status, node_id, write_targets, depends_on)
                VALUES ($task, $seq, $agent, $prompt, 'pending', $node, $writes, $deps)
                """;
            var pTask = cmd.Parameters.Add("$task", SqliteType.Integer);
            var pSeq = cmd.Parameters.Add("$seq", SqliteType.Integer);
            var pAgent = cmd.Parameters.Add("$agent", SqliteType.Text);
            var pPrompt = cmd.Parameters.Add("$prompt", SqliteType.Text);
            var pNode = cmd.Parameters.Add("$node", SqliteType.Text);
            var pWrites = cmd.Parameters.Add("$writes", SqliteType.Text);
            var pDeps = cmd.Parameters.Add("$deps", SqliteType.Text);
            foreach (var n in nodes)
            {
                pTask.Value = taskId;
                pSeq.Value = n.Seq;
                pAgent.Value = n.Agent;
                pPrompt.Value = n.Prompt;
                pNode.Value = n.NodeId;
                pWrites.Value = System.Text.Json.JsonSerializer.Serialize(n.WriteTargets.ToList());
                pDeps.Value = System.Text.Json.JsonSerializer.Serialize(n.DependsOn.ToList());
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE tasks SET status = 'preview', current_step = 0, fail_reason = NULL, updated_at = datetime('now','localtime')
                WHERE id = $task
                """;
            cmd.Parameters.AddWithValue("$task", taskId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>CEO 拆解失败：任务转 parse_failed（可人工 [重新拆解]），记原因。</summary>
    public void SetPlanFailed(int taskId, string reason)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE tasks SET status = 'parse_failed', fail_reason = $reason, updated_at = datetime('now','localtime')
            WHERE id = $task
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.ExecuteNonQuery();
    }

    /// <summary>任务失败原因（预算超限红标 / 拆解失败共用）。</summary>
    public void SetFailReason(int taskId, string? reason)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tasks SET fail_reason = $reason, updated_at = datetime('now','localtime') WHERE id = $task";
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>改某节点 prompt（③ 验收：重跑前改写提示词）；返回旧值不存在时 false。</summary>
    public bool UpdateStepPrompt(int taskId, int seq, string prompt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE task_steps SET prompt = $prompt WHERE task_id = $task AND seq = $seq AND role IS NULL";
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$prompt", prompt);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>人工重跑计数 +1，返回已用次数（超限与否由 BudgetService 判定）。</summary>
    public int IncrementReruns(int taskId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE tasks SET reruns_used = reruns_used + 1, updated_at = datetime('now','localtime') WHERE id = $task;
            SELECT reruns_used FROM tasks WHERE id = $task;
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>启动清扫用：planning 任务改标 parse_failed（宿主重启打断 CEO 拆解），返回受影响任务。</summary>
    public List<(int Id, string Title)> MarkPlanningInterrupted()
    {
        using var conn = Open();
        var affected = new List<(int, string)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title FROM tasks WHERE status = 'planning'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                affected.Add((r.GetInt32(0), r.GetString(1)));
        }
        if (affected.Count == 0)
            return affected;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE tasks SET status = 'parse_failed',
                    fail_reason = '宿主重启打断 CEO 拆解（可点 [重新拆解]）', updated_at = datetime('now','localtime')
                WHERE status = 'planning'
                """;
            cmd.ExecuteNonQuery();
        }
        return affected;
    }

    public void SetTaskStatus(int taskId, string status, int? currentStep = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = currentStep is null
            ? "UPDATE tasks SET status = $status, updated_at = datetime('now','localtime') WHERE id = $id"
            : "UPDATE tasks SET status = $status, current_step = $cur, updated_at = datetime('now','localtime') WHERE id = $id";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$id", taskId);
        if (currentStep is not null)
            cmd.Parameters.AddWithValue("$cur", currentStep);
        cmd.ExecuteNonQuery();
    }

    public void SetStepRunning(int taskId, int seq, string? sessionId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE task_steps
            SET status = 'running', started_at = datetime('now','localtime'), session_id = COALESCE($sid, session_id)
            WHERE task_id = $task AND seq = $seq
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$sid", (object?)sessionId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void SetStepFinished(int taskId, int seq, string status, string? sessionId, string? error)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE task_steps
            SET status = $status, finished_at = datetime('now','localtime'),
                session_id = COALESCE($sid, session_id), error = $err
            WHERE task_id = $task AND seq = $seq
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$sid", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void SetTaskArtifacts(int taskId, IEnumerable<string> files)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tasks SET artifacts = $json, updated_at = datetime('now','localtime') WHERE id = $id";
        cmd.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(files.ToList()));
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.ExecuteNonQuery();
    }

    // ---- 崩溃中断与重跑（轮 5） ----

    /// <summary>
    /// 启动清扫用：working 任务改标 interrupted（含 interrupted_at），返回受影响的 (id, title)。
    /// </summary>
    public List<(int Id, string Title)> MarkWorkingInterrupted()
    {
        using var conn = Open();
        var affected = new List<(int, string)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title FROM tasks WHERE status = 'working'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                affected.Add((r.GetInt32(0), r.GetString(1)));
        }
        if (affected.Count == 0)
            return affected;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE tasks
                SET status = 'interrupted', interrupted_at = datetime('now','localtime'), updated_at = datetime('now','localtime')
                WHERE status = 'working'
                """;
            cmd.ExecuteNonQuery();
        }
        return affected;
    }

    /// <summary>
    /// 启动清扫用：queued（轮 7 FIFO 排队中）任务改标 interrupted，返回受影响的 (id, title)。
    /// 排队任务虽没在跑，但宿主重启后排队队列在内存里已丢——不标记就永远停在"排队中"。
    /// </summary>
    public List<(int Id, string Title)> MarkQueuedInterrupted()
    {
        using var conn = Open();
        var affected = new List<(int, string)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, title FROM tasks WHERE status = 'queued'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                affected.Add((r.GetInt32(0), r.GetString(1)));
        }
        if (affected.Count == 0)
            return affected;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE tasks
                SET status = 'interrupted', interrupted_at = datetime('now','localtime'), updated_at = datetime('now','localtime')
                WHERE status = 'queued'
                """;
            cmd.ExecuteNonQuery();
        }
        return affected;
    }

    /// <summary>running 步骤改标 interrupted，返回受影响行数。</summary>
    public int MarkRunningStepsInterrupted()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE task_steps
            SET status = 'interrupted', interrupted_at = datetime('now','localtime')
            WHERE status = 'running'
            """;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 重跑重置（轮 5：新会话、同 prompt、同工作区——不是 --resume）：
    /// seq >= fromSeq 的步骤回到 pending 并清 session/error/时间戳，任务回 working、产物清空待重盘。
    /// </summary>
    public void ResetForRerun(int taskId, int fromSeq)
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE task_steps
                SET status = 'pending', session_id = NULL, started_at = NULL,
                    finished_at = NULL, error = NULL, interrupted_at = NULL
                WHERE task_id = $task AND seq >= $seq
                """;
            cmd.Parameters.AddWithValue("$task", taskId);
            cmd.Parameters.AddWithValue("$seq", fromSeq);
            cmd.ExecuteNonQuery();
        }
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = """
            UPDATE tasks
            SET status = 'working', current_step = $seq, artifacts = NULL,
                interrupted_at = NULL, updated_at = datetime('now','localtime')
            WHERE id = $task
            """;
        cmd2.Parameters.AddWithValue("$seq", fromSeq);
        cmd2.Parameters.AddWithValue("$task", taskId);
        cmd2.ExecuteNonQuery();
    }

    /// <summary>单任务（含步骤）；不存在返回 null。</summary>
    public TaskRecord? LoadTask(int taskId) =>
        LoadTasks(limit: 1000).FirstOrDefault(t => t.Id == taskId);

    /// <summary>全部任务（含步骤与产物，倒序）——看板与重启恢复用。</summary>
    public List<TaskRecord> LoadTasks(int limit = 100)
    {
        using var conn = Open();
        var tasks = new List<TaskRecord>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, title, status, current_step, created_at, updated_at, artifacts, interrupted_at,
                       kind, demand, budget_tokens, budget_wall_seconds, budget_reruns, fail_reason, reruns_used
                FROM tasks ORDER BY id DESC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                tasks.Add(new TaskRecord(
                    Id: r.GetInt32(0),
                    Title: r.GetString(1),
                    Status: r.IsDBNull(2) ? "pending" : r.GetString(2),
                    CurrentStep: r.IsDBNull(3) ? 0 : r.GetInt32(3),
                    CreatedAt: ParseTime(r, 4),
                    UpdatedAt: ParseTime(r, 5),
                    Artifacts: ParseArtifacts(r.IsDBNull(6) ? null : r.GetString(6)),
                    Steps: new List<TaskStepRecord>(),
                    InterruptedAt: ParseTimeNullable(r, 7),
                    Kind: r.IsDBNull(8) ? "pipeline" : r.GetString(8),
                    Demand: r.IsDBNull(9) ? null : r.GetString(9),
                    BudgetTokens: r.IsDBNull(10) ? null : r.GetInt64(10),
                    BudgetWallSeconds: r.IsDBNull(11) ? null : r.GetInt32(11),
                    BudgetReruns: r.IsDBNull(12) ? null : r.GetInt32(12),
                    FailReason: r.IsDBNull(13) ? null : r.GetString(13),
                    RerunsUsed: r.IsDBNull(14) ? 0 : r.GetInt32(14)));
            }
        }

        foreach (var t in tasks)
            ((List<TaskStepRecord>)t.Steps).AddRange(LoadSteps(conn, t.Id));
        return tasks;
    }

    private static IEnumerable<TaskStepRecord> LoadSteps(SqliteConnection conn, int taskId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT seq, agent, prompt, status, session_id, started_at, finished_at, error, interrupted_at,
                   role, node_id, write_targets, depends_on
            FROM task_steps WHERE task_id = $task ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return new TaskStepRecord(
                Seq: r.GetInt32(0),
                Agent: r.GetString(1),
                Prompt: r.GetString(2),
                Status: r.IsDBNull(3) ? "pending" : r.GetString(3),
                SessionId: r.IsDBNull(4) ? null : r.GetString(4),
                StartedAt: ParseTimeNullable(r, 5),
                FinishedAt: ParseTimeNullable(r, 6),
                Error: r.IsDBNull(7) ? null : r.GetString(7),
                InterruptedAt: ParseTimeNullable(r, 8),
                Role: r.IsDBNull(9) ? null : r.GetString(9),
                NodeId: r.IsDBNull(10) ? null : r.GetString(10),
                WriteTargets: ParseStrings(r.IsDBNull(11) ? null : r.GetString(11)),
                DependsOn: ParseStrings(r.IsDBNull(12) ? null : r.GetString(12)));
        }
    }

    private static IReadOnlyList<string>? ParseStrings(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ParseArtifacts(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new List<string>();
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static DateTime ParseTime(SqliteDataReader r, int i) =>
        ParseTimeNullable(r, i) ?? DateTime.Now;

    private static DateTime? ParseTimeNullable(SqliteDataReader r, int i)
    {
        if (r.IsDBNull(i))
            return null;
        return DateTime.TryParse(r.GetString(i), out var t) ? t : null;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
