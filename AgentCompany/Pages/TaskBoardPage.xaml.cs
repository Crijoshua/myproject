using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using AgentCompany.Core;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Desktop.Pages;

/// <summary>卡片上的一个步骤行（序号 + 员工 + 状态；当前步骤高亮；DAG 节点行带所有权/依赖）。</summary>
public sealed class StepVm : INotifyPropertyChanged
{
    private string _status = "pending";
    private string? _error;
    private bool _isCurrent;
    private bool _canRerunNode;

    public int Seq { get; init; }
    public string Agent { get; init; } = "";
    /// <summary>归属任务（节点级 [重跑] 回调用）。</summary>
    public int TaskId { get; init; }
    /// <summary>DAG 节点 id（n1/n2/…）；流水线步骤为 null。</summary>
    public string? NodeId { get; init; }
    /// <summary>本节点独占的写目标（相对路径）。</summary>
    public IReadOnlyList<string> Writes { get; init; } = Array.Empty<string>();
    /// <summary>前置节点 id。</summary>
    public IReadOnlyList<string> Deps { get; init; } = Array.Empty<string>();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsFailed)); OnPropertyChanged(nameof(IsDone)); }
    }

    public string? Error
    {
        get => _error;
        set { _error = value; OnPropertyChanged(nameof(Display)); }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set { _isCurrent = value; OnPropertyChanged(); }
    }

    /// <summary>轮 6：终态 DAG 任务卡上的节点可单独 [重跑]（弹窗可改写提示词）。</summary>
    public bool CanRerunNode
    {
        get => _canRerunNode;
        set { _canRerunNode = value; OnPropertyChanged(); }
    }

    public bool IsDone => Status == "done";
    public bool IsFailed => Status is "failed" or "cancelled" or "interrupted";

    /// <summary>
    /// 一行文本：● ① claude · 工作中（失败时附原因）。圆点=进度：●进行 ○待办 ✓完成 ✕失败 ⏸中断。
    /// DAG 节点行：● ① n1·claude · 等待中〔写 index.html、styles.css〕←无，预览时即可见所有权与依赖。
    /// </summary>
    public string Display =>
        $"{Dot(_status)} {Circle(Seq)} {Head()} · {StatusLabel()}{NodeInfo()}" +
        (string.IsNullOrEmpty(_error) ? "" : $"（{_error}）");

    private string Head() => NodeId is null ? Agent : $"{NodeId}·{Agent}";

    private string NodeInfo()
    {
        if (NodeId is null)
            return "";
        var writes = Writes.Count == 0 ? "" : $"〔写 {string.Join("、", Writes)}〕";
        var deps = Deps.Count == 0 ? "" : $" ←依赖 {string.Join("、", Deps)}";
        return $" {writes}{deps}";
    }

    private string StatusLabel() => _status switch
    {
        "running" => "工作中",
        "done" => "已完成",
        "failed" => "失败",
        "cancelled" => "已取消",
        "interrupted" => "已中断",
        _ => "等待中",
    };

    private static string Dot(string status) => status switch
    {
        "running" => "●",
        "done" => "✓",
        "failed" or "cancelled" => "✕",
        "interrupted" => "⏸",
        _ => "○",
    };

    private static string Circle(int n) => n switch
    {
        1 => "①",
        2 => "②",
        3 => "③",
        4 => "④",
        _ => $"{n}.",
    };

    public void Refresh() => OnPropertyChanged(nameof(Display));

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}

/// <summary>任务卡（三列看板共用；状态变化时整卡刷新绑定属性）。</summary>
public sealed class TaskCardVm : INotifyPropertyChanged
{
    private string _status = "pending";
    private string _kind = "pipeline";
    private string? _failReason;
    private int _currentStep;
    private IReadOnlyList<string> _artifacts = Array.Empty<string>();
    private DateTime? _interruptedAt;
    private string _liveActionText = "";
    private string _elapsedText = "";

    public int Id { get; init; }
    public string Title { get; init; } = "";
    public ObservableCollection<StepVm> Steps { get; } = new();

    /// <summary>UIA/调试可读名（否则 ItemsControl 条目名是类名 ToString）。</summary>
    public override string ToString() => $"#{Id} {Title}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRerun));
            OnPropertyChanged(nameof(CanRerunStep));
            OnPropertyChanged(nameof(CanConfirm));
            OnPropertyChanged(nameof(CanReplan));
            OnPropertyChanged(nameof(IsWorking));
        }
    }

    /// <summary>pipeline（手工流水线）/ dag（CEO 拆解的项目任务）。</summary>
    public string Kind
    {
        get => _kind;
        set { _kind = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRerunStep)); }
    }

    /// <summary>失败/拆解失败原因（轮 6 红标：预算超限等）。</summary>
    public string? FailReason
    {
        get => _failReason;
        set { _failReason = value; OnPropertyChanged(nameof(FailReasonText)); OnPropertyChanged(nameof(HasFailReason)); }
    }

    public int CurrentStep
    {
        get => _currentStep;
        set
        {
            _currentStep = value;
            foreach (var s in Steps)
                s.IsCurrent = s.Seq == value && _status == "working";
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> Artifacts
    {
        get => _artifacts;
        set { _artifacts = value; OnPropertyChanged(nameof(ArtifactsText)); OnPropertyChanged(nameof(CanOpenArtifacts)); }
    }

    /// <summary>中断时刻（轮 5：卡片显示"已中断（含时间）"）。</summary>
    public DateTime? InterruptedAt
    {
        get => _interruptedAt;
        set { _interruptedAt = value; OnPropertyChanged(nameof(StatusText)); }
    }

    /// <summary>实时动作行（轮 5：事件流最新一条，如 🔧 company.file_write → story.txt）。</summary>
    public string LiveActionText
    {
        get => _liveActionText;
        set { _liveActionText = value; OnPropertyChanged(); }
    }

    /// <summary>已耗时（轮 5：每秒由页面计时器刷新，仅运行中卡片可见）。</summary>
    public string ElapsedText
    {
        get => _elapsedText;
        set { _elapsedText = value; OnPropertyChanged(); }
    }

    public string StatusKind => _status;

    public string StatusText => _status switch
    {
        "working" => "进行中",
        "queued" => "排队中", // 轮 7：并发满员，FIFO 等空位
        "planning" => "CEO 拆解中",
        "preview" => "待确认",
        "parse_failed" => "拆解失败",
        "completed" => "已完成",
        "failed" => "失败",
        "cancelled" => "已取消",
        "interrupted" => _interruptedAt is { } t ? $"已中断（{t:HH:mm:ss}）" : "已中断",
        _ => "待办",
    };

    /// <summary>失败/拆解失败原因红标文本。</summary>
    public string FailReasonText => $"⚠ {_failReason}";

    public bool HasFailReason => !string.IsNullOrEmpty(_failReason);

    /// <summary>产物清单文本（完成后显示）。</summary>
    public string ArtifactsText =>
        _artifacts.Count == 0 ? "" : $"产物：{string.Join("、", _artifacts)}";

    public bool IsWorking => _status == "working";
    /// <summary>排队任务也可取消（只出队不杀进程）。</summary>
    public bool CanCancel => _status is "working" or "queued";
    /// <summary>中断/失败/取消的任务可一键恢复（轮 5）；DAG 任务完成后也可整条重跑（③）。</summary>
    public bool CanRerun => _status is "interrupted" or "failed" or "cancelled"
        || (_kind == "dag" && _status == "completed");
    /// <summary>卡级 [重跑此步骤] 只属流水线；DAG 用节点行上的 [重跑]（带提示词改写）。</summary>
    public bool CanRerunStep => CanRerun && _kind != "dag";
    /// <summary>轮 6：DAG 预览卡人工卡点。</summary>
    public bool CanConfirm => _kind == "dag" && _status == "preview";
    public bool CanReplan => _kind == "dag" && _status is "preview" or "parse_failed";
    public bool CanOpenArtifacts => _artifacts.Count > 0;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}

/// <summary>
/// 任务看板（轮 1 骨架 → 轮 4 实装 → 轮 5 实时进度 + 一键恢复）：
/// 表单新建串行流水线（claude → opencode）+ 三列卡片 + 实时两步状态 +
/// 完成后产物清单 / 打开产物文件夹 / 运行中终止整条流水线；重启后从 TaskStore 恢复历史。
/// 轮 5：卡片实时动作行 + 已耗时（1s 计时器拉网关 GetLive）；中断/失败/取消卡
/// 增 [重跑此步骤] / [整条重跑]（新会话、同 prompt、同工作区，非 --resume）。
/// </summary>
public partial class TaskBoardPage : UserControl
{
    private readonly ObservableCollection<TaskCardVm> _todo = new();
    private readonly ObservableCollection<TaskCardVm> _working = new();
    private readonly ObservableCollection<TaskCardVm> _done = new();
    private readonly Dictionary<int, TaskCardVm> _cards = new();
    private readonly System.Windows.Threading.DispatcherTimer _liveTimer;

    public TaskBoardPage()
    {
        InitializeComponent();

        TodoList.ItemsSource = _todo;
        WorkingList.ItemsSource = _working;
        DoneList.ItemsSource = _done;

        // 重启恢复：从库里读回历史任务（含终态与产物清单）——
        // 注意播种推迟到 Loaded 之后（见下），构造期播种的容器 UIA 全盲


        var gateway = App.Current.Gateway;
        gateway.TaskChanged += OnTaskChanged;
        // 任务历史事件里出现新建也刷新（Submit 后 store 已落库，事件到达时刷一遍）
        App.Current.Bus.EventReceived += OnBusEvent;

        // 轮 5：实时进度——每秒拉一次在途任务的最新动作行 + 已耗时（避开事件风暴，读侧轮询最稳）
        _liveTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _liveTimer.Tick += (_, _) => RefreshLive();
        _liveTimer.Start();

        // 轮 5：页面不缓存、每次导航新建——旧实例卸载时退订 + 停表（否则事件泄漏、计时器钉住实例不回收）
        Unloaded += OnUnloaded;

        // 轮 6：构造期播种的历史卡容器在页面挂到窗口前生成，其 UIA peer 不注册——
        // UIA 只见空壳 DataItem（轮 5 盲区变种；ItemsSource 置空重绑也救不活，验收第 4
        // 跑实证）。唯一一直健康的路径是"运行中新增卡片"：容器在已挂载的列表上实时生成。
        // 因此播种推迟到 Loaded 且渲染完一拍之后（Background 优先级在布局/渲染之后）。
        Loaded += (_, _) =>
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                () => ReloadAll());
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Current.Gateway.TaskChanged -= OnTaskChanged;
        App.Current.Bus.EventReceived -= OnBusEvent;
        _liveTimer.Stop();
    }

    /// <summary>把网关的实时动作/耗时写进运行中卡片（轮 5）。</summary>
    private void RefreshLive()
    {
        var gateway = App.Current.Gateway;
        foreach (var card in _working)
        {
            var live = gateway.GetLive(card.Id);
            if (live is null)
                continue;
            var (action, since) = live.Value;
            if (action.Length > 0)
                card.LiveActionText = action;
            card.ElapsedText = $"已耗时 {FormatElapsed(DateTime.Now - since)}";
        }
    }

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";

    private void OnBusEvent(AppEvent e)
    {
        if (e.Category == EventCategories.Task && e.Message.Contains("已创建"))
            ReloadAll();
    }

    private void OnTaskChanged(int taskId) => ReloadAll();

    // ---- 表单 ----

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var title = NewTaskTitle.Text.Trim();
        var step1 = NewTaskStep1.Text.Trim();
        var step2 = NewTaskStep2.Text.Trim();

        if (title.Length == 0)
        {
            NewTaskTitle.Focus();
            return;
        }
        if (step1.Length == 0 && step2.Length == 0)
        {
            App.Current.Bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                "⚠ 新建失败：标题与至少一个步骤提示词必填"));
            return;
        }

        try
        {
            var id = App.Current.Gateway.Submit(title,
                step1.Length == 0 ? null : step1,
                step2.Length == 0 ? null : step2);
            NewTaskTitle.Clear();
            NewTaskStep1.Clear();
            NewTaskStep2.Clear();
            ReloadAll();
        }
        catch (Exception ex)
        {
            App.Current.Bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⚠ 新建失败：{ex.Message}"));
        }
    }

    // ---- 项目任务（轮 6） ----

    /// <summary>最近一次 [选择计划] 读入的文件路径（提示行展示 + 下次对话框定位；提交后清空）。</summary>
    private string? _planFile;

    /// <summary>计划文件字数上限：再大 CEO stdin 也吃得下，但需求框渲染会卡，且远超默认 token 预算。</summary>
    private const int PlanFileMaxChars = 100_000;

    /// <summary>
    /// [选择计划]：从 .md 计划文件读全文填入需求框——一句话提示词装不下的长计划走这里。
    /// 读入后可编辑（补约束/删过时段落），再照常 [提交 CEO 拆解]；不直接开跑。
    /// 解码复用 OutputDecoder（严格 UTF-8 优先、OEM/GBK 回退），中文 .md 两种编码都不乱码。
    /// </summary>
    private void OnPickPlanClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择计划文件（Markdown）",
            Filter = "Markdown 计划 (*.md;*.markdown)|*.md;*.markdown|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };
        if (!string.IsNullOrWhiteSpace(_planFile))
            dlg.InitialDirectory = Path.GetDirectoryName(_planFile);

        if (dlg.ShowDialog() != true)
            return;

        string text;
        try
        {
            text = OutputDecoder.Decode(File.ReadAllBytes(dlg.FileName));
        }
        catch (Exception ex)
        {
            App.Current.Bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⚠ 读取计划失败：{ex.Message}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            App.Current.Bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⚠ 计划文件是空的：{dlg.FileName}"));
            return;
        }
        if (text.Length > PlanFileMaxChars)
        {
            App.Current.Bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
                $"⚠ 计划文件过大（{text.Length:N0} 字 > {PlanFileMaxChars:N0} 字上限），请精简后重选"));
            return;
        }

        _planFile = dlg.FileName;
        ProjectDemand.Text = text;
        PlanFileHint.Text = $"📄 已读取计划 {Path.GetFileName(dlg.FileName)}（{text.Length:N0} 字，可编辑后 [提交 CEO 拆解]）";
        PlanFileHint.Visibility = Visibility.Visible;
        ProjectDemand.Focus();
        ProjectDemand.CaretIndex = ProjectDemand.Text.Length;
        ProjectDemand.ScrollToEnd();
    }

    /// <summary>提交 CEO 拆解：需求（手输或 .md 计划全文）+ 三上限（token / 墙钟分钟 / 重跑次数）。</summary>
    private void OnSubmitProjectClick(object sender, RoutedEventArgs e)
    {
        var demand = ProjectDemand.Text.Trim();
        if (demand.Length == 0)
        {
            App.Current.Bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task, "⚠ 项目任务需求不能为空"));
            ProjectDemand.Focus();
            return;
        }

        long? tokens = ParsePositive(ProjBudgetTokens.Text);
        var minutes = ParsePositive(ProjBudgetWallMinutes.Text);
        int? reruns = ParsePositive(ProjBudgetReruns.Text) is { } r ? (int)r : null;
        try
        {
            App.Current.Gateway.SubmitProject(demand, tokens,
                minutes is { } m ? (int)(m * 60) : null, reruns);
            ProjectDemand.Clear();
            _planFile = null;
            PlanFileHint.Text = "";
            PlanFileHint.Visibility = Visibility.Collapsed;
            ProjBudgetTokens.Text = "100000";
            ProjBudgetWallMinutes.Text = "30";
            ProjBudgetReruns.Text = "3";
            ReloadAll();
        }
        catch (Exception ex)
        {
            App.Current.Bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task, $"⚠ 提交失败：{ex.Message}"));
        }
    }

    /// <summary>正整数解析（空/非正 = null = 用默认值）。</summary>
    private static long? ParsePositive(string text) =>
        long.TryParse(text.Trim(), out var v) && v > 0 ? v : null;

    /// <summary>[确认开工]：preview → 波次执行（确认前绝不执行）。</summary>
    private void OnConfirmStartClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskCardVm card)
            App.Current.Gateway.ConfirmStart(card.Id);
    }

    /// <summary>[重新拆解]：preview / parse_failed 再调一次 CEO。</summary>
    private void OnReplanClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskCardVm card)
            App.Current.Gateway.Replan(card.Id);
    }

    /// <summary>
    /// DAG 节点级 [重跑]（③ 验收）：弹窗展示并允许改写该节点 prompt 后，
    /// 从该节点起整棵重跑（改写后的 prompt 落库持久）。
    /// </summary>
    private async void OnRerunNodeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StepVm step)
            return;

        var record = App.Current.Gateway.Store.LoadTask(step.TaskId);
        var node = record?.Steps.FirstOrDefault(s => s.Seq == step.Seq && s.Role is null);
        if (node is null)
            return;

        var box = new TextBox
        {
            Text = node.Prompt,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 140,
            MaxHeight = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 12,
        };
        var dialog = new ModernWpf.Controls.ContentDialog
        {
            Title = $"重跑任务#{step.TaskId} 节点{step.Seq}（{node.NodeId}·{node.Agent}，可改写提示词）",
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "改写提示词后从该节点起重跑（更早节点保留产物）：",
                        FontSize = 12,
                        Opacity = 0.7,
                        Margin = new Thickness(0, 0, 0, 8),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    box,
                },
            },
            PrimaryButtonText = "确认重跑",
            CloseButtonText = "取消",
            DefaultButton = ModernWpf.Controls.ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ModernWpf.Controls.ContentDialogResult.Primary)
            return;
        App.Current.Gateway.RerunNode(step.TaskId, step.Seq,
            string.IsNullOrWhiteSpace(box.Text) ? null : box.Text);
    }

    private void OnCancelTaskClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskCardVm card)
            App.Current.Gateway.Cancel(card.Id);
    }

    /// <summary>轮 5：从卡上的失败/中断步骤起新会话重跑（不是 --resume）。</summary>
    private void OnRerunStepClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskCardVm card)
            return;
        var from = Math.Clamp(card.CurrentStep == 0 ? 1 : card.CurrentStep, 1, Math.Max(1, card.Steps.Count));
        App.Current.Gateway.Rerun(card.Id, from);
    }

    /// <summary>轮 5：整条从步骤 1 重跑（同 prompt、同工作区）；DAG 走 RerunNode（completed 也可重跑）。</summary>
    private void OnRerunAllClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskCardVm card)
            return;
        if (card.Kind == "dag")
            App.Current.Gateway.RerunNode(card.Id, 1, null);
        else
            App.Current.Gateway.Rerun(card.Id, 1);
    }

    private void OnOpenArtifactsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskCardVm card)
            return;
        var dir = WorkspaceLayout.PipelineWorkspace(card.Id);
        if (!Directory.Exists(dir))
            return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    // ---- 刷新 ----

    /// <summary>全量同步：DB → VM（任务量 ≤100，事件频率低，整刷最稳）。</summary>
    private void ReloadAll()
    {
        var records = App.Current.Gateway.Store.LoadTasks();

        var seen = new HashSet<int>();
        foreach (var r in records)
        {
            seen.Add(r.Id);
            if (!_cards.TryGetValue(r.Id, out var vm))
            {
                vm = new TaskCardVm { Id = r.Id, Title = r.Title };
                _cards[r.Id] = vm;
            }
            Apply(vm, r);
        }

        // 移除已被清理出查询窗口的任务
        foreach (var stale in _cards.Keys.Where(id => !seen.Contains(id)).ToList())
            _cards.Remove(stale);

        // 轮 6：planning（CEO 拆解中）/ preview（DAG 待确认）/ parse_failed（拆解失败）都停在待办列
        // 轮 7：queued（并发满员排队中）也停在待办列（没在跑，不占进行中列）
        Place(_todo, "pending", "planning", "preview", "parse_failed", "queued");
        Place(_working, "working");
        Place(_done, "completed", "failed", "cancelled", "interrupted");
        UpdateChrome();
    }

    private static void Apply(TaskCardVm vm, TaskRecord r)
    {
        // 轮 1 遗留的 todo/done 状态值归一到轮 4 起的五态（轮 5 增 interrupted，轮 6 增 DAG 三态）
        var status = r.Status switch
        {
            "todo" => "pending",
            "done" => "completed",
            var s => s,
        };
        vm.Kind = r.Kind;
        vm.FailReason = r.FailReason;
        vm.InterruptedAt = r.InterruptedAt; // 先于 Status 赋值：StatusText 依赖它
        vm.Status = status;
        vm.CurrentStep = r.CurrentStep;
        vm.Artifacts = r.Artifacts;

        // CEO 记账行（role=ceo）不上卡；节点结构变了（重新拆解）重建行
        var nodes = r.Steps.Where(s => s.Role is null).ToList();
        var structureChanged = vm.Steps.Count != nodes.Count
            || vm.Steps.Zip(nodes).Any(p => p.First.Seq != p.Second.Seq);
        if (structureChanged)
        {
            vm.Steps.Clear();
            foreach (var s in nodes)
                vm.Steps.Add(MakeStep(r.Id, s));
        }
        var terminal = status is "completed" or "interrupted" or "failed" or "cancelled";
        for (var i = 0; i < nodes.Count && i < vm.Steps.Count; i++)
        {
            var src = nodes[i];
            var dst = vm.Steps[i];
            if (dst.Seq != src.Seq)
                continue;
            dst.Status = src.Status;
            dst.Error = src.Error;
            dst.CanRerunNode = r.Kind == "dag" && terminal;
            dst.Refresh();
        }
        vm.CurrentStep = r.CurrentStep; // 步行状态变化后重算 IsCurrent
        if (status != "working")
        {
            vm.LiveActionText = "";
            vm.ElapsedText = "";
        }
    }

    private static StepVm MakeStep(int taskId, TaskStepRecord s) => new()
    {
        TaskId = taskId,
        Seq = s.Seq,
        Agent = s.Agent,
        NodeId = s.NodeId,
        Writes = s.WriteTargets ?? Array.Empty<string>(),
        Deps = s.DependsOn ?? Array.Empty<string>(),
    };

    private void Place(ObservableCollection<TaskCardVm> target, params string[] statuses)
    {
        var wanted = _cards.Values
            .Where(c => statuses.Contains(c.StatusKind))
            .OrderByDescending(c => c.Id)
            .ToList();
        if (target.SequenceEqual(wanted))
            return;
        // 增量同步：只增删/移动变化的卡，不动没变的卡。整列 Clear+Add 会把既有卡的容器
        // 拆掉重建，重建后的容器 UIA peer 不再注册——卡在视觉上完好、automation 全盲
        // （轮 6 验收 ③ 的根因：⑤ 的任务完成瞬间 DoneList 重建，旁边的夜间模式卡失明）。
        for (var i = target.Count - 1; i >= 0; i--)
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i]))
                continue;
            var at = target.IndexOf(wanted[i]);
            if (at >= 0)
                target.Move(at, i);
            else
                target.Insert(i, wanted[i]);
        }
    }

    private void UpdateChrome()
    {
        TodoCount.Text = _todo.Count.ToString();
        WorkingCount.Text = _working.Count.ToString();
        DoneCount.Text = _done.Count.ToString();
        TodoHint.Visibility = _todo.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkingHint.Visibility = _working.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneHint.Visibility = _done.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
