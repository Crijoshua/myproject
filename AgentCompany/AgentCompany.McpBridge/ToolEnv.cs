using AgentCompany.Core;

namespace AgentCompany.McpBridge;

/// <summary>进程级工具上下文：宿主经环境变量注入（AGENTCOMPANY_WORKSPACE / AGENTCOMPANY_PIPE / AGENTCOMPANY_AGENT）。</summary>
internal static class ToolEnv
{
    public static string Workspace { get; private set; } = "";
    public static string PipeName { get; private set; } = ApprovalPipe.DefaultPipeName;

    /// <summary>发起工具调用的员工名（轮 4 起 opencode 也走同一 Bridge；审批请求/落库按此归属）。</summary>
    public static string Agent { get; private set; } = "claude";

    /// <summary>
    /// 轮 6：文件所有权白名单（DAG 节点的 write_targets，分号分隔相对路径）。
    /// null/空 = 手工流水线/直发任务：不限制（轮 4 行为兼容）。
    /// </summary>
    public static IReadOnlyList<string>? AllowedWrites { get; private set; }

    /// <summary>轮 7：来源任务标题（审批中心多请求排队时显示；非任务直发为 null）。</summary>
    public static string? TaskTitle { get; private set; }

    public static void Init(string workspace, string pipeName, string? agent)
    {
        Workspace = workspace;
        PipeName = pipeName;
        if (!string.IsNullOrWhiteSpace(agent))
            Agent = agent;
        TaskTitle = Environment.GetEnvironmentVariable("AGENTCOMPANY_TASK_TITLE");

        var writes = Environment.GetEnvironmentVariable("AGENTCOMPANY_ALLOWED_WRITES");
        if (!string.IsNullOrWhiteSpace(writes))
            AllowedWrites = writes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// 所有权判定：注入了白名单时，path（规范化后）必须在列表内。
    /// 归一与宿主侧 DagValidator.NormalizePath 同口径：Trim + 反斜杠→斜杠 + 去开头 ./ + 去首尾 /。
    /// </summary>
    public static bool IsWriteAllowed(string path)
    {
        if (AllowedWrites is null || AllowedWrites.Count == 0)
            return true;
        return AllowedWrites.Contains(Normalize(path), StringComparer.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        return p.Trim('/');
    }
}

/// <summary>
/// Bridge 诊断日志：%APPDATA%\AgentCompany\bridge.log（审批链路时序、工具调用、超时都落这里，
/// 排查"claude 说被拒绝但不知道为什么"用）。
/// </summary>
internal static class BridgeLog
{
    private static readonly object Gate = new();
    private static string? _path;

    private static string LogPath => _path ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AgentCompany", "bridge.log");

    public static void Info(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败不影响工具执行
            }
        }
    }
}
