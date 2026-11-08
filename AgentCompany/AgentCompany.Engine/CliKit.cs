using System.IO;
using System.Text;

namespace AgentCompany.Desktop.Services;

/// <summary>agentcompany 命令行参数解析结果（Error 非空 = 用法错误，携带完整用法文本）。</summary>
public sealed record CliOptions(string? Folder, bool ShowVersion, bool ShowHelp, string? Error);

/// <summary>
/// CLI 支撑件（M4 特性 D）：命令行参数解析与 /peek 文件代读的纯函数，放引擎侧供
/// AgentCompany.Cli 与单元测试共用（测试项目不引 Cli 工程，逻辑必须下沉到 Engine）。
/// </summary>
public static class CliArgs
{
    /// <summary>用法文本（--help 与未知参数共用）。</summary>
    public const string Usage =
        """
        用法：agentcompany [--folder <绝对路径>] [--version] [--help]

          无参数          进入 CEO 终端 REPL（多行输入需求，空行提交）
          --folder <路径> 预置项目文件夹（宿主代读目录树拼进拆解输入；须为已存在的绝对路径）
          --version       显示版本
          --help          显示本用法

        REPL 命令：/folder <绝对路径>  /peek <相对路径>  /status  /budget
                    /memory [n]  /prompts  /quit（Ctrl+C 优雅退出）
        """;

    /// <summary>
    /// 解析命令行：只做语法层（--folder 值存在性/未知参数），路径合法性（绝对/存在）由
    /// FolderSnapshot.RenderTree 在使用时校验（中文报错统一口径）。
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        string? folder = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--folder":
                    if (i + 1 >= args.Length)
                        return new CliOptions(null, false, false, $"--folder 缺少路径参数\n{Usage}");
                    folder = args[++i];
                    break;
                case "--version":
                    return new CliOptions(folder, true, false, null);
                case "--help":
                case "-h":
                case "/?":
                    return new CliOptions(folder, false, true, null);
                default:
                    return new CliOptions(null, false, false, $"未知参数：{args[i]}\n{Usage}");
            }
        }
        return new CliOptions(folder, false, false, null);
    }
}

/// <summary>/peek 文件代读（M4 特性 D3）：把项目文件夹内单个文件内容附加进下一次拆解输入。</summary>
public static class CliPeek{
    /// <summary>单文件读取上限（字节）：50KB，超出截断留痕。</summary>
    public const int MaxBytes = 50 * 1024;

    /// <summary>
    /// 读取 root 下的一枚相对路径文件：必须为相对路径且解析后不出 root（防 ../ 逃逸）；
    /// ≤50KB 全文返回；超限截断到 50KB（Truncated=true，尾部半个多字节字符按替换符剥除）。
    /// 任何问题返回 Error（中文），调用方打印后继续 REPL。
    /// </summary>
    public static (string Content, bool Truncated, string? Error) ReadFile(string root, string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath))
            return ("", false, "用法：/peek <相对路径>");
        if (Path.IsPathRooted(relPath))
            return ("", false, $"必须为相对路径：{relPath}");
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, relPath));
        }
        catch (Exception ex)
        {
            return ("", false, $"路径不合法：{relPath}（{ex.Message}）");
        }
        var rootFull = Path.GetFullPath(root);
        // 联审修复（m234 #1）：前缀判定补分隔符边界——root=C:\repo 时 ../repo2/x.txt 解析为
        // C:\repo2\x.txt，StartsWith(rootFull) 会误放行。改为"相等或 rootFull+分隔符 前缀"。
        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullNorm = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!(string.Equals(fullNorm, rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
              || full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)))
            return ("", false, $"路径越出项目文件夹：{relPath}");
        if (!File.Exists(full))
            return ("", false, $"文件不存在：{relPath}");

        byte[] bytes;
        var truncated = false;
        try
        {
            bytes = File.ReadAllBytes(full);
            if (bytes.Length > MaxBytes)
            {
                bytes = bytes[..MaxBytes];
                truncated = true;
            }
        }
        catch (Exception ex)
        {
            return ("", false, $"读取失败：{relPath}（{ex.Message}）");
        }

        // 截断可能切坏多字节字符：宽松解码后剥掉尾部的替换符
        var text = Encoding.UTF8.GetString(bytes);
        if (truncated && text.EndsWith('�'))
            text = text.TrimEnd('�');
        return (text, truncated, null);
    }
}

/// <summary>
/// 审批管道占用探测（M4 单宿主互斥）：以 client 连一下管道名，能连上 = 已有宿主持有服务端
/// （桌面版或另一个 CLI 在跑）。CLI 启动先探测，被占即退出码 2。
/// </summary>
public static class PipeProbe
{
    public static bool IsOccupied(string pipeName, int timeoutMs = 500)
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", pipeName, System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            return true;
        }
        catch
        {
            return false; // 超时/连不上 = 没有宿主在听
        }
    }
}
