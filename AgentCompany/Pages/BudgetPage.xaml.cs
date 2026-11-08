using System.Collections.ObjectModel;
using System.Windows.Controls;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Desktop.Pages;

/// <summary>一任务的三上限与已用（轮 6 预算总览第一段）。</summary>
public sealed class BudgetTaskRow
{
    // 必须是属性：{Binding} 不认公共字段（首跑 ⑤c 实踩——字段绑定静默失败、整页空行）
    public string Head { get; set; } = "";
    public string TokenText { get; set; } = "";
    public string WallText { get; set; } = "";
    public string RerunText { get; set; } = "";
    public override string ToString() => Head;
}

/// <summary>并行中任务的实时预算行（轮 7 多任务视图）。</summary>
public sealed class BudgetLiveRow
{
    public string Head { get; set; } = "";
    public string TokenText { get; set; } = "";
    public string WallText { get; set; } = "";
    public override string ToString() => Head;
}

/// <summary>任务 × 员工（含 CEO）token 矩阵行（第二段；轮 7 加 crush 列）。</summary>
public sealed class BudgetMatrixRow
{
    public string Head { get; set; } = "";
    public string ClaudeText { get; set; } = "—";
    public string OpencodeText { get; set; } = "—";
    public string CeoText { get; set; } = "—";
    public string CrushText { get; set; } = "—";
    public string TotalText { get; set; } = "0";
    public override string ToString() => Head;
}

/// <summary>一条熔断历史行（第三段）。</summary>
public sealed class BudgetFuseRow
{
    public string Line { get; set; } = "";
    public override string ToString() => Line;
}

/// <summary>
/// 预算总览（轮 6 实装）：各任务三上限与剩余、任务×员工（含 CEO）token 矩阵、熔断历史。
/// 数据全来自 budget_ledger / tasks / events(type=budget)；页面每次导航新建，构造时加载 +
/// 总线上出现预算/任务事件时刷新（执行中的任务数字会动）。
/// </summary>
public partial class BudgetPage : UserControl
{
    private readonly ObservableCollection<BudgetLiveRow> _live = new();
    private readonly ObservableCollection<BudgetTaskRow> _tasks = new();
    private readonly ObservableCollection<BudgetMatrixRow> _matrix = new();
    private readonly ObservableCollection<BudgetFuseRow> _fuses = new();
    private readonly System.Windows.Threading.DispatcherTimer _liveTimer;

    public BudgetPage()
    {
        InitializeComponent();
        LiveList.ItemsSource = _live;
        TaskBudgetList.ItemsSource = _tasks;
        MatrixList.ItemsSource = _matrix;
        FuseList.ItemsSource = _fuses;
        Reload();

        App.Current.Bus.EventReceived += OnBusEvent;

        // 轮 7：并行任务的墙钟每秒在走——页面打开期间 1s 拉一次实时快照（预算事件不触发的间隙也有数字）
        _liveTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _liveTimer.Tick += (_, _) => ReloadLive();
        _liveTimer.Start();

        Unloaded += (_, _) => { App.Current.Bus.EventReceived -= OnBusEvent; _liveTimer.Stop(); };
    }

    private void OnBusEvent(AppEvent e)
    {
        if (e.EventType == EventTypes.Budget || e.Category is EventCategories.Task or EventCategories.Blackbox)
            Reload();
    }

    /// <summary>轮 7：并行任务实时预算——一行一条在跑任务 + 汇总行（token/墙钟独立记账；crush 只推墙钟）。</summary>
    private void ReloadLive()
    {
        var app = App.Current;
        var snapshot = app.Budget.ActiveSnapshot();
        var titles = app.Gateway.Store.LoadTasks(200).ToDictionary(t => t.Id, t => t.Title);

        _live.Clear();
        foreach (var s in snapshot.OrderBy(s => s.TaskId))
        {
            _live.Add(new BudgetLiveRow
            {
                Head = titles.TryGetValue(s.TaskId, out var title) ? $"#{s.TaskId} {title}" : $"#{s.TaskId}",
                TokenText = $"token {s.UsedTokens} / {s.TokenCap}",
                WallText = $"墙钟 {s.UsedWallSeconds}s / {s.WallCapSeconds}s",
            });
        }
        LiveHint.Visibility = _live.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        if (_live.Count == 0)
        {
            LiveSummary.Text = "";
            return;
        }
        var tokUsed = snapshot.Sum(s => s.UsedTokens);
        var tokCap = snapshot.Sum(s => s.TokenCap);
        var wallUsed = snapshot.Sum(s => s.UsedWallSeconds);
        LiveSummary.Text =
            $"合计（{snapshot.Count} 条并行）：token {tokUsed} / {tokCap} · 墙钟累计 {wallUsed}s（各任务独立结算，互不串账）";
    }

    private void Reload()
    {
        ReloadLive();

        var app = App.Current;
        var tasks = app.Gateway.Store.LoadTasks(200);
        var ledger = app.Budget.LoadLedger(1000);

        _tasks.Clear();
        foreach (var t in tasks)
        {
            var tokenCap = t.BudgetTokens is > 0 ? t.BudgetTokens.Value : BudgetService.DefaultTokenCap;
            var wallCap = t.BudgetWallSeconds is > 0 ? t.BudgetWallSeconds.Value : BudgetService.DefaultWallSeconds;
            var rerunCap = t.BudgetReruns is > 0 ? t.BudgetReruns.Value : BudgetService.DefaultRerunCap;
            var used = ledger.Where(l => l.TaskId == t.Id).Sum(l => l.Tokens);
            var left = Math.Max(0, tokenCap - used);
            _tasks.Add(new BudgetTaskRow
            {
                Head = $"#{t.Id} {t.Title}",
                TokenText = $"token {used} / {tokenCap}（剩 {left}）"
                    + (used >= tokenCap ? " ⛔已熔断" : ""),
                WallText = $"墙钟上限 {wallCap / 60} 分钟",
                RerunText = $"重跑 {t.RerunsUsed} / {rerunCap}",
            });
        }
        TaskBudgetHint.Visibility = _tasks.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        _matrix.Clear();
        foreach (var t in tasks)
        {
            var rows = ledger.Where(l => l.TaskId == t.Id).ToList();
            if (rows.Count == 0)
                continue;
            long Claude() => rows.Where(l => l.Agent == "claude" && l.Role != "ceo").Sum(l => l.Tokens);
            long Opencode() => rows.Where(l => l.Agent == "opencode").Sum(l => l.Tokens);
            var ceoCalls = rows.Count(l => l.Role == "ceo");
            var ceoTokens = rows.Where(l => l.Role == "ceo").Sum(l => l.Tokens);
            // crush：token 恒 0（台账 0 行也入账），墙钟在多任务实时视图里计
            var crushCalls = rows.Count(l => l.Agent == "crush");
            _matrix.Add(new BudgetMatrixRow
            {
                Head = $"#{t.Id} {t.Title}",
                ClaudeText = $"{Claude()}",
                OpencodeText = $"{Opencode()}",
                CeoText = $"{ceoCalls} 次 / {ceoTokens}",
                CrushText = crushCalls == 0 ? "—" : $"{crushCalls} 次 / token 0（墙钟计量）",
                TotalText = $"{rows.Sum(l => l.Tokens)}",
            });
        }
        MatrixHint.Visibility = _matrix.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        _fuses.Clear();
        foreach (var ev in app.Db.LoadEvents(500).Where(ev => ev.Type == EventTypes.Budget))
            _fuses.Add(new BudgetFuseRow { Line = $"{ev.Ts:yyyy-MM-dd HH:mm:ss} {ev.Subject}：{ev.Detail}" });
        FuseHint.Visibility = _fuses.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }
}
