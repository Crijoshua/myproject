using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Desktop.Pages;

/// <summary>
/// 审批中心（轮 3 激活）：待审批列表（命令/Agent/请求时间/倒计时 + 批准/拒绝）+ 审批历史（落库持久）。
/// 待审批集合归 ApprovalService 持有，本页只绑定与回决定。
/// </summary>
public partial class ApprovalsPage : UserControl, INavigationAware
{
    private readonly ApprovalService _approval;

    public ApprovalsPage()
    {
        InitializeComponent();
        _approval = App.Current.Approval;

        PendingList.ItemsSource = _approval.Pending;
        _approval.Pending.CollectionChanged += OnPendingChanged;
        UpdateEmptyState();

        RefreshHistory();
    }

    public void OnNavigatedTo() => RefreshHistory();

    private void OnPendingChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateEmptyState();

    private void UpdateEmptyState()
    {
        var has = _approval.Pending.Count > 0;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        PendingScroll.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnApproveClick(object sender, RoutedEventArgs e) =>
        Decide(sender, new ApprovalDecision(Approved: true, Decision: "approved"));

    private void OnDenyClick(object sender, RoutedEventArgs e) =>
        Decide(sender, new ApprovalDecision(Approved: false, Decision: "denied"));

    /// <summary>
    /// 轮 7 [全部拒绝]：安全兜底一键清空（fail-safe=拒绝方向）。刻意不提供"全部批准"——
    /// 批准必须逐条过目（多任务并行时尤其要确认来源任务再放行）。
    /// </summary>
    private void OnDenyAllClick(object sender, RoutedEventArgs e)
    {
        // Decide 会边遍历边从 Pending 移除：先拍快照
        foreach (var item in _approval.Pending.ToArray())
            _approval.Decide(item, new ApprovalDecision(Approved: false, Decision: "denied"));
    }

    private void Decide(object sender, ApprovalDecision decision)
    {
        if (sender is Button { Tag: PendingApproval item })
            _approval.Decide(item, decision);
    }

    private void RefreshHistory()
    {
        var records = App.Current.Db.GetApprovalHistory();
        HistoryList.ItemsSource = new ObservableCollection<ApprovalHistoryRow>(
            records.Select(r => new ApprovalHistoryRow(
                r.Agent, r.Command, r.Decision,
                r.RequestedAt.ToString("HH:mm:ss"), r.DecidedAt.ToString("HH:mm:ss"))));
    }
}

/// <summary>历史行（仅供显示）。</summary>
public sealed record ApprovalHistoryRow(
    string Agent, string Command, string Decision, string RequestedAtText, string DecidedAtText);
