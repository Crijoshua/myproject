using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AgentCompany.Core;
using ModelContextProtocol.Server;

namespace AgentCompany.McpBridge;

/// <summary>
/// company 工具集（轮 3）：claude 读写文件、执行命令只能走这里。
/// 沙箱规则：所有路径先过 Sandbox.ResolveInside（仅工作区内，越界返回 SANDBOX_DENIED 文本，不抛异常）。
/// </summary>
[McpServerToolType]
public static class CompanyTools
{
    private const int MaxFileReadChars = 20000;
    private const int MaxListEntries = 500;

    // ---- file_read ----

    [McpServerTool(Name = "file_read")]
    [Description("读取工作区内文本文件（UTF-8）。path 为相对工作区根的路径。")]
    public static string FileRead(string path)
    {
        var full = Sandbox.ResolveInside(ToolEnv.Workspace, path);
        if (full is null)
        {
            BridgeLog.Info($"file_read 越界拒绝：{path}");
            return Sandbox.Denied(path);
        }
        if (!File.Exists(full))
            return $"ERROR: 文件不存在：{path}";

        try
        {
            var text = File.ReadAllText(full, Encoding.UTF8);
            BridgeLog.Info($"file_read OK：{path}（{text.Length} 字符）");
            return text.Length <= MaxFileReadChars
                ? text
                : text[..MaxFileReadChars] + $"\n…（超长已截断，全文 {text.Length} 字符）";
        }
        catch (Exception ex)
        {
            return $"ERROR: 读取失败：{ex.Message}";
        }
    }

    // ---- file_list ----

    [McpServerTool(Name = "file_list")]
    [Description("列出工作区内目录：目录带 / 后缀。path 为相对工作区根的路径，留空列根目录。")]
    public static string FileList(string path)
    {
        var full = Sandbox.ResolveInside(ToolEnv.Workspace, string.IsNullOrWhiteSpace(path) ? "." : path);
        if (full is null)
        {
            BridgeLog.Info($"file_list 越界拒绝：{path}");
            return Sandbox.Denied(path);
        }
        if (!Directory.Exists(full))
            return $"ERROR: 目录不存在：{path}";

        try
        {
            var entries = Directory.GetFileSystemEntries(full)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var shown = entries.Take(MaxListEntries)
                .Select(p => Directory.Exists(p) ? Path.GetFileName(p) + "/" : Path.GetFileName(p));
            BridgeLog.Info($"file_list OK：{path}（{entries.Count} 项）");
            var total = entries.Count > MaxListEntries ? $"（只列前 {MaxListEntries}）" : string.Empty;
            return $"共 {entries.Count} 项{total}：\n" + string.Join("\n", shown);
        }
        catch (Exception ex)
        {
            return $"ERROR: 列目录失败：{ex.Message}";
        }
    }

    // ---- file_write ----

    [McpServerTool(Name = "file_write")]
    [Description("写工作区内文件（新建或覆盖，UTF-8 无 BOM）。path 为相对工作区根的路径。多步骤任务中每个步骤只允许写自己的 write_targets 清单内的文件。")]
    public static string FileWrite(string path, string content)
    {
        var full = Sandbox.ResolveInside(ToolEnv.Workspace, path);
        if (full is null)
        {
            BridgeLog.Info($"file_write 越界拒绝：{path}");
            return Sandbox.Denied(path);
        }

        // 轮 6：文件所有权——DAG 节点只准写自己的 write_targets；越权写返回 DENIED（任务书文案）
        if (!ToolEnv.IsWriteAllowed(path))
        {
            BridgeLog.Info($"file_write 所有权拒绝：{path}（允许：{string.Join("、", ToolEnv.AllowedWrites ?? Array.Empty<string>())}）");
            return "DENIED: 该文件归其他步骤所有。请只写本步骤清单内的文件，或说明原因并结束。";
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false)); // 无 BOM：中文无乱码、无隐形前缀
            BridgeLog.Info($"file_write OK：{path}（{content.Length} 字符）");
            return $"OK: 已写入 {path}（{content.Length} 字符）";
        }
        catch (Exception ex)
        {
            return $"ERROR: 写入失败：{ex.Message}";
        }
    }

    // ---- terminal ----

    [McpServerTool(Name = "terminal")]
    [Description("在工作区根执行命令（cmd）。git status/diff/add/commit、npm test、npm run build、dotnet build/test、node -v、dir、ls 在白名单内直接执行；其余命令需宿主弹窗审批，可能被拒绝。命令参数里不要出现工作区外的绝对路径或 .. 穿越（会被沙箱直接拒绝）。")]
    public static async Task<string> Terminal(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "ERROR: 命令为空";

        // ① 参数路径越界（区外绝对路径 / .. 穿越）：直接拒绝，不执行也不进审批
        var escaping = Sandbox.FindEscapingPathArg(ToolEnv.Workspace, command);
        if (escaping is not null)
        {
            BridgeLog.Info($"terminal 越界拒绝（参数 {escaping}）：{command}");
            return Sandbox.Denied(escaping);
        }

        // ② 白名单内：直接执行
        if (!CommandWhitelist.IsWhitelisted(command))
        {
            // ③ 白名单外：经命名管道请求宿主审批，批准才执行
            BridgeLog.Info($"terminal 审批请求：{command}");
            var sw = Stopwatch.StartNew();
            var outcome = await ApprovalPipeClient.AskNamedPipeAsync(ToolEnv.PipeName,
                new ApprovalRequest(Guid.NewGuid().ToString("N"), ToolEnv.Agent, "terminal", command, ToolEnv.TaskTitle));
            BridgeLog.Info($"terminal 审批结局={outcome.Failure}/{outcome.Response?.Approved ?? false}" +
                           $"，往返 {sw.ElapsedMilliseconds}ms");

            if (!outcome.IsApproved)
            {
                // 拒绝文案让 claude 能看见原因并改道，而不是反复重试
                var reason = outcome.Failure switch
                {
                    ApprovalFailure.TimedOut => "DENIED: 审批超时",
                    ApprovalFailure.Unreachable => "DENIED: 审批通道不可达（宿主未响应），已按拒绝处理",
                    _ => string.Equals(outcome.Response?.Note, "timeout", StringComparison.OrdinalIgnoreCase)
                        ? "DENIED: 审批超时"
                        : $"DENIED: 用户拒绝了命令：{command}",
                };
                return reason + "。请改用别的方法（例如白名单内命令）或说明原因，不要原样重试同一命令。";
            }
        }

        return await TerminalRunner.RunAsync(ToolEnv.Workspace, command);
    }
}
