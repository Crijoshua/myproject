using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using AgentCompany.Core;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Cli;

/// <summary>
/// agentcompany —— CEO 终端入口（M4 特性 D）：与桌面版同一引擎（AgentCompany.Engine），
/// 接线仿 App.xaml.cs，只把 UI 层换成控制台 REPL：
///  - 专用 STA 线程跑 Dispatcher 泵（EventBus 派发 / BudgetService DispatcherTimer /
///    A2aGateway 的 Dispatcher.CurrentDispatcher 依赖都在这根线程上，主线程做控制台交互）；
///  - 单宿主互斥：启动探测审批管道 AgentCompany.Approval，被占（桌面版在跑）即退出码 2；
///  - 需求多行输入（空行提交）→ CEO 拆解 → DAG 表格预览 → y/r/n → 执行期事件流 + 实时动作行；
///  - 审批走 TerminalApprovalGate（终端 y/n + 115s 倒计时，与 GUI 同管道同落库口径）；
///  - /folder /peek /status /budget /memory /prompts /quit；Ctrl+C 优雅退出（杀树、任务收口）。
/// </summary>
internal static partial class Program
{
    private static readonly object PrintGate = new();

    /// <summary>CLI 主机上下文（STA 线程构造，接线同 App.xaml.cs）。</summary>
    private sealed class HostContext
    {
        public EventBus Bus = null!;
        public EventRecorder Recorder = null!;
        public TerminalApprovalGate Approval = null!;
        public BudgetService Budget = null!;
        public CeoPlanner Planner = null!;
        public A2aGateway Gateway = null!;
    }

    /// <summary>REPL 会话状态（/folder、/peek 设置，提交时消费）。</summary>
    private sealed class ReplState
    {
        public string? Folder;
        public string? FolderTree;
        public string? PeekRel;
        public string? PeekContent;
        public bool PeekTruncated;
    }

    private static HostContext _host = null!;
    private static SelfCheckService _selfCheck = null!;
    private static int _shutdownStarted;

    private static int Main(string[] args)
    {
        // 编码：先记原代码页（检测必须在切 UTF-8 之前），输出/输入全 UTF-8，不做 GBK 转码
        var legacyCodePage = 0;
        try { legacyCodePage = Console.OutputEncoding.CodePage; } catch { /* 重定向时可能拿不到 */ }
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch { /* 重定向下设置失败：继续，横幅按 legacyCodePage 提示 */ }

        var options = CliArgs.Parse(args);
        if (options.Error is not null)
        {
            Print(options.Error);
            return 2;
        }
        if (options.ShowVersion)
        {
            Print($"{AppInfo.ProductName} · 终端版 v{AppInfo.Version}");
            return 0;
        }
        if (options.ShowHelp)
        {
            Print(CliArgs.Usage);
            return 0;
        }

        // 单宿主互斥：管道有服务端在听 = 桌面版（或另一个 CLI）在跑
        if (PipeOccupied())
        {
            Print("桌面版正在运行，请先关闭后重试。");
            return 2;
        }

        // 全局异常兜底落 diag.log（仿 App.xaml.cs，杜绝无声退出）
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Fatal("CLI 未处理异常", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Fatal("CLI 未观察任务异常", e.Exception);

        // 接线（仿 App.xaml.cs，只换 UI 层：审批门 = TerminalApprovalGate）
        var db = new Database();
        db.EnsureCreated();
        WorkspaceLayout.Init(() => db.GetSetting(WorkspaceLayout.SettingKey));
        CliPaths.Init(agent => db.GetSetting(agent + "_path"));

        _host = StartEngineOnStaThread(db);

        // 启动自检 + 派活闸门（与 GUI 同源：SelfCheck 结果决定 AvailabilityGate）
        _selfCheck = new SelfCheckService();
        var check = _selfCheck.Run(db);
        AgentCliProcessHost.AvailabilityGate = agent => _selfCheck.IsAgentAvailable(agent);
        _host.Recorder.Record(EventTypes.SelfCheck, "CLI 启动自检",
            $"共 {check.Count} 项，通过 {check.Count(c => c.Ok)}：{string.Join("；", check.Select(c => $"{c.Title}={(c.Ok ? "OK" : "✗ " + c.Detail)}"))}");

        // 事件订阅：任务生命周期 + 三员工事件流/诊断/状态 + 系统行，每事件一行
        _host.Bus.EventReceived += RenderEvent;
        _host.Approval.Start();

        PrintBanner(check, legacyCodePage);

        // Ctrl+C：优雅退出（在途任务杀树收口、CEO 终止、任务可重跑）
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Shutdown("收到 Ctrl+C");
        };

        var state = new ReplState();
        if (options.Folder is not null)
            TrySetFolder(state, options.Folder);

        return Repl(state);
    }

    // ---- 引擎启动（专用 STA 线程 Dispatcher 泵；A2aGateway/EventBus 的 Dispatcher 依赖在此满足） ----

    private static HostContext StartEngineOnStaThread(Database db)
    {
        var ready = new TaskCompletionSource<HostContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = new HostContext();
        var thread = new Thread(() =>
        {
            try
            {
                ctx.Bus = new EventBus(); // Dispatcher.CurrentDispatcher 绑定本 STA 线程
                ctx.Recorder = new EventRecorder(db, ctx.Bus);
                ctx.Bus.Publish(new AppEvent(DateTime.Now, EventCategories.System, $"状态库 OK：{db.StatusText()}"));

                // 启动清扫（轮 5 双保险）：须在任何宿主启动之前（接线同 App.xaml.cs）
                new StartupSweep(db, new TaskStore(db), ctx.Recorder).Run();

                ctx.Approval = new TerminalApprovalGate(db, ctx.Bus, ctx.Recorder,
                    write: s => Print(s));
                ctx.Budget = new BudgetService(db, ctx.Recorder, ctx.Approval); // DispatcherTimer 心跳走本线程泵
                ctx.Planner = new CeoPlanner(db, ctx.Bus);
                ctx.Gateway = new A2aGateway(db, ctx.Bus, ctx.Recorder, ctx.Approval, ctx.Budget, ctx.Planner);
                ready.TrySetResult(ctx);
                Dispatcher.Run(); // 泵：EventBus 派发 / DispatcherTimer / BeginInvoke
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "cli-engine" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>探测审批管道是否已有服务端在等（500ms 连不上 = 空闲，CLI 可接管；实现在 Engine CliKit.PipeProbe）。</summary>
    private static bool PipeOccupied() => PipeProbe.IsOccupied(ApprovalPipe.DefaultPipeName, 500);

    // ---- 横幅 ----

    private static void PrintBanner(IReadOnlyList<CheckItem> check, int legacyCodePage)
    {
        var bar = new string('═', 58);
        Print(bar);
        Print($" {AppInfo.ProductName} · 终端版 v{AppInfo.Version}");
        Print(bar);
        Print(" 启动自检：");
        foreach (var c in check)
            Print($"   {(c.Ok ? "[OK] " : "[✗]  ")}{c.Title}：{c.Detail}");
        if (!_selfCheck.IsAgentAvailable("claude"))
            Print(" ⚠ claude CLI 不可用（自检未通过）：已禁用提交（同桌面版派活闸门）。");
        if (legacyCodePage is not 0 and not 65001)
            Print(" 提示：当前代码页非 UTF-8，中文可能乱码，可先执行 chcp 65001（不阻塞）。");
        Print(" 输入需求（多行，空行提交）；/help 查看命令。终端里只对话 CEO，它统管全局。");
    }

    // ---- REPL ----

    private static int Repl(ReplState state)
    {
        while (true)
        {
            Print("");
            Print(state.Folder is null
                ? "需求>（多行，空行提交；/help 查看命令）"
                : $"需求>［文件夹 {Path.GetFileName(state.Folder.TrimEnd('\\', '/'))}］");
            var demand = new StringBuilder();
            while (true)
            {
                string? line;
                // 读行不持 PrintGate：空闲等输入期间审批横幅/事件流能即时插行打印
                // （持锁会让 TerminalApprovalGate 的渲染压到下一次回车之后）。
                line = Console.ReadLine();
                if (line is null)
                    return Shutdown("输入结束"); // EOF（重定向耗尽）：等价 /quit
                if (line.TrimStart().StartsWith('/'))
                {
                    var quit = HandleCommand(state, line.Trim());
                    if (quit)
                        return Shutdown("/quit");
                    if (demand.Length == 0)
                        break; // 命令处理完回外层重新提示
                    demand.Clear(); // 输入中途的命令：丢弃已攒行，回外层（命令可能改了 folder/peek）
                    break;
                }
                if (line.Length == 0)
                {
                    if (demand.Length == 0)
                        continue; // 空行忽略，等正文
                    break; // 空行提交
                }
                demand.AppendLine(line);
            }

            var text = demand.ToString().TrimEnd();
            if (text.Length == 0)
                continue;
            if (!_selfCheck.IsAgentAvailable("claude"))
            {
                var why = _selfCheck.Last.FirstOrDefault(c => c.Key == "cli:claude")?.Detail ?? "自检未通过";
                Print($"⚠ claude CLI 不可用：{why}。提交已禁用（可在桌面版设置页修路径后[重新自检]）。");
                continue;
            }

            // v1.2（联审 m234 低危）：提交前按三员工完整自检提示——claude 缺席仍硬禁（CEO 必需），
            // opencode/crush 缺席仅告警（可提交，但对应节点会在执行期被派活闸门拒绝、任务可能失败收口）
            foreach (var agent in new[] { "opencode", "crush" })
            {
                if (_selfCheck.IsAgentAvailable(agent)) continue;
                var why = _selfCheck.Last.FirstOrDefault(c => c.Key == "cli:" + agent)?.Detail ?? "自检未通过";
                Print($"⚠ {agent} CLI 不可用：{why}。仍可提交，但拆出的 {agent} 节点会被派活闸门拒绝（任务可能失败收口）。");
            }

            var full = ComposeDemand(state, text);
            state.PeekRel = null; // peek 只拼进下一次拆解
            state.PeekContent = null;
            state.PeekTruncated = false;

            int taskId;
            try
            {
                taskId = _host.Gateway.SubmitProject(full, null, null, null);
            }
            catch (Exception ex)
            {
                Print($"提交失败：{ex.Message}");
                continue;
            }
            WaitPlanAndConfirm(taskId);
        }
    }

    /// <summary>
    /// 组装拆解输入：需求原文 +【项目文件夹】节（目录树）+【补充文件】节（/peek 内容）。
    /// 需求原文放最前（A2aGateway.TitleFromDemand 取首行作任务标题，树放前部会把标题
    /// 变成「【项目文件夹】C:\…」——偏离任务书"拼在 demand 前部"的措辞，理由见交付报告）。
    /// </summary>
    private static string ComposeDemand(ReplState state, string demandText)
    {
        var sb = new StringBuilder(demandText);
        if (state.Folder is not null)
            sb.Append("\n\n【项目文件夹】").Append(state.Folder).Append('\n').Append(state.FolderTree);
        if (state.PeekContent is not null)
            sb.Append("\n\n【补充文件】").Append(state.PeekRel)
              .Append(state.PeekTruncated ? "（超过 50KB，已截断）" : "").Append('\n').Append(state.PeekContent);
        return sb.ToString();
    }

    /// <summary>命令分发；返回 true = /quit。</summary>
    private static bool HandleCommand(ReplState state, string cmdLine)
    {
        var sp = cmdLine.IndexOf(' ');
        var cmd = (sp < 0 ? cmdLine : cmdLine[..sp]).ToLowerInvariant();
        var arg = sp < 0 ? "" : cmdLine[(sp + 1)..].Trim();

        switch (cmd)
        {
            case "/help":
                Print(CliArgs.Usage);
                return false;
            case "/quit":
            case "/exit":
                return true;
            case "/folder":
                if (arg.Length == 0)
                {
                    Print(state.Folder is null
                        ? "用法：/folder <绝对路径>"
                        : $"当前项目文件夹：{state.Folder}（用法：/folder <绝对路径> 切换）");
                    return false;
                }
                TrySetFolder(state, arg);
                return false;
            case "/peek":
                HandlePeek(state, arg);
                return false;
            case "/status":
                HandleStatus();
                return false;
            case "/budget":
                HandleBudget();
                return false;
            case "/memory":
                HandleMemory(arg);
                return false;
            case "/prompts":
                HandlePrompts();
                return false;
            default:
                Print($"未知命令：{cmd}（/help 查看命令）");
                return false;
        }
    }

    /// <summary>设置/切换项目文件夹并立即打印树摘要。</summary>
    private static void TrySetFolder(ReplState state, string path)
    {
        try
        {
            var (tree, truncated, entries) = FolderSnapshot.RenderTree(path);
            state.Folder = Path.GetFullPath(path);
            state.FolderTree = tree;
            Print($"项目文件夹已设置：{state.Folder}（{entries} 项{(truncated ? "，超过 500 已截断" : "")}）");
            Print(tree);
        }
        catch (ArgumentException ex)
        {
            Print($"⚠ {ex.Message}");
        }
    }

    private static void HandlePeek(ReplState state, string arg)
    {
        if (state.Folder is null)
        {
            Print("请先 /folder <绝对路径> 设置项目文件夹，再 /peek。");
            return;
        }
        var (content, truncated, error) = CliPeek.ReadFile(state.Folder, arg);
        if (error is not null)
        {
            Print($"⚠ {error}");
            return;
        }
        state.PeekRel = arg;
        state.PeekContent = content;
        state.PeekTruncated = truncated;
        Print($"已附加 {arg}（{content.Length} 字符{(truncated ? "，超过 50KB 已截断" : "")}），将拼进下一次拆解输入。");
    }

    /// <summary>最近任务状态（tasks 表最近 5 条）。</summary>
    private static void HandleStatus()
    {
        var tasks = _host.Gateway.Store.LoadTasks(5);
        if (tasks.Count == 0)
        {
            Print("暂无任务。");
            return;
        }
        Print("最近任务：");
        foreach (var t in tasks)
            Print($"  #{t.Id} [{StatusPhrase(t.Status)}] {t.Title}（{t.Kind}）更新 {t.UpdatedAt:MM-dd HH:mm}"
                + (string.IsNullOrEmpty(t.FailReason) ? "" : $" ⚠ {t.FailReason}"));
    }

    /// <summary>预算台账汇总（budget_ledger 按任务/员工聚合）。</summary>
    private static void HandleBudget()
    {
        var rows = _host.Budget.LoadLedger(500);
        if (rows.Count == 0)
        {
            Print("预算台账暂无记录（三上限默认：token 100000 / 墙钟 30 分钟 / 重跑 3 次）。");
            return;
        }
        Print("预算台账（token）：");
        foreach (var g in rows.GroupBy(r => r.TaskId).OrderBy(g => g.Key))
        {
            var parts = g.GroupBy(r => r.Agent)
                .Select(a => $"{a.Key} {a.Sum(x => x.Tokens)}");
            Print($"  任务#{g.Key}：{string.Join("、", parts)}");
        }
        Print($"  合计 {rows.Sum(r => r.Tokens)} token");
    }

    /// <summary>公司记忆尾部 n 行（默认 20）。</summary>
    private static void HandleMemory(string arg)
    {
        var n = int.TryParse(arg, out var v) && v >= 1 ? Math.Min(v, 1000) : 20;
        var path = WorkspaceLayout.CompanyMemoryPath();
        if (!File.Exists(path))
        {
            Print("公司记忆 MEMORY.md 尚无内容（任务成功完成后自动追加）。");
            return;
        }
        var lines = File.ReadAllLines(path);
        var tail = lines.Skip(Math.Max(0, lines.Length - n)).ToList();
        Print($"公司记忆（尾部 {tail.Count}/{lines.Length} 行）：");
        foreach (var l in tail)
            Print($"  {l}");
    }

    /// <summary>四键提示词来源状态（口径同设置页三态：内置 / 外置 / 外置·非法已回退）。</summary>
    private static void HandlePrompts()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentCompany", "prompts");
        Print($"提示词来源（外置目录 {dir}）：");
        foreach (var key in new[] { "planner", "claude", "opencode", "crush" })
        {
            var file = Path.Combine(dir, key + ".md");
            if (!File.Exists(file))
            {
                Print($"  {key,-9}：内置（默认）");
                continue;
            }
            var bad = true;
            try
            {
                var text = File.ReadAllText(file, new UTF8Encoding(false, throwOnInvalidBytes: true));
                bad = string.IsNullOrWhiteSpace(text)
                    || PromptStore.AnchorTable[key].Any(a => !text.Contains(a, StringComparison.Ordinal));
            }
            catch
            {
                // 坏字节：按非法回退口径展示
            }
            Print(bad
                ? $"  {key,-9}：外置·非法已回退内置（缺锚点/坏编码/空文件）"
                : $"  {key,-9}：外置（生效中）");
        }
    }

    // ---- 拆解等待与确认/执行 ----

    /// <summary>planning → preview/parse_failed；preview 打表格等 y/r/n；y → 执行到终态。</summary>
    private static void WaitPlanAndConfirm(int taskId)
    {
        var store = _host.Gateway.Store;
        while (true)
        {
            Print($"🧠 任务#{taskId} CEO 拆解中…");
            while (true)
            {
                var status = store.LoadTask(taskId)?.Status;
                if (status is not ("planning" or "pending"))
                    break;
                Thread.Sleep(500);
            }

            var task = store.LoadTask(taskId);
            if (task is null)
                return;
            switch (task.Status)
            {
                case "parse_failed":
                case "failed":
                    Print($"⚠ 任务#{taskId} 拆解失败：{task.FailReason ?? "模型输出不可用"}");
                    if (PromptChoice("r=重新拆解 n=取消", "rn") != "r")
                    {
                        Print($"已取消（任务#{taskId} 停留在拆解失败状态，可随时在桌面版重新拆解）。");
                        return;
                    }
                    _host.Gateway.Replan(taskId);
                    continue;
                case "preview":
                    ShowDag(task);
                    var key = PromptChoice("y=确认开工 r=重新拆解 n=取消", "yrn");
                    if (key == "y")
                    {
                        _host.Gateway.ConfirmStart(taskId);
                        WaitForTerminal(taskId);
                        return;
                    }
                    if (key == "r")
                    {
                        _host.Gateway.Replan(taskId);
                        continue;
                    }
                    Print($"已取消：任务#{taskId} 停留在待确认状态（preview），未确认绝不执行。");
                    return;
                default:
                    // 熔断等罕见终态：直接展示
                    var extra = string.IsNullOrEmpty(task.FailReason) ? "" : $"（{task.FailReason}）";
                    Print($"任务#{taskId} 状态：{StatusPhrase(task.Status)}{extra}");
                    return;
            }
        }
    }

    /// <summary>DAG 表格（节点号/员工/节点id/写目标/依赖；节点标题不落库，GUI 同口径用 NodeId）。</summary>
    private static void ShowDag(TaskRecord task)
    {
        Print($"CEO 拆解完成：任务#{task.Id}「{task.Title}」（待确认，未确认绝不执行）");
        Print($"  {"节点",4}  {"员工",-9}{"节点id",-8}{"写目标",-30}依赖");
        foreach (var s in task.Steps.Where(s => s.Role is null))
        {
            var writes = s.WriteTargets is { Count: > 0 } ? string.Join("、", s.WriteTargets) : "无限制";
            var deps = s.DependsOn is { Count: > 0 } ? string.Join("、", s.DependsOn) : "-";
            Print($"  {s.Seq,4}  {PadDisplay(s.Agent, 9)}{PadDisplay(s.NodeId ?? "n?", 8)}{PadDisplay(writes, 30)}{deps}");
        }
    }

    /// <summary>执行期：事件流由总线订阅渲染，这里轮询状态 + 每 5s 打一次实时动作行，直到终态。</summary>
    private static void WaitForTerminal(int taskId)
    {
        Print($"▶ 任务#{taskId} 开工（Ctrl+C 终止并退出）…");
        string? lastAction = null;
        var lastPrint = DateTime.MinValue;
        while (true)
        {
            Thread.Sleep(1000);
            var task = _host.Gateway.Store.LoadTask(taskId);
            if (task is null)
                return;
            if (task.Status is "completed" or "failed" or "cancelled" or "interrupted")
            {
                switch (task.Status)
                {
                    case "completed":
                        Print($"🏁 任务#{taskId} 完成：产物 {task.Artifacts.Count} 个（{string.Join("、", task.Artifacts)}）");
                        break;
                    case "failed":
                        Print($"■ 任务#{taskId} 失败：{task.FailReason ?? "存在失败节点"}");
                        break;
                    case "cancelled":
                        Print($"■ 任务#{taskId} 已取消。");
                        break;
                    default:
                        Print($"■ 任务#{taskId} 已中断（重启后可一键重跑）。");
                        break;
                }
                return;
            }
            var live = _host.Gateway.GetLive(taskId);
            if (live is { Action.Length: > 0 } l && l.Action != lastAction
                && (DateTime.Now - lastPrint).TotalSeconds >= 5)
            {
                Print($"  · {TruncForLine(l.Action, 110)}");
                lastAction = l.Action;
                lastPrint = DateTime.Now;
            }
        }
    }

    private static string PromptChoice(string prompt, string keys)
    {
        while (true)
        {
            Print($"{prompt} >");
            // 同 REPL 读行：不持 PrintGate，免得审批渲染被压在确认输入后面。
            string? line = Console.ReadLine();
            if (line is null)
                return "n"; // EOF：安全侧取消
            var k = line.Trim().ToLowerInvariant();
            if (k.Length == 1 && keys.Contains(k))
                return k;
            Print($"请输入：{string.Join(" / ", keys.Select(c => c.ToString()))}");
        }
    }

    // ---- 退出 ----

    /// <summary>优雅退出（GracefulExit 思路）：在途任务杀树收口（cancelled，可一键重跑）+ CEO 终止 + 排队清空。</summary>
    private static int Shutdown(string reason)
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) == 1)
        {
            Thread.Sleep(5000); // 已在退出：等 Environment.Exit 生效
            Environment.Exit(0);
        }
        Print($"—— {reason}，正在收尾…");
        try
        {
            _host.Gateway.CancelAll(); // 在途杀树 + planner.Cancel + 排队出队
            for (var i = 0; i < 80 && (_host.Gateway.HasInFlight || _host.Gateway.IsPlanning || _host.Gateway.HasQueued); i++)
                Thread.Sleep(100); // 等收口最多 8s（超时留给下次启动清扫兜底）
            _host.Gateway.Dispose();
            _host.Approval.Dispose();
            _host.Recorder.Record(EventTypes.Exit, "CLI 退出", reason);
        }
        catch
        {
            // 退出路径不再抛
        }
        Print("已退出：在途任务已终止并标记，重启后可一键重跑。再见！");
        Environment.Exit(0);
        return 0;
    }

    // ---- 渲染 ----

    /// <summary>订阅的类别：任务生命周期 + 三员工事件流/诊断/状态 + 系统行（Approval 由审批门自己渲染详情）。</summary>
    private static readonly HashSet<string> RenderCategories = new(StringComparer.Ordinal)
    {
        EventCategories.System,
        EventCategories.Task,
        EventCategories.Agent, EventCategories.AgentDiag, EventCategories.AgentStatus,
        EventCategories.Opencode, EventCategories.OpencodeDiag, EventCategories.OpencodeStatus,
        EventCategories.Crush, EventCategories.CrushDiag, EventCategories.CrushStatus,
    };

    /// <summary>事件 → 一行；高频行（工具结果折叠/思考）降级为 …（回调在 STA 泵线程）。</summary>
    private static void RenderEvent(AppEvent e)
    {
        try
        {
            if (!RenderCategories.Contains(e.Category))
                return;
            var msg = e.Message;
            if (msg.Contains("工具结果(已折叠)") || msg.Contains("工具结果（已折叠）") || msg.Contains("思考"))
                msg = "…";
            lock (PrintGate)
                Console.WriteLine($"[{e.Timestamp:HH:mm:ss}] {msg}");
        }
        catch
        {
            // 渲染失败不影响引擎
        }
    }

    private static void Print(string s)
    {
        lock (PrintGate)
            Console.WriteLine(s);
    }

    private static string StatusPhrase(string status) => status switch
    {
        "pending" => "待启动",
        "planning" => "拆解中",
        "preview" => "待确认",
        "parse_failed" => "拆解失败",
        "working" => "执行中",
        "completed" => "已完成",
        "failed" => "失败",
        "cancelled" => "已取消",
        "interrupted" => "已中断",
        "queued" => "排队中",
        _ => status,
    };

    /// <summary>显示宽度垫片（CJK 字符按 2 列计，终端对齐用）。</summary>
    private static string PadDisplay(string s, int width)
    {
        int WidthOf(char c) => c >= 0x1100 && (c <= 0x115F || c >= 0x2E80 && c <= 0xA4CF
            || c >= 0xAC00 && c <= 0xD7A3 || c >= 0xF900 && c <= 0xFAFF || c >= 0xFF00 && c <= 0xFF60) ? 2 : 1;
        var w = s.Sum(WidthOf);
        return w >= width ? s : s + new string(' ', width - w);
    }

    private static string TruncForLine(string s, int max)
    {
        var lines = s.Split('\n');
        var one = lines[0].TrimEnd('\r');
        return one.Length <= max ? one : one[..max] + "…";
    }

    private static void Fatal(string kind, Exception? ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AgentCompany", "diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [{kind}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响流程
        }
    }
}
