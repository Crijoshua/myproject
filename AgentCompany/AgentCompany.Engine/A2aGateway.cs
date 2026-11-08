using System.Collections.Concurrent;
using System.IO;
using System.Windows.Threading;

namespace AgentCompany.Desktop.Services;

/// <summary>一次运行的公共上下文（实时进度/终止/熔断收口用）。</summary>
internal class RunCtx
{
    public required int TaskId { get; init; }
    public required string Title { get; init; }
    public required string Workspace { get; init; }
    /// <summary>当前动作行（事件流最新一条，轮 5）。</summary>
    public string? LiveAction;
    /// <summary>本次运行开始时刻（卡片"已耗时"计时起点）。</summary>
    public DateTime LiveSince = DateTime.Now;
    /// <summary>预算熔断标记（BudgetService.FuseTripped 置位；终态按 failed("预算超限") 收口）。</summary>
    public bool Fused;
    public string? FuseReason;
    /// <summary>用户终止标记（终态 cancelled，与熔断的 failed 区分）。</summary>
    public bool CancelRequested;
}

/// <summary>串行流水线运行期上下文（轮 4）。</summary>
internal sealed class PipelineRun : RunCtx
{
    public required List<(int Seq, string Agent, string Prompt)> Steps { get; init; }
    /// <summary>重跑起点（seq &lt; FromSeq 视为已完成直接跳过；新任务 = 1）。</summary>
    public int FromSeq = 1;
    public AgentCliProcessHost? CurrentHost;
}

/// <summary>DAG 波次执行运行期上下文（轮 6）。</summary>
internal sealed class DagRun : RunCtx
{
    public required List<DagNode> Nodes { get; init; }
    /// <summary>重跑起点（seq &lt; FromSeq 的节点保留成果，视为已满足的依赖）。</summary>
    public int FromSeq = 1;
    /// <summary>在跑节点 seq → 宿主（波次并发 ≤ max_node_parallelism；每节点一台一次性宿主，写自己的 write_targets）。</summary>
    public readonly ConcurrentDictionary<int, AgentCliProcessHost> Running = new();
}

/// <summary>
/// A2A 网关（轮 4 串行流水线 → 轮 5 崩溃恢复/重跑 → 轮 6 CEO 拆解 + DAG 波次执行 + 预算熔断 →
/// 轮 7 多任务并行 + crush 评审节点）。
///  - 串行流水线（手工新建，轮 4 行为兼容）：claude → opencode 共享 workspace\pipeline\task-N；
///    轮 7 起每步一次性宿主（不再共用双宿主），多条流水线可真并行；
///  - 项目任务（轮 6）：CEO（CeoPlanner）拆一句话需求成 DAG → preview 卡人工 [确认开工] →
///    依赖满足即启动、并发 ≤max_node_parallelism（默认 2）、节点带 write_targets 文件所有权；节点失败 → 下游取消 → 任务 failed；
///    crush 节点（轮 7）：纯文本评审员，不接 MCP，最终输出由宿主代写成 write_targets 首个目标
///    （缺省 review.md）；
///  - 多任务并行（轮 7）：同时最多 max_parallel_tasks 条任务在跑（设置页 1–4，默认 2），
///    超出 FIFO 排队（任务卡"排队中"）；任务内部波次并行与节点并发上限照旧；预算独立结算；
///    CEO 拆解单进程，多任务的拆解请求排队串行；
///  - 预算三上限（token/墙钟/重跑）由 BudgetService 判定，熔断回调这里杀树并按 failed("预算超限") 收口；
///  - 任务成功后向 workspace\MEMORY.md 追加一行（公司记忆，下次 CEO 拆解时读入 stdin）。
/// 真 A2A 网络协议不在本轮，协作全部收敛在这一个组件后面。
/// </summary>
public sealed class A2aGateway : IDisposable
{
    /// <summary>设置键：同时运行任务数上限（1–4）。</summary>
    public const string MaxParallelSettingKey = "max_parallel_tasks";
    public const int DefaultMaxParallel = 2;

    /// <summary>设置键：单任务 DAG 节点并发上限（1–4；v1.1 B1，默认 2 = v1.0 行为）。</summary>
    public const string MaxNodeParallelismSettingKey = "max_node_parallelism";
    public const int DefaultMaxNodeParallelism = 2;

    /// <summary>设置键：CEO 拆解读公司记忆 MEMORY.md 的尾部行数（0 = 不限制 = 全文）。</summary>
    public const string MemoryWindowSettingKey = "memory_window_lines";
    public const int DefaultMemoryWindowLines = 40;

    /// <summary>公司记忆单行超长截断阈值（字符，超过截断加 …；防单条记忆撑爆 CEO 上下文）。</summary>
    internal const int MemoryMaxLineLength = 500;

    private readonly Database _db;
    private readonly IEventBus _bus;
    private readonly EventRecorder _recorder;
    private readonly TaskStore _store;
    private readonly Dispatcher _ui;
    private readonly IApprovalGate _approval;
    private readonly BudgetService _budget;
    private readonly CeoPlanner _planner;
    private readonly Dictionary<int, RunCtx> _running = new();
    /// <summary>排队任务的重跑起点（taskId → fromSeq；新任务 = 1）。</summary>
    private readonly Dictionary<int, int> _fromSeq = new();
    /// <summary>CEO 拆解中的任务（CEO 进程实际在跑的；熔断/优雅退出要杀 CEO 进程树用）。</summary>
    private readonly HashSet<int> _planning = new();
    /// <summary>多任务并发闸门（在 _gate 锁内驱动）。</summary>
    private readonly ParallelTaskGate _taskGate = new(DefaultMaxParallel);
    /// <summary>CEO 单进程互斥：多任务的拆解请求在此串行（先到先拆）。</summary>
    private readonly SemaphoreSlim _ceoLock = new(1, 1);
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>任务状态变化（UI 线程回调；看板页据此刷新卡片）。</summary>
    public event Action<int>? TaskChanged;

    public A2aGateway(Database db, IEventBus bus, EventRecorder recorder,
        IApprovalGate approval, BudgetService budget, CeoPlanner planner)
    {
        _db = db;
        _bus = bus;
        _recorder = recorder;
        _store = new TaskStore(db);
        _ui = Dispatcher.CurrentDispatcher; // 须在 UI 线程构造
        _approval = approval;
        _budget = budget;
        _planner = planner;
        bus.EventReceived += OnBusEvent;
        budget.FuseTripped += OnBudgetFuse;
    }

    public TaskStore Store => _store;
    public CeoPlanner Planner => _planner;

    /// <summary>是否有流水线/DAG 在途（优雅退出判定用）。</summary>
    public bool HasInFlight
    {
        get { lock (_gate) return _running.Count > 0; }
    }

    /// <summary>是否有任务在排队（优雅退出判定用；排队任务无进程，但退出应告知用户）。</summary>
    public bool HasQueued
    {
        get { lock (_gate) return _taskGate.QueuedIds.Count > 0; }
    }

    /// <summary>CEO 是否在拆解（优雅退出判定用）。</summary>
    public bool IsPlanning => _planner.IsRunning;

    /// <summary>员工是否忙（监视页员工状态条用）；返回忙则给运行起点，否则 null。</summary>
    public DateTime? AgentRunningSince(string agent)
    {
        lock (_gate)
        {
            DateTime? best = null;
            foreach (var ctx in _running.Values)
            {
                if (ctx is PipelineRun { CurrentHost: { } ph } && ph.AgentId == agent && ph.IsRunning)
                    best = Min(best, ph.StartedAt);
                if (ctx is DagRun dr)
                    foreach (var host in dr.Running.Values)
                        if (host.AgentId == agent && host.IsRunning)
                            best = Min(best, host.StartedAt);
            }
            return best;
        }

        static DateTime? Min(DateTime? a, DateTime b) => a is null || b < a ? b : a;
    }

    /// <summary>
    /// 三员工忙闲快照（v1.1 B4，须持 _gate）：白名单序逐员工找在跑宿主 → 任务号
    /// （判定口径与 <see cref="AgentRunningSince"/> 一致；null = 空闲）。
    /// CEO 拆解进程本身不占员工位（单实例串行、随拆解结束）。
    /// </summary>
    private List<(string Agent, int? TaskId)> AgentBusySnapshot()
    {
        lock (_gate)
        {
            int? TaskOf(string agent)
            {
                foreach (var ctx in _running.Values)
                {
                    if (ctx is PipelineRun { CurrentHost: { } ph } && ph.AgentId == agent && ph.IsRunning)
                        return ctx.TaskId;
                    if (ctx is DagRun dr)
                        foreach (var host in dr.Running.Values)
                            if (host.AgentId == agent && host.IsRunning)
                                return ctx.TaskId;
                }
                return null;
            }
            return DagValidator.AllowedAgents.Select(a => (a, TaskOf(a))).ToList();
        }
    }

    /// <summary>忙闲表文本（纯函数，internal 供单测）：每行「员工:空闲 / 忙碌(任务#N)」。</summary>
    internal static string FormatAgentStatus(IReadOnlyList<(string Agent, int? TaskId)> busy) =>
        string.Join("\n", busy.Select(b => $"{b.Agent}:{(b.TaskId is { } id ? $"忙碌(任务#{id})" : "空闲")}"));

    /// <summary>卡片实时进度：最新动作行 + 本次运行起点（无运行中任务返回 null）。</summary>
    public (string Action, DateTime Since)? GetLive(int taskId)
    {
        lock (_gate)
        {
            return _running.TryGetValue(taskId, out var run)
                ? (run.LiveAction ?? "", run.LiveSince)
                : null;
        }
    }

    /// <summary>
    /// 新建流水线：步骤1（claude）与步骤2（opencode）至少填一项（留空 = 单步任务）。
    /// 轮 7：并发满员时进入 FIFO 排队（任务卡"排队中"），有空位才开跑。
    /// </summary>
    public int Submit(string title, string? step1Prompt, string? step2Prompt)
    {
        var steps = new List<(string Agent, string Prompt)>();
        if (!string.IsNullOrWhiteSpace(step1Prompt))
            steps.Add(("claude", step1Prompt.Trim()));
        if (!string.IsNullOrWhiteSpace(step2Prompt))
            steps.Add(("opencode", step2Prompt.Trim()));
        if (steps.Count == 0)
            throw new ArgumentException("流水线至少需要一个步骤");

        var id = _store.CreatePipeline(title.Trim(), steps);
        var workspace = WorkspaceLayout.PipelineWorkspace(id);
        Directory.CreateDirectory(workspace);

        var chain = string.Join(" → ", steps.Select(s => s.Agent));
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
            $"📋 任务#{id}「{title.Trim()}」已创建：{chain} · 工作区 {workspace}"));
        _recorder.Record(EventTypes.Task, $"任务#{id}", $"已创建「{title.Trim()}」：{chain} 串行");

        StartOrEnqueue(id, fromSeq: 1);
        return id;
    }

    // ---- 多任务并发闸门（轮 7） ----

    /// <summary>并发上限（设置页可改；钳制 1–4）。</summary>
    public int MaxParallelTasks
    {
        get
        {
            var s = _db.GetSetting(MaxParallelSettingKey);
            return int.TryParse(s, out var v) && v >= 1 ? Math.Min(v, 4) : DefaultMaxParallel;
        }
    }

    /// <summary>公司记忆窗口行数（设置页可改；0 = 全文，非法/缺省回落默认 40；仿 MaxParallelTasks 读法）。</summary>
    public int MemoryWindowLines
    {
        get
        {
            var s = _db.GetSetting(MemoryWindowSettingKey);
            return int.TryParse(s, out var v) && v >= 0 ? v : DefaultMemoryWindowLines;
        }
    }

    /// <summary>
    /// 节点并发上限（v1.1 B1：原 const 2 改读设置 max_node_parallelism，钳制 1–4，
    /// 默认 2 = v1.0 行为；读法仿 MaxParallelTasks）。属性名/语义照旧：RunDagAsync
    /// 的事件文案与 DagScheduler 上限；排队/看门狗/所有权逻辑不动。
    /// </summary>
    public int MaxConcurrentNodes
    {
        get
        {
            var s = _db.GetSetting(MaxNodeParallelismSettingKey);
            return ClampNodeParallelism(s);
        }
    }

    /// <summary>节点并发上限钳制（纯函数，internal 供单测）：v ≥ 1 取 Min(v, 4)，否则默认 2。</summary>
    internal static int ClampNodeParallelism(string? setting) =>
        int.TryParse(setting, out var v) && v >= 1 ? Math.Min(v, 4) : DefaultMaxNodeParallelism;

    /// <summary>
    /// 启动或排队：有空位立即开跑；满员标"queued"进 FIFO（重跑起点记住，轮到时按它起）。
    /// 排队期间不登记预算（墙钟不空转），开跑时才 RegisterBudget。
    /// </summary>
    private void StartOrEnqueue(int taskId, int fromSeq)
    {
        var max = MaxParallelTasks;
        bool launch;
        lock (_gate)
        {
            _taskGate.MaxParallel = max;
            launch = _taskGate.TryAcquire(taskId);
            if (launch)
                _fromSeq.Remove(taskId);
            else
                _fromSeq[taskId] = fromSeq;
        }

        if (launch)
        {
            LaunchTask(taskId, fromSeq);
            return;
        }

        _store.SetTaskStatus(taskId, "queued");
        Changed(taskId);
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
            $"⏳ 任务#{taskId} 排队中（并发已满 max={max}，FIFO 等待空位）"));
        _recorder.Record(EventTypes.Task, $"任务#{taskId}",
            $"queued：并发已满（max={max}），进入 FIFO 排队（重跑起点 {fromSeq}）");
    }

    /// <summary>从库里重建运行上下文并开跑（串行流水线 / DAG 按任务类型分派）。</summary>
    private void LaunchTask(int taskId, int fromSeq)
    {
        var task = _store.LoadTask(taskId);
        if (task is null)
        {
            EndRun(taskId); // 任务被删：收口 + 放行下一条排队任务（占位转移语义下不能裸 Release 丢棒）
            return;
        }

        var workspace = WorkspaceLayout.PipelineWorkspace(taskId);
        Directory.CreateDirectory(workspace);
        RegisterBudget(taskId, task.BudgetTokens, task.BudgetWallSeconds, task.BudgetReruns, task.Title);

        if (task.Kind == "dag")
        {
            LaunchDag(task, LoadNodes(task), fromSeq);
            return;
        }

        var run = new PipelineRun
        {
            TaskId = task.Id,
            Title = task.Title,
            Workspace = workspace,
            Steps = task.Steps.Where(s => s.Role is null)
                .Select(s => (s.Seq, s.Agent, s.Prompt)).ToList(),
            FromSeq = fromSeq,
        };
        lock (_gate) _running[task.Id] = run;
        _ = Task.Run(() => RunAsync(run));
    }

    /// <summary>
    /// 运行收口（幂等）：移出在跑表 + 注销预算 + 腾出并发位并 FIFO 放行下一条排队任务。
    /// 所有终态路径（完成/失败/取消/熔断）都经它，排队任务才有人接续启动。
    /// </summary>
    private void EndRun(int taskId)
    {
        int? next;
        lock (_gate)
        {
            _running.Remove(taskId);
            next = _taskGate.Release(taskId);
        }
        _budget.Unregister(taskId);

        if (next is { } nid)
        {
            var from = 1;
            lock (_gate) { if (_fromSeq.TryGetValue(nid, out var fs)) from = fs; _fromSeq.Remove(nid); }
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"▶ 任务#{nid} 轮到空位，退出排队开跑（从步骤{from} 起）"));
            LaunchTask(nid, from);
        }
    }

    // ---- CEO 拆解（轮 6） ----

    /// <summary>新建项目任务：一句话需求 → CEO 拆解 → preview 卡（未 [确认开工] 绝不执行）。</summary>
    public int SubmitProject(string demand, long? budgetTokens, int? budgetWallSeconds, int? budgetReruns)
    {
        demand = demand.Trim();
        if (demand.Length == 0)
            throw new ArgumentException("需求不能为空");

        var title = TitleFromDemand(demand);
        var id = _store.CreateProject(title, demand, budgetTokens, budgetWallSeconds, budgetReruns);
        var workspace = WorkspaceLayout.PipelineWorkspace(id);
        Directory.CreateDirectory(workspace);

        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
            $"📋 任务#{id}「{title}」项目任务已创建（token 上限 {budgetTokens ?? BudgetService.DefaultTokenCap} / "
            + $"墙钟 {(budgetWallSeconds ?? BudgetService.DefaultWallSeconds) / 60} 分钟 / "
            + $"重跑 {budgetReruns ?? BudgetService.DefaultRerunCap} 次）：CEO 拆解中"));
        _recorder.Record(EventTypes.Task, $"任务#{id}",
            $"项目任务已创建「{title}」，CEO 拆解中（三上限 {budgetTokens ?? BudgetService.DefaultTokenCap} token / "
            + $"{(budgetWallSeconds ?? BudgetService.DefaultWallSeconds) / 60} 分钟 / {budgetReruns ?? BudgetService.DefaultRerunCap} 次重跑）");

        // 拆解阶段就登记预算：CEO 的 token 也计入，超限在拆解阶段即可熔断；
        // 拆解结束（preview/parse_failed）即注销——等人确认期间墙钟不计时
        RegisterBudget(id, budgetTokens, budgetWallSeconds, budgetReruns, title);
        _ = Task.Run(() => PlanAsync(id, demand));
        return id;
    }

    /// <summary>
    /// 需求/计划 → 任务标题：取首个非空行并剥掉 Markdown 标题的 # 前缀（[选择计划] 导入的 .md
    /// 首行常是“# 计划标题”，直接截前 24 字会把 # 和换行混进标题）；行内空白折成单空格，超 24 字截断。
    /// </summary>
    public static string TitleFromDemand(string demand)
    {
        var firstLine = demand.Split('\n')
            .Select(static l => l.Trim())
            .FirstOrDefault(static l => l.Length > 0, demand);
        var heading = firstLine.TrimStart('#', ' ', '\t', '　');
        var source = heading.Length > 0 ? heading : demand;
        var oneLine = System.Text.RegularExpressions.Regex.Replace(source, @"\s+", " ").Trim();
        return oneLine.Length <= 24 ? oneLine : oneLine[..24].TrimEnd() + "…";
    }

    /// <summary>
    /// CEO 拆解（新建与 [重新拆解] 共用）；重试 1 次与三保险提取都在 CeoPlanner 里。
    /// 轮 7：CEO 单进程，多任务的拆解请求经 _ceoLock 串行（等待期间状态仍是 planning）；
    /// 等待期间被熔断（状态已 failed）则拿到锁后直接跳过。
    /// </summary>
    private async Task PlanAsync(int taskId, string demand)
    {
        _store.UpsertCeoStep(taskId, demand);
        _store.SetTaskStatus(taskId, "planning");
        Changed(taskId);

        await _ceoLock.WaitAsync();
        try
        {
            if (_disposed)
                return;
            // 排队等锁期间任务已被熔断/终态（状态离开 planning）→ 不再拆解
            var current = _store.LoadTask(taskId)?.Status;
            if (current is null || current != "planning")
            {
                _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                    $"⚠ 任务#{taskId} 拆解跳过：等锁期间状态已变为 {current}"));
                return;
            }

            lock (_gate) _planning.Add(taskId);
            try
            {
                var memory = ReadCompanyMemory();
                // v1.1 B4：拆解前采集三员工忙闲快照拼进 CEO stdin（纯提示层参考，不改任何校验）
                var agentStatus = FormatAgentStatus(AgentBusySnapshot());
                var outcome = await _planner.PlanAsync(taskId, demand, memory, agentStatus);
                // CEO 调用本身计入任务预算（budget_ledger role=ceo，预算页 CEO 调用记录）
                _budget.AddTokens(taskId, "claude", "ceo", outcome.Tokens);

                if (_disposed)
                    return;
                // 拆解阶段熔断：OnBudgetFuse 已把任务标 failed + 记原因，这里不再覆盖状态
                if (_store.LoadTask(taskId)?.Status == "failed")
                    return;

                if (outcome.Success && outcome.Nodes is { Count: > 0 })
                {
                    // 两员工分工保底（验收 ①c 不能赌模型心情）：提示词已要求，这里兜底并留痕
                    var (nodes, note) = CeoPlanner.EnsureTwoAgents(outcome.Nodes);
                    if (note is not null)
                        _recorder.Record(EventTypes.Task, $"任务#{taskId}", $"CEO 拆解保底：{note}");
                    // v1.1 B3：负载感知再平衡（≥3 节点且单一员工 >2/3 时调最重给空闲员工，只改归属）
                    var (balanced, balanceNote) = CeoPlanner.EnsureBalanced(nodes);
                    if (balanceNote is not null)
                        _recorder.Record(EventTypes.Task, $"任务#{taskId}", $"CEO 拆解保底：{balanceNote}");
                    nodes = balanced;
                    _store.SetPlanPreview(taskId, nodes);
                    _store.SetCeoStepFinished(taskId, "done", null);
                    var summary = string.Join("、", nodes.Select(n => $"{n.NodeId}:{n.Agent}"));
                    _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                        $"🧠 任务#{taskId} CEO 拆解完成：{outcome.Nodes.Count} 节点（{summary}）· 待人工确认开工"));
                    _recorder.Record(EventTypes.Task, $"任务#{taskId}",
                        $"CEO 拆解完成：{outcome.Nodes.Count} 节点（{summary}），待确认（CEO 用 token {outcome.Tokens}）");
                }
                else
                {
                    var reason = outcome.Errors.Count > 0 ? string.Join("；", outcome.Errors) : "模型输出不可用";
                    _store.SetPlanFailed(taskId, reason);
                    _store.SetCeoStepFinished(taskId, "failed", reason.Length <= 300 ? reason : reason[..300]);
                    _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                        $"⚠ 任务#{taskId} 拆解失败：{reason}（可点 [重新拆解]）"));
                    _recorder.Record(EventTypes.Task, $"任务#{taskId}", $"拆解失败：{reason}");
                }
            }
            finally
            {
                lock (_gate) _planning.Remove(taskId);
            }
        }
        catch (Exception ex)
        {
            _store.SetPlanFailed(taskId, $"拆解异常：{ex.Message}");
            try { _store.SetCeoStepFinished(taskId, "failed", $"拆解异常：{ex.Message}"); } catch { /* 记账失败无碍 */ }
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task, $"⚠ 任务#{taskId} 拆解异常：{ex.Message}"));
            _recorder.Record(EventTypes.Task, $"任务#{taskId}", $"拆解异常：{ex.Message}");
        }
        finally
        {
            _ceoLock.Release();
            _budget.Unregister(taskId); // preview/parse_failed 等人确认：墙钟不再计时（执行时重新登记）
            Changed(taskId);
        }
    }

    /// <summary>[重新拆解]（preview / parse_failed 卡上）：再调一次 CEO（预算页 CEO 调用记录 +1）。</summary>
    public void Replan(int taskId)
    {
        var task = _store.LoadTask(taskId);
        if (task is null || task.Kind != "dag" || task.Status is not ("preview" or "parse_failed"))
        {
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⚠ 重新拆解被拒：任务#{taskId} 状态不可重拆（{task?.Status ?? "无"}）"));
            return;
        }
        _recorder.Record(EventTypes.Task, $"任务#{taskId}", $"人工 [重新拆解]（原状态 {task.Status}）");
        RegisterBudget(taskId, task.BudgetTokens, task.BudgetWallSeconds, task.BudgetReruns, task.Title);
        _ = Task.Run(() => PlanAsync(taskId, task.Demand ?? task.Title));
    }

    /// <summary>[确认开工]：preview → 波次执行（未确认绝不执行）；轮 7 满员时排队。</summary>
    public void ConfirmStart(int taskId)
    {
        var task = _store.LoadTask(taskId);
        if (task is null || task.Kind != "dag" || task.Status != "preview")
        {
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⚠ 开工被拒：任务#{taskId} 不是待确认状态（{task?.Status ?? "无"}）"));
            return;
        }

        var nodes = LoadNodes(task);
        if (nodes.Count == 0)
        {
            _store.SetPlanFailed(taskId, "没有可执行节点");
            Changed(taskId);
            return;
        }

        _recorder.Record(EventTypes.Task, $"任务#{taskId}", "人工 [确认开工]，进入执行（满员则排队）");
        StartOrEnqueue(taskId, fromSeq: 1);
    }

    private void LaunchDag(TaskRecord task, List<DagNode> nodes, int fromSeq)
    {
        var run = new DagRun
        {
            TaskId = task.Id,
            Title = task.Title,
            Workspace = WorkspaceLayout.PipelineWorkspace(task.Id),
            Nodes = nodes,
            FromSeq = fromSeq,
        };
        lock (_gate) _running[task.Id] = run;
        _ = Task.Run(() => RunDagAsync(run));
    }

    /// <summary>从库里的节点步骤行重建 DAG（节点标题不落库，用 NodeId 当标题）。</summary>
    private static List<DagNode> LoadNodes(TaskRecord task) =>
        task.Steps.Where(s => s.Role is null)
            .OrderBy(s => s.Seq)
            .Select(s => new DagNode(
                s.Seq,
                s.NodeId ?? $"n{s.Seq}",
                s.NodeId ?? $"n{s.Seq}",
                s.Agent,
                s.Prompt,
                s.WriteTargets ?? Array.Empty<string>(),
                s.DependsOn ?? Array.Empty<string>()))
            .ToList();

    // ---- 重跑与终止 ----

    /// <summary>
    /// 一键恢复（轮 5）：interrupted / failed / cancelled 的任务从 fromSeq 起新会话重跑——
    /// 同 prompt、同工作区，之前完成的步骤保留，后续步骤照常接力。这是重跑，不是 --resume 续接。
    /// 轮 6：重跑计数入预算（超限拒绝）；DAG 任务走波次重跑。
    /// 轮 7：并发满员时排队（不再"忙即拒不排队"）。
    /// </summary>
    public void Rerun(int taskId, int fromSeq)
    {
        var task = _store.LoadTask(taskId);
        if (task is null || task.Status is not ("interrupted" or "failed" or "cancelled"))
        {
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⚠ 重跑被拒：任务#{taskId} 不存在或状态不可重跑（{task?.Status ?? "无"}）"));
            return;
        }
        LaunchRerun(task, fromSeq);
    }

    /// <summary>
    /// [重跑此步骤]（DAG 版，③ 验收）：可带改写的 prompt（例：把 README.md 改写一遍——
    /// 该文件归另一节点，Bridge 会返回 DENIED: 该文件归其他步骤所有）。
    /// 终态任务（含 completed）也可重跑：从该节点起重跑整棵 DAG 的后续部分。
    /// </summary>
    public void RerunNode(int taskId, int seq, string? newPrompt)
    {
        var task = _store.LoadTask(taskId);
        if (task is null || task.Kind != "dag"
            || task.Status is not ("completed" or "interrupted" or "failed" or "cancelled"))
        {
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⚠ 重跑被拒：任务#{taskId} 不是可重跑的 DAG 任务（{task?.Status ?? "无"}）"));
            return;
        }
        if (newPrompt is { Length: > 0 } && !_store.UpdateStepPrompt(taskId, seq, newPrompt.Trim()))
        {
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⚠ 重跑被拒：任务#{taskId} 没有序号为 {seq} 的节点"));
            return;
        }
        LaunchRerun(task, seq);
    }

    /// <summary>重跑共用收口：预算判定 → 重置 → 启动或排队（轮 7 起串行/DAG 统一走闸门）。</summary>
    private void LaunchRerun(TaskRecord task, int fromSeq)
    {
        if (RefuseIfRerunOverBudget(task))
            return;

        fromSeq = Math.Clamp(fromSeq, 1, Math.Max(1, task.Steps.Count(s => s.Role is null)));
        _store.ResetForRerun(task.Id, fromSeq);
        Directory.CreateDirectory(WorkspaceLayout.PipelineWorkspace(task.Id));

        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
            $"🔁 任务#{task.Id} 从步骤{fromSeq} 重跑（新会话，保留更早步骤产物）"));
        _recorder.Record(EventTypes.Task, $"任务#{task.Id}",
            $"一键恢复：从步骤{fromSeq} 重跑（原状态 {task.Status}，第 {task.RerunsUsed} 次重跑）");

        StartOrEnqueue(task.Id, fromSeq);
    }

    /// <summary>
    /// 终止任务：排队中的直接移出队列收口；在跑的杀进程树，任务终态 = cancelled，后续不启动。
    /// </summary>
    public void Cancel(int taskId)
    {
        bool wasQueued;
        lock (_gate)
        {
            wasQueued = _taskGate.IsQueued(taskId);
            if (wasQueued)
            {
                _taskGate.Release(taskId); // 排队任务：只出队不腾位
                _fromSeq.Remove(taskId);
            }
        }
        if (wasQueued)
        {
            _store.SetTaskStatus(taskId, "cancelled");
            Changed(taskId);
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task, $"■ 任务#{taskId} 已取消（从排队中移出）"));
            _recorder.Record(EventTypes.Task, $"任务#{taskId}", "cancelled：用户在排队中终止");
            return;
        }

        RunCtx? ctx;
        lock (_gate) _running.TryGetValue(taskId, out ctx);
        if (ctx is null)
            return;
        ctx.CancelRequested = true;
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task, $"🛑 任务#{taskId} 收到终止指令"));
        CancelRun(ctx);
    }

    private static void CancelRun(RunCtx ctx)
    {
        if (ctx is PipelineRun pr)
            pr.CurrentHost?.Cancel();
        else if (ctx is DagRun dr)
            foreach (var host in dr.Running.Values.ToList())
                host.Cancel();
    }

    /// <summary>终止全部在途（优雅退出 [终止并退出] 用）：含排队清空与 CEO 拆解进程。</summary>
    public void CancelAll()
    {
        List<int> queued;
        RunCtx[] runs;
        lock (_gate)
        {
            queued = _taskGate.QueuedIds.ToList();
            foreach (var id in queued)
            {
                _taskGate.Release(id); // 出队（不腾位，反正要退出了）
                _fromSeq.Remove(id);
            }
            runs = _running.Values.ToArray();
        }
        foreach (var run in runs)
        {
            run.CancelRequested = true;
            CancelRun(run);
        }
        _planner.Cancel();
    }

    // ---- 串行流水线执行（轮 4/5 行为保持；轮 7 每步一次性宿主，多流水线可并行） ----

    private async Task RunAsync(PipelineRun run)
    {
        var firstSeq = Math.Max(run.FromSeq, 1);
        _store.SetTaskStatus(run.TaskId, "working", currentStep: firstSeq);
        Changed(run.TaskId);
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
            $"▶ 任务#{run.TaskId} 启动：{run.Steps.Count} 步串行（从步骤{firstSeq} 起）"));
        _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}", $"working：{run.Steps.Count} 步串行，从步骤{firstSeq} 起");

        foreach (var (seq, agent, prompt) in run.Steps)
        {
            if (_disposed)
                return; // 宿主在退出：进程树已随 Dispose 杀掉，清扫下次标 interrupted
            if (run.Fused)
            {
                FinishFused(run);
                return;
            }
            if (seq < run.FromSeq)
                continue; // 重跑：之前的步骤已完成，直接接力

            // 轮 7：每步一次性宿主（同 DAG 节点），多条流水线/任务并行时不再争用共享宿主
            var host = new AgentCliProcessHost(_db, _bus, _approval, EmployeeRegistry.Create(agent));
            lock (_gate) { run.CurrentHost = host; run.LiveAction = null; }
            _store.SetTaskStatus(run.TaskId, "working", currentStep: seq);
            _store.SetStepRunning(run.TaskId, seq, null);
            Changed(run.TaskId);
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⚙ 任务#{run.TaskId} 步骤{seq}（{agent}）开始：{Trunc(prompt, 40)}"));

            var state = await RunStepAsync(run, host, seq, agent, prompt, allowedWrites: null);
            // 步骤 token 入预算台账（claude 取 result.usage，opencode 累计 step_finish.tokens；crush 恒 0）
            _budget.AddTokens(run.TaskId, agent, null, host.InputTokens + host.OutputTokens);

            if (state != AgentRunState.Finished)
            {
                if (run.Fused)
                {
                    FinishFused(run);
                    return;
                }
                var terminal = state == AgentRunState.Cancelled ? "cancelled" : "failed";
                _store.SetTaskStatus(run.TaskId, terminal, currentStep: seq);
                if (terminal == "failed")
                {
                    // B3 修复：流水线失败同样落库原因（看门狗超时/异常退出），看板卡出 ⚠ 红标
                    var cause = state == AgentRunState.Timeout ? "看门狗超时" : "进程异常退出";
                    _store.SetFailReason(run.TaskId, $"步骤{seq}（{agent}）{cause}");
                }
                EndRun(run.TaskId);
                Changed(run.TaskId);
                var label = terminal == "cancelled" ? "已取消" : "失败";
                _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                    $"■ 任务#{run.TaskId} {label}：步骤{seq} 未完成，后续步骤不启动"));
                _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}", $"{label}：步骤{seq} 未完成，止损不接力");
                return;
            }
        }

        Complete(run.TaskId, run.Workspace, run.Steps.Count);
    }

    /// <summary>
    /// 单步执行（闭包订阅宿主 RunFinished 拿终态回执；轮 4 模式）。
    /// 轮 6 起兼跑 DAG 节点：一次性宿主 + allowedWrites（write_targets 所有权注入）。
    /// 轮 7：带任务标题（审批请求回源显示）；crush 节点完成后宿主代写评审报告。
    /// </summary>
    private async Task<AgentRunState> RunStepAsync(RunCtx run, AgentCliProcessHost host, int seq, string agent,
        string prompt, IReadOnlyList<string>? allowedWrites)
    {
        var done = new TaskCompletionSource<AgentRunState>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(AgentRunState s) => done.TrySetResult(s);
        host.RunFinished += OnFinished;
        try
        {
            host.Start(prompt, run.Workspace, run.TaskId, seq, allowedWrites, run.Title);
            if (!host.IsRunning && !done.Task.IsCompleted)
            {
                // 启动被拒（命令行审计命中）：宿主不会再发 RunFinished，这里直接失败
                var refused = $"{agent} 启动被拒（命令行审计命中），本步骤未运行";
                _store.SetStepFinished(run.TaskId, seq, "failed", null, refused);
                Fail(run, refused);
                return AgentRunState.Error;
            }
            var state = await done.Task;

            var stepStatus = state switch
            {
                AgentRunState.Finished => "done",
                AgentRunState.Cancelled => "cancelled",
                _ => "failed",
            };
            var error = state switch
            {
                AgentRunState.Timeout => "看门狗超时",
                AgentRunState.Error => "进程异常退出（exit != 0）",
                _ => null,
            };
            _store.SetStepFinished(run.TaskId, seq, stepStatus, host.SessionId, error);
            Changed(run.TaskId);
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"{(state == AgentRunState.Finished ? "✔" : "✖")} 任务#{run.TaskId} 步骤{seq}（{agent}）{stepStatus}"));
            _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}", $"步骤{seq}（{agent}）{stepStatus}"
                + (string.IsNullOrEmpty(error) ? "" : $"：{error}"));

            // 轮 7：crush 评审报告代写（受信操作：宿主直写工作区，不走 ALLOWED_WRITES）。
            // 文件名 = 节点 write_targets 首个目标（CEO 提示词约定 review.md），缺省 review.md。
            if (agent == "crush" && state == AgentRunState.Finished)
                WriteReviewReport(run, host.LastReply,
                    allowedWrites is { Count: > 0 } ? allowedWrites[0] : CrushAdapter.DefaultReportName);

            return state;
        }
        finally
        {
            host.RunFinished -= OnFinished;
            if (run is PipelineRun pr)
                lock (_gate) pr.CurrentHost = null;
        }
    }

    /// <summary>
    /// crush 评审报告代写：全部 stdout 去首尾空白（剥外层围栏）→ write_targets 首个目标文件；
    /// 全文以 crush 事件进监视页事件流（"完成后全文展示"），空输出大声留痕不代写。
    /// </summary>
    private void WriteReviewReport(RunCtx run, string? rawReply, string fileName)
    {
        string report;
        try
        {
            report = CrushAdapter.ExtractReport(rawReply);
        }
        catch (Exception ex)
        {
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Crush,
                $"⚠ 任务#{run.TaskId} 评审报告整形失败：{ex.Message}", Agent: "crush"));
            return;
        }

        if (report.Length == 0)
        {
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Crush,
                $"⚠ 任务#{run.TaskId} crush 无评审输出，未代写评审报告", Agent: "crush"));
            _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}", "crush 评审节点无输出，未代写 review");
            return;
        }

        try
        {
            File.WriteAllText(Path.Combine(run.Workspace, fileName), report + Environment.NewLine,
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Crush,
                $"⚠ 任务#{run.TaskId} 评审报告代写失败：{ex.Message}", Agent: "crush"));
            _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}", $"crush 评审报告代写失败：{ex.Message}");
            return;
        }

        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Crush,
            $"📝 任务#{run.TaskId}：crush 评审报告已代写为 {fileName}（{report.Length} 字符）——全文如下：\n{report}",
            Agent: "crush"));
        _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}",
            $"crush 评审报告已代写为 {fileName}（{report.Length} 字符）");
    }

    // ---- DAG 波次执行（轮 6） ----

    /// <summary>
    /// 波次引擎循环：依赖满足且所有权空闲且并发 ≤2 即启动；任一节点失败 → 下游取消、
    /// 其余待办一并止损（不启动），在跑的兄弟节点让它跑完（写自己的文件，无害）；
    /// 用户取消 / 预算熔断 → 杀树后排空收口。
    /// </summary>
    private async Task RunDagAsync(DagRun run)
    {
        _store.SetTaskStatus(run.TaskId, "working", currentStep: run.FromSeq);
        Changed(run.TaskId);
        var executing = run.Nodes.Where(n => n.Seq >= run.FromSeq).ToList();
        var pending = executing.ToList();
        // 保留成果的节点（重跑起点之前）视为已满足的依赖
        var doneIds = run.Nodes.Where(n => n.Seq < run.FromSeq).Select(n => n.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        var finished = new List<Task<int>>(); // 每个启动节点的完成信号（返回 seq）
        // v1.1 B1：节点并发上限读设置（本轮运行取快照，改设置只影响之后开跑的任务）
        var maxNodes = MaxConcurrentNodes;
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
            $"▶ 任务#{run.TaskId} 开工：DAG {run.Nodes.Count} 节点（本轮执行 {executing.Count}），并发 ≤{maxNodes}"));
        _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}",
            $"working：DAG 波次执行，{executing.Count} 节点，从节点序 {run.FromSeq} 起");

        string? failure = null; // 首个失败描述（下游取消/终态 failed 的依据）

        while (!_disposed)
        {
            if (run.Fused || run.CancelRequested)
                break;
            if (pending.Count == 0 && run.Running.Count == 0)
                break; // 全部收尾

            var runningIds = run.Nodes.Where(n => run.Running.ContainsKey(n.Seq))
                .Select(n => n.NodeId).ToHashSet(StringComparer.Ordinal);
            // v1.1 B2：员工多样性优先 + 同员工串行（依赖/所有权/止损/下游取消语义不动）
            foreach (var node in DagScheduler.SelectReady(pending, doneIds, runningIds, run.Nodes,
                         run.Running.Count, maxNodes, serializePerAgent: true))
            {
                pending.Remove(node);
                var host = new AgentCliProcessHost(_db, _bus, _approval, EmployeeRegistry.Create(node.Agent));
                run.Running[node.Seq] = host;
                _store.SetStepRunning(run.TaskId, node.Seq, null);
                _store.SetTaskStatus(run.TaskId, "working", currentStep: node.Seq);
                Changed(run.TaskId);
                _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                    $"⚙ 任务#{run.TaskId} 节点{node.Seq}（{node.Agent}）开始：{node.Title}" +
                    (node.WriteTargets.Count > 0 ? $" · 写 {string.Join("、", node.WriteTargets)}" : "")));
                _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}",
                    $"节点{node.Seq}（{node.Agent}）启动：{node.Title}（写 {(node.WriteTargets.Count == 0 ? "无限制" : string.Join("、", node.WriteTargets))}，依赖 {(node.DependsOn.Count == 0 ? "无" : string.Join("、", node.DependsOn))}）");

                var seq = node.Seq;
                var agent = node.Agent;
                var prompt = node.Prompt;
                var writes = node.WriteTargets.Count > 0 ? node.WriteTargets : null;
                finished.Add(Task.Run(async () =>
                {
                    var state = await RunStepAsync(run, host, seq, agent, prompt, writes);
                    // 节点 token 入预算台账并即时判定（超限即熔断）
                    _budget.AddTokens(run.TaskId, agent, null, host.InputTokens + host.OutputTokens);
                    run.Running.TryRemove(seq, out _);
                    return seq;
                }));
            }

            if (run.Running.Count == 0 && pending.Count > 0)
            {
                // 没有在跑也挑不出就绪：依赖死锁（校验后不应发生）——止损
                failure ??= "调度死锁（没有可启动的节点）";
                break;
            }

            if (finished.Count == 0)
                continue;

            var doneSeq = await await Task.WhenAny(finished);
            finished.RemoveAll(t => t.IsCompleted);
            var fin = run.Nodes.First(n => n.Seq == doneSeq);
            var status = _store.LoadTask(run.TaskId)?.Steps.FirstOrDefault(s => s.Seq == doneSeq)?.Status;

            if (status == "done")
            {
                doneIds.Add(fin.NodeId);
            }
            else
            {
                // B6 修复：上游终态分 failed/cancelled/interrupted，别一律写"失败"误导排障
                // （观察记录 #124：用户终止触发的下游取消被写成"上游 n1 失败"）
                failure ??= $"节点{fin.Seq}（{fin.Agent}）{StepStatusPhrase(status)}，下游取消";
                var downstream = DagScheduler.DownstreamOf(fin.NodeId, run.Nodes);
                foreach (var p in pending)
                {
                    var reason = downstream.Contains(p.NodeId)
                        ? $"上游 {fin.NodeId} {StepStatusPhrase(status)}，下游取消"
                        : "任务失败止损";
                    _store.SetStepFinished(run.TaskId, p.Seq, "cancelled", null, reason);
                    _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}", $"节点{p.Seq} 取消：{reason}");
                }
                pending.Clear();
                // 在跑的兄弟节点让它跑完（写自己的文件，无害），循环等 Running 排空
            }
        }

        // 排空在跑节点（用户取消/熔断已杀树，它们很快报终态；_disposed 时不再等）
        while (run.Running.Count > 0 && finished.Count > 0 && !_disposed)
        {
            await await Task.WhenAny(finished);
            finished.RemoveAll(t => t.IsCompleted);
        }

        EndRun(run.TaskId); // 轮 7：收口 + 腾并发位 + FIFO 放行下一条排队任务

        if (run.Fused)
        {
            FinishFused(run);
            return;
        }

        // 终态按节点落库状态收口：任一 failed → failed；全 done → completed；否则（用户取消/止损未启动）→ cancelled
        var steps = _store.LoadTask(run.TaskId)?.Steps.ToDictionary(s => s.Seq) ?? new Dictionary<int, TaskStepRecord>();
        var statuses = executing.Select(n => steps.TryGetValue(n.Seq, out var s) ? s.Status : "").ToList();
        if (run.CancelRequested || (!statuses.Contains("failed") && !statuses.All(st => st == "done")))
        {
            _store.SetTaskStatus(run.TaskId, "cancelled", currentStep: run.FromSeq);
            Changed(run.TaskId);
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task, $"■ 任务#{run.TaskId} 已取消"));
            _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}", "cancelled：用户终止");
            return;
        }
        if (statuses.Contains("failed"))
        {
            _store.SetTaskStatus(run.TaskId, "failed", currentStep: run.FromSeq);
            // B3 修复：失败原因此前只进事件流不落库，看板卡无 ⚠ 红标（对比熔断路径有原因）
            _store.SetFailReason(run.TaskId, failure ?? "存在失败节点");
            Changed(run.TaskId);
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"■ 任务#{run.TaskId} 失败：{failure ?? "存在失败节点"}"));
            _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}", $"failed：{failure ?? "存在失败节点"}");
            return;
        }

        Complete(run.TaskId, run.Workspace, run.Nodes.Count);
    }

    // ---- 完成收口与公司记忆（轮 6） ----

    /// <summary>任务完成：盘点产物 + 落库 + 公司记忆追加一行。</summary>
    private void Complete(int taskId, string workspace, int stepCount)
    {
        var artifacts = Directory.Exists(workspace)
            ? Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(workspace, p))
                .Where(IsRealArtifact)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();
        _store.SetTaskArtifacts(taskId, artifacts);
        _store.SetTaskStatus(taskId, "completed", currentStep: stepCount);
        EndRun(taskId);
        Changed(taskId);
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
            $"🏁 任务#{taskId} 完成：产物 {artifacts.Count} 个（{string.Join("、", artifacts.Take(5))}{(artifacts.Count > 5 ? "…" : "")}）"));
        _recorder.Record(EventTypes.Task, $"任务#{taskId}",
            $"completed：产物 {artifacts.Count} 个（{string.Join("、", artifacts.Take(5))}）");

        AppendCompanyMemory(taskId, artifacts);
    }

    /// <summary>
    /// 产物判定（O8 修复，internal 供单测）：排除宿主投放的约束文件与 CLI 自身的状态目录——
    /// opencode.json / AGENTS.md / .crushrc / CRUSH.md（宿主投放）、.oc-config\（opencode 配置锚定）、
    /// .crush\（crush 运行库：crush.db/logs/.gitignore，观察记录 #125 实测混入产物清单）。
    /// </summary>
    internal static bool IsRealArtifact(string relPath) =>
        relPath != "opencode.json" && relPath != "AGENTS.md"
        && relPath != ".crushrc" && relPath != "CRUSH.md"
        && !relPath.StartsWith(".oc-config", StringComparison.OrdinalIgnoreCase)
        && !relPath.StartsWith(".crush", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// B6 修复（internal 供单测）：上游节点非 done 终态 → 准确的中文短语。
    /// 观察记录 #124：用户终止（cancelled）触发的下游取消被一律写成"上游失败"，误导排障方向。
    /// </summary>
    internal static string StepStatusPhrase(string? status) => status switch
    {
        "failed" => "失败",
        "cancelled" => "已取消",
        "interrupted" => "已中断",
        _ => "未完成",
    };

    /// <summary>公司记忆：任务成功完成后向 workspace 根 MEMORY.md 追加一行（日期 + 标题 + 产物清单）。</summary>
    private void AppendCompanyMemory(int taskId, IReadOnlyList<string> artifacts)
    {
        try
        {
            var task = _store.LoadTask(taskId);
            if (task is null)
                return;
            var line = $"{DateTime.Now:yyyy-MM-dd} #{taskId} {task.Title} · 产物：{(artifacts.Count == 0 ? "无" : string.Join("、", artifacts))}";
            File.AppendAllText(WorkspaceLayout.CompanyMemoryPath(), line + Environment.NewLine);
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task, $"🧠 公司记忆 MEMORY.md +1 行：{line}"));
            _recorder.Record(EventTypes.Task, $"任务#{taskId}", $"公司记忆追加：{line}");
        }
        catch (Exception ex)
        {
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task, $"⚠ 公司记忆追加失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 公司记忆读取（v1.1 特性 A3：滚动窗口）——读尾部 N 行（设置 memory_window_lines，默认 40，
    /// 0 = 全文 = v1.0 行为），单行超 <see cref="MemoryMaxLineLength"/> 截断加 …。
    /// MEMORY.md 本体照旧全量追加不改写（历史数据不动），这里只优化读取侧；
    /// 文件不存在/空文件/读取失败仍返回 null（既有语义，ComposeStdin 会省略记忆段）。
    /// </summary>
    private string? ReadCompanyMemory()
    {
        string? raw;
        try
        {
            var path = WorkspaceLayout.CompanyMemoryPath();
            if (!File.Exists(path))
                return null;
            raw = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        var (text, truncated) = TailMemory(raw, MemoryWindowLines, MemoryMaxLineLength);
        Diag($"记忆读取 {LineCount(text)}/{LineCount(raw)} 行{(truncated ? "(截断)" : "")}");
        return text;
    }

    /// <summary>
    /// 记忆滚动窗口（纯函数，internal 供单测，仿 StepStatusPhrase 口径）：取尾部 window 行
    /// （≤0 = 全文），单行超 maxLine 截断加 …；null/空/全空白输入返回 (null, false)。
    /// 末尾换行产生的空行不计行也不入文（"a\nb\n" 是 2 行）；行分隔统一回填
    /// Environment.NewLine（ComposeStdin 本来就要 Trim，语义等价）。
    /// </summary>
    internal static (string? Text, bool Truncated) TailMemory(string? text, int window, int maxLine)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (null, false);

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var truncated = false;
        if (window > 0 && lines.Count > window)
        {
            lines = lines[^window..];
            truncated = true;
        }
        if (maxLine >= 0)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length > maxLine)
                {
                    lines[i] = lines[i][..maxLine] + "…";
                    truncated = true;
                }
            }
        }
        return (string.Join(Environment.NewLine, lines), truncated);
    }

    /// <summary>诊断行数（末尾换行不计；null/空为 0）。</summary>
    private static int LineCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var lines = text.Split('\n');
        return lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
    }

    /// <summary>诊断落盘：%APPDATA%\AgentCompany\diag.log（格式同 CeoPlanner.Diag；失败不影响拆解）。</summary>
    private static void Diag(string message)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AgentCompany", "diag.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    // ---- 预算（轮 6） ----

    // title：来源任务标题（v1.1 B5.1 按任务粒度暂停墙钟的匹配键；缺省 = 全局口径）
    private void RegisterBudget(int taskId, long? tokens, int? wall, int? reruns, string? title = null) =>
        _budget.Register(taskId, tokens, wall, reruns, title);

    /// <summary>人工重跑超预算判定：超限 → 记熔断事件 + 拒绝（任务不重置）。计数在拒绝时也 +1（可见）。</summary>
    private bool RefuseIfRerunOverBudget(TaskRecord task)
    {
        var used = _store.IncrementReruns(task.Id);
        var reason = _budget.CheckRerun(task.Id, used, task.BudgetReruns);
        if (reason is null)
            return false;
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
            $"⚠ 任务#{task.Id} 重跑被拒：{reason}（已用 {used} 次）"));
        return true;
    }

    /// <summary>熔断回调（BudgetService）：杀整棵进程树，任务按 failed("预算超限") 收口（红标+原因）。</summary>
    private void OnBudgetFuse(int taskId, string reason)
    {
        RunCtx? ctx = null;
        lock (_gate)
        {
            if (_running.TryGetValue(taskId, out ctx))
            {
                ctx.Fused = true;
                ctx.FuseReason = reason;
            }
            else if (_planning.Contains(taskId))
            {
                // CEO 拆解阶段超限：杀 CEO 进程，任务标 failed
                _planner.Cancel();
                _planning.Remove(taskId);
            }
        }

        if (ctx is not null)
        {
            CancelRun(ctx); // 进程树先杀；终态由运行循环看到 Fused 后统一收口
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⛔ 任务#{taskId} 预算熔断：{reason}（杀进程树，任务标失败）"));
            Changed(taskId);
            return;
        }

        // planning 阶段熔断（任务不在 _running）
        _store.SetTaskStatus(taskId, "failed");
        _store.SetFailReason(taskId, reason);
        _budget.Unregister(taskId);
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
            $"⛔ 任务#{taskId} 预算熔断（拆解阶段）：{reason}"));
        _recorder.Record(EventTypes.Task, $"任务#{taskId}", $"failed：{reason}（CEO 拆解阶段熔断）");
        Changed(taskId);
    }

    /// <summary>熔断收口：任务 failed + 失败原因（看板红标"预算超限"）。</summary>
    private void FinishFused(RunCtx run)
    {
        _store.SetTaskStatus(run.TaskId, "failed");
        _store.SetFailReason(run.TaskId, run.FuseReason);
        EndRun(run.TaskId);
        Changed(run.TaskId);
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task, $"■ 任务#{run.TaskId} 失败：{run.FuseReason}"));
        _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}", $"failed：{run.FuseReason}");
    }

    /// <summary>总线回听（UI 线程）：把当前在跑员工的最新事件行滚动进卡片实时动作行。</summary>
    private void OnBusEvent(AppEvent e)
    {
        if (string.IsNullOrEmpty(e.Agent))
            return;
        lock (_gate)
        {
            foreach (var run in _running.Values)
            {
                var hit = run switch
                {
                    PipelineRun { CurrentHost: { } ph } => ph.AgentId == e.Agent && ph.IsRunning,
                    DagRun dr => dr.Running.Values.Any(h => h.AgentId == e.Agent && h.IsRunning),
                    _ => false,
                };
                if (hit)
                    run.LiveAction = e.Message;
            }
        }
    }

    private void Fail(RunCtx run, string error)
    {
        _store.SetTaskStatus(run.TaskId, "failed");
        EndRun(run.TaskId);
        Changed(run.TaskId);
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task, $"■ 任务#{run.TaskId} 失败：{error}"));
        _recorder.Record(EventTypes.Task, $"任务#{run.TaskId}", $"failed：{error}");
    }

    private void Changed(int taskId)
    {
        if (_ui.CheckAccess())
            TaskChanged?.Invoke(taskId);
        else
            _ui.BeginInvoke(() => TaskChanged?.Invoke(taskId));
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        _disposed = true;
        lock (_gate)
        {
            foreach (var run in _running.Values)
                CancelRun(run);
            _running.Clear();
        }
        _planner.Dispose();
    }
}
