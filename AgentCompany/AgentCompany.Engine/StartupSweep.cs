using System.Diagnostics;
using System.IO;

namespace AgentCompany.Desktop.Services;

/// <summary>一次启动清扫的结果（事件与测试断言用）。</summary>
public sealed record SweepResult(
    IReadOnlyList<int> KilledPids,
    IReadOnlyList<int> InterruptedTaskIds,
    int InterruptedSteps,
    IReadOnlyList<string> DeletedTempFiles);

/// <summary>
/// 启动清扫（轮 5，双保险的兜底层）：app 每次启动时——
///  a. process_journal 未销账且 PID 仍存活的 → taskkill /PID /T /F 杀树（Job Object 漏网的残党在这收口；
///     PID 被无关进程复用时不杀，只销账）；
///  b. working 状态的 tasks / running 状态的 task_steps → interrupted + interrupted_at；
///  c. 清理 %TEMP%\AgentCompany 里的 mcp-*.json / prompt-*.txt 残留（宿主崩溃时 CleanupTempArtifacts 没跑）；
///  d. 每个动作记一条黑匣子事件（events 表）。
/// 存活/进程名/杀树通过委托注入——单元测试伪造 journal 行 + mock PID 即可验证判定。
/// </summary>
public sealed class StartupSweep
{
    private readonly Database _db;
    private readonly TaskStore _store;
    private readonly EventRecorder _recorder;
    private readonly Func<int, bool> _isAlive;
    private readonly Func<int, string?> _processName;
    private readonly Action<int> _killTree;
    private readonly string _tempDir;

    public StartupSweep(
        Database db,
        TaskStore store,
        EventRecorder recorder,
        Func<int, bool>? isAlive = null,
        Func<int, string?>? processName = null,
        Action<int>? killTree = null,
        string? tempDir = null)
    {
        _db = db;
        _store = store;
        _recorder = recorder;
        _isAlive = isAlive ?? DefaultAlive;
        _processName = processName ?? DefaultName;
        _killTree = killTree ?? DefaultKill;
        _tempDir = tempDir ?? Path.Combine(Path.GetTempPath(), "AgentCompany");
    }

    /// <summary>执行清扫（app 启动、单实例互斥已过之后调用；须在开任何宿主之前）。</summary>
    public SweepResult Run()
    {
        var killed = new List<int>();

        // a. journal 未销账行：杀存活的树，全部销账
        foreach (var row in _db.LoadUncleanedJournal())
        {
            try
            {
                if (_isAlive(row.Pid))
                {
                    var name = _processName(row.Pid);
                    if (name is null || name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
                    {
                        // journal 记的是树根 cmd.exe；名字仍是 cmd 才认定是自己的残党
                        _killTree(row.Pid);
                        killed.Add(row.Pid);
                        _recorder.Record(EventTypes.Sweep, "进程",
                            $"强杀残留进程树 pid={row.Pid}（{row.Agent}{TaskLabel(row)}）");
                    }
                    else
                    {
                        _recorder.Record(EventTypes.Sweep, "进程",
                            $"pid={row.Pid} 已被无关程序复用（{name}），不杀，仅销账");
                    }
                }
                else
                {
                    // 进程已不在（Job Object 陪葬已带走 / 自然退出但没走到销账）：也要留痕含 PID
                    _recorder.Record(EventTypes.Sweep, "进程",
                        $"记账残留 pid={row.Pid}（{row.Agent}{TaskLabel(row)}）已不存在，销账");
                }
            }
            finally
            {
                try { _db.MarkJournalCleaned(row.Id); } catch { /* 销账失败下次再扫 */ }
            }
        }

        // b. working → interrupted（宿主崩溃没来得及走终态的任务）
        var interrupted = _store.MarkWorkingInterrupted();
        foreach (var (id, title) in interrupted)
            _recorder.Record(EventTypes.Interrupt, $"任务#{id}", $"「{title}」宿主异常退出，标记为已中断");
        var steps = _store.MarkRunningStepsInterrupted();

        // b2. planning → parse_failed（轮 6：CEO 拆解进程随宿主陪葬，拆解没完成，改标可 [重新拆解]）
        var planning = _store.MarkPlanningInterrupted();
        foreach (var (id, title) in planning)
            _recorder.Record(EventTypes.Interrupt, $"任务#{id}", $"「{title}」宿主重启打断 CEO 拆解，标记为拆解失败");

        // b3. queued → interrupted（轮 7：FIFO 队列在内存，重启即丢；不标记会永远停在"排队中"）
        var queued = _store.MarkQueuedInterrupted();
        foreach (var (id, title) in queued)
            _recorder.Record(EventTypes.Interrupt, $"任务#{id}", $"「{title}」宿主重启时仍在排队，标记为已中断");

        // c. 宿主投放的临时文件残留（claude 适配器：mcp-*.json / prompt-*.txt）
        var deleted = new List<string>();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                foreach (var file in Directory.EnumerateFiles(_tempDir))
                {
                    var name2 = Path.GetFileName(file);
                    if (!(name2.StartsWith("mcp-", StringComparison.OrdinalIgnoreCase)
                          && name2.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        && !(name2.StartsWith("prompt-", StringComparison.OrdinalIgnoreCase)
                             && name2.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    File.Delete(file);
                    deleted.Add(name2);
                }
            }
        }
        catch
        {
            // 清理失败无碍：临时文件只是脏，不影响正确性
        }
        if (deleted.Count > 0)
            _recorder.Record(EventTypes.Sweep, "临时文件", $"清理残留 {deleted.Count} 个（{string.Join("、", deleted.Take(5))}）");

        _recorder.Record(EventTypes.Sweep, "启动清扫",
            $"杀残留进程 {killed.Count} 个、标记中断 {interrupted.Count} 任务/{steps} 步骤、清临时文件 {deleted.Count} 个");
        return new SweepResult(killed, interrupted.Select(t => t.Id).ToList(), steps, deleted);
    }

    private static string TaskLabel(JournalRow row) =>
        row.TaskId is { } id ? $"，任务#{id} 步骤{row.Seq}" : "，监视页直发";

    private static bool DefaultAlive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static string? DefaultName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch (ArgumentException) { return null; }
    }

    private static void DefaultKill(int pid)
    {
        // /T 连子树一起；Journal 根是 cmd.exe，后代 node/CLI/McpBridge 都在 /T 范围内
        using var proc = Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        proc?.WaitForExit(5000);
    }
}
