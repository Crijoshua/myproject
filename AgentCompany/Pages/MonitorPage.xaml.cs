using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Desktop.Pages;

/// <summary>
/// 运行监视：任务派发 + 实时事件流（100ms 节流批量上屏，环形 500 条）+ 完成摘要 + 诊断日志。
/// </summary>
public partial class MonitorPage : UserControl, INavigationAware
{
    private const int MaxEvents = 500;
    private const int MaxDiags = 200;

    private readonly ObservableCollection<AppEvent> _events = new();
    private readonly ObservableCollection<AppEvent> _diags = new();

    // 节流缓冲：EventBus 回调（UI 线程）只入队，DispatcherTimer 每 100ms 批量上屏
    private readonly List<AppEvent> _pendingEvents = new();
    private readonly List<AppEvent> _pendingDiags = new();
    private readonly DispatcherTimer _flushTimer;

    private readonly AgentCliProcessHost _host;
    private readonly IApprovalGate _approval;
    private readonly ICollectionView _eventView;
    private ICollectionView? _diagView;

    // 当前员工筛选（"" = 全部；claude / opencode 按 AppEvent.Agent 过滤）
    private string _agentFilter = "";

    // 轮 5：诊断日志黑匣子类型筛选（"" = 全部，含普通诊断行）
    private string _diagTypeFilter = "";

    // 轮 5：员工状态条——两名员工各自的最新动作行（事件流里署名该员工的行）
    private readonly Dictionary<string, string> _lastAgentAction = new();

    public MonitorPage()
    {
        InitializeComponent();
        _host = App.Current.Host;
        _approval = App.Current.Approval;

        EventList.ItemsSource = _events;
        DiagList.ItemsSource = _diags;
        _eventView = CollectionViewSource.GetDefaultView(_events);
        _eventView.Filter = PassesAgentFilter;
        _diagView = CollectionViewSource.GetDefaultView(_diags);
        _diagView.Filter = PassesDiagFilter;

        // 轮 5：黑匣子回填——events 表最近记录先进诊断日志（"昨晚发生了什么"重启可查）
        foreach (var row in App.Current.Recorder.Recent(120).Reverse())
            _diags.Add(ToDiagEvent(row));

        foreach (var e in App.Current.Bus.Snapshot())
            Route(e);
        App.Current.Bus.EventReceived += Route;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();

        // 轮 5：页面不缓存、每次导航新建——卸载时退订 + 停表，并把尾批事件刷上屏
        Unloaded += OnUnloaded;

        RefreshSessionCount();
        UpdateStateUI();
        UpdateAgentBars();
        // 轮 8：直发员工（claude）自检不可用的禁用与提示统一在 UpdateStateUI 收口（宿主层还有闸门兜底）
    }

    private static AppEvent ToDiagEvent(EventRow row) =>
        new(row.Ts, EventCategories.Blackbox,
            $"[{EventTypes.Label(row.Type)}] {row.Subject}：{row.Detail}", EventType: row.Type);

    /// <summary>诊断日志类型筛选下拉变化：重设视图过滤。</summary>
    private void OnDiagFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _diagTypeFilter = (DiagTypeFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        // InitializeComponent 期间默认选中即触发回调，此时 _diagView 尚未创建（OnAgentFilterChanged 同款守卫）
        if (_diagView is null) return;
        _diagView.Refresh();
    }

    private bool PassesDiagFilter(object item)
    {
        if (_diagTypeFilter.Length == 0)
            return true;
        return item is AppEvent e && e.EventType == _diagTypeFilter;
    }

    public void OnNavigatedTo()
    {
        RefreshSessionCount();
        UpdateStateUI();
    }

    private void OnDispatchClick(object sender, RoutedEventArgs e)
    {
        // 轮 8：自检闸门兜底（按钮已禁用；防御 UI 状态未刷新的路径）
        if (!App.Current.SelfCheck.IsAgentAvailable("claude"))
        {
            StatusDetail.Text = "claude 未安装（自检未通过）：派活已禁用——设置页检查路径覆盖后[重新自检]";
            return;
        }
        var task = TaskInput.Text.Trim();
        if (task.Length == 0)
            return;

        ReplyText.Text = "—";
        UsageText.Text = "—";
        SummaryPanel.Visibility = Visibility.Collapsed;
        _host.Start(task);
        UpdateStateUI();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _host.Cancel();
    }

    /// <summary>员工筛选下拉变化：重设视图过滤。</summary>
    private void OnAgentFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _agentFilter = (AgentFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        // InitializeComponent 期间 ComboBox 默认选中第一项即触发本回调，此时 _eventView 尚未创建；
        // 不防这一手会把构造函数炸掉（全局兜底吞异常后表现为“运行监视”页永远打不开）
        if (_eventView is null) return;
        _eventView.Refresh();
    }

    private bool PassesAgentFilter(object item) =>
        _agentFilter.Length == 0 || (item as AppEvent)?.Agent == _agentFilter;

    /// <summary>按类别路由事件（EventBus 已保证 UI 线程回调）。</summary>
    private void Route(AppEvent e)
    {
        switch (e.Category)
        {
            case EventCategories.AgentDiag:
            case EventCategories.OpencodeDiag:
                _pendingDiags.Add(e);
                break;

            case EventCategories.Blackbox:
                // 轮 5：黑匣子事件同时进诊断日志（可按类型筛选）
                _pendingDiags.Add(e);
                break;

            case EventCategories.AgentStatus:
                UpdateStateUI();
                UpdateAgentBars();
                break;

            case EventCategories.OpencodeStatus:
                // 流水线 opencode 宿主的状态：不改本页派活区（监视页只管直发的 claude），仅供组织页联动
                UpdateAgentBars();
                break;

            case EventCategories.CrushStatus:
                // 轮 7：crush 状态（无事件流，状态条只在起跑/收口时变化）
                UpdateAgentBars();
                break;

            case EventCategories.CrushDiag:
                _pendingDiags.Add(e);
                break;

            default:
                _pendingEvents.Add(e);
                // claude / opencode 两类宿主的归档事件都要刷新会话计数
                if (e.Category == EventCategories.System && e.Message.Contains("会话已归档"))
                    RefreshSessionCount();
                break;
        }

        // 轮 5：员工状态条滚动更新最新动作（事件流里署名该员工的行）
        if (e.Agent.Length > 0 && e.Category is EventCategories.Agent or EventCategories.Opencode)
            _lastAgentAction[e.Agent] = TruncateText(e.Message, 60);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Current.Bus.EventReceived -= Route;
        _flushTimer.Stop();
        Flush();
    }

    private void Flush()
    {
        if (_pendingEvents.Count > 0)
        {
            foreach (var e in _pendingEvents)
                InsertHead(_events, e, MaxEvents);
            _pendingEvents.Clear();
        }

        if (_pendingDiags.Count > 0)
        {
            foreach (var e in _pendingDiags)
                InsertHead(_diags, e, MaxDiags);
            _pendingDiags.Clear();
        }

        // 状态条：审批等待 > 思考中 > 运行中（审批期间看门狗暂停，这里持续倒计时）
        if (_host.State == AgentRunState.Running)
        {
            var waiting = _approval.WaitingOnCommand;
            if (waiting is not null)
            {
                StatusDetail.Text = $"⏳ 等待审批：{TruncateText(waiting, 50)}（剩余 {_approval.WaitingRemainingSeconds}s）"
                    + SessionSuffix();
            }
            else if (_host.ThinkingTokens > 0)
            {
                StatusDetail.Text = $"claude 运行中 · 💭 思考中（约 {_host.ThinkingTokens} tokens）"
                    + SessionSuffix();
            }
            else
            {
                StatusDetail.Text = "claude 运行中" + SessionSuffix();
            }
        }

        UpdateAgentBars();
    }

    /// <summary>
    /// 轮 5：员工状态条——claude / opencode 各一行（空闲 / 运行中·当前动作·已耗时）。
    /// 轮 7：加 crush 行——纯文本员工无事件流，运行中只显示占位与已耗时，完成后全文进事件流。
    /// 运行判定合并两台宿主：监视页直发 claude（App.Host）+ 流水线各任务的一次性宿主（Gateway）。
    /// </summary>
    private void UpdateAgentBars()
    {
        ClaudeBarText.Text = AgentBarText("claude");
        OpencodeBarText.Text = AgentBarText("opencode");
        CrushBarText.Text = AgentBarText("crush");
    }

    private string AgentBarText(string agent)
    {
        DateTime? since = null;
        if (agent == "claude" && App.Current.Host.IsRunning)
            since = App.Current.Host.StartedAt;
        var gatewaySince = App.Current.Gateway.AgentRunningSince(agent);
        if (gatewaySince is not null && (since is null || gatewaySince < since))
            since = gatewaySince;

        if (since is null)
        {
            _lastAgentAction.Remove(agent);
            return "空闲";
        }

        var elapsed = DateTime.Now - since.Value;
        var elapsedText = elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:D2}";
        if (agent == "crush")
            return $"运行中…（纯文本员工，无事件流）· 已耗时 {elapsedText}";
        return _lastAgentAction.TryGetValue(agent, out var action) && action.Length > 0
            ? $"运行中 · {action} · 已耗时 {elapsedText}"
            : $"运行中 · 已耗时 {elapsedText}";
    }

    private static void InsertHead(ObservableCollection<AppEvent> list, AppEvent e, int max)
    {
        list.Insert(0, e);
        while (list.Count > max)
            list.RemoveAt(list.Count - 1);
    }

    private string SessionSuffix()
    {
        if (_host.SessionId is null)
            return string.Empty;
        var shortId = _host.SessionId.Length <= 8 ? _host.SessionId : _host.SessionId[..8] + "…";
        return $" · session={shortId} · model={_host.Model}";
    }

    private static string TruncateText(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private void UpdateStateUI()
    {
        var s = _host.State;
        var label = s switch
        {
            AgentRunState.Running => "运行中",
            AgentRunState.Finished => "空闲",
            AgentRunState.Cancelled => "已终止",
            AgentRunState.Timeout => "超时",
            AgentRunState.Error => "错误",
            _ => "空闲",
        };
        StateBadgeText.Text = label;
        StateBadge.Background = new SolidColorBrush(s switch
        {
            AgentRunState.Running => Color.FromArgb(0x33, 0x00, 0x78, 0xD7),
            AgentRunState.Timeout => Color.FromArgb(0x33, 0xC4, 0x2B, 0x1C),
            AgentRunState.Error => Color.FromArgb(0x33, 0xC4, 0x2B, 0x1C),
            _ => Color.FromArgb(0x29, 0x60, 0x80, 0x60),
        });

        // 轮 8：派活可用 = 非运行中 且 claude 自检通过。统一在此收口——
        // ctor 设过的禁用会被本函数（OnNavigatedTo 也会调）覆盖，不能分散两处。
        var claudeUsable = App.Current.SelfCheck.IsAgentAvailable("claude");
        DispatchButton.IsEnabled = s != AgentRunState.Running && claudeUsable;
        CancelButton.IsEnabled = s == AgentRunState.Running;

        if (!claudeUsable)
        {
            StatusDetail.Text = "claude 未安装（自检未通过）：派活已禁用——设置页检查路径覆盖后[重新自检]";
        }
        else if (s is AgentRunState.Finished or AgentRunState.Cancelled
            or AgentRunState.Timeout or AgentRunState.Error)
        {
            var seconds = _host.LastDurationMs > 0 ? _host.LastDurationMs / 1000.0
                : (DateTime.Now - _host.StartedAt).TotalSeconds;
            UsageText.Text = $"输入 {_host.InputTokens} · 输出 {_host.OutputTokens} · 缓存读 {_host.CacheReadTokens} tokens"
                + $" · 用时 {seconds:0.0}s · 状态 {label}";
            ReplyText.Text = string.IsNullOrEmpty(_host.LastReply) ? "（无回复文本）" : _host.LastReply;
            SummaryPanel.Visibility = Visibility.Visible;
            StatusDetail.Text = $"claude {label} · 会话结束，共 {(int)Math.Max(1, seconds)}s"
                + SessionSuffix();
        }
    }

    private void RefreshSessionCount()
    {
        SessionCountText.Text = $"{App.Current.Db.CountAgentSessions()} 次";
    }
}
