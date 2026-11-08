using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// 看门狗判定（B1 修复）：总墙钟一刀切 → 静默判活 + 5×硬上限。
/// 观察记录场景：n1 写到 4/8 文件、事件流持续，仍被 600s 总墙钟击杀——修复后活跃节点不再被杀。
/// </summary>
public class WatchdogTests
{
    [Fact]
    public void 判定_活跃节点持续输出_不杀()
    {
        // B1 复现场景：全程有事件流（静默仅 5s），总时长已到 600s——不再被杀
        var (kill, _) = AgentCliProcessHost.EvaluateWatchdog(idleSeconds: 5, totalSeconds: 600, timeoutSeconds: 600);
        Assert.False(kill);
    }

    [Fact]
    public void 判定_静默达到超时_击杀挂死()
    {
        var (kill, why) = AgentCliProcessHost.EvaluateWatchdog(idleSeconds: 600, totalSeconds: 700, timeoutSeconds: 600);
        Assert.True(kill);
        Assert.Contains("静默超时", why);
    }

    [Fact]
    public void 判定_失控循环达硬上限_击杀()
    {
        var (kill, why) = AgentCliProcessHost.EvaluateWatchdog(idleSeconds: 1, totalSeconds: 3000, timeoutSeconds: 600);
        Assert.True(kill);
        Assert.Contains("硬上限", why);
    }

    [Fact]
    public void 判定_未静默未达硬上限_不杀()
    {
        var (kill, _) = AgentCliProcessHost.EvaluateWatchdog(idleSeconds: 179, totalSeconds: 179, timeoutSeconds: 180);
        Assert.False(kill);
    }

    [Fact]
    public void 判定_两条件同时满足_按硬上限报因()
    {
        // 挂死且超过硬上限：报更严重的硬上限
        var (kill, why) = AgentCliProcessHost.EvaluateWatchdog(idleSeconds: 900, totalSeconds: 900, timeoutSeconds: 180);
        Assert.True(kill);
        Assert.Contains("硬上限", why);
    }

    [Fact]
    public void 判定_静默临界值_刚好达到即杀()
    {
        var (kill, _) = AgentCliProcessHost.EvaluateWatchdog(idleSeconds: 180, totalSeconds: 200, timeoutSeconds: 180);
        Assert.True(kill);
    }
}
