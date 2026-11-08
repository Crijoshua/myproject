namespace AgentCompany.Desktop.Services;

/// <summary>
/// 宿主对审批门的按任务视图（v1.1 B5.1：轮 7 已知问题 4——看门狗/墙钟暂停从
/// 「任一待审批全局暂停」改为按任务粒度：任务 A 等审批只停 A 的表，B 照计）。
/// 审批请求经 Bridge 只回传来源任务标题（AGENTCOMPANY_TASK_TITLE 环境变量，
/// Bridge 侧不动），故按标题精确匹配；不实现本接口的审批门（旧测试桩等）
/// 由调用方回退 v1.0 全局口径（偏松不误杀）。
/// </summary>
public interface ITaskApprovalGate
{
    /// <summary>某来源任务标题是否存在待审批命令（无匹配 = 该任务不停表）。</summary>
    bool HasPendingFor(string taskTitle);
}
