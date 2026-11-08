using System.IO;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>测试用事件总线桩：只收不发（EventRecorder 需要 IEventBus）。</summary>
public sealed class FakeBus : IEventBus
{
    public List<AppEvent> Received { get; } = new();

    public void Publish(AppEvent evt) => Received.Add(evt);

    public event Action<AppEvent>? EventReceived
    {
        add { }
        remove { }
    }

    public IReadOnlyList<AppEvent> Snapshot() => Received;
}

/// <summary>
/// 轮 5：启动清扫判定——伪造 process_journal 行 + mock PID 存活/进程名/杀树，
/// 验证三档判定（杀/复用放行/死透销账）、working→interrupted、临时文件清理、黑匣子留痕。
/// </summary>
public class StartupSweepTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _tempDir;
    private readonly Database _db;
    private readonly TaskStore _store;
    private readonly EventRecorder _recorder;
    private readonly List<int> _killed = new();

    public StartupSweepTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ac-sweep-{Guid.NewGuid():N}.db");
        _tempDir = Path.Combine(Path.GetTempPath(), $"ac-sweep-tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _db = new Database(_dbPath);
        _db.EnsureCreated();
        _store = new TaskStore(_db);
        _recorder = new EventRecorder(_db, new FakeBus());
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* 无碍 */ }
        try { Directory.Delete(_tempDir, true); } catch { /* 无碍 */ }
    }

    private StartupSweep MakeSweep(Func<int, bool> alive, Func<int, string?> name) =>
        new(_db, _store, _recorder,
            isAlive: alive,
            processName: name,
            killTree: pid => _killed.Add(pid),
            tempDir: _tempDir);

    [Fact]
    public void Alive_Own_Tree_Gets_Killed_And_Journal_Cleaned()
    {
        var pid = 4321;
        _db.InsertJournal(7, 1, "claude", pid);

        var result = MakeSweep(alive: _ => true, name: _ => "cmd").Run();

        Assert.Equal(new[] { pid }, result.KilledPids);
        Assert.Equal(new[] { pid }, _killed);
        Assert.Empty(_db.LoadUncleanedJournal()); // 全部销账
        // 黑匣子留痕含 PID
        Assert.Contains(_db.LoadEvents(), e => e.Type == EventTypes.Sweep && e.Detail.Contains($"pid={pid}"));
    }

    [Fact]
    public void Dead_Pid_Cleaned_Without_Kill()
    {
        _db.InsertJournal(null, null, "claude", 999);

        var result = MakeSweep(alive: _ => false, name: _ => "cmd").Run();

        Assert.Empty(result.KilledPids);
        Assert.Empty(_killed);
        Assert.Empty(_db.LoadUncleanedJournal());
    }

    [Fact]
    public void Recycled_Pid_Spared()
    {
        var pid = 31337;
        _db.InsertJournal(8, 2, "opencode", pid);

        var result = MakeSweep(alive: _ => true, name: _ => "notepad").Run();

        // PID 被无关程序复用：不杀，只销账并留痕
        Assert.Empty(result.KilledPids);
        Assert.Empty(_killed);
        Assert.Empty(_db.LoadUncleanedJournal());
        Assert.Contains(_db.LoadEvents(), e => e.Type == EventTypes.Sweep && e.Detail.Contains("复用"));
    }

    [Fact]
    public void Working_Task_Marked_Interrupted_With_Event()
    {
        var id = _store.CreatePipeline("崩溃测试", new[] { ("claude", "写"), ("opencode", "摘要") });
        _store.SetTaskStatus(id, "working", currentStep: 1);
        _store.SetStepRunning(id, 1, null);

        var result = MakeSweep(alive: _ => false, name: _ => "cmd").Run();

        Assert.Equal(new[] { id }, result.InterruptedTaskIds);
        Assert.Equal(1, result.InterruptedSteps);
        var t = _store.LoadTask(id)!;
        Assert.Equal("interrupted", t.Status);
        Assert.NotNull(t.InterruptedAt);
        Assert.Equal("interrupted", t.Steps[0].Status);
        Assert.Contains(_db.LoadEvents(), e => e.Type == EventTypes.Interrupt && e.Subject == $"任务#{id}");
    }

    [Fact]
    public void Leftover_Temp_Files_Deleted_Others_Kept()
    {
        File.WriteAllText(Path.Combine(_tempDir, "mcp-20260101-000000-abcd.json"), "{}");
        File.WriteAllText(Path.Combine(_tempDir, "prompt-20260101-000000-abcd.txt"), "提示词");
        File.WriteAllText(Path.Combine(_tempDir, "无关文件.txt"), "保留");

        var result = MakeSweep(alive: _ => false, name: _ => "cmd").Run();

        Assert.Equal(2, result.DeletedTempFiles.Count);
        Assert.Contains("无关文件.txt", Directory.EnumerateFiles(_tempDir).Select(Path.GetFileName));
    }
}
