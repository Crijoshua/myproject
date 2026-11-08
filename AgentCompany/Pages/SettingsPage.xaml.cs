using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AgentCompany.Desktop.Services;
using ContentDialog = ModernWpf.Controls.ContentDialog;
using ContentDialogResult = ModernWpf.Controls.ContentDialogResult;

namespace AgentCompany.Desktop.Pages;

/// <summary>自检结果行（轮 8：设置页展示；必须是属性，{Binding} 不认字段）。</summary>
public sealed class SelfCheckRow
{
    public string Line { get; set; } = "";
    public override string ToString() => Line;
}

/// <summary>
/// 设置：三态主题切换（即时生效 + 持久化）、任务超时、并发上限、审批弹窗开关、
/// 轮 8 收官——可移植（工作区根 / 三 CLI 路径覆盖）、启动自检面板、数据自主
/// （打开数据文件夹 / 备份状态库）、M2b 提示词外置编辑（四键编辑器 + 锚点校验 +
/// 恢复默认）、关于（版本 1.1.1）、恢复默认。
/// </summary>
public partial class SettingsPage : UserControl, INavigationAware
{
    private bool _loaded;

    // ---- M2b：提示词外置编辑（特性 A 的 UI 部分） ----

    /// <summary>提示词外置目录（固定约定，与 PromptStore 默认根一致；本页负责建目录）。</summary>
    private static readonly string PromptsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentCompany", "prompts");

    /// <summary>四组编辑器控件索引（键 → 编辑框 / 状态徽标 / 红色提示行）。</summary>
    private readonly List<(string Key, TextBox Editor, TextBlock Status, TextBlock Hint)> _promptGroups = new();

    public SettingsPage()
    {
        InitializeComponent();

        _promptGroups.AddRange(new (string, TextBox, TextBlock, TextBlock)[]
        {
            ("planner", PromptEditor_planner, PromptStatus_planner, PromptHint_planner),
            ("claude", PromptEditor_claude, PromptStatus_claude, PromptHint_claude),
            ("opencode", PromptEditor_opencode, PromptStatus_opencode, PromptHint_opencode),
            ("crush", PromptEditor_crush, PromptStatus_crush, PromptHint_crush),
        });

        // 回显当前主题（App 启动时已按持久化值应用）。
        var mode = App.Current.Db.GetSetting(ThemeController.SettingKey) ?? ThemeController.FollowSystem;
        SelectRadio(mode);

        // 回显任务超时
        var timeout = App.Current.Db.GetSetting(AgentCliProcessHost.TimeoutSettingKey);
        TimeoutInput.Text = int.TryParse(timeout, out var t) && t >= 1 ? t.ToString()
            : AgentCliProcessHost.DefaultTimeoutSeconds.ToString();

        // 轮 7：回显多任务并发上限（默认 2，钳制 1–4）
        MaxParallelInput.Text = App.Current.Gateway.MaxParallelTasks.ToString();

        // v1.1 B1：回显单任务节点并发上限（默认 2，钳制 1–4；库里的原始值经网关同口径钳制后展示）
        MaxNodeParallelismBox.Text = App.Current.Gateway.MaxConcurrentNodes.ToString();

        // 回显审批弹窗开关（默认开）
        ApprovalDialogToggle.IsChecked = App.Current.Db.GetSetting(ApprovalService.DialogSettingKey) != "false";

        // 轮 8：回显可移植设置（空 = 默认）并展示启动自检结果
        WorkspaceRootInput.Text = App.Current.Db.GetSetting(WorkspaceLayout.SettingKey) ?? "";
        CliPathClaude.Text = App.Current.Db.GetSetting("claude_path") ?? "";
        CliPathOpencode.Text = App.Current.Db.GetSetting("opencode_path") ?? "";
        CliPathCrush.Text = App.Current.Db.GetSetting("crush_path") ?? "";
        RenderSelfCheck(App.Current.SelfCheck.Last);
        AboutVersionRun.Text = AppInfo.Version;
        UpdateCliInstallBadge();

        _loaded = true;
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;

        var mode = sender == RbLight ? ThemeController.Light
                  : sender == RbDark ? ThemeController.Dark
                  : ThemeController.FollowSystem;
        ApplyAndSave(mode);
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "恢复默认设置",
            Content = "将把主题恢复为“跟随系统”、超时恢复为 180 秒、任务并发与节点并发恢复为 2，并清空工作区根与三个 CLI 路径覆盖（回到 exe 旁 workspace\\ + PATH 查找），是否继续？",
            PrimaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = ModernWpf.Controls.ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        SelectRadio(ThemeController.FollowSystem);
        ApplyAndSave(ThemeController.FollowSystem);
        TimeoutInput.Text = AgentCliProcessHost.DefaultTimeoutSeconds.ToString();
        App.Current.Db.SetSetting(AgentCliProcessHost.TimeoutSettingKey,
            AgentCliProcessHost.DefaultTimeoutSeconds.ToString());
        MaxParallelInput.Text = A2aGateway.DefaultMaxParallel.ToString();
        App.Current.Db.SetSetting(A2aGateway.MaxParallelSettingKey, A2aGateway.DefaultMaxParallel.ToString());
        MaxNodeParallelismBox.Text = A2aGateway.DefaultMaxNodeParallelism.ToString();
        App.Current.Db.SetSetting(A2aGateway.MaxNodeParallelismSettingKey,
            A2aGateway.DefaultMaxNodeParallelism.ToString());
        ClearPortableOverrides();
        RunSelfCheck();
    }

    /// <summary>轮 7：失焦保存并发上限（钳制 1–4；下调不杀在跑任务，只挡新启动）。</summary>
    private void OnMaxParallelLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;

        var ok = int.TryParse(MaxParallelInput.Text.Trim(), out var v);
        if (!ok || v < 1) v = 1;
        if (v > 4) v = 4;
        MaxParallelInput.Text = v.ToString();
        App.Current.Db.SetSetting(A2aGateway.MaxParallelSettingKey, v.ToString());
    }

    /// <summary>
    /// v1.1 B1：失焦保存单任务节点并发上限（设置键 max_node_parallelism）。非 1–4 的整数
    /// （0 / 空 / 9 / abc）回落默认 2 并回显——非法值不钳边而是回默认，库里的值始终合法，
    /// 引擎侧 ClampNodeParallelism 只是兜底（仿 OnMaxParallelLostFocus 的失焦保存模式）。
    /// </summary>
    private void OnMaxNodeParallelismLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;

        var v = int.TryParse(MaxNodeParallelismBox.Text.Trim(), out var parsed) && parsed >= 1 && parsed <= 4
            ? parsed
            : A2aGateway.DefaultMaxNodeParallelism;
        MaxNodeParallelismBox.Text = v.ToString();
        App.Current.Db.SetSetting(A2aGateway.MaxNodeParallelismSettingKey, v.ToString());
    }

    /// <summary>失焦保存超时（钳制 5–3600）。</summary>
    private void OnTimeoutLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;

        var ok = int.TryParse(TimeoutInput.Text.Trim(), out var v);
        if (!ok || v < 5) v = 5;
        if (v > 3600) v = 3600;
        TimeoutInput.Text = v.ToString();
        App.Current.Db.SetSetting(AgentCliProcessHost.TimeoutSettingKey, v.ToString());
    }

    /// <summary>审批弹窗开关持久化（approval_show_dialog，"false"=关）。</summary>
    private void OnApprovalDialogToggle(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        App.Current.Db.SetSetting(ApprovalService.DialogSettingKey,
            ApprovalDialogToggle.IsChecked == true ? "true" : "false");
    }

    // ---- 轮 8：可移植与自检 ----

    /// <summary>工作区根覆盖项落库（空 = 默认 exe 旁；只影响新任务，历史不迁移）。</summary>
    private void OnWorkspaceRootLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        var v = WorkspaceRootInput.Text.Trim();
        if (v.Length == 0)
        {
            App.Current.Db.SetSetting(WorkspaceLayout.SettingKey, "");
            WorkspaceRootInput.Text = "";
            return;
        }
        try
        {
            var full = Path.GetFullPath(v); // 顺手校验是合法路径
            WorkspaceRootInput.Text = full;
            App.Current.Db.SetSetting(WorkspaceLayout.SettingKey, full);
        }
        catch
        {
            MessageBox.Show($"“{v}”不是合法路径，未保存。", "工作区根", MessageBoxButton.OK, MessageBoxImage.Warning);
            WorkspaceRootInput.Text = App.Current.Db.GetSetting(WorkspaceLayout.SettingKey) ?? "";
        }
    }

    /// <summary>三 CLI 路径覆盖落库（sender 区分员工；空 = PATH 查找）。</summary>
    private void OnCliPathLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;
        var box = (TextBox)sender;
        var agent = box == CliPathClaude ? "claude" : box == CliPathOpencode ? "opencode" : "crush";
        App.Current.Db.SetSetting(agent + "_path", box.Text.Trim());
        RunSelfCheck(); // 覆盖项变了立即重检——组织页/派活闸门马上反映
    }

    private void OnReselfCheckClick(object sender, RoutedEventArgs e) => RunSelfCheck();

    private void RunSelfCheck()
    {
        var items = App.Current.SelfCheck.Run(App.Current.Db);
        App.Current.Recorder.Record(EventTypes.SelfCheck, "手动自检",
            $"共 {items.Count} 项，通过 {items.Count(c => c.Ok)}：{string.Join("；", items.Select(c => $"{c.Title}={(c.Ok ? "OK" : "✗ " + c.Detail)}"))}");
        RenderSelfCheck(items);
    }

    private void RenderSelfCheck(IReadOnlyList<CheckItem> items)
    {
        SelfCheckList.Items.Clear();
        foreach (var c in items)
            SelfCheckList.Items.Add(new SelfCheckRow { Line = $"{(c.Ok ? "[OK] " : "[✗]  ")}{c.Title}：{c.Detail}" });
    }

    // ---- 轮 8：数据自主 ----

    /// <summary>
    /// M4 特性 D：[安装终端命令]——把本程序所在目录（publish 根，agentcompany.cmd shim 所在）
    /// 追加到用户 PATH。
    /// v1.2（联审 m234 低危）三项加固：
    /// ① 校验 cli\agentcompany.exe 本体在位（此前只看 shim——开发目录 bin\ 下没有 cli\，
    ///   该检查天然阻断"把构建输出目录写进用户 PATH"）；
    /// ② 读写改走注册表原始值（DoNotExpandEnvironmentNames）——Environment.GetEnvironmentVariable
    ///   会把 REG_EXPAND_SZ 里的 %…% 展开固化，改回原样读写并保持原值类型不破坏；
    /// ③ Registry.SetValue 不像 Environment.SetEnvironmentVariable 那样广播 WM_SETTINGCHANGE——
    ///   手动补广播，保证"重开终端即可用"的承诺（Explorer 缓存环境变量，靠此消息刷新）。
    /// 不用 setx（1024 截断）；幂等（已在 PATH 不重复追加）。结果就地提示（非模态，模态框会卡 UIA）。
    /// </summary>
    private void OnInstallCliCommandClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = CliInstallDir();
            if (!File.Exists(Path.Combine(dir, "agentcompany.cmd")) ||
                !File.Exists(Path.Combine(dir, "cli", "agentcompany.exe")))
            {
                CliInstallResultText.Foreground = System.Windows.Media.Brushes.Red;
                CliInstallResultText.Text =
                    $"目录缺少 agentcompany.cmd 或 cli\\agentcompany.exe（{dir}）：请先用 发布.bat 重新发布后再安装。";
                return;
            }
            var (userPath, kind) = ReadUserPathRaw();
            var already = userPath.Split(';')
                .Any(p => string.Equals(p.Trim().TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (!already)
            {
                WriteUserPathRaw(userPath.TrimEnd(';').TrimEnd(' ') + ";" + dir, kind);
                BroadcastEnvChange();
            }
            CliInstallResultText.Foreground = System.Windows.Media.Brushes.Green;
            CliInstallResultText.Text = "已写入用户 PATH，重开终端后 agentcompany 可用。";
            App.Current.Recorder.Record(EventTypes.SelfCheck, "安装终端命令",
                already ? $"PATH 已包含 {dir}（幂等跳过）" : $"PATH 追加 {dir}");
        }
        catch (Exception ex)
        {
            CliInstallResultText.Foreground = System.Windows.Media.Brushes.Red;
            CliInstallResultText.Text = $"写入用户 PATH 失败：{ex.Message}";
        }
        UpdateCliInstallBadge();
    }

    /// <summary>shim 所在目录 = 本程序 exe 目录（发布布局即 publish 根）。</summary>
    private static string CliInstallDir() => AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>原始读用户 PATH（不展开 %…%，保留原注册表值类型；不存在则空串 + 默认 ExpandString）。</summary>
    private static (string Raw, Microsoft.Win32.RegistryValueKind Kind) ReadUserPathRaw()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: false);
        if (key?.GetValue("PATH", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw)
            return ("", Microsoft.Win32.RegistryValueKind.ExpandString); // PATH 惯例类型
        return (raw, key.GetValueKind("PATH"));
    }

    private static void WriteUserPathRaw(string value, Microsoft.Win32.RegistryValueKind kind)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment", writable: true)
                        ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Environment");
        key.SetValue("PATH", value, kind);
    }

    /// <summary>广播环境变量变更（WM_SETTINGCHANGE "Environment"）——新开终端（Explorer 派生）才能看到新 PATH。</summary>
    private static void BroadcastEnvChange()
    {
        NativeWin.BroadcastEnvironmentChange();
    }

    /// <summary>按钮旁状态徽标：原始读用户 PATH 判断 publish 路径是否已在其中（与安装写入同一口径）。</summary>
    private void UpdateCliInstallBadge()
    {
        var dir = CliInstallDir().TrimEnd('\\');
        var (userPath, _) = ReadUserPathRaw();
        var installed = userPath.Split(';')
            .Any(p => string.Equals(p.Trim().TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase));
        CliInstallStatus.Text = installed ? "终端命令：已安装" : "终端命令：未安装";
    }

    /// <summary>打开 %APPDATA%\AgentCompany（状态库 / 日志 / 备份都在这）。</summary>
    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentCompany");
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    /// <summary>状态库一致性快照：state-backup-yyyyMMdd-HHmmss.db（VACUUM INTO）；结果就地提示（非模态）。</summary>
    private void OnBackupDbClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(App.Current.Db.DbPath)!;
            var dest = Path.Combine(dir, $"state-backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            App.Current.Db.BackupTo(dest);
            App.Current.Recorder.Record(EventTypes.SelfCheck, "备份状态库", dest);
            BackupResultText.Foreground = System.Windows.Media.Brushes.Green;
            BackupResultText.Text = $"已备份到：{dest}（{(new FileInfo(dest).Length / 1024.0):F0} KB）";
        }
        catch (Exception ex)
        {
            BackupResultText.Foreground = System.Windows.Media.Brushes.Red;
            BackupResultText.Text = $"备份失败：{ex.Message}";
        }
    }

    private void ClearPortableOverrides()
    {
        foreach (var (box, key) in new[]
        {
            (WorkspaceRootInput, WorkspaceLayout.SettingKey),
            (CliPathClaude, "claude_path"),
            (CliPathOpencode, "opencode_path"),
            (CliPathCrush, "crush_path"),
        })
        {
            box.Text = "";
            App.Current.Db.SetSetting(key, "");
        }
    }

    private void SelectRadio(string mode)
    {
        RbLight.IsChecked = mode == ThemeController.Light;
        RbDark.IsChecked = mode == ThemeController.Dark;
        RbSystem.IsChecked = mode != ThemeController.Light && mode != ThemeController.Dark;
    }

    private void ApplyAndSave(string mode)
    {
        ThemeController.Apply(mode);
        App.Current.Db.SetSetting(ThemeController.SettingKey, mode);
    }

    // ---- M2b：提示词外置编辑（特性 A 的 UI 部分） ----

    /// <summary>
    /// 进入设置页（MainWindow 导航回调）：回填四组编辑器——外置文件存在且合法 → 外置内容、
    /// 徽标「外置」；文件不存在 → 内置文本、徽标「内置」；存在但非法 → 内置文本、徽标「外置·非法已回退」。
    /// </summary>
    public void OnNavigatedTo()
    {
        foreach (var g in _promptGroups)
            LoadPromptGroup(g);
    }

    /// <summary>按三态口径回填一组编辑器（口径同 PromptStore.Resolve：UTF-8 解码失败 / 全空白 / 缺锚点 = 非法）。</summary>
    private static void LoadPromptGroup((string Key, TextBox Editor, TextBlock Status, TextBlock Hint) g)
    {
        var text = PromptStore.BuiltIn(g.Key);
        var badge = "内置";
        var color = System.Windows.Media.Brushes.Gray;

        byte[]? bytes = null;
        try
        {
            var file = Path.Combine(PromptsDir, g.Key + ".md");
            if (File.Exists(file))
                bytes = File.ReadAllBytes(file);
        }
        catch
        {
            // 读取异常也按"非法已回退"展示（与 PromptStore 状态②一致）
        }

        if (bytes is not null)
        {
            var decoded = TryDecodeUtf8(bytes);
            if (decoded is null || string.IsNullOrWhiteSpace(decoded) || MissingAnchors(g.Key, decoded).Count > 0)
            {
                badge = "外置·非法已回退";
                color = System.Windows.Media.Brushes.Red;
            }
            else
            {
                badge = "外置";
                color = System.Windows.Media.Brushes.Green;
                text = decoded;
            }
        }

        g.Editor.Text = text;
        g.Status.Text = badge;
        g.Status.Foreground = color;
        g.Hint.Text = "";
    }

    /// <summary>[保存此份]：先按锚点表校验（合法才落盘），写 UTF-8 无 BOM，失效缓存后徽标刷新「外置」。</summary>
    private void OnPromptSaveClick(object sender, RoutedEventArgs e)
    {
        var g = PromptGroupOf(sender);

        // 先校验锚点（PromptStore.AnchorTable 同口径）：缺锚点 → 不落盘，红字点名缺哪些
        var missing = MissingAnchors(g.Key, g.Editor.Text);
        if (missing.Count > 0)
        {
            g.Hint.Text = $"未保存：锚点校验失败（缺少 {string.Join("、", missing)}）";
            return;
        }

        try
        {
            Directory.CreateDirectory(PromptsDir); // 目录不存在先建（PromptStore 只读不建）
            var file = Path.Combine(PromptsDir, g.Key + ".md");
            File.WriteAllText(file, g.Editor.Text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)); // UTF-8 无 BOM
            PromptStore.InvalidateCache();
            g.Status.Text = "外置";
            g.Status.Foreground = System.Windows.Media.Brushes.Green;
            g.Hint.Text = "";
        }
        catch (Exception ex)
        {
            g.Hint.Text = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>[恢复默认]：删除该键外置文件（不存在也视为成功，幂等）→ 失效缓存 → 回填内置文本、徽标「内置」。</summary>
    private void OnPromptResetClick(object sender, RoutedEventArgs e)
    {
        var g = PromptGroupOf(sender);
        try
        {
            var file = Path.Combine(PromptsDir, g.Key + ".md");
            if (File.Exists(file))
                File.Delete(file);
            PromptStore.InvalidateCache();
            g.Editor.Text = PromptStore.BuiltIn(g.Key);
            g.Status.Text = "内置";
            g.Status.Foreground = System.Windows.Media.Brushes.Gray;
            g.Hint.Text = "";
        }
        catch (Exception ex)
        {
            g.Hint.Text = $"恢复默认失败：{ex.Message}";
        }
    }

    /// <summary>[打开提示词文件夹]：explorer 打开 %APPDATA%\AgentCompany\prompts\（不存在先创建）。</summary>
    private void OnOpenPromptsFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(PromptsDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = PromptsDir,
            UseShellExecute = true,
        });
    }

    /// <summary>按钮 → 所属组（保存/恢复按钮的 Tag 存键名）。</summary>
    private (string Key, TextBox Editor, TextBlock Status, TextBlock Hint) PromptGroupOf(object sender)
    {
        var key = (string)((FrameworkElement)sender).Tag;
        return _promptGroups.First(g => g.Key == key);
    }

    /// <summary>缺哪些锚点（口径同 PromptStore.MissingAnchors：AnchorTable[key] 逐项 Ordinal Contains）。</summary>
    private static List<string> MissingAnchors(string key, string text)
    {
        var missing = new List<string>();
        foreach (var anchor in PromptStore.AnchorTable[key])
            if (!text.Contains(anchor, StringComparison.Ordinal))
                missing.Add(anchor);
        return missing;
    }

    /// <summary>UTF-8 解码（StreamReader 检测剥 BOM；坏字节返回 null → 按"非法已回退"展示）。</summary>
    private static string? TryDecodeUtf8(byte[] bytes)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
