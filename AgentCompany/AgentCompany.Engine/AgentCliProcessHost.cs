using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using AgentCompany.Core;

namespace AgentCompany.Desktop.Services;

/// <summary>
/// 员工 CLI 子进程宿主（轮 2 建 claude 基本启动；轮 3 加 MCP 工具 + 审批门；轮 4 泛化为
/// 按 IAgentAdapter 支持多名员工，claude / opencode 差异全部在适配器里）。
/// 本机实测约束（勿改）：
///  1. claude / opencode 都无 exe，只有 npm 垫片 —— 必须经 cmd.exe /c 启动；
///  2. claude 的 stream-json 必须带 --verbose；opencode 必须 --standalone（默认连后台 daemon 会污染环境且进程残留）；
///  3. 任务文本走 stdin（UTF-8 管道），不拼命令行（中文会被 cmd 的 GBK 码页弄乱）；
///  4. stdout/stderr/stdin 三个 Encoding 全部 UTF-8；
///  5. Start 后必须 BeginOutputReadLine/BeginErrorReadLine——只订阅事件不排空管道 = 子进程写满缓冲后静默挂死（轮 2 最深坑）。
/// </summary>
public sealed class AgentCliProcessHost : IAgentCliProcessHost, ISessionSink, IDisposable
{
    /// <summary>禁传参数黑名单（命中即拒绝启动）。--auto 是 opencode 的自动批准，绝不放行。</summary>
    public static readonly string[] ForbiddenArgs =
    {
        "--dangerously-skip-permissions",
        "bypassPermissions",
        "--auto",
        "--yolo",
        "--full-auto",
    };

    /// <summary>设置页的任务超时键（秒）。</summary>
    public const string TimeoutSettingKey = "task_timeout_seconds";
    public const int DefaultTimeoutSeconds = 180;

    /// <summary>
    /// 轮 8 可用性闸门：入参员工名，返回是否允许启动。App 启动时接 SelfCheckService；
    /// 默认全放行（单测/宿主外构造时行为与轮 2-7 一致）。
    /// </summary>
    public static Func<string, bool> AvailabilityGate { get; set; } = _ => true;

    private readonly Database _db;
    private readonly IEventBus _bus;
    private readonly IApprovalGate _approval;
    private readonly IAgentAdapter _adapter;
    private readonly (string Event, string Diag, string Status) _cat;
    private readonly object _gate = new();

    private Process? _process;
    private CancellationTokenSource? _watchdog;
    private AgentRunState _decided; // kill/退出竞态下已确定的终态
    private bool _exited;
    private long _lastActivityMs;   // 子进程最近一次 stdout/stderr 输出（TickCount64；看门狗静默判定，B1）
    private List<string> _tempArtifacts = new(); // 适配器生成的临时文件（结束后删）
    private long _journalRowId;    // process_journal 行（轮 5：PID 记账；正常结束销账，崩溃留给启动清扫）
    private int? _journalTaskId;   // 记账归属（流水线任务；监视页直发为 null）
    private string? _taskTitle;    // 来源任务标题（v1.1 B5.1：看门狗按任务暂停；直发 null → 全局口径）

    public AgentRunState State { get; private set; } = AgentRunState.Idle;
    public bool IsRunning => State == AgentRunState.Running;

    /// <summary>任务终态回执（轮 4：A2aGateway 靠它接力/止损，不轮询状态）。</summary>
    public event Action<AgentRunState>? RunFinished;

    // ---- 本次运行摘录（UI 读取；由 stdout 线程写、UI 线程读，容忍良性竞态）----
    public string? SessionId { get; private set; }
    public string? Model { get; private set; }
    public string? LastReply { get; private set; }
    public long ThinkingTokens { get; private set; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public long CacheReadTokens { get; private set; }
    public long LastDurationMs { get; private set; }
    public DateTime StartedAt { get; private set; }

    public string AgentId => _adapter.Id;
    long ISessionSink.TotalInput => InputTokens;
    long ISessionSink.TotalOutput => OutputTokens;
    long ISessionSink.TotalCacheRead => CacheReadTokens;

    public AgentCliProcessHost(Database db, IEventBus bus, IApprovalGate approval, IAgentAdapter adapter)
    {
        _db = db;
        _bus = bus;
        _approval = approval;
        _adapter = adapter;
        _cat = EventCategories.ForAgent(adapter.Id);
    }

    /// <summary>启动一次任务（默认工作区 = workspace\&lt;员工名&gt;；监视页直发用）。</summary>
    public void Start(string taskText) => Start(taskText, EnsureWorkspace(_adapter.DefaultWorkspaceFolder));

    /// <summary>
    /// 启动一次任务到指定工作区（轮 4：流水线用 workspace\pipeline\task-N 共享工作区，
    /// 两步同 cwd + 同沙箱根，B 天然读到 A 的产物）。参数审计命中黑名单时拒绝启动并记诊断日志。
    /// 轮 5：taskId/seq 用于 process_journal 记账（监视页直发任务传 null）。
    /// 轮 6：allowedWrites = DAG 节点的 write_targets（注入 AGENTCOMPANY_ALLOWED_WRITES，
    /// Bridge 的 file_write 只允许写列表内路径）；null = 手工流水线/直发，不注入、行为不变。
    /// 轮 7：taskTitle = 来源任务标题（注入 AGENTCOMPANY_TASK_TITLE，Bridge 审批请求带回，
    /// 多任务并行时审批中心按它区分；null = 监视页直发，不注入）。
    /// </summary>
    public void Start(string taskText, string workspace, int? taskId = null, int? seq = null,
        IReadOnlyList<string>? allowedWrites = null, string? taskTitle = null)
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                Diag("已有任务在运行，忽略新的派活请求");
                return;
            }
            _journalTaskId = taskId;
            _taskTitle = taskTitle;
        }

        // 轮 8：可用性闸门（App 启动时接 SelfCheck）。CLI 不可用直接判错收口，
        // 不让任务挂着等看门狗超时；默认全放行（单测/无 UI 场景行为与轮 2-7 一致）。
        if (!AvailabilityGate(_adapter.Id))
        {
            Diag($"拒绝启动：员工 {_adapter.Id} 自检未通过（CLI 不可用）。请到设置页检查路径覆盖后[重新自检]。");
            CleanupTempArtifacts();
            lock (_gate) { _decided = AgentRunState.Error; }
            Finish(null!, AgentRunState.Error, -1);
            return;
        }

        Directory.CreateDirectory(workspace);
        Diag($"工作区：{workspace}");

        // 公司工具 + 审批门。mcp-config 指向 exe 旁 McpBridge\AgentCompany.McpBridge.exe。
        var bridgeExe = Path.Combine(AppContext.BaseDirectory, "McpBridge", "AgentCompany.McpBridge.exe");
        string arguments;
        if (File.Exists(bridgeExe))
        {
            (arguments, _tempArtifacts) = _adapter.BuildArguments(bridgeExe, workspace);
            Diag($"MCP Bridge 就绪：{bridgeExe}");
        }
        else
        {
            // 缺 Bridge 时仍可跑（降级为无公司工具），但要大声记下来
            (arguments, _tempArtifacts) = _adapter.BuildArguments(null, workspace);
            Diag($"未找到 McpBridge（{bridgeExe}）：本次任务没有公司工具与审批门。请用 发布.bat 重新发布。");
        }

        lock (_gate)
        {
            var banned = FindForbiddenArg(arguments);
            if (banned is not null)
            {
                Diag($"拒绝启动：命令行含禁传参数 {banned}");
                CleanupTempArtifacts();
                return;
            }

            // 重置本次运行统计
            SessionId = null;
            Model = null;
            LastReply = null;
            ThinkingTokens = 0;
            InputTokens = OutputTokens = CacheReadTokens = 0;
            LastDurationMs = 0;
            _decided = AgentRunState.Idle;
            _exited = false;
            _journalRowId = 0;
            _lastActivityMs = Environment.TickCount64; // 看门狗从启动即开始静默计时（B1）
            StartedAt = DateTime.Now;
            SetState(AgentRunState.Running);
        }

        var psi = new ProcessStartInfo
        {
            // 员工 CLI 是 npm 垫片（.cmd），.NET 无法直接启动 .cmd，必须经 cmd.exe。
            FileName = "cmd.exe",
            Arguments = arguments,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        // Bridge 沙箱与审批管道的注入（员工 CLI 会把自身环境传给 MCP 子进程；
        // opencode.json / mcp-config 里也带一份，双保险）
        psi.EnvironmentVariables["AGENTCOMPANY_WORKSPACE"] = workspace;
        psi.EnvironmentVariables["AGENTCOMPANY_PIPE"] = ApprovalPipe.DefaultPipeName;
        psi.EnvironmentVariables["AGENTCOMPANY_AGENT"] = _adapter.Id;
        // 轮 6：文件所有权（仅 DAG 节点注入；手工流水线不注入 = 不限制，行为兼容）
        if (allowedWrites is { Count: > 0 })
            psi.EnvironmentVariables["AGENTCOMPANY_ALLOWED_WRITES"] = string.Join(';', allowedWrites);
        // 轮 7：来源任务标题（员工 CLI 经环境继承传给 MCP Bridge，审批请求带回宿主）
        if (!string.IsNullOrEmpty(taskTitle))
            psi.EnvironmentVariables["AGENTCOMPANY_TASK_TITLE"] = taskTitle;
        // 员工专属环境（opencode：XDG_CONFIG_HOME 锚定工作区配置，见 OpencodeAdapter 注释）
        _adapter.ConfigureEnvironment(psi.EnvironmentVariables, workspace);

        var proc = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
            SynchronizingObject = null,
        };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { NotifyActivity(); _adapter.ParseLine(e.Data, this); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { NotifyActivity(); Diag("[stderr] " + e.Data); } };
        proc.Exited += (_, _) => OnExited(proc);

        try
        {
            proc.Start();
            // 关键：订阅 OutputDataReceived/ErrorDataReceived 不会自动开始异步读，
            // 必须 Start 后显式 Begin*，否则没人排空管道 → 子进程写满缓冲后阻塞挂死。
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Diag($"启动失败：{ex.Message}");
            lock (_gate) { _decided = AgentRunState.Error; }
            Finish(proc, AgentRunState.Error, -1);
            return;
        }

        // 轮 5：挂入公司 Job Object（宿主死 = 整棵树陪葬）+ PID 记账（启动清扫的兜底依据）。
        // 失败不拦启动：Job 没挂上只是少了陪葬，记账照写，清扫还能兜底。
        try
        {
            JobObject.Assign(proc);
        }
        catch (Exception ex)
        {
            Diag($"Job Object 挂入失败（{ex.Message}）：依赖启动清扫兜底");
        }
        try
        {
            _journalRowId = _db.InsertJournal(_journalTaskId, seq, _adapter.Id, proc.Id);
        }
        catch (Exception ex)
        {
            Diag($"process_journal 记账失败：{ex.Message}");
        }

        lock (_gate) _process = proc;

        // 任务文本走 stdin UTF-8 管道（避免 cmd 参数 GBK 乱码 / 引号注入 / 参数超长）。
        var stdin = proc.StandardInput;
        Task.Run(() =>
        {
            try
            {
                stdin.Write(taskText);
                stdin.Close();
            }
            catch (Exception ex)
            {
                Diag($"写入 stdin 失败：{ex.Message}");
            }
        });

        // 看门狗（B1 修复）：静默检测 + 总时长硬上限，杀整棵进程树。
        //  - 静默计时：stdout/stderr 每一行输出都算存活（NotifyActivity），连续静默 timeout 秒才杀——
        //    修复"总墙钟一刀切击杀高产出节点"（观察记录 B1：n1 写到 4/8 文件被 600s 墙钟杀死；
        //    claude stream-json 的 thinking_tokens/tool_use 事件、opencode 的 step 事件都在 stdout）；
        //  - 硬上限 5×timeout：防"一直有输出的失控循环"无限续命（DAG 任务另有任务级墙钟预算兜底）；
        //  - 审批等待期间暂停计时——用户没点按钮的那段时间不算任务超时（轮 4 行为保留；
        //    v1.1 B5.1 起按任务粒度：仅该任务来源的待审批才暂停本宿主，其余任务照计）。
        var timeout = ReadTimeoutSeconds();
        var cts = new CancellationTokenSource();
        lock (_gate) _watchdog = cts;
        var taskGate = _approval as ITaskApprovalGate; // v1.1 B5.1：按任务粒度暂停（不支持则回退全局）
        _ = Task.Run(async () =>
        {
            long total = 0;
            try
            {
                while (true)
                {
                    await Task.Delay(1000, cts.Token);
                    if (BudgetService.PauseForTask(taskGate, _approval.PendingCount > 0, _taskTitle))
                        continue; // 该任务有待审批项（直发宿主无标题回退全局口径）：本轮不计时不递增
                    total++;
                    var idle = (Environment.TickCount64 - Volatile.Read(ref _lastActivityMs)) / 1000;
                    var (kill, why) = EvaluateWatchdog(idle, total, timeout);
                    if (!kill)
                        continue;

                    bool alreadyExited;
                    lock (_gate) alreadyExited = _exited;
                    if (alreadyExited)
                        return;

                    Diag($"看门狗：{why}，终止进程树");
                    lock (_gate) { if (_decided == AgentRunState.Idle) _decided = AgentRunState.Timeout; }
                    KillTree(proc);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return; // 正常结束，看门狗撤销
            }
        });

        var who = _adapter.Id == "claude" ? "" : _adapter.Id + " ";
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.System,
            $"{who}已派活（超时 {timeout}s）：{Truncate(taskText, 40)}"));
    }

    /// <summary>手动终止。</summary>
    public void Cancel()
    {
        Process? proc;
        lock (_gate)
        {
            if (!IsRunning || _process is null)
                return;
            proc = _process;
            if (_decided == AgentRunState.Idle)
                _decided = AgentRunState.Cancelled;
        }
        Diag("手动终止：杀进程树");
        KillTree(proc);
    }

    public void Dispose()
    {
        try
        {
            lock (_gate)
            {
                if (_process is { HasExited: false })
                {
                    if (_decided == AgentRunState.Idle)
                        _decided = AgentRunState.Cancelled;
                    KillTree(_process);
                }
            }
        }
        catch
        {
            // 退出路径不再抛错
        }
    }

    // ---- ISessionSink（解析器回写；claude 轮 2/3 行为原样） ----

    void ISessionSink.Text(string message, bool dim) => Agent(message, dim);

    void ISessionSink.Diag(string message) => Diag(message);

    void ISessionSink.SetSession(string? sessionId) => SessionId = sessionId;

    void ISessionSink.SetModel(string? model)
    {
        Model = model;
        Status(); // 组织页联动 model 名
    }

    void ISessionSink.SetReply(string? text) => LastReply = text;

    void ISessionSink.AddUsage(long inputTokens, long outputTokens, long cacheReadTokens)
    {
        InputTokens += inputTokens;
        OutputTokens += outputTokens;
        CacheReadTokens += cacheReadTokens;
    }

    void ISessionSink.SetThinking(long estimatedTokens) => ThinkingTokens = estimatedTokens;

    void ISessionSink.SetDuration(long milliseconds) => LastDurationMs = milliseconds;

    // ---- 退出与终态 ----

    private void OnExited(Process proc)
    {
        bool already;
        lock (_gate) already = _exited;
        if (already)
            return;
        lock (_gate) _exited = true;

        try { proc.WaitForExit(); } catch { /* 已退出 */ }
        _watchdog?.Cancel();

        int exitCode;
        try { exitCode = proc.ExitCode; } catch { exitCode = -1; }

        AgentRunState decided;
        lock (_gate)
        {
            decided = _decided;
            if (decided == AgentRunState.Idle)
                decided = exitCode == 0 ? AgentRunState.Finished : AgentRunState.Error;
        }

        Finish(proc, decided, exitCode);
    }

    private void Finish(Process proc, AgentRunState state, int exitCode)
    {
        _watchdog?.Cancel();
        CleanupTempArtifacts();
        lock (_gate)
        {
            if (proc is null || _process == proc)
                _process = null;
        }

        // 轮 5：正常结束销账（崩溃时走不到这里——未销账行由下次启动清扫处理）
        var journalId = _journalRowId;
        if (journalId != 0)
        {
            try { _db.MarkJournalCleaned(journalId); } catch { /* 销账失败留给清扫 */ }
            _journalRowId = 0;
        }

        try { proc?.Dispose(); } catch { }

        // 轮 7：crush 无事件流——全部 stdout 在适配器里累积，进程退出后一次性取走当最终回复
        if (string.IsNullOrEmpty(LastReply))
        {
            try { LastReply = _adapter.FinalReply(); } catch { /* 适配器失败按无回复处理 */ }
        }

        // B2 修复：异常终止（看门狗/进程错误）时 result.usage 信封随进程丢失，账本会记 0
        // （观察记录：n1 被杀后 budget_ledger 记 0 token，预算页/熔断系统性低估）。
        // 兜底：thinking_tokens 事件流是 CLI 自报的估计量——计数全空时按它入账，
        // 真值仍以正常结束的 result.usage 为准（本机智谱端点实测：assistant 消息 usage 恒 0，
        // 仅 result 有真数，故只能击杀前靠估计兜底）。
        if (state is AgentRunState.Timeout or AgentRunState.Error
            && InputTokens == 0 && OutputTokens == 0 && ThinkingTokens > 0)
        {
            OutputTokens = ThinkingTokens;
            Diag($"异常终止且无 usage 信封：按 CLI 思计量估计入账 {ThinkingTokens} token（估计值）");
        }

        // 落库 agent_sessions
        try
        {
            _db.InsertAgentSession(new AgentSessionRecord(
                Agent: _adapter.Id,
                SessionId: SessionId,
                Model: Model,
                StartedAt: StartedAt,
                FinishedAt: DateTime.Now,
                InputTokens: InputTokens,
                OutputTokens: OutputTokens,
                CacheReadTokens: CacheReadTokens,
                ExitCode: exitCode,
                Status: state switch
                {
                    AgentRunState.Finished => "ok",
                    AgentRunState.Timeout => "timeout",
                    AgentRunState.Cancelled => "cancelled",
                    _ => "error",
                }));
        }
        catch (Exception ex)
        {
            Diag($"会话落库失败：{ex.Message}");
        }

        AgentRunState finalState;
        lock (_gate)
        {
            SetState(state);
            finalState = state;
        }
        var who = _adapter.Id == "claude" ? "" : _adapter.Id + " ";
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.System,
            $"{who}会话已归档：{state switch { AgentRunState.Finished => "完成", AgentRunState.Timeout => "超时", AgentRunState.Cancelled => "已终止", _ => "错误" }} · 历史会话 {_db.CountAgentSessions()} 次"));

        RunFinished?.Invoke(finalState);
    }

    private void SetState(AgentRunState state)
    {
        State = state;
        Status();
    }

    private void Status() => _bus.Publish(new AppEvent(DateTime.Now, _cat.Status,
        stateLabel(State) + (Model is null ? string.Empty : $"|{Model}")));

    private static string stateLabel(AgentRunState s) => s switch
    {
        AgentRunState.Running => "运行中",
        AgentRunState.Finished => "空闲",
        AgentRunState.Cancelled => "空闲",
        AgentRunState.Timeout => "错误",
        AgentRunState.Error => "错误",
        _ => "空闲",
    };

    // ---- 工具 ----

    /// <summary>启动前参数审计：返回命中的首个禁传参数（未命中返回 null）。</summary>
    internal static string? FindForbiddenArg(string arguments)
    {
        foreach (var banned in ForbiddenArgs)
        {
            if (arguments.Contains(banned, StringComparison.OrdinalIgnoreCase))
                return banned;
        }
        return null;
    }

    /// <summary>子进程有输出即存活（B1：看门狗按静默判活，不再按总墙钟一刀切）。</summary>
    private void NotifyActivity() => Volatile.Write(ref _lastActivityMs, Environment.TickCount64);

    /// <summary>
    /// 看门狗判定（internal 供单测）：
    ///  - 连续静默 ≥ timeoutSeconds → 杀（真挂死：管道全无输出）；
    ///  - 总时长 ≥ 5×timeoutSeconds → 杀（失控循环硬上限：一直有输出也最多活 5 倍配置时长）；
    ///  - 其余不杀（活跃节点不再被总墙钟误杀，B1）。
    /// </summary>
    internal static (bool Kill, string Reason) EvaluateWatchdog(long idleSeconds, long totalSeconds, int timeoutSeconds)
    {
        var hardCap = (long)timeoutSeconds * 5;
        if (totalSeconds >= hardCap)
            return (true, $"总时长 {totalSeconds}s 达硬上限 {hardCap}s（{timeoutSeconds}s×5，防失控循环）");
        if (idleSeconds >= timeoutSeconds)
            return (true, $"连续 {idleSeconds}s 无任何输出（静默超时 {timeoutSeconds}s）");
        return (false, "");
    }

    private int ReadTimeoutSeconds()
    {
        if (int.TryParse(_db.GetSetting(TimeoutSettingKey), out var v) && v >= 1)
            return v;
        return DefaultTimeoutSeconds;
    }

    private static string EnsureWorkspace(string folder)
    {
        var workspace = WorkspaceLayout.AgentWorkspace(folder);
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    /// <summary>删除本次运行的临时文件（任务结束/启动被拒时）。</summary>
    private void CleanupTempArtifacts()
    {
        List<string> temps;
        lock (_gate) { temps = _tempArtifacts; _tempArtifacts = new List<string>(); }
        foreach (var path in temps)
        {
            try { File.Delete(path); } catch { /* 临时文件删除失败无碍 */ }
        }
    }

    /// <summary>杀整棵进程树（cmd → node → 员工 CLI）；失败回退 taskkill /T /F。</summary>
    private void KillTree(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            DiagStatic($"Kill(entireProcessTree) 失败（{ex.Message}），回退 taskkill /T /F");
            try
            {
                Process.Start(new ProcessStartInfo("taskkill", $"/PID {proc.Id} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit(5000);
            }
            catch (Exception ex2)
            {
                DiagStatic($"taskkill 回退也失败：{ex2.Message}");
            }
        }
    }

    private void Agent(string message, bool dim = false) =>
        _bus.Publish(new AppEvent(DateTime.Now, _cat.Event, message, dim, _adapter.Id));

    private void Diag(string message)
    {
        _bus.Publish(new AppEvent(DateTime.Now, _cat.Diag, message, Agent: _adapter.Id));
        LogToFile(message);
    }

    /// <summary>诊断落盘：%APPDATA%\AgentCompany\diag.log（排查子进程问题用）。</summary>
    private static void LogToFile(string message)
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

    /// <summary>
    /// 杀树路径的诊断上总线（crush M1 评审采纳项，M4 落实）：构造注入的 _bus 直发，
    /// 不再经 Application.Current 反射取 GUI 全局总线（CLI 宿主没有 Application，反射永远
    /// 静默落空，诊断会丢）。Bus 为 null 时静默跳过（与原反射落空口径一致）。
    /// </summary>
    private void DiagStatic(string message) =>
        _bus?.Publish(new AppEvent(DateTime.Now, EventCategories.AgentDiag, message));

    private static string Truncate(string? s, int max) =>
        s is null ? string.Empty : s.Length <= max ? s : s[..max] + "…";
}

/// <summary>agent_sessions 落库记录。</summary>
public sealed record AgentSessionRecord(
    string Agent,
    string? SessionId,
    string? Model,
    DateTime StartedAt,
    DateTime FinishedAt,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    int ExitCode,
    string Status);
