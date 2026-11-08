using System.IO;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// v1.1 B5.1（轮 7 已知问题 4）：看门狗/墙钟暂停从「任一待审批全局暂停」改为按任务粒度。
/// 直调 BudgetService.Heartbeat（与 DispatcherTimer 同一入口）断言墙钟账：
/// 任务 A 有待审批 → 仅 A 停表；任务 B 无 → B 照计；A 审批完结 → A 恢复计时。
/// 旧式审批门（未实现 ITaskApprovalGate）与未带标题登记的任务回退 v1.0 全局口径。
/// </summary>
public class WatchdogPerTaskTests
{
    /// <summary>按任务审批门桩：可增删待审批的来源任务标题。</summary>
    private sealed class TaskGate : IApprovalGate, ITaskApprovalGate
    {
        private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

        public int PendingCount => _pending.Count;
        public string? WaitingOnCommand => null;
        public int WaitingRemainingSeconds => 0;
        public bool HasPendingFor(string taskTitle) => _pending.Contains(taskTitle);

        public void Add(string taskTitle) => _pending.Add(taskTitle);
        public void Clear() => _pending.Clear();
    }

    /// <summary>旧式审批门桩（只有 IApprovalGate，PendingCount 恒 1）：验证 v1.0 全局口径回退。</summary>
    private sealed class LegacyGate : IApprovalGate
    {
        public int PendingCount => 1;
        public string? WaitingOnCommand => null;
        public int WaitingRemainingSeconds => 0;
    }

    private static (BudgetService Svc, string DbPath) NewService(IApprovalGate gate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ac-watchdog-pt-{Guid.NewGuid():N}.db");
        var db = new Database(path);
        db.EnsureCreated();
        return (new BudgetService(db, new EventRecorder(db, new FakeBus()), gate), path);
    }

    private static Dictionary<int, int> Walls(BudgetService svc) =>
        svc.ActiveSnapshot().ToDictionary(s => s.TaskId, s => s.UsedWallSeconds);

    [Fact]
    public void 任务A有待审批_仅A停表_任务B照计()
    {
        var gate = new TaskGate();
        var (svc, path) = NewService(gate);
        try
        {
            svc.Register(1, null, 3600, null, "任务A标题");
            svc.Register(2, null, 3600, null, "任务B标题");
            gate.Add("任务A标题");

            svc.Heartbeat();
            svc.Heartbeat();

            var walls = Walls(svc);
            Assert.Equal(0, walls[1]); // A 等审批：停表
            Assert.Equal(2, walls[2]); // B 无审批：照计（v1.0 会一起停——本修的目标）
        }
        finally
        {
            svc.Unregister(1);
            svc.Unregister(2);
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void 任务A审批完结_A恢复计时()
    {
        var gate = new TaskGate();
        var (svc, path) = NewService(gate);
        try
        {
            svc.Register(1, null, 3600, null, "任务A标题");
            gate.Add("任务A标题");
            svc.Heartbeat();
            Assert.Equal(0, Walls(svc)[1]);

            gate.Clear(); // 用户点了批准/拒绝：待审批清空
            svc.Heartbeat();
            svc.Heartbeat();
            Assert.Equal(2, Walls(svc)[1]); // A 恢复走表
        }
        finally
        {
            svc.Unregister(1);
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void 暂停判定_纯函数_标题任务看该任务_无标题或旧门回退全局()
    {
        var gate = new TaskGate();
        gate.Add("任务A标题");
        // 登记了标题 + 门支持按任务查询 → 只看该任务
        Assert.True(BudgetService.PauseForTask(gate, anyPending: true, "任务A标题"));
        Assert.False(BudgetService.PauseForTask(gate, anyPending: true, "任务B标题")); // 别人的审批不停 B
        // 无标题（直发/旧调用点）→ v1.0 全局口径
        Assert.True(BudgetService.PauseForTask(gate, anyPending: true, null));
        Assert.False(BudgetService.PauseForTask(gate, anyPending: false, null));
        // 门不支持按任务查询（旧测试桩等）→ v1.0 全局口径
        Assert.True(BudgetService.PauseForTask(null, anyPending: true, "任务A标题"));
        Assert.False(BudgetService.PauseForTask(null, anyPending: false, "任务A标题"));
    }

    [Fact]
    public void 旧式审批门_全部任务按全局口径暂停()
    {
        var (svc, path) = NewService(new LegacyGate());
        try
        {
            svc.Register(1, null, 3600, null, "任务A标题"); // PendingCount 恒 1：全局暂停
            svc.Register(2, null, 3600, null);              // 无标题：本就全局口径
            svc.Heartbeat();
            var walls = Walls(svc);
            Assert.Equal(0, walls[1]);
            Assert.Equal(0, walls[2]);
        }
        finally
        {
            svc.Unregister(1);
            svc.Unregister(2);
            try { File.Delete(path); } catch { }
        }
    }
}
