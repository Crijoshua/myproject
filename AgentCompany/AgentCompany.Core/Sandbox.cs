namespace AgentCompany.Core;

/// <summary>
/// 工作区沙箱路径判定（轮 3）：所有文件类工具（file_read / file_list / file_write）
/// 与 terminal 的路径参数都先过这里。判定规则：
///  - 工作区根 = 调用方注入（宿主注入 AGENTCOMPANY_WORKSPACE）；
///  - Path.GetFullPath 解析（相对路径按工作区根拼接）后必须以根为前缀（OrdinalIgnoreCase），
///    含 .. 穿越、绝对路径指向区外、大小写变体一律拒绝；
///  - 前缀必须带目录分隔符，防 C:\ws2 伪前缀命中 C:\ws；
///  - 解析异常（非法字符等）一律拒绝，绝不抛出。
/// </summary>
public static class Sandbox
{
    /// <summary>拒绝时返回文本的前缀（见 Denied）。</summary>
    public const string DeniedPrefix = "SANDBOX_DENIED: 路径越界：";

    /// <summary>
    /// 解析工作区内绝对路径；越界/非法/为空返回 null。
    /// 返回值为规范化后的绝对路径（调用方只用它做 IO，不再用原始入参）。
    /// </summary>
    public static string? ResolveInside(string? workspaceRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return null;
        // 空路径视为工作区根（file_list("") 列根目录）
        if (string.IsNullOrWhiteSpace(path))
            path = ".";

        try
        {
            var root = TrimTrailingSeparator(Path.GetFullPath(workspaceRoot));
            var full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(root, path));

            // 本产品工作区恒为本地盘：UNC / 设备命名空间路径直接拒绝
            if (root.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith(@"\\", StringComparison.Ordinal))
                return null;

            var cmp = StringComparison.OrdinalIgnoreCase;
            if (!full.Equals(root, cmp) && !full.StartsWith(root + Path.DirectorySeparatorChar, cmp))
                return null;

            // .NET Core 的 GetFullPath 不再拦截 < > | : 等字符（Win32 层才报错），
            // 显式检查根之后的每个路径段（GetInvalidFileNameChars 含分隔符，须按段查），
            // 命中非法字符一律拒绝（fail-safe）
            var remainder = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var segment in remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Length > 0 && segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return null;
            }

            return full;
        }
        catch
        {
            return null; // 非法字符、非法盘符等，一律拒绝
        }
    }

    /// <summary>terminal 命令参数的越界扫描：返回第一个指向工作区外的路径样式 token（无则 null）。</summary>
    public static string? FindEscapingPathArg(string workspaceRoot, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        // 绝对盘符路径 / UNC / ../ 或 ..\ 开头的相对穿越。git 的 HEAD..HEAD~1 不含分隔符后缀，不会误伤。
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     command, @"[A-Za-z]:\\[^\s""]*|\\\\[^\s""]*|\.\.[\\/][^\s""]*"))
        {
            var token = m.Value.TrimEnd('"', ';', ',');
            if (ResolveInside(workspaceRoot, token) is null)
                return token;
        }
        return null;
    }

    /// <summary>工具返回用的拒绝文本（不抛异常）。</summary>
    public static string Denied(string? path) => DeniedPrefix + (path ?? "<空路径>");

    private static string TrimTrailingSeparator(string p)
    {
        if (p.Length <= 3) // "C:\" 这类盘根保留
            return p;
        return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
