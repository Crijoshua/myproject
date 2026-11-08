using System.Windows.Controls;
using System.Windows.Media;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Desktop.Pages;

/// <summary>
/// 组织：三位数字员工卡片；claude / opencode 卡片与各自宿主状态事件联动（轮 4 起）。
/// 状态事件带 "标签|model"（model 可缺省）；opencode 事件流无 model 字段，副标兜底"智谱 Coding Plan"。
/// 流水线的 claude 宿主与监视页直发的 claude 宿主共用 claude-status 类别，卡片取最新一条。
/// </summary>
public partial class OrganizationPage : UserControl
{
    private string? _claudeModel;

    public OrganizationPage()
    {
        InitializeComponent();

        App.Current.Bus.EventReceived += OnEvent;
        // 轮 5：页面不缓存、每次导航新建——卸载时退订，旧实例不残留收事件
        Unloaded += (_, _) => App.Current.Bus.EventReceived -= OnEvent;

        // 轮 6：新实例只收得到未来的状态事件（换页前发的"CEO 拆解中"不会重放）——
        // 构造时先从网关/宿主种一次当前徽标，否则 CEO 拆解期间换页会看到默认「空闲」
        SeedBadges();

        // 轮 8：自检可用性（就绪 / 未安装灰显；每次导航新建页面 = 自动读到最新自检结果）
        ApplyAvailability();
    }

    /// <summary>
    /// 轮 8 自检联动：CLI 不可用的员工整卡灰显 + 文案提示（派活闸门在宿主层强制拒绝，
    /// 监视页派活按钮也会禁用——这里负责"看得见原因"）。
    /// </summary>
    private void ApplyAvailability()
    {
        void Apply(Border card, TextBlock ready, string agent)
        {
            if (App.Current.SelfCheck.IsAgentAvailable(agent))
            {
                card.Opacity = 1.0;
                ready.Text = "自检：就绪";
                ready.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x2D, 0x7D, 0x46));
            }
            else
            {
                card.Opacity = 0.55; // 灰显
                ready.Text = $"未安装（不可用）：{agent} 派活已禁用——设置页检查路径覆盖后[重新自检]";
                ready.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
            }
        }
        Apply(ClaudeCard, ClaudeReadyText, "claude");
        Apply(OpencodeCard, OpencodeReadyText, "opencode");
        Apply(CrushCard, CrushReadyText, "crush");
    }

    /// <summary>构造时种子徽标：CEO 拆解中（网关 planning）优先，其次任一宿主在跑 = 运行中。</summary>
    private void SeedBadges()
    {
        var gateway = App.Current.Gateway;
        var claude = gateway.IsPlanning ? "CEO 拆解中"
            : gateway.AgentRunningSince("claude") is not null || App.Current.Host.IsRunning ? "运行中"
            : "空闲";
        UpdateBadge(ClaudeBadge, ClaudeBadgeText, claude);
        UpdateBadge(OpencodeBadge, OpencodeBadgeText,
            gateway.AgentRunningSince("opencode") is not null ? "运行中" : "空闲");
        // 轮 7：crush 接入（纯文本员工，无事件流——徽标靠种子 + CrushStatus 状态事件）
        UpdateBadge(CrushBadge, CrushBadgeText,
            gateway.AgentRunningSince("crush") is not null ? "运行中" : "空闲");
    }

    private void OnEvent(AppEvent e)
    {
        switch (e.Category)
        {
            case EventCategories.AgentStatus:
                UpdateBadge(ClaudeBadge, ClaudeBadgeText, e.Message);
                UpdateClaudeModel(e.Message);
                break;

            case EventCategories.OpencodeStatus:
                UpdateBadge(OpencodeBadge, OpencodeBadgeText, e.Message);
                break;

            case EventCategories.CrushStatus:
                UpdateBadge(CrushBadge, CrushBadgeText, e.Message);
                break;
        }
    }

    /// <summary>徽标：空闲（灰）/ 运行中（蓝）/ 错误（红）/ CEO 拆解中（蓝，轮 6）。</summary>
    private static void UpdateBadge(Border badge, TextBlock text, string message)
    {
        var label = message.Split('|')[0];
        text.Text = label;
        badge.Background = label switch
        {
            "运行中" or "CEO 拆解中" => new SolidColorBrush(Color.FromArgb(0x44, 0x00, 0x78, 0xD7)),
            "错误" => new SolidColorBrush(Color.FromArgb(0x44, 0xC4, 0x2B, 0x1C)),
            _ => new SolidColorBrush(Color.FromArgb(0x29, 0x60, 0x80, 0x60)),
        };
    }

    private void UpdateClaudeModel(string message)
    {
        var parts = message.Split('|');
        if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
            _claudeModel = parts[1];

        ClaudeModelText.Text = _claudeModel is null
            ? "CEO 兼首席 · 战略规划 / 架构决策 / 任务分派"
            : $"CEO 兼首席 · model={_claudeModel} · 战略规划 / 架构决策 / 任务分派";
    }
}
