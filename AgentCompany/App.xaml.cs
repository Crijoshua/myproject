using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using AgentCompany.Core;
using AgentCompany.Desktop.Services;
using ModernWpf;

namespace AgentCompany.Desktop;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    /// <summary>单实例互斥体名（轮 5：二次启动只激活已有窗口，防抢管道/互相干扰清扫）。</summary>
    private const string InstanceMutexName = "AgentCompany.SingleInstance.Mutex";

    private Mutex? _instanceMutex;

    public Database Db { get; private set; } = null!;

    public EventBus Bus { get; private set; } = null!;

    /// <summary>events 黑匣子（轮 5：任务/审批/清扫/退出/中断全落库，诊断日志可筛选）。</summary>
    public EventRecorder Recorder { get; private set; } = null!;

    /// <summary>审批门（轮 3：命名管道服务端 + 待审批队列 + 落库）。</summary>
    public ApprovalService Approval { get; private set; } = null!;

    /// <summary>预算熔断（轮 6：token/墙钟/重跑三上限 + budget_ledger 台账）。</summary>
    public BudgetService Budget { get; private set; } = null!;

    /// <summary>CEO 规划器（轮 6：claude 换装规划模式拆 DAG）。</summary>
    public CeoPlanner Planner { get; private set; } = null!;

    /// <summary>claude CLI 进程宿主（轮 2 起；监视页直发任务用）。</summary>
    public AgentCliProcessHost Host { get; private set; } = null!;

    /// <summary>A2A 网关（轮 4：串行流水线 claude → opencode，含流水线专属双宿主）。</summary>
    public A2aGateway Gateway { get; private set; } = null!;

    /// <summary>启动自检（轮 8：三员工 CLI / McpBridge / 工作区 / SQLite，结果进设置页）。</summary>
    public SelfCheckService SelfCheck { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 全局异常兜底：任何线程的未处理异常都落 diag.log，杜绝"无声退出"。
        DispatcherUnhandledException += (_, ex) =>
        {
            Fatal("UI 线程未处理异常", ex.Exception);
            ex.Handled = true; // 记录后保活，不让单个异常带走整个指挥中心
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Fatal("AppDomain 未处理异常", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
            Fatal("未观察任务异常", ex.Exception);

        // 单实例（轮 5）：Mutex 抢不到说明已有实例——把它的窗口带到前台后自行退出
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            NativeWin.ActivateOtherInstance();
            Shutdown();
            return;
        }

        // v1.1 M6（6.3#13 反向互斥）：终端版 agentcompany 在跑时拒绝启动 GUI——
        // 与 CLI 侧 PipeProbe 同一判定（管道被占 = 另一宿主在位；命名管道不随进程残留，无误报面）。
        if (PipeProbe.IsOccupied(ApprovalPipe.DefaultPipeName, 500))
        {
            MessageBox.Show(
                "终端版（agentcompany）正在运行，请先关闭后重试。",
                "启动冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        // 主题色锁定 #0078D7（Win10 默认强调色）。
        ThemeManager.Current.AccentColor = Color.FromRgb(0x00, 0x78, 0xD7);

        Db = new Database();
        Db.EnsureCreated();

        // 轮 8 可移植：路径解析全部走"设置项 + 默认"——每次实时读库，设置页改完即生效
        WorkspaceLayout.Init(() => Db.GetSetting(WorkspaceLayout.SettingKey));
        CliPaths.Init(agent => Db.GetSetting(agent + "_path"));

        Bus = new EventBus();
        Recorder = new EventRecorder(Db, Bus);
        Bus.Publish(new AppEvent(DateTime.Now, EventCategories.System, $"状态库 OK：{Db.StatusText()}"));

        // 启动清扫（轮 5，双保险兜底）：须在任何宿主启动之前——
        // 杀 journal 未销账的残党进程树、working → interrupted、清临时文件，全部记黑匣子
        var sweep = new StartupSweep(Db, new TaskStore(Db), Recorder);
        sweep.Run();

        Approval = new ApprovalService(Db, Bus, Recorder);
        // 轮 6：预算服务（须在 UI 线程构造：DispatcherTimer 心跳）+ CEO 规划器
        Budget = new BudgetService(Db, Recorder, Approval);
        Planner = new CeoPlanner(Db, Bus);
        // 监视页直发的 claude 宿主（轮 2 行为不变）；轮 7 起流水线宿主改为每步一次性实例
        // （多任务并行不再共享双宿主），由 A2aGateway 内部创建与释放。
        Host = new AgentCliProcessHost(Db, Bus, Approval, EmployeeRegistry.Create("claude"));
        Gateway = new A2aGateway(Db, Bus, Recorder, Approval, Budget, Planner);

        // 启动即恢复上次的主题选择（settings 表 key=theme）。
        ThemeController.Apply(Db.GetSetting(ThemeController.SettingKey) ?? ThemeController.FollowSystem);

        // 轮 8：启动自检（三 CLI / Bridge / 工作区 / SQLite）+ 派活闸门接线 + 黑匣子留痕。
        var check = SelfCheck.Run(Db);
        AgentCliProcessHost.AvailabilityGate = agent => SelfCheck.IsAgentAvailable(agent);
        Recorder.Record(EventTypes.SelfCheck, "启动自检",
            $"共 {check.Count} 项，通过 {check.Count(c => c.Ok)}：{string.Join("；", check.Select(c => $"{c.Title}={(c.Ok ? "OK" : "✗ " + c.Detail)}"))}");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 自检异常项每次启动弹一次提示（不逐项弹、会话内不再打扰）
        var bad = check.Where(c => !c.Ok).ToList();
        if (bad.Count > 0)
        {
            MessageBox.Show(
                "启动自检发现以下问题：\n\n" + string.Join("\n", bad.Select(c => $"· {c.Title}：{c.Detail}")) +
                "\n\n对应员工将被禁止派活。可到 设置 → 可移植与自检 修正后[重新自检]。",
                "启动自检", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 审批管道监听须在 UI 就绪后启动（弹窗走 UI 线程）
        Approval.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 窗口关闭时若仍有任务在跑，杀掉整棵进程树，不留孤儿 node/claude/opencode
        Gateway?.Dispose();
        Host?.Dispose();
        // 未决审批一律按超时回绝（fail-safe = 拒绝），再关管道
        Approval?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch { /* 非属主（二次实例）：无事 */ }
        base.OnExit(e);
    }

    /// <summary>致命异常落盘：%APPDATA%\AgentCompany\diag.log。</summary>
    private static void Fatal(string kind, Exception? ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AgentCompany", "diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [{kind}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响流程
        }
    }
}
