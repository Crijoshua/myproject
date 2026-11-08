namespace AgentCompany.Desktop.Services;

/// <summary>
/// events 黑匣子记录器（轮 5）：task 状态变更、审批决定、启动清扫、优雅退出、崩溃中断
/// 统一经这里落库（events 表）并转发到事件总线（诊断日志区按类型筛选可见）——
/// 排查"昨晚发生了什么"以这张表为准。
/// </summary>
public sealed class EventRecorder
{
    private readonly Database _db;
    private readonly IEventBus _bus;

    public EventRecorder(Database db, IEventBus bus)
    {
        _db = db;
        _bus = bus;
    }

    /// <summary>记一条（type 见 <see cref="EventTypes"/>）；落库失败不抛（黑匣子不拖垮业务）。</summary>
    public void Record(string type, string subject, string detail)
    {
        try
        {
            _db.InsertEvent(type, subject, detail);
        }
        catch
        {
            // 黑匣子写失败不影响主流程
        }
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Blackbox,
            $"[{EventTypes.Label(type)}] {subject}：{detail}", EventType: type));
    }

    /// <summary>最近事件（最新在前），诊断日志区回填用。</summary>
    public IReadOnlyList<EventRow> Recent(int limit = 200) => _db.LoadEvents(limit);
}
