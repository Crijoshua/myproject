namespace AgentCompany.Core;

/// <summary>
/// 命令白名单（轮 3）：白名单内的命令不弹审批直接执行；名单外一律走宿主审批。
/// 匹配规则：忽略大小写与前导空白，整条命中或"条目 + 空格"前缀命中（git commit -m "x" 命中 git commit）。
/// 命令含 cmd 元字符（&amp; | &lt; &gt; ^ % `）时不视为白名单——防止 "dir &amp; 危险命令" 借前缀匹配直通，转交审批。
/// </summary>
public static class CommandWhitelist
{
    /// <summary>白名单条目（宿主与文档共用这一份事实源）。</summary>
    public static readonly string[] Entries =
    {
        "git status",
        "git diff",
        "git add",
        "git commit",
        "npm test",
        "npm run build",
        "dotnet build",
        "dotnet test",
        "node -v",
        "dir",
        "ls",
    };

    private static readonly char[] BannedChars = { '&', '|', '<', '>', '^', '%', '`', '\n', '\r' };

    /// <summary>是否白名单内（可直接执行，无需审批）。</summary>
    public static bool IsWhitelisted(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var c = command.Trim();
        if (c.IndexOfAny(BannedChars) >= 0)
            return false;

        foreach (var entry in Entries)
        {
            if (c.Equals(entry, StringComparison.OrdinalIgnoreCase) ||
                c.StartsWith(entry + " ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
