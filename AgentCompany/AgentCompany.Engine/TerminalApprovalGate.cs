using System.IO;
using System.IO.Pipes;
using System.Text;
using AgentCompany.Core;

namespace AgentCompany.Desktop.Services;

/// <summary>终端审批决定（口径同 GUI 版 ApprovalDecision：Approved + 标签 + 回给 Bridge 的 note）。</summary>
public sealed record TerminalDecision(bool Approved, string Decision, string? Note = null);

/// <summary>
/// CLI 版审批门（M4 特性 D2）：命名管道服务端与 GUI ApprovalService 完全同协议同源——
/// 管道名/一行 JSON/超时全用 <see cref="ApprovalPipe"/> 常量，收到请求 → 终端渲染
/// 来源任务 + 完整命令 + [y=批准 n=拒绝 剩余 Ns] 倒计时 → 决定写回管道并按同口径落
/// approval_records（agent/command/decision/requested_at/decided_at）。
/// fail-safe 方向与 GUI 一致：超时=拒绝（note=timeout），一切异常往"拒绝"收敛。
/// 交互（读写/时钟）全部经委托注入，<see cref="Decide"/> 为纯函数可单测（假钟，不真等）。
/// </summary>
public sealed class TerminalApprovalGate : IApprovalGate, ITaskApprovalGate, IDisposable
{
    /// <summary>审批等待上限（秒）。115 与 GUI ApprovalService.WaitSeconds 同值同源——
    /// Bridge 侧等 120s，留 5s 余量让响应先到（Engine 不引 Desktop，复制值并在此注明出处）。</summary>
    public const int WaitSeconds = 115;

    private const int PipeBusyRetryMs = 1000;

    private readonly Database _db;
    private readonly IEventBus _bus;
    private readonly EventRecorder? _recorder;
    private readonly Func<int, string?> _readLine;   // (timeoutMs) => 行｜null（窗口内无输入）
    private readonly Func<DateTime> _clock;
    private readonly Action<string> _write;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingItem> _pending = new();
    /// <summary>终端交互互斥（联审修复 m234 #2）：一次只允许一条审批占台交互，其余 FIFO 等待——
    /// 防两条并发请求的 Decide 循环交错读写同一控制台（提示串台、y/n 归属错乱）。
    /// 等待方挂起期间仍留在 _pending：其任务墙钟照 GUI 语义暂停；拿到台子时 Decide 首查
    /// 剩余时间，过线即按 timeout 拒绝（fail-safe 不变）。</summary>
    private readonly SemaphoreSlim _interact = new(1, 1);
    private CancellationTokenSource? _cts;

    private sealed record PendingItem(string Id, string Agent, string Command, string? TaskTitle, DateTime RequestedAt)
    {
        public DateTime Deadline => RequestedAt.AddSeconds(WaitSeconds);
    }

    /// <summary>readLine/clock/write 缺省接真实控制台（Program 可换成注入实现与测试口径）。</summary>
    public TerminalApprovalGate(Database db, IEventBus bus, EventRecorder? recorder = null,
        Func<int, string?>? readLine = null, Func<DateTime>? clock = null, Action<string>? write = null)
    {
        _db = db;
        _bus = bus;
        _recorder = recorder;
        _readLine = readLine ?? (timeout => ReadConsoleLine(timeout));
        _clock = clock ?? (() => DateTime.Now);
        _write = write ?? (s => Console.WriteLine(s));
    }

    /// <summary>启动管道监听（须在互斥探测通过后调用；GUI 与 CLI 不并存）。</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    // ---- 决定（纯函数，注入读写与时钟；单测用假钟不真等 115s） ----

    /// <summary>
    /// 终端 y/n 决定：渲染请求与倒计时，readLine 在 timeoutMs 窗口内没等到输入返回 null
    /// （真实控制台带倒计时轮询；重定向/测试下可立即给行）；y/Y 批准、n/N 拒绝，
    /// 其余输入重提示；clock 过 deadline 即按 timeout 拒绝（note=timeout，与 GUI 同口径）。
    /// </summary>
    public static TerminalDecision Decide(ApprovalRequest request, int countdownSeconds,
        Func<int, string?> readLine, Func<DateTime> clock, Action<string> write)
    {
        var deadline = clock().AddSeconds(countdownSeconds);
        write("────────── 审批请求 ──────────");
        write($"来源：{(string.IsNullOrEmpty(request.TaskTitle) ? "未标记任务" : request.TaskTitle)} · 员工 {request.Agent}");
        write($"命令：{request.Command}");
        var lastShown = -1;
        while (true)
        {
            var remaining = (int)Math.Ceiling((deadline - clock()).TotalSeconds);
            if (remaining <= 0)
                return new TerminalDecision(false, "timeout", "timeout");
            if (remaining != lastShown)
            {
                write($"[y=批准 n=拒绝 剩余 {remaining}s]");
                lastShown = remaining;
            }
            var line = readLine(1000);
            if (line is null)
                continue; // 本秒窗口无输入：下一轮刷新剩余时间
            var key = line.Trim().ToLowerInvariant();
            if (key is "y" or "yes")
                return new TerminalDecision(true, "approved");
            if (key is "n" or "no")
                return new TerminalDecision(false, "denied");
            write("（请输入 y 或 n）");
        }
    }

    /// <summary>真实控制台行读：交互终端用 KeyAvailable 逐字符攒行（窗口满返回 null，
    /// 倒计时得以推进）；输入重定向（管道/文件）下 KeyAvailable 不可用，退化为单枚
    /// 挂起的阻塞 ReadLine 任务并只等当前窗口（联审修复 m234 #2）——窗口内没等到返回
    /// null，倒计时与超时判定得以推进，杜绝"ReadLine 永久阻塞 → 请求滞留 _pending →
    /// 墙钟/看门狗无限暂停"。</summary>
    private static Task<string?>? _redirectedRead;
    private static readonly object _redirectedReadLock = new();

    private static string? ReadConsoleLine(int timeoutMs)
    {
        try
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            var buf = new StringBuilder();
            while (Environment.TickCount64 < deadline)
            {
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(50);
                    continue;
                }
                var ki = Console.ReadKey(intercept: true);
                if (ki.Key == ConsoleKey.Enter)
                    return buf.ToString();
                if (ki.Key == ConsoleKey.Backspace)
                {
                    if (buf.Length > 0)
                        buf.Remove(buf.Length - 1, 1);
                }
                else
                {
                    buf.Append(ki.KeyChar);
                }
            }
            return null;
        }
        catch (InvalidOperationException)
        {
            // 重定向输入没有 KeyAvailable：同一时刻只挂一枚阻塞 ReadLine（复用直到完成），
            // 本窗口等不到就交还 Decide 的时钟判定（到 deadline 即 timeout 拒绝）
            lock (_redirectedReadLock)
            {
                _redirectedRead ??= Task.Run(() => Console.ReadLine());
            }
            if (!_redirectedRead.Wait(Math.Max(1, timeoutMs)))
                return null; // 窗口内无输入：倒计时继续
            var line = _redirectedRead.IsFaulted ? null : _redirectedRead.Result;
            lock (_redirectedReadLock) _redirectedRead = null; // 已消费，下次重挂
            return line;
        }
    }

    // ---- 管道服务端（仿 GUI ApprovalService：一行 JSON 请求/响应 + fail-safe=拒绝） ----

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
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
        PendingItem? item = null;
        try
        {
            using var srv = server;
            var reader = new StreamReader(srv, Encoding.UTF8);
            var writer = new StreamWriter(srv, new UTF8Encoding(false)) { AutoFlush = true };

            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
                return; // 空请求直接断开（Bridge 侧按不可达=拒绝处理）
            var req = ApprovalPipe.DecodeRequest(line);

            item = new PendingItem(req.Id, req.Agent, req.Command, req.TaskTitle, DateTime.Now);
            lock (_gate) _pending.Add(req.Id, item);
            Diag($"审批请求到达：{req.Agent} id={req.Id[..Math.Min(8, req.Id.Length)]} 命令={req.Command}");
            _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Approval,
                $"🔐 审批请求（{req.Agent}）：{req.Command}"));

            // 终端交互（独立任务线程，不阻塞事件流渲染）；异常一律按 timeout 收尾（fail-safe=拒绝）
            // 联审修复 m234 #2：占台互斥——并发请求逐条交互（FIFO），等待期间倒计时继续
            TerminalDecision decision;
            try
            {
                if (_interact.CurrentCount == 0)
                    _write("（审批排队中：终端一次只交互一条，按到达顺序处理；等待期间倒计时继续，超时按拒绝）");
                await _interact.WaitAsync(ct);
                try
                {
                    decision = Decide(req, WaitSeconds, _readLine, _clock, _write);
                }
                finally
                {
                    _interact.Release();
                }
            }
            catch (OperationCanceledException)
            {
                throw; // 宿主退出：走外层统一收尾（Dispose 清 _pending）
            }
            catch (Exception ex)
            {
                Diag($"终端审批交互异常（{ex.Message}）：按超时拒绝");
                decision = new TerminalDecision(false, "timeout", "timeout");
            }

            lock (_gate) _pending.Remove(req.Id);
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
            Diag($"审批链路：{item.Command} 决定={decision.Decision}");
        }
        catch (OperationCanceledException)
        {
            // 宿主退出
        }
        catch (Exception ex)
        {
            Diag($"审批连接处理异常：{ex.Message}");
            if (item is not null)
                lock (_gate) _pending.Remove(item.Id);
        }
    }

    // ---- IApprovalGate / ITaskApprovalGate（看门狗与预算墙钟的暂停口径，与 GUI 同语义） ----

    public int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    public string? WaitingOnCommand
    {
        get { lock (_gate) return _pending.Values.FirstOrDefault()?.Command; }
    }

    public int WaitingRemainingSeconds
    {
        get
        {
            lock (_gate)
            {
                var first = _pending.Values.FirstOrDefault();
                return first is null ? 0 : Math.Max(0, (int)(first.Deadline - DateTime.Now).TotalSeconds);
            }
        }
    }

    public bool HasPendingFor(string taskTitle)
    {
        lock (_gate)
            return _pending.Values.Any(p => string.Equals(p.TaskTitle, taskTitle, StringComparison.Ordinal));
    }

    // ---- 收尾 ----

    private void Diag(string message)
    {
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.AgentDiag, message));
        try
        {
            File.AppendAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AgentCompany", "diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [审批CLI] {message}{Environment.NewLine}");
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
        _cts?.Cancel();
        lock (_gate) _pending.Clear();
    }
}
