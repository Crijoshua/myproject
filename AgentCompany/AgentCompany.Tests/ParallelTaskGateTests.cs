using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// 轮 7 多任务并发闸门（纯状态机，mock 执行器 = 闸门自身不碰进程）：
/// 2 任务并行 + 1 排队、FIFO 放行、排队取消不腾位、上限钳制、幂等占位。
/// </summary>
public class ParallelTaskGateTests
{
    [Fact]
    public void 两任务并行_第三个排队_释放后FIFO放行()
    {
        var gate = new ParallelTaskGate(maxParallel: 2);

        Assert.True(gate.TryAcquire(1));   // 任务 1 占位开跑
        Assert.True(gate.TryAcquire(2));   // 任务 2 占位开跑
        Assert.False(gate.TryAcquire(3));  // 满员：任务 3 进 FIFO 排队
        Assert.True(gate.IsQueued(3));
        Assert.Equal(new[] { 1, 2 }, gate.RunningIds.OrderBy(x => x));
        Assert.Equal(new[] { 3 }, gate.QueuedIds);

        // 任务 1 终态腾位 → 占位直接转移给队首（任务 3 已在跑，不重走 TryAcquire）
        Assert.Equal(3, gate.Release(1));
        Assert.Equal(new[] { 2, 3 }, gate.RunningIds.OrderBy(x => x));
        Assert.Empty(gate.QueuedIds);

        // 排队任务也被放行占位后，最后两个终态都无人接续
        Assert.Null(gate.Release(2));
        Assert.Null(gate.Release(3));
        Assert.Equal(0, gate.RunningCount);
    }

    [Fact]
    public void 排队任务被取消_只出队不腾位()
    {
        var gate = new ParallelTaskGate(maxParallel: 1);
        Assert.True(gate.TryAcquire(1));
        Assert.False(gate.TryAcquire(2));
        Assert.False(gate.TryAcquire(3));
        Assert.Equal(new[] { 2, 3 }, gate.QueuedIds);

        // 排队中的任务 2 被取消：不出现在任何列表，也不释放并发位（它本来就没占）
        Assert.Null(gate.Release(2));
        Assert.Equal(new[] { 3 }, gate.QueuedIds);
        Assert.Equal(new[] { 1 }, gate.RunningIds.OrderBy(x => x));

        // 在跑任务 1 结束仍按 FIFO 放行任务 3（不是已取消的 2）
        Assert.Equal(3, gate.Release(1));
    }

    [Fact]
    public void 上限钳制1到4()
    {
        Assert.Equal(1, new ParallelTaskGate(0).MaxParallel);
        Assert.Equal(1, new ParallelTaskGate(-3).MaxParallel);
        Assert.Equal(4, new ParallelTaskGate(9).MaxParallel);

        // 运行中下调不杀在跑任务：先 4 位占满，再降到 1 → 在跑照旧，新任务排队
        var gate = new ParallelTaskGate(4);
        for (var id = 1; id <= 4; id++)
            Assert.True(gate.TryAcquire(id));
        gate.MaxParallel = 1;
        Assert.Equal(4, gate.RunningCount);
        Assert.False(gate.TryAcquire(5));
    }

    [Fact]
    public void 在跑任务重复TryAcquire_幂等true_重复Release幂等()
    {
        var gate = new ParallelTaskGate(1);
        Assert.True(gate.TryAcquire(7));
        Assert.True(gate.TryAcquire(7));   // 已在跑：幂等 true（不重复占位）
        Assert.Equal(1, gate.RunningCount);

        Assert.Null(gate.Release(7));      // 无排队
        Assert.Null(gate.Release(7));      // 二次 Release：未知 id 幂等 null
        Assert.Equal(0, gate.RunningCount);
    }

    [Fact]
    public void FIFO顺序放行_并发满员时按入队顺序交棒()
    {
        var gate = new ParallelTaskGate(2);
        Assert.True(gate.TryAcquire(1));
        Assert.True(gate.TryAcquire(2));
        Assert.False(gate.TryAcquire(3));
        Assert.False(gate.TryAcquire(4));
        Assert.False(gate.TryAcquire(5));

        Assert.Equal(3, gate.Release(2));  // 第一个空位给最早排队的 3（占位已转移，3 即在跑）
        Assert.True(gate.TryAcquire(3));   // 幂等：转移后再 TryAcquire 仍 true
        Assert.Equal(4, gate.Release(1));
        Assert.Equal(5, gate.Release(3));
        Assert.Null(gate.Release(5));      // 队列已清空，无人接续
        Assert.Null(gate.Release(4));
        Assert.Equal(0, gate.RunningCount);
    }
}
