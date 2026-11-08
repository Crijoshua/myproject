using System.IO;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace AgentCompany.Desktop.Services;

/// <summary>一次预算判定结果：是否熔断 + 人读原因。</summary>
public sealed record BudgetDecision(bool Breached, string? Reason)
{
    public static BudgetDecision Ok => new(false, null);
}

/// <summary>
/// 预算熔断（轮 6 真实现）：token / 墙钟 / 重跑三路判定，任一超限 → 杀整棵进程树 →
/// 任务 failed("预算超限") → 看板红标 + 原因 → 预算总览记账。
///  - token：各员工计量累计（claude 取 result.usage、opencode 累计 step_finish.tokens、含 CEO 调用）；
///  - 墙钟：任务运行累计（1s 心跳采样；审批等待期间按任务暂停——v1.1 B5.1 修全局口径）；
///  - 重跑：人工 [重跑] 计数（网关重跑前递增并判定，超限拒绝重跑并记熔断）。
/// 判定逻辑是纯函数（Evaluate），单元测试直测；进程树由网关杀（Budget 不持有宿主）。
/// </summary>
public sealed class BudgetService
{
    /// <summary>三上限默认值（任务书：token 100000 / 墙钟 30 分钟 / 重跑 3）。</summary>
    public const long DefaultTokenCap = 100_000;
    public const int DefaultWallSeconds = 30 * 60;
    public const int DefaultRerunCap = 3;

    private sealed class TaskBudget
    {
        public long TokenCap;
        public int WallCapSeconds;
        public int RerunCap;
        public long UsedTokens;
        public int UsedWallSeconds;
        /// <summary>来源任务标题（v1.1 B5.1 按任务粒度暂停的匹配键；null = 未登记 → 全局口径）。</summary>
        public string? Title;
    }

    private readonly Database _db;
    private readonly EventRecorder _recorder;
    private readonly IApprovalGate _approval;
    private readonly Dictionary<int, TaskBudget> _active = new();
    private readonly DispatcherTimer _tick;
    private readonly object _gate = new();

    /// <summary>熔断回调（网关注入：杀树 + 任务 failed + 红标）。</summary>
    public event Action<int, string>? FuseTripped;

    public BudgetService(Database db, EventRecorder recorder, IApprovalGate approval)
    {
        _db = db;
        _recorder = recorder;
        _approval = approval;
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Heartbeat();
        _tick.Start();
    }

    /// <summary>
    /// 任务开跑时登记三上限（UI 线程调用；未登记的任务不参与熔断）。
    /// token 结转台账已入账量：CEO 拆解发生在登记（拆解期）→ 注销（等人确认）→ 再登记
    /// （确认开工）之间，拆解期烧的 token 不能凭空消失（验收 ⑤ 实锤：CEO 2556 + 节点
    /// 2534 = 5090 ≥ 上限 3000 却没熔断，就坏在再登记时清零）。
    /// v1.1 B5.1：taskTitle = 来源任务标题（审批等待按任务暂停的匹配键，可选参数零破坏；
    /// 不带标题的任务回退 v1.0 全局暂停口径）。
    /// </summary>
    public void Register(int taskId, long? tokenCap, int? wallCapSeconds, int? rerunCap, string? taskTitle = null)
    {
        lock (_gate)
            _active[taskId] = new TaskBudget
            {
                TokenCap = tokenCap is > 0 ? tokenCap.Value : DefaultTokenCap,
                WallCapSeconds = wallCapSeconds is > 0 ? wallCapSeconds.Value : DefaultWallSeconds,
                RerunCap = rerunCap is > 0 ? rerunCap.Value : DefaultRerunCap,
                UsedTokens = LoadTaskTokens(taskId),
                Title = string.IsNullOrWhiteSpace(taskTitle) ? null : taskTitle.Trim(),
            };
    }

    /// <summary>任务终态时注销。</summary>
    public void Unregister(int taskId)
    {
        lock (_gate) _active.Remove(taskId);
    }

    /// <summary>
    /// 一次 CLI 调用的 token 入账（agent 维度落 budget_ledger，并即时判定）。
    /// 0 token 也记账（CEO 调用次数的证据），只是不参与判定。
    /// </summary>
    public void AddTokens(int taskId, string agent, string? role, long tokens)
    {
        if (tokens < 0)
            return;
        InsertLedger(taskId, agent, role, tokens);
        if (tokens == 0)
            return;
        BudgetDecision verdict;
        lock (_gate)
        {
            if (!_active.TryGetValue(taskId, out var b))
                return;
            b.UsedTokens += tokens;
            verdict = Evaluate(b.UsedTokens, b.TokenCap, b.UsedWallSeconds, b.WallCapSeconds);
        }
        if (verdict.Breached)
            Trip(taskId, verdict.Reason!);
    }

    /// <summary>重跑判定（网关在人工 [重跑] 前调用）：返回超限原因，未超限返回 null。</summary>
    public string? CheckRerun(int taskId, int rerunsUsed, int? rerunCap)
    {
        var verdict = Evaluate(0, 0, 0, 0, rerunsUsed, rerunCap);
        if (verdict.Breached)
        {
            _recorder.Record(EventTypes.Budget, $"任务#{taskId}",
                $"重跑超限：已用 {rerunsUsed}/{(rerunCap is > 0 ? rerunCap : DefaultRerunCap)}，拒绝重跑");
            return verdict.Reason;
        }
        return null;
    }

    /// <summary>纯函数三路判定（单元测试直测）。</summary>
    public static BudgetDecision Evaluate(
        long usedTokens, long tokenCap,
        int usedWallSeconds, int wallCapSeconds,
        int rerunsUsed = 0, int? rerunCap = null)
    {
        if (tokenCap > 0 && usedTokens >= tokenCap)
            return new BudgetDecision(true, $"预算超限（token：{usedTokens} ≥ 上限 {tokenCap}）");
        if (wallCapSeconds > 0 && usedWallSeconds >= wallCapSeconds)
            return new BudgetDecision(true, $"预算超限（墙钟：{usedWallSeconds}s ≥ 上限 {wallCapSeconds}s）");
        var cap = rerunCap is > 0 ? rerunCap.Value : DefaultRerunCap;
        if (rerunsUsed > cap)
            return new BudgetDecision(true, $"预算超限（重跑：已用 {rerunsUsed} > 上限 {cap}）");
        return BudgetDecision.Ok;
    }

    /// <summary>熔断：记账（events 黑匣子 type=budget）+ 通知网关杀树。</summary>
    private void Trip(int taskId, string reason)
    {
        lock (_gate) _active.Remove(taskId);
        _recorder.Record(EventTypes.Budget, $"任务#{taskId}", reason);
        FuseTripped?.Invoke(taskId, reason);
    }

    /// <summary>
    /// 墙钟心跳（UI 线程 1s）：审批等待期间暂停计时。v1.1 B5.1（轮 7 已知问题 4）起按
    /// 任务粒度：仅该任务的待审批命令存在时停该任务的表，其余在跑任务照计
    /// （v1.0 是任一待审批 → 全部暂停，多任务并行时账不准）。internal 供单测直调。
    /// </summary>
    internal void Heartbeat()
    {
        List<KeyValuePair<int, TaskBudget>> snapshot;
        lock (_gate) snapshot = _active.ToList();
        if (snapshot.Count == 0)
            return;
        var taskGate = _approval as ITaskApprovalGate;
        var anyPending = _approval.PendingCount > 0;
        foreach (var (taskId, b) in snapshot)
        {
            if (PauseForTask(taskGate, anyPending, b.Title))
                continue;
            BudgetDecision verdict;
            lock (_gate)
            {
                b.UsedWallSeconds++;
                verdict = Evaluate(b.UsedTokens, b.TokenCap, b.UsedWallSeconds, b.WallCapSeconds);
            }
            if (verdict.Breached)
                Trip(taskId, verdict.Reason!);
        }
    }

    /// <summary>
    /// 暂停判定（B5.1，纯函数供单测）：审批门支持按任务查询且该任务登记了标题 → 只看该
    /// 任务的待审批；否则（旧测试桩等未实现 <see cref="ITaskApprovalGate"/> 的审批门、
    /// 未带标题登记的任务）回退 v1.0 全局口径——任一待审批即暂停，偏松不误杀。
    /// </summary>
    internal static bool PauseForTask(ITaskApprovalGate? taskGate, bool anyPending, string? title) =>
        taskGate is not null && !string.IsNullOrEmpty(title)
            ? taskGate.HasPendingFor(title)
            : anyPending;

    // ---- budget_ledger 台账（预算总览页数据源） ----

    /// <summary>某任务在台账里的累计 token（Register 结转用）。</summary>
    private long LoadTaskTokens(int taskId)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _db.DbPath }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(tokens), 0) FROM budget_ledger WHERE task_id = $task";
            cmd.Parameters.AddWithValue("$task", taskId);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch
        {
            return 0; // 台账读不了（表未建等）：宁可漏判不误杀
        }
    }

    private void InsertLedger(int taskId, string agent, string? role, long tokens)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _db.DbPath }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO budget_ledger (task_id, agent, role, tokens, created_at)
                VALUES ($task, $agent, $role, $tokens, datetime('now','localtime'))
                """;
            cmd.Parameters.AddWithValue("$task", taskId);
            cmd.Parameters.AddWithValue("$agent", agent);
            cmd.Parameters.AddWithValue("$role", (object?)role ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tokens", tokens);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 台账失败不影响任务执行；预算页少一行可查
        }
    }

    /// <summary>
    /// 在跑任务的实时预算快照（轮 7 预算总览多任务视图：一行一条并行任务 + 汇总行）。
    /// 排队任务未登记不在此列；crush 只推墙钟（token 恒 0）。
    /// </summary>
    public List<(int TaskId, long UsedTokens, long TokenCap, int UsedWallSeconds, int WallCapSeconds)> ActiveSnapshot()
    {
        lock (_gate)
            return _active.Select(kv =>
                (kv.Key, kv.Value.UsedTokens, kv.Value.TokenCap, kv.Value.UsedWallSeconds, kv.Value.WallCapSeconds)).ToList();
    }

    /// <summary>任务×员工（含 CEO）token 矩阵行。</summary>
    public List<LedgerRow> LoadLedger(int limit = 500)
    {
        var rows = new List<LedgerRow>();
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _db.DbPath }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT task_id, agent, COALESCE(role,''), SUM(tokens)
                FROM budget_ledger GROUP BY task_id, agent, role
                ORDER BY MAX(id) DESC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new LedgerRow(r.GetInt32(0), r.GetString(1), r.GetString(2), (long)r.GetInt64(3)));
        }
        catch
        {
            // 表还没建（首启动前）就是空
        }
        return rows;
    }

    /// <summary>公司记忆/审计用途：某任务 CEO 调用次数。</summary>
    public int CeoCallCount(int taskId) =>
        LoadLedger().Count(l => l.TaskId == taskId && l.Role == "ceo");
}

/// <summary>budget_ledger 聚合行。</summary>
public sealed record LedgerRow(int TaskId, string Agent, string Role, long Tokens);
