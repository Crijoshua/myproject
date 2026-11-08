using AgentCompany.Core;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// 轮 7 审批队列：多任务并行时逐条路由（来源任务标题经管道带回）与独立倒计时状态机。
/// 倒计时用 PendingApproval 的纯函数直测（ComputeRemaining/TimeoutDue），不经 1s 计时器。
/// </summary>
public class ApprovalQueueTests
{
    private static PendingApproval Make(string? taskTitle = null, int ageSeconds = 0) => new()
    {
        Id = "id-" + Guid.NewGuid().ToString("N")[..8],
        Agent = "claude",
        Tool = "terminal",
        Command = "npm install lodash",
        RequestedAt = DateTime.Now.AddSeconds(-ageSeconds),
        TaskTitle = taskTitle,
    };

    [Fact]
    public void 倒计时状态机_剩余秒递减夹零_到点翻转()
    {
        var item = Make(taskTitle: "官网改版");
        var t0 = item.RequestedAt;

        // 刚到达：剩余 ≈ 等待上限（允许取整误差 ±2s）
        Assert.InRange(item.ComputeRemaining(t0.AddSeconds(1)), ApprovalService.WaitSeconds - 3, ApprovalService.WaitSeconds);
        Assert.False(item.TimeoutDue(t0.AddSeconds(1)));

        // 逐秒递减
        var mid = item.ComputeRemaining(t0.AddSeconds(60));
        var later = item.ComputeRemaining(t0.AddSeconds(70));
        Assert.InRange(mid - later, 9, 11);

        // 到点：夹 0 且 TimeoutDue 翻转（超时按拒绝处理）
        var after = t0.AddSeconds(ApprovalService.WaitSeconds + 30);
        Assert.Equal(0, item.ComputeRemaining(after));
        Assert.True(item.TimeoutDue(after));
    }

    [Fact]
    public void 来源行_多任务带标题_直发无标题()
    {
        var fromTask = Make(taskTitle: "官网改版");
        var direct = Make(taskTitle: null);

        Assert.Contains("官网改版", fromTask.SourceText);          // 审批中心按它区分来源任务
        Assert.StartsWith("来源任务：", fromTask.SourceText);
        Assert.StartsWith("来源：监视页直发", direct.SourceText);   // 无标题 = 监视页直发
        Assert.Equal("剩余", direct.RemainingText[..2]);            // 剩余文案形态稳定（绑定显示）
    }

    [Fact]
    public void 管道编解码_带任务标题往返_null不写字段()
    {
        var req = new ApprovalRequest("id-9", "opencode", "terminal", "git push origin main", "营销页重构");
        var encoded = ApprovalPipe.Encode(req);

        Assert.Contains("\"task_title\":\"营销页重构\"", encoded);  // 中文标题原样可读
        var decoded = ApprovalPipe.DecodeRequest(encoded);
        Assert.Equal("营销页重构", decoded.TaskTitle);

        // 无标题（监视页直发）：字段省略，不是 null 字面量
        var legacy = new ApprovalRequest("id-10", "claude", "terminal", "dir");
        var legacyEncoded = ApprovalPipe.Encode(legacy);
        Assert.DoesNotContain("task_title", legacyEncoded);
        Assert.Null(ApprovalPipe.DecodeRequest(legacyEncoded).TaskTitle);
    }

    [Fact]
    public void 管道解码_旧版JSON没有task_title_兼容为null()
    {
        // 轮 6 及以前的 Bridge 不发 task_title：宿主解码必须容忍缺字段
        var line = "{\"id\":\"abc\",\"agent\":\"opencode\",\"tool\":\"terminal\",\"command\":\"npm i\"}";
        var decoded = ApprovalPipe.DecodeRequest(line);
        Assert.Equal("abc", decoded.Id);
        Assert.Equal("opencode", decoded.Agent);
        Assert.Equal("npm i", decoded.Command);
        Assert.Null(decoded.TaskTitle);
    }

    [Fact]
    public void 多条待审批_各自独立倒计时互不干扰()
    {
        var a = Make(taskTitle: "任务甲", ageSeconds: 100); // 已等 100s
        var b = Make(taskTitle: "任务乙", ageSeconds: 10);  // 刚到 10s
        var now = DateTime.Now;

        var ra = a.ComputeRemaining(now);
        var rb = b.ComputeRemaining(now);

        Assert.True(ra < rb, $"甲({ra}s) 应比 乙({rb}s) 剩得少——各自按 RequestedAt 独立计时");
        Assert.False(a.TimeoutDue(now));
        Assert.False(b.TimeoutDue(now));

        // 甲先到点（超时=拒绝），乙仍在等：批准/拒绝逐条决定，互不串线
        Assert.True(a.TimeoutDue(now.AddSeconds(20)));
        Assert.False(b.TimeoutDue(now.AddSeconds(20)));
    }
}
