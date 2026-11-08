using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AgentCompany.Desktop.Pages;
using AgentCompany.Desktop.Services;
using ModernWpf.Controls;

namespace AgentCompany.Desktop;

public partial class MainWindow : Window
{
    private string? _currentTag;
    private bool _forceClose;      // [终止并退出] 之后放行关闭
    private bool _exitPromptOpen;  // 同时只弹一个退出确认

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// 优雅退出（轮 5）：有任务/审批在途时点 X → ContentDialog 二选一——
    /// [终止并退出]（杀树、任务=cancelled、退出）/ [继续跑]（取消关闭）；无在途则直接退出。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        var gatewayBusy = App.Current.Gateway.HasInFlight || App.Current.Gateway.HasQueued
                          || App.Current.Gateway.IsPlanning; // 轮 7：排队任务也在途（不确认会丢队列）
        var monitorBusy = App.Current.Host.IsRunning;
        var approvalPending = App.Current.Approval.PendingCount > 0;

        var decision = GracefulExit.Evaluate(gatewayBusy || monitorBusy, approvalPending);
        if (decision == GracefulExitDecision.PromptDialog && !_forceClose)
        {
            e.Cancel = true;
            if (!_exitPromptOpen)
                _ = ConfirmExitAsync(gatewayBusy || monitorBusy, approvalPending);
            return;
        }

        App.Current.Recorder.Record(EventTypes.Exit, "宿主",
            (gatewayBusy || monitorBusy || approvalPending) ? "退出：终止在途任务/审批" : "退出：无在途任务，直接关闭");
        base.OnClosing(e);
    }

    private async Task ConfirmExitAsync(bool taskInFlight, bool approvalPending)
    {
        _exitPromptOpen = true;
        try
        {
            var busy = (taskInFlight ? "有流水线/任务在途" : "")
                     + (taskInFlight && approvalPending ? "、" : "")
                     + (approvalPending ? "有审批待决" : "");
            var dialog = new ContentDialog
            {
                Title = "有任务在途",
                Content = $"{busy}。\n\n[终止并退出]：杀掉子进程树，任务记为「已取消」后退出；\n[继续跑]：不退出，窗口保持运行。",
                PrimaryButtonText = "终止并退出",
                SecondaryButtonText = "继续跑",
                DefaultButton = ContentDialogButton.Secondary,
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                App.Current.Recorder.Record(EventTypes.Exit, "宿主", "继续跑：用户取消本次关闭，任务继续运行");
                return;
            }

            // 终止并退出：杀树 → 等终态（最多 10s）→ 仍在途的兜底标 cancelled → 关窗
            App.Current.Recorder.Record(EventTypes.Exit, "宿主", "终止并退出：杀进程树并把任务记为已取消");
            App.Current.Gateway.CancelAll();
            App.Current.Host.Cancel();
            for (var i = 0; i < 50 && App.Current.Gateway.HasInFlight; i++)
                await Task.Delay(200);
            foreach (var t in App.Current.Gateway.Store.LoadTasks())
            {
                if (t.Status is "working" or "pending" or "queued")
                    App.Current.Gateway.Store.SetTaskStatus(t.Id, "cancelled");
            }

            _forceClose = true;
            Close();
        }
        finally
        {
            _exitPromptOpen = false;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 默认落在第一项（组织）。
        if (Nav.SelectedItem is null && Nav.MenuItems.Count > 0)
            Nav.SelectedItem = (NavigationViewItem)Nav.MenuItems[0]!;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag || tag == _currentTag)
            return;

        _currentTag = tag;
        // 轮 5：不缓存页面实例——缓存页"卸下再挂回"后 WPF UIA 不再暴露其 ItemsControl 模板内容
        // （视觉正常，自动化树/屏幕阅读器全盲，验收 ③ 即栽在此）；每次新建实例走首挂路径，永远健康
        var page = CreatePage(tag);
        PageHost.Child = page;
        (page as INavigationAware)?.OnNavigatedTo();

        App.Current.Bus.Publish(new AppEvent(DateTime.Now, "导航", $"导航 → {item.Content}"));
    }

    private UserControl CreatePage(string tag) => tag switch
    {
        "board" => new TaskBoardPage(),
        "monitor" => new MonitorPage(),
        "approvals" => new ApprovalsPage(),
        "budget" => new BudgetPage(),
        "settings" => new SettingsPage(),
        _ => new OrganizationPage(),
    };
}
