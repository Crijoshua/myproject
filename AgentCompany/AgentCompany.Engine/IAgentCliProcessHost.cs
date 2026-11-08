namespace AgentCompany.Desktop.Services;

/// <summary>
/// 员工 CLI 子进程宿主。
/// 轮 2：claude（cmd.exe /c claude -p --output-format stream-json --verbose，任务文本走 stdin UTF-8 管道）。
/// 后续轮次：opencode / crush 复用同一抽象。
/// </summary>
public interface IAgentCliProcessHost
{
    /// <summary>当前状态。</summary>
    AgentRunState State { get; }

    /// <summary>是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>启动一次任务（参数审计命中黑名单时拒绝启动并记诊断日志）。</summary>
    void Start(string taskText);

    /// <summary>手动终止当前任务（杀整棵进程树）。</summary>
    void Cancel();
}

/// <summary>宿主运行状态。</summary>
public enum AgentRunState
{
    Idle,
    Running,
    Finished,
    Cancelled,
    Timeout,
    Error,
}
