namespace AgentCompany.Desktop.Services;

/// <summary>进程内事件（导航、系统、员工事件流、诊断、任务生命周期等）。</summary>
public sealed record AppEvent(DateTime Timestamp, string Category, string Message, bool IsDim = false, string Agent = "", string EventType = "")
{
    /// <summary>事件流里展示的一行文本。</summary>
    public string Display => $"[{Timestamp:HH:mm:ss}] {Message}";

    /// <summary>折叠/弱化行的透明度（XAML 绑定用）。</summary>
    public double DimOpacity => IsDim ? 0.45 : 1.0;
}

/// <summary>事件类别约定。</summary>
public static class EventCategories
{
    public const string System = "系统";
    public const string Navigation = "导航";
    /// <summary>claude 事件流行（上屏）。</summary>
    public const string Agent = "claude";
    /// <summary>claude 诊断行（非 JSON / 未知 type / stderr）。</summary>
    public const string AgentDiag = "claude-diag";
    /// <summary>claude 宿主状态变化（空闲/运行中/完成/已终止/超时/错误）。</summary>
    public const string AgentStatus = "claude-status";
    /// <summary>opencode 事件流行（轮 4）。</summary>
    public const string Opencode = "opencode";
    /// <summary>opencode 诊断行（轮 4）。</summary>
    public const string OpencodeDiag = "opencode-diag";
    /// <summary>opencode 宿主状态变化（轮 4）。</summary>
    public const string OpencodeStatus = "opencode-status";
    /// <summary>crush 事件流行（轮 7：仅评审报告全文/代写通知，运行期无事件流）。</summary>
    public const string Crush = "crush";
    /// <summary>crush 诊断行（轮 7）。</summary>
    public const string CrushDiag = "crush-diag";
    /// <summary>crush 宿主状态变化（轮 7）。</summary>
    public const string CrushStatus = "crush-status";
    /// <summary>审批门事件（轮 3：请求/批准/拒绝/超时）。</summary>
    public const string Approval = "审批";
    /// <summary>流水线任务生命周期事件（轮 4：创建/步骤流转/终态）。</summary>
    public const string Task = "任务";
    /// <summary>events 黑匣子事件（轮 5：状态变更/审批/清扫/退出/中断，落库 + 诊断日志）。</summary>
    public const string Blackbox = "黑匣子";

    /// <summary>按员工 id 取事件流类别（事件/诊断/状态三连）。</summary>
    public static (string Event, string Diag, string Status) ForAgent(string agent) =>
        agent switch
        {
            "opencode" => (Opencode, OpencodeDiag, OpencodeStatus),
            "crush" => (Crush, CrushDiag, CrushStatus),
            _ => (Agent, AgentDiag, AgentStatus),
        };
}

/// <summary>events 黑匣子表的类型枚举（轮 5；诊断日志按它筛选）。</summary>
public static class EventTypes
{
    /// <summary>任务状态变更（创建/启动/步骤流转/终态/重跑）。</summary>
    public const string Task = "task";
    /// <summary>审批决定（批准/拒绝/超时）。</summary>
    public const string Approval = "approval";
    /// <summary>启动清扫动作（杀残留进程/清临时文件）。</summary>
    public const string Sweep = "sweep";
    /// <summary>宿主退出（直接退出/终止并退出/继续跑）。</summary>
    public const string Exit = "exit";
    /// <summary>崩溃中断（working 任务被清扫改标 interrupted）。</summary>
    public const string Interrupt = "interrupt";
    /// <summary>预算事件（轮 6：token/墙钟/重跑熔断记账，预算总览页的熔断历史）。</summary>
    public const string Budget = "budget";
    /// <summary>启动自检（轮 8：三 CLI / Bridge / 工作区 / SQLite 结果）。</summary>
    public const string SelfCheck = "selfcheck";

    /// <summary>类型 → 筛选下拉显示名。</summary>
    public static string Label(string type) => type switch
    {
        Task => "任务",
        Approval => "审批",
        Sweep => "清扫",
        Exit => "退出",
        Interrupt => "中断",
        Budget => "预算",
        SelfCheck => "自检",
        _ => type,
    };
}
