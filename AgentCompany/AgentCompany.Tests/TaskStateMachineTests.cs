using System.IO;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// 轮 5：任务状态机全转换——pending/working/completed/failed/cancelled/interrupted
/// 与 interrupted→working（重跑）的持久化正确性。每例用独立临时库。
/// </summary>
public class TaskStateMachineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly TaskStore _store;

    public TaskStateMachineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ac-test-{Guid.NewGuid():N}.db");
        var db = new Database(_dbPath);
        db.EnsureCreated();
        _store = new TaskStore(db);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* 清理失败无碍 */ }
    }

    private int MakeTask(string title = "测试流水线") =>
        _store.CreatePipeline(title, new[] { ("claude", "写故事"), ("opencode", "写摘要") });

    [Fact]
    public void Working_To_Completed()
    {
        var id = MakeTask();
        _store.SetTaskStatus(id, "working", currentStep: 1);
        _store.SetStepRunning(id, 1, "s1");
        _store.SetStepFinished(id, 1, "done", "s1", null);
        _store.SetStepRunning(id, 2, "s2");
        _store.SetStepFinished(id, 2, "done", "s2", null);
        _store.SetTaskArtifacts(id, new[] { "story.txt", "summary.txt" });
        _store.SetTaskStatus(id, "completed", currentStep: 2);

        var t = _store.LoadTask(id)!;
        Assert.Equal("completed", t.Status);
        Assert.Equal(2, t.CurrentStep);
        Assert.All(t.Steps, s => Assert.Equal("done", s.Status));
        Assert.Equal(new[] { "story.txt", "summary.txt" }, t.Artifacts);
    }

    [Fact]
    public void Working_To_Failed_Step_Loss_Cut()
    {
        var id = MakeTask();
        _store.SetTaskStatus(id, "working", currentStep: 1);
        _store.SetStepRunning(id, 1, null);
        _store.SetStepFinished(id, 1, "failed", null, "进程异常退出（exit != 0）");
        _store.SetTaskStatus(id, "failed", currentStep: 1);

        var t = _store.LoadTask(id)!;
        Assert.Equal("failed", t.Status);
        Assert.Equal("failed", t.Steps[0].Status);
        Assert.Equal("进程异常退出（exit != 0）", t.Steps[0].Error);
        // 止损：步骤 2 从未启动
        Assert.Equal("pending", t.Steps[1].Status);
    }

    [Fact]
    public void Working_To_Cancelled()
    {
        var id = MakeTask();
        _store.SetTaskStatus(id, "working", currentStep: 1);
        _store.SetStepRunning(id, 1, null);
        _store.SetStepFinished(id, 1, "cancelled", null, null);
        _store.SetTaskStatus(id, "cancelled", currentStep: 1);

        Assert.Equal("cancelled", _store.LoadTask(id)!.Status);
    }

    [Fact]
    public void Working_To_Interrupted_With_Time()
    {
        var id = MakeTask();
        _store.SetTaskStatus(id, "working", currentStep: 1);
        _store.SetStepRunning(id, 1, "s1");

        var affected = _store.MarkWorkingInterrupted();
        var steps = _store.MarkRunningStepsInterrupted();

        Assert.Equal(new[] { id }, affected.Select(a => a.Id).ToArray());
        Assert.Equal(1, steps);
        var t = _store.LoadTask(id)!;
        Assert.Equal("interrupted", t.Status);
        Assert.NotNull(t.InterruptedAt);
        Assert.Equal("interrupted", t.Steps[0].Status);
        Assert.NotNull(t.Steps[0].InterruptedAt);
        Assert.Equal("pending", t.Steps[1].Status);
    }

    [Fact]
    public void Interrupted_Rerun_From_Step2_To_Completed()
    {
        var id = MakeTask();
        // 步骤 1 完成、步骤 2 中断
        _store.SetTaskStatus(id, "working", currentStep: 1);
        _store.SetStepRunning(id, 1, "s1");
        _store.SetStepFinished(id, 1, "done", "s1", null);
        _store.SetTaskStatus(id, "working", currentStep: 2);
        _store.SetStepRunning(id, 2, "s2");
        _store.MarkWorkingInterrupted();
        _store.MarkRunningStepsInterrupted();

        // 一键恢复：从步骤 2 重跑（新会话——旧 session/error 必须清掉）
        _store.ResetForRerun(id, 2);
        var mid = _store.LoadTask(id)!;
        Assert.Equal("working", mid.Status);
        Assert.Equal(2, mid.CurrentStep);
        Assert.Equal("done", mid.Steps[0].Status);       // 步骤 1 保留
        Assert.Null(mid.InterruptedAt);
        Assert.Equal("pending", mid.Steps[1].Status);    // 步骤 2 回待办
        Assert.Null(mid.Steps[1].SessionId);
        Assert.Null(mid.Steps[1].InterruptedAt);

        // 重跑成功 → completed
        _store.SetStepRunning(id, 2, "s2-new");
        _store.SetStepFinished(id, 2, "done", "s2-new", null);
        _store.SetTaskStatus(id, "completed", currentStep: 2);
        var done = _store.LoadTask(id)!;
        Assert.Equal("completed", done.Status);
        Assert.Equal("s2-new", done.Steps[1].SessionId);
    }

    [Fact]
    public void Interrupted_Rerun_All_From_Step1_Clears_Everything()
    {
        var id = MakeTask();
        _store.SetTaskStatus(id, "working", currentStep: 2);
        _store.SetStepRunning(id, 1, "s1");
        _store.SetStepFinished(id, 1, "done", "s1", null);
        _store.SetStepRunning(id, 2, "s2");
        _store.MarkWorkingInterrupted();
        _store.MarkRunningStepsInterrupted();
        _store.SetTaskArtifacts(id, new[] { "story.txt" });

        // 整条重跑：步骤 1 也要回到 pending、产物清空待重盘
        _store.ResetForRerun(id, 1);
        var t = _store.LoadTask(id)!;
        Assert.Equal("working", t.Status);
        Assert.Equal(1, t.CurrentStep);
        Assert.All(t.Steps, s => Assert.Equal("pending", s.Status));
        Assert.All(t.Steps, s => Assert.Null(s.SessionId));
        Assert.Empty(t.Artifacts);
    }

    [Fact]
    public void Failed_Rerun_To_Cancelled()
    {
        var id = MakeTask();
        _store.SetTaskStatus(id, "working", currentStep: 1);
        _store.SetStepRunning(id, 1, null);
        _store.SetStepFinished(id, 1, "failed", null, "claude 正忙");
        _store.SetTaskStatus(id, "failed", currentStep: 1);

        _store.ResetForRerun(id, 1);
        Assert.Equal("working", _store.LoadTask(id)!.Status);

        _store.SetStepFinished(id, 1, "cancelled", null, null);
        _store.SetTaskStatus(id, "cancelled", currentStep: 1);
        Assert.Equal("cancelled", _store.LoadTask(id)!.Status);
    }
}
