using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>轮 5：优雅退出决策——有任务/审批在途才弹框，否则直接退出。</summary>
public class GracefulExitTests
{
    [Fact]
    public void No_InFlight_Exits_Directly() =>
        Assert.Equal(GracefulExitDecision.ExitDirectly, GracefulExit.Evaluate(taskInFlight: false, approvalPending: false));

    [Fact]
    public void Task_InFlight_Prompts() =>
        Assert.Equal(GracefulExitDecision.PromptDialog, GracefulExit.Evaluate(taskInFlight: true, approvalPending: false));

    [Fact]
    public void Approval_Pending_Prompts() =>
        Assert.Equal(GracefulExitDecision.PromptDialog, GracefulExit.Evaluate(taskInFlight: false, approvalPending: true));

    [Fact]
    public void Both_InFlight_Prompts() =>
        Assert.Equal(GracefulExitDecision.PromptDialog, GracefulExit.Evaluate(taskInFlight: true, approvalPending: true));
}
