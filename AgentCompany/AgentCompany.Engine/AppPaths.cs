namespace AgentCompany.Desktop.Services;

/// <summary>产品版本（v1.1：窗口标题 / 设置页“关于”/ CLI 横幅 / 发布摘要共用，四同源）。</summary>
public static class AppInfo
{
    public const string Version = "1.1.1";
    public const string ProductName = "小公司 · Agent 指挥中心";
}

/// <summary>
/// 员工 CLI 定位（轮 8 可移植）：默认按 PATH 解析（cmd /c &lt;员工名&gt; 由 shell 自己找）；
/// 设置表 &lt;员工名&gt;_path 覆盖项优先（绝对路径，含空格自动加引号）。
/// Init 由 App 启动时接 Database.GetSetting（每次调用实时读，设置页改完即生效）；
/// 单测不 Init 时默认无覆盖 = 行为与轮 2-7 完全一致。
/// </summary>
public static class CliPaths
{
    private static Func<string, string?> _overrideProvider = _ => null;

    /// <summary>App 启动时注入覆盖项读取器（key = 员工名，返回 null = 无覆盖）。</summary>
    public static void Init(Func<string, string?> provider) => _overrideProvider = provider;

    /// <summary>还原默认（单测隔离用）。</summary>
    public static void Reset() => _overrideProvider = _ => null;

    /// <summary>覆盖项原始值（已 Trim；无覆盖 / 空串返回 null）。</summary>
    public static string? RawOverride(string agent) => _overrideProvider(agent)?.Trim() is { Length: > 0 } v ? v : null;

    /// <summary>cmd /c 之后的命令头：覆盖路径（含空格加引号）或员工名（走 PATH）。</summary>
    public static string Command(string agent)
    {
        var o = RawOverride(agent);
        if (o is null)
            return agent;
        // cmd 引号规则：路径含空格必须整体加引号；已带引号不重复包
        return o.Contains(' ') && !o.StartsWith('"') ? $"\"{o}\"" : o;
    }
}
