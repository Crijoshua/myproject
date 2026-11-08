using System;
using System.Windows;
using Microsoft.Win32;
using ModernWpf;

namespace AgentCompany.Desktop.Services;

/// <summary>
/// 主题控制：亮 / 暗 / 跟随系统，值持久化在 settings 表（key = theme）。
/// M5b 起 Apply 同时完成三件事（macOS 换肤接线点）：
/// ① ModernWpf 明暗主题（语义与 v1.0 相同）；
/// ② ModernWpf 强调色锁 macOS 系统蓝 #007AFF（侧栏选中条 / 焦点框 / 复选框等随之变蓝；
///    覆盖 App 启动早期锁的 #0078D7——Apply 在其后执行，以最后一次为准）；
/// ③ 热切 Application 级 MacOSTokens 字典（Light/Dark），全部 Macos* DynamicResource 即时换色不重启。
/// system 模式的深浅判定选型：读 HKCU Personalize\AppsUseLightTheme 注册表（1=浅 0=深，异常按浅），
/// 在 Apply 调用时刻解析——不监听系统实时切换（v1.0 的 ModernWpf 跟随也不做热切换，行为口径一致）。
/// </summary>
public static class ThemeController
{
    public const string SettingKey = "theme";
    public const string FollowSystem = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>macOS 系统蓝（与 Themes\MacOSTokens.*.xaml 的 MacosAccentColor 同值，单一来源注释互指）。</summary>
    private static readonly System.Windows.Media.Color MacosAccent =
        System.Windows.Media.Color.FromRgb(0x00, 0x7A, 0xFF);

    public static void Apply(string mode)
    {
        ThemeManager.Current.ApplicationTheme = mode switch
        {
            Light => ApplicationTheme.Light,
            Dark => ApplicationTheme.Dark,
            _ => null, // null = 跟随系统
        };

        ThemeManager.Current.AccentColor = MacosAccent;

        SwapTokenDictionary(ResolveTokens(mode));
    }

    /// <summary>三态 → 实际加载的 token 字典：light/dark 直取；system 读注册表判定。</summary>
    private static string ResolveTokens(string mode) => mode switch
    {
        Light => Light,
        Dark => Dark,
        _ => IsSystemLightTheme() ? Light : Dark,
    };

    private static bool IsSystemLightTheme()
    {
        try
        {
            var v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return v is int i ? i != 0 : true;
        }
        catch
        {
            return true; // 读不到按浅色（fail-safe：亮色可读性更稳）
        }
    }

    /// <summary>
    /// 把 Application.Resources.MergedDictionaries 里的 MacOSTokens 字典换成对应 Light/Dark 实例。
    /// 替换整个字典实例会触发资源失效通知，所有 DynamicResource Macos* 引用即时重取（热切换不重启）。
    /// 找不到该字典（异常装配）时静默跳过——控件回退 ModernWpf 原生主题，不影响功能。
    /// </summary>
    private static void SwapTokenDictionary(string which)
    {
        var app = Application.Current;
        if (app == null) return;

        var dicts = app.Resources.MergedDictionaries;
        var wanted = $"MacOSTokens.{which}.xaml";
        for (var i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString;
            if (src == null || !src.Contains("MacOSTokens.", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!src.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                dicts[i] = new ResourceDictionary
                {
                    Source = new Uri($"Themes/{wanted}", UriKind.Relative),
                };
            return; // 只管理唯一占位位
        }
    }
}
