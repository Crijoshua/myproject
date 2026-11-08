namespace AgentCompany.Desktop.Services;

/// <summary>
/// 多任务并发闸门（轮 7）：同时最多 maxParallel 条任务在跑，超出 FIFO 排队。
/// 纯状态机（不碰进程/DB），网关在 _gate 锁内驱动它，单元测试直测。
/// 设置页可改上限 1–4（A2aGateway.MaxParallelSettingKey），默认 2。
/// </summary>
public sealed class ParallelTaskGate
{
    private readonly Queue<int> _waiting = new();
    private readonly HashSet<int> _running = new();
    private int _max;

    public ParallelTaskGate(int maxParallel) => MaxParallel = maxParallel;

    /// <summary>并发上限（钳制 1–4，可运行中调整：下调不杀在跑任务，只挡新启动）。</summary>
    public int MaxParallel
    {
        get => _max;
        set => _max = Math.Clamp(value, 1, 4);
    }

    /// <summary>尝试启动前调用方须持有的一致性视图：在跑 × 排队 不变式由网关的 _gate 锁保证。</summary>
    public int RunningCount
    {
        get { lock (this) return _running.Count; }
    }

    /// <summary>在跑任务 id 快照。</summary>
    public IReadOnlyList<int> RunningIds
    {
        get { lock (this) return _running.ToList(); }
    }

    /// <summary>排队任务 id 快照（FIFO 序）。</summary>
    public IReadOnlyList<int> QueuedIds
    {
        get { lock (this) return _waiting.ToList(); }
    }

    /// <summary>是否在排队。</summary>
    public bool IsQueued(int taskId)
    {
        lock (this) return _waiting.Contains(taskId);
    }

    /// <summary>
    /// 尝试启动：有空位立即占位返回 true（调用方随即开跑）；满员则入队返回 false
    /// （调用方把任务标"排队中"）。已在跑的任务幂等返回 true。
    /// </summary>
    public bool TryAcquire(int taskId)
    {
        lock (this)
        {
            if (_running.Contains(taskId))
                return true;
            if (_running.Count >= _max)
            {
                if (!_waiting.Contains(taskId))
                    _waiting.Enqueue(taskId);
                return false;
            }
            _running.Add(taskId);
            return true;
        }
    }

    /// <summary>
    /// 任务离开闸门：排队中被移除（取消/拒绝）→ 只出队，不腾位；
    /// 在跑终态 → 占位直接转移给队首排队任务（它随即视为在跑，不重走 TryAcquire——
    /// 否则转移前的窗口期会被并发新任务多占一位），并返回它的 id（无排队则 null）。
    /// </summary>
    public int? Release(int taskId)
    {
        lock (this)
        {
            if (_waiting.Contains(taskId))
            {
                RemoveFromQueue(taskId);
                return null;
            }
            if (!_running.Remove(taskId))
                return null;
            if (_waiting.Count == 0)
                return null;
            var next = _waiting.Dequeue();
            _running.Add(next);
            return next;
        }
    }

    private void RemoveFromQueue(int taskId)
    {
        var rest = new Queue<int>();
        while (_waiting.Count > 0)
        {
            var id = _waiting.Dequeue();
            if (id != taskId)
                rest.Enqueue(id);
        }
        while (rest.Count > 0)
            _waiting.Enqueue(rest.Dequeue());
    }
}
