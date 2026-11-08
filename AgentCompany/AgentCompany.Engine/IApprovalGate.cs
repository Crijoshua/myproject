namespace AgentCompany.Desktop.Services;

/// <summary>宿主对审批门的只读视图（AgentCliProcessHost 暂停看门狗、监视页状态条用）。</summary>
public interface IApprovalGate
{
    /// <summary>当前待审批数（>0 时任务看门狗暂停计时）。</summary>
    int PendingCount { get; }

    /// <summary>最早一条待审批的命令（无则 null，监视页状态条显示用）。</summary>
    string? WaitingOnCommand { get; }

    /// <summary>待审批剩余秒数（最早一条；无待审批时 0）。</summary>
    int WaitingRemainingSeconds { get; }
}
