using System.IO;
using System.Text;

namespace AgentCompany.Desktop.Services;

/// <summary>
/// 项目文件夹快照（M4 特性 D3：文件夹感知拆解）：宿主侧代读目录树，拼进 CEO 拆解 stdin 的
/// 【项目文件夹】节。安全边界不变——CEO 进程仍是 --restricted 受限进程、不新增任何文件工具，
/// 读取全部发生在宿主侧；执行员工（claude/opencode/crush 节点）不携带文件夹内容。
/// 纯静态函数供 CLI（--folder / /folder）与单元测试共用。
/// </summary>
public static class FolderSnapshot
{
    /// <summary>排除目录名（OrdinalIgnoreCase）：构建产物 / 依赖 / 版本库 / 员工 CLI 运行库。</summary>
    public static readonly string[] ExcludedDirs =
        { "bin", "obj", "node_modules", ".git", ".oc-config", ".crush", ".vs", "packages" };

    /// <summary>
    /// 渲染目录树：根行（绝对路径）+ ├─/└─ 风格的相对路径条目；文件与目录都计条目，
    /// 超过 maxEntries 截断并以「…(截断,共 N 项)」收尾（Truncated=true）；深度超过
    /// maxDepth 的子树不再展开（静默、不计条目）。root 必须是已存在的绝对路径——
    /// 空 / 相对路径 / 不存在 / 顶层无权限都抛 ArgumentException（中文消息，不拆解）。
    /// </summary>
    public static (string Tree, bool Truncated, int Entries) RenderTree(
        string root, int maxDepth = 3, int maxEntries = 500)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("项目文件夹路径不能为空");
        // v1.2（联审 m234 低危）：Path.IsPathRooted 对盘相对路径（"C:foo"）也返回 true——会被
        // 当作绝对路径按该盘当前目录解析，结果偏离用户意图。要求完整根：盘符+目录（C:\x、
        // C:/x）或 UNC（\\srv\share、\\?\C:\x）；盘相对（C:foo）、根相对（\foo）均拒绝。
        if (!IsFullyRooted(root))
            throw new ArgumentException($"项目文件夹必须是完整绝对路径（盘符+目录，如 C:\\work\\proj；不接受盘相对路径）：{root}");
        if (!Directory.Exists(root))
            throw new ArgumentException($"项目文件夹不存在：{root}");
        try
        {
            // 顶层可达性试探（无权限在此抛，转成中文报错；子层无权限在渲染时静默跳过）
            _ = Directory.EnumerateFileSystemEntries(root).FirstOrDefault();
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"项目文件夹无法读取：{root}（{ex.Message}）");
        }

        var sb = new StringBuilder();
        var entries = 0;
        var truncated = false;
        sb.AppendLine(root);
        RenderDir(root, "", maxDepth, maxEntries, sb, ref entries, ref truncated);
        if (truncated)
            sb.Append("…(截断,共 ").Append(entries).Append(" 项)");
        return (sb.ToString().TrimEnd(), truncated, entries);
    }

    /// <summary>完整根判定（v1.2）：C:\x / C:/x / \\srv\share / \\?\C:\x → true；C:foo（盘相对）、\foo（根相对）、相对路径 → false。</summary>
    internal static bool IsFullyRooted(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.Length >= 2 && path[1] == ':')
            return path.Length >= 3 && (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar);
        return path.StartsWith(@"\\", StringComparison.Ordinal);
    }

    private static void RenderDir(string dir, string indent, int maxDepth, int maxEntries,
        StringBuilder sb, ref int entries, ref bool truncated)    {
        if (truncated)
            return;

        List<string> subDirs;
        List<string> files;
        try
        {
            // 目录在前、文件在后，各自按名称序（OrdinalIgnoreCase）；排除目录整个剪枝
            subDirs = Directory.EnumerateDirectories(dir)
                .Where(d => !IsExcluded(Path.GetFileName(d)))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
            files = Directory.EnumerateFiles(dir)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return; // 该层无权限：静默跳过（顶层已在 RenderTree 试探过）
        }

        var children = subDirs.Select(d => (Path: d, Name: Path.GetFileName(d), IsDir: true))
            .Concat(files.Select(f => (Path: f, Name: Path.GetFileName(f), IsDir: false)))
            .ToList();
        for (var i = 0; i < children.Count; i++)
        {
            if (entries >= maxEntries)
            {
                truncated = true;
                return;
            }
            var (path, name, isDir) = children[i];
            var last = i == children.Count - 1;
            sb.Append(indent).Append(last ? "└─ " : "├─ ").AppendLine(name);
            entries++;
            if (isDir && maxDepth > 1)
                RenderDir(path, indent + (last ? "   " : "│  "), maxDepth - 1, maxEntries, sb, ref entries, ref truncated);
            if (truncated)
                return;
        }
    }

    private static bool IsExcluded(string dirName) =>
        ExcludedDirs.Contains(dirName, StringComparer.OrdinalIgnoreCase);
}
