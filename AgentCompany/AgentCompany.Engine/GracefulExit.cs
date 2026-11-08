namespace AgentCompany.Desktop.Services;

public enum GracefulExitDecision
{
    /// <summary>无在途任务/审批：直接退出。</summary>
    ExitDirectly,
    /// <summary>有在途：弹 ContentDialog 让用户二选一（终止并退出 / 继续跑）。</summary>
    PromptDialog,
}

/// <summary>
/// 优雅退出决策（轮 5）：关闭主窗口时是否有任务/审批在途——判定独立成纯函数供单元测试。
/// </summary>
public static class GracefulExit
{
    public static GracefulExitDecision Evaluate(bool taskInFlight, bool approvalPending) =>
        taskInFlight || approvalPending ? GracefulExitDecision.PromptDialog : GracefulExitDecision.ExitDirectly;
}
