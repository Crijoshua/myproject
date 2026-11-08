using System.Windows.Threading;

namespace AgentCompany.Desktop.Services;

/// <summary>
/// 进程内事件总线：任意线程 Publish，订阅方固定在 UI 线程收到回调。
/// </summary>
public interface IEventBus
{
    /// <summary>发布事件（线程安全；回调统一派发回 UI 线程）。</summary>
    void Publish(AppEvent evt);

    /// <summary>UI 线程上触发。</summary>
    event Action<AppEvent>? EventReceived;

    /// <summary>历史事件快照（最多 200 条，按时间正序）。</summary>
    IReadOnlyList<AppEvent> Snapshot();
}

/// <summary>默认实现：内部环形保留最近 200 条，事件经 Dispatcher 派发回 UI 线程。</summary>
public sealed class EventBus : IEventBus
{
    private const int Capacity = 200;

    private readonly object _gate = new();
    private readonly Queue<AppEvent> _history = new();
    private readonly Dispatcher _ui;
    private event Action<AppEvent>? _received;

    /// <summary>须在 UI 线程上构造。</summary>
    public EventBus()
    {
        _ui = Dispatcher.CurrentDispatcher;
    }

    public event Action<AppEvent>? EventReceived
    {
        add { lock (_gate) _received += value; }
        remove { lock (_gate) _received -= value; }
    }

    public void Publish(AppEvent evt)
    {
        Action<AppEvent>? handlers;
        lock (_gate)
        {
            _history.Enqueue(evt);
            while (_history.Count > Capacity)
                _history.Dequeue();

            handlers = _received;
        }

        if (handlers is null)
            return;

        if (_ui.CheckAccess())
            handlers(evt);
        else
            _ui.BeginInvoke(handlers, evt);
    }

    public IReadOnlyList<AppEvent> Snapshot()
    {
        lock (_gate)
            return _history.ToArray();
    }
}
