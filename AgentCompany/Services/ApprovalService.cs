using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows.Threading;
using AgentCompany.Core;

namespace AgentCompany.Desktop.Services;

/// <summary>
/// 审批决定：Approved + 决定标签（approved/denied/timeout）+ 回给 Bridge 的 note。
/// </summary>
public sealed record ApprovalDecision(bool Approved, string Decision, string? Note = null);

// IApprovalGate 接口已随 M1「引擎下沉」抽至 AgentCompany.Engine\IApprovalGate.cs（namespace 不变）。

/// <summary>待审批项（审批中心页直接绑定）。</summary>
public sealed class PendingApproval : INotifyPropertyChanged
{
    private int _remainingSeconds = ApprovalService.WaitSeconds;

    public required string Id { get; init; }
    public required string Agent { get; init; }
    public required string Tool { get; init; }
    public required string Command { get; init; }
    public required DateTime RequestedAt { get; init; }

    /// <summary>轮 7：来源任务标题（多任务并行时审批中心按它区分；null = 监视页直发）。</summary>
    public string? TaskTitle { get; init; }

    public DateTime Deadline => RequestedAt.AddSeconds(ApprovalService.WaitSeconds);
    public string RequestedAtText => RequestedAt.ToString("HH:mm:ss");
    public string RemainingText => $"剩余 {_remainingSeconds}s";
    /// <summary>来源行文本（每条待审批独立显示，多任务并行不串线）。</summary>
    public string SourceText =>
        string.IsNullOrEmpty(TaskTitle) ? "来源：监视页直发" : $"来源任务：{TaskTitle}";

    /// <summary>剩余秒数（纯函数，独立于 1s 计时器，便于直测倒计时状态机）。</summary>
    public int ComputeRemaining(DateTime now) => Math.Max(0, (int)(Deadline - now).TotalSeconds);

    /// <summary>是否到时（到点按拒绝收尾）。</summary>
    public bool TimeoutDue(DateTime now) => now >= Deadline;

    public int RemainingSeconds
    {
        get => _remainingSeconds;
        set
        {
            if (_remainingSeconds == value)
                return;
            _remainingSeconds = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemainingText)));
        }
    }

    // RunContinuationsAsynchronously：决定后管道写回不在 UI 线程上跑
    internal TaskCompletionSource<ApprovalDecision> Tcs { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 宿主侧审批门（轮 3）：
///  - 命名管道 AgentCompany.Approval 服务端，接收 Bridge 的 terminal 审批请求；
///  - 请求进待审批列表 + 弹 ContentDialog（设置可关），批准/拒绝后写回管道；
///  - WaitSeconds 内无人决定 → 按 timeout 处理（回 approved=false + note=timeout，同时落库）；
///  - approval_records 落库（agent/command/decision/requested_at/decided_at），重启仍在。
/// fail-safe 方向：一切异常（管道坏、写回失败、Bridge 已断）都往"拒绝"收敛，绝不放行。
/// </summary>
public sealed class ApprovalService : IApprovalGate, ITaskApprovalGate, IDisposable
{
    /// <summary>审批等待上限（秒）。取 115（Bridge 侧等 120s），留 5s 余量让响应先到。</summary>
    public const int WaitSeconds = 115;

    /// <summary>设置键：审批请求是否同时弹窗（默认开，"false" 表示关）。</summary>
    public const string DialogSettingKey = "approval_show_dialog";

    private const int PipeBusyRetryMs = 1000;

    private readonly Database _db;
    private readonly IEventBus _bus;
    private readonly EventRecorder? _recorder;
    private readonly Dispatcher _ui;
    private readonly Dictionary<string, PendingApproval> _byId = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private bool _dialogOpen;
    private bool _disposed;

    /// <summary>待审批列表（UI 线程读写；审批中心页绑定）。</summary>
    public ObservableCollection<PendingApproval> Pending { get; } = new();

    public int PendingCount
    {
        get { lock (_gate) return _byId.Count; }
    }

    /// <summary>
    /// B5.1：某来源任务标题是否存在待审批命令（看门狗/墙钟按任务粒度暂停用；
    /// 与 PendingApproval.TaskTitle 精确匹配，审批请求经 Bridge 原样带回）。
    /// </summary>
    public bool HasPendingFor(string taskTitle)
    {
        lock (_gate)
            return _byId.Values.Any(p => string.Equals(p.TaskTitle, taskTitle, StringComparison.Ordinal));
    }

    public string? WaitingOnCommand
    {
        get { lock (_gate) return _byId.Values.FirstOrDefault()?.Command; }
    }

    public int WaitingRemainingSeconds
    {
        get
        {
            lock (_gate)
            {
                var first = _byId.Values.FirstOrDefault();
                return first is null ? 0 : Math.Max(0, (int)(first.Deadline - DateTime.Now).TotalSeconds);
            }
        }
    }

    /// <summary>须在 UI 线程上构造（App 启动时）。轮 5：recorder 把决定写进 events 黑匣子。</summary>
    public ApprovalService(Database db, IEventBus bus, EventRecorder? recorder = null)
    {
        _db = db;
        _bus = bus;
        _recorder = recorder;
        _ui = Dispatcher.CurrentDispatcher;
    }

    /// <summary>启动管道监听与超时计时（App 主窗口显示后调用）。</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();
    }

    // ---- 管道服务端 ----

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // MaxAllowedServerInstances：上一条连接还挂着时也能立刻建下一条
                server = new NamedPipeServerStream(
                    ApprovalPipe.DefaultPipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                TryDispose(server);
                return;
            }
            catch (Exception ex)
            {
                Diag($"审批管道监听异常：{ex.Message}");
                TryDispose(server);
                await Task.Delay(PipeBusyRetryMs, ct).ContinueWith(_ => { });
                continue;
            }

            var accepted = server;
            _ = Task.Run(() => HandleConnectionAsync(accepted, ct), ct);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        PendingApproval? item = null;
        try
        {
            using var srv = server;
            var reader = new StreamReader(srv, Encoding.UTF8);
            var writer = new StreamWriter(srv, new UTF8Encoding(false)) { AutoFlush = true };

            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
                return; // 空请求：直接断开（Bridge 侧会按不可达=拒绝处理）
            var req = ApprovalPipe.DecodeRequest(line); // 坏 JSON 抛异常 → 走 finally 外的 catch

            item = new PendingApproval
            {
                Id = req.Id,
                Agent = req.Agent,
                Tool = req.Tool,
                Command = req.Command,
                RequestedAt = DateTime.Now,
                TaskTitle = req.TaskTitle,
            };

            var t0 = DateTime.Now;
            lock (_gate) _byId.Add(req.Id, item);
            Diag($"审批请求到达：{req.Agent}/{req.Tool} id={req.Id[..8]} 命令={req.Command}");
            var incoming = item;
            _ = _ui.BeginInvoke(() =>
            {
                Pending.Add(incoming);
                MaybeShowDialog(incoming);
            });
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Approval,
                $"🔐 审批请求（{item.Agent}/{item.Tool}）：{item.Command}"));

            var decision = await item.Tcs.Task; // UI 按钮 / 弹窗 / 超时，三者必居其一

            // 决定写回管道（Bridge 可能已超时断开，写失败只记日志）
            try
            {
                await writer.WriteLineAsync(ApprovalPipe.Encode(
                    new ApprovalResponse(req.Id, decision.Approved, decision.Note)));
                await srv.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                Diag($"审批响应写回失败（{ex.Message}）——Bridge 可能已超时");
            }

            _db.InsertApprovalRecord(new ApprovalRecord(0, req.Agent, item.Command, decision.Decision,
                item.RequestedAt, DateTime.Now));
            _recorder?.Record(EventTypes.Approval, item.Agent,
                $"{item.Command} → {decision.Decision}（请求 {item.RequestedAt:HH:mm:ss}）");
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Approval,
                decision.Decision switch
                {
                    "approved" => $"✅ 已批准：{item.Command}",
                    "timeout" => $"⏱ 审批超时（按拒绝处理）：{item.Command}",
                    _ => $"❌ 已拒绝：{item.Command}",
                }));
            Diag($"审批链路：{item.Command} 决定={decision.Decision}" +
                 $" 请求→决定 {(DateTime.Now - t0).TotalMilliseconds:0}ms");

            var decided = item;
            _ = _ui.BeginInvoke(() => Pending.Remove(decided));
        }
        catch (OperationCanceledException)
        {
            // 应用退出
        }
        catch (Exception ex)
        {
            Diag($"审批连接处理异常：{ex.Message}");
            // 请求已进列表但没等到决定（异常路径）→ 按 timeout 收尾，别让看门狗永远暂停
            if (item is not null)
                Decide(item, new ApprovalDecision(false, "timeout", "timeout"));
        }
    }

    // ---- 决定 ----

    /// <summary>审批中心页 [批准]/[拒绝] 按钮与弹窗按钮都走这里（幂等，二次调用无效果）。</summary>
    public void Decide(PendingApproval item, ApprovalDecision decision)
    {
        lock (_gate)
        {
            if (!_byId.Remove(item.Id))
                return; // 已被另一路决定（按钮/弹窗/超时三选一先到者赢）
        }
        item.Tcs.TrySetResult(decision);
    }

    private void OnTimerTick()
    {
        PendingApproval[] snapshot;
        lock (_gate) snapshot = _byId.Values.ToArray();

        var now = DateTime.Now;
        foreach (var item in snapshot)
        {
            item.RemainingSeconds = Math.Max(0, (int)(item.Deadline - now).TotalSeconds);
            if (now >= item.Deadline)
                Decide(item, new ApprovalDecision(false, "timeout", "timeout"));
        }
    }

    // ---- 弹窗 ----

    private async void MaybeShowDialog(PendingApproval item)
    {
        if (_disposed || !_ui.CheckAccess())
        {
            DiagLog($"弹窗跳过：disposed={!_disposed} ui线程={_ui.CheckAccess()}");
            return;
        }
        var setting = _db.GetSetting(DialogSettingKey);
        if (setting == "false")
        {
            DiagLog($"弹窗跳过：设置 approval_show_dialog=false（命令：{item.Command}）");
            return; // 设置里关了弹窗，只留审批中心列表
        }
        if (_dialogOpen)
        {
            DiagLog($"弹窗跳过：已有弹窗打开（命令：{item.Command}）");
            return; // 同时只弹一个；排队中的请求在列表里等用户点
        }
        DiagLog($"弹窗准备弹出：{item.Command}");

        _dialogOpen = true;
        try
        {
            var dialog = new ModernWpf.Controls.ContentDialog
            {
                Title = "命令审批",
                Content = $"员工 {item.Agent} 请求执行命令：\n\n{item.Command}\n\n批准后将在工作区内执行（工作目录=工作区根）。",
                PrimaryButtonText = "批准",
                CloseButtonText = "拒绝",
                DefaultButton = ModernWpf.Controls.ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            Decide(item, result == ModernWpf.Controls.ContentDialogResult.Primary
                ? new ApprovalDecision(true, "approved")
                : new ApprovalDecision(false, "denied"));
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ---- 收尾 ----

    private void Diag(string message)
    {
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.AgentDiag, message));
        DiagLog(message);
    }

    /// <summary>审批诊断落盘（%APPDATA%\AgentCompany\diag.log），排弹窗/管道问题用。</summary>
    private static void DiagLog(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AgentCompany", "diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [审批] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响流程
        }
    }

    private static void TryDispose(NamedPipeServerStream? server)
    {
        try { server?.Dispose(); } catch { /* 收尾失败无碍 */ }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Stop();
        _cts?.Cancel();
        lock (_gate)
        {
            foreach (var item in _byId.Values)
                item.Tcs.TrySetResult(new ApprovalDecision(false, "timeout", "timeout"));
            _byId.Clear();
        }
    }
}
