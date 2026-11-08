using System.IO;
using System.Text;
using System.Text.Json;
using AgentCompany.Core;

namespace AgentCompany.Desktop.Services;

/// <summary>宿主给事件解析器的回写通道（会话/模型/token/上屏文本/诊断）。</summary>
public interface ISessionSink
{
    string AgentId { get; }

    /// <summary>上屏一行（事件流）。</summary>
    void Text(string message, bool dim = false);

    /// <summary>非 JSON 行 / 未知事件 / 解析失败 → 诊断通道（绝不抛异常）。</summary>
    void Diag(string message);

    void SetSession(string? sessionId);
    void SetModel(string? model);
    void SetReply(string? text);
    void AddUsage(long inputTokens, long outputTokens, long cacheReadTokens);
    void SetThinking(long estimatedTokens);
    void SetDuration(long milliseconds);

    /// <summary>累计 token 计量（claude 的 🏁 result 行显示用，保持轮 2 文案）。</summary>
    long TotalInput { get; }
    long TotalOutput { get; }
    long TotalCacheRead { get; }
}

/// <summary>
/// 员工 CLI 适配器：命令行构造 + stdout 事件解析，一人一套。
/// 轮 4 起 claude / opencode 共用 AgentCliProcessHost，差异全部收敛在适配器里。
/// </summary>
public interface IAgentAdapter
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>默认工作区目录名（workspace\ 下；监视页直发任务用，流水线传显式工作区）。</summary>
    string DefaultWorkspaceFolder { get; }

    /// <summary>
    /// 启动前准备（写配置/临时文件）并构造 cmd /c 之后的参数。
    /// bridgeExe 为 null 表示未找到 McpBridge（降级为无公司工具运行，宿主会大声记日志）。
    /// 返回 (参数, 任务结束后待删除的临时文件列表)。
    /// </summary>
    (string Args, List<string> TempArtifacts) BuildArguments(string? bridgeExe, string workspace);

    /// <summary>
    /// 启动前注入员工专属环境变量（默认无）。opencode 用它指 XDG_CONFIG_HOME，
    /// 见 OpencodeAdapter 的注释。
    /// </summary>
    void ConfigureEnvironment(System.Collections.Specialized.StringDictionary env, string workspace) { }

    /// <summary>逐行解析 stdout（空行跳过；非 JSON / 坏 JSON 由适配器进诊断，不抛异常）。</summary>
    void ParseLine(string line, ISessionSink sink);

    /// <summary>
    /// 进程退出后的最终回复（轮 7：crush 无事件流，全部 stdout 就是最终文本，由宿主在
    /// Finish 时一次性取走代写成 review.md；JSON 流员工的默认实现为 null = 沿用 SetReply）。
    /// </summary>
    string? FinalReply() => null;
}

/// <summary>员工花名册（轮 4：claude + opencode；轮 7：crush 入职，公司满编）。</summary>
public static class EmployeeRegistry
{
    public static IAgentAdapter Create(string id) => id switch
    {
        "opencode" => new OpencodeAdapter(),
        "crush" => new CrushAdapter(),
        _ => new ClaudeAdapter(),
    };
}

// ==================== claude ====================

/// <summary>
/// claude 适配器（轮 2/3 行为原样搬入，勿改）：
/// cmd /c claude -p --output-format stream-json --verbose + MCP 旗标 + stream-json 解析。
/// </summary>
public sealed class ClaudeAdapter : IAgentAdapter
{
    public string Id => "claude";
    public string DisplayName => "claude";
    public string DefaultWorkspaceFolder => "claude";

    // 轮 8 可移植：claude 命令头可被设置表 claude_path 覆盖（默认按 PATH）
    private static string Arguments => $"/c {CliPaths.Command("claude")} -p --output-format stream-json --verbose";

    /// <summary>预授权的四个 company MCP 工具（headless 下未预授权 = 直接拒）。</summary>
    public const string AllowedMcpTools =
        "mcp__company__file_read mcp__company__file_list mcp__company__file_write mcp__company__terminal";

    /// <summary>
    /// 禁掉的内置工具：轮 2 名单 + 轮 3 实测 init 事件里残余的全部内置工具，
    /// 强制员工只走公司工具。名单有出入时以最新一轮 init 日志为准修正。
    /// </summary>
    public const string DisallowedTools =
        "Read Write Edit NotebookEdit Bash Glob Grep LS WebFetch WebSearch Task TodoWrite " +
        "CronCreate CronDelete CronList DesignSync EnterWorktree ExitWorktree ListAgents " +
        "ReportFindings ScheduleWakeup SendMessage Skill ToolSearch Workflow";

    /// <summary>
    /// 追加系统提示（写入 UTF-8 无 BOM 临时文件，经 --append-system-prompt-file 传入）。
    /// v1.1 特性 A1：内容来源改 PromptStore（外置 %APPDATA%\AgentCompany\prompts\claude.md 优先，
    /// 带锚点校验与内置回退；内置原文已逐字搬入 PromptStore）。const → static 属性，其余口径不变。
    /// </summary>
    private static string SystemPrompt => PromptStore.Get("claude");

    private string? _mcpConfigPath; // 本次运行的 mcp-config.json（结束后删）
    private string? _promptPath;    // 本次运行的 system-prompt.txt（结束后删）

    public (string Args, List<string> TempArtifacts) BuildArguments(string? bridgeExe, string workspace)
    {
        var temps = new List<string>();
        var arguments = Arguments;
        if (bridgeExe is not null)
        {
            var (mcpConfig, prompt) = WriteMcpArtifacts(bridgeExe, workspace);
            _mcpConfigPath = mcpConfig;
            _promptPath = prompt;
            temps.Add(mcpConfig);
            temps.Add(prompt);
            arguments += $" --strict-mcp-config --mcp-config \"{mcpConfig}\""
                       + $" --allowedTools \"{AllowedMcpTools}\""
                       + $" --disallowedTools \"{DisallowedTools}\""
                       + $" --append-system-prompt-file \"{prompt}\"";
        }
        return (arguments, temps);
    }

    public void ParseLine(string line, ISessionSink sink)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            sink.Diag(trimmed);
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(trimmed);
        }
        catch (JsonException ex)
        {
            sink.Diag($"JSON 解析失败（{ex.Message}）：{Truncate(trimmed, 120)}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                sink.Diag($"缺少 type 字段：{Truncate(trimmed, 120)}");
                return;
            }

            switch (typeEl.GetString())
            {
                case "system":
                    ParseSystem(root, sink);
                    break;
                case "assistant":
                    ParseAssistant(root, sink);
                    break;
                case "user":
                    ParseUser(root, sink);
                    break;
                case "result":
                    ParseResult(root, sink);
                    break;
                default:
                    // 未知 type：跳过并记诊断，绝不抛异常
                    sink.Diag($"未知 type={typeEl.GetString()}，已跳过：{Truncate(trimmed, 120)}");
                    break;
            }
        }
    }

    private static void ParseSystem(JsonElement root, ISessionSink sink)
    {
        if (!root.TryGetProperty("subtype", out var sub) || sub.ValueKind != JsonValueKind.String)
            return;

        switch (sub.GetString())
        {
            case "init":
                sink.SetSession(GetString(root, "session_id"));
                sink.SetModel(GetString(root, "model"));
                sink.Text($"✅ 已连接 · model={GetString(root, "model")} · session={Truncate(GetString(root, "session_id"), 8)}…");
                break;

            case "thinking_tokens":
                // 高频增量：绝不上屏，只累计供"思考中"指示
                sink.SetThinking(GetLong(root, "estimated_tokens"));
                break;
        }
    }

    private static void ParseAssistant(JsonElement root, ISessionSink sink)
    {
        if (root.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (GetString(block, "type"))
                {
                    case "text":
                        var text = GetString(block, "text");
                        if (!string.IsNullOrEmpty(text))
                            sink.Text(text);
                        break;
                    case "tool_use":
                        sink.Text($"🔧 {DescribeToolUse(block)}");
                        break;
                    case "thinking":
                        sink.Text("💭 思考…（已折叠）", dim: true);
                        break;
                }
            }
        }
    }

    private static void ParseUser(JsonElement root, ISessionSink sink)
    {
        if (root.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (GetString(block, "type") == "tool_result")
                    sink.Text("📥 工具结果（已折叠）", dim: true);
            }
        }
    }

    private static void ParseResult(JsonElement root, ISessionSink sink)
    {
        // 注意：可能不止一条 result —— 状态以最后一条为准，usage 各条累加
        var duration = GetLong(root, "duration_ms");
        if (duration > 0)
            sink.SetDuration(duration);

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            sink.AddUsage(GetLong(usage, "input_tokens"), GetLong(usage, "output_tokens"),
                GetLong(usage, "cache_read_input_tokens"));

        var isErr = GetBool(root, "is_error");
        var reply = GetString(root, "result");
        if (!string.IsNullOrEmpty(reply))
            sink.SetReply(reply);

        sink.Text($"🏁 result({GetString(root, "subtype")}) · 累计 输入 {sink.TotalInput} / 输出 {sink.TotalOutput} / 缓存读 {sink.TotalCacheRead}"
            + (isErr ? " · is_error" : string.Empty));
    }

    /// <summary>tool_use 事件的可读描述：company 工具显示为 company.terminal → npm install lodash。</summary>
    private static string DescribeToolUse(JsonElement block)
    {
        var name = GetString(block, "name") ?? "?";
        if (!name.StartsWith("mcp__company__", StringComparison.Ordinal))
            return name;

        var display = "company." + name["mcp__company__".Length..];
        var arg = FirstInputString(block, "command", "path", "content");
        return arg is null ? display : $"{display} → {Truncate(arg, 60)}";
    }

    /// <summary>
    /// 每次派活生成两个临时文件：mcp-config.json（指向 Bridge 绝对路径，含环境注入）
    /// 与 system-prompt.txt（中文系统提示，UTF-8 无 BOM——参数直传中文会被 GBK 码页弄花）。
    /// </summary>
    private static (string Config, string Prompt) WriteMcpArtifacts(string bridgeExe, string workspace)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AgentCompany");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];

        var config = Path.Combine(dir, $"mcp-{stamp}.json");
        var json = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["company"] = new
                {
                    command = bridgeExe,
                    args = Array.Empty<string>(),
                    env = new Dictionary<string, string>
                    {
                        ["AGENTCOMPANY_WORKSPACE"] = workspace,
                        ["AGENTCOMPANY_PIPE"] = ApprovalPipe.DefaultPipeName,
                    },
                },
            },
        });
        File.WriteAllText(config, json);

        var prompt = Path.Combine(dir, $"prompt-{stamp}.txt");
        File.WriteAllText(prompt, SystemPrompt, new UTF8Encoding(false));

        return (config, prompt);
    }

    private static string? FirstInputString(JsonElement block, params string[] keys)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in keys)
        {
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    internal static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    internal static bool GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    internal static string Truncate(string? s, int max) =>
        s is null ? string.Empty : s.Length <= max ? s : s[..max] + "…";
}

// ==================== opencode ====================

/// <summary>
/// opencode 适配器（轮 4，全部本机实测 v2.1.4）：
///  - 命令：cmd /c opencode run --standalone --format json（垫片同 claude 须过 cmd）；
///    --standalone 必须带：默认连后台 daemon，跨任务环境会污染且进程残留，验收⑥过不了；
///  - 任务文本走 stdin（UTF-8 写入后关闭），中文无损；
///  - 事件一行一个 JSON：step_start(顶层 sessionID) / text(part.text) /
///    tool_use(part.tool + part.state.status/error/output) / step_finish(part.tokens，cost 恒 0 不用)；
///    未知 type 跳过记诊断；
///  - 无 model 字段 → 组织页显示"智谱 Coding Plan"兜底；
///  - 权限封锁（硬性要求）：工作区放项目级 opencode.json，permission.deny 掉内置
///    write/edit/bash/webfetch（deny = 从可用工具列表移除，强试报 "No tool named …"）；
///    MCP 配置写 mcp.servers.company.environment（实测生效），只经 JS execute 运行时
///    tools.company["…"] 调用（该运行时无 require，fs 直写逃逸实测失败）；
///  - 禁传 --auto（自动批准），宿主启动参数审计已有；
///  - 行为约束走工作区 AGENTS.md（opencode 自动读取）。
/// </summary>
public sealed class OpencodeAdapter : IAgentAdapter
{
    public string Id => "opencode";
    public string DisplayName => "opencode";
    public string DefaultWorkspaceFolder => "opencode";

    // --model 必须显式指定：不传时 opencode 默认选 opencode Zen 免费模型，免费额度耗尽即
    // provider.free-limit 秒败（2026-10-26 实测）；用户已配 zhipuai-coding-plan（包月，cost 恒 0），
    // 显式走该通道，模型清单见 `opencode models`。
    // 轮 8 可移植：opencode 命令头可被设置表 opencode_path 覆盖（默认按 PATH）
    private static string Arguments => $"/c {CliPaths.Command("opencode")} run --standalone --model zhipuai-coding-plan/glm-5.3 --format json";

    /// <summary>
    /// 行为约束（opencode 自动读取工作区 AGENTS.md；v1.1 特性 A1：内容来源改 PromptStore，
    /// 外置 %APPDATA%\AgentCompany\prompts\opencode.md 优先，带锚点校验与内置回退）。const → static 属性。
    /// </summary>
    private static string AgentsMd => PromptStore.Get("opencode");

    public (string Args, List<string> TempArtifacts) BuildArguments(string? bridgeExe, string workspace)
    {
        // 项目级 opencode.json：权限封锁 + MCP 接入（都留在工作区内，随任务持久化）
        var config = new Dictionary<string, object>
        {
            ["permission"] = new Dictionary<string, string>
            {
                ["edit"] = "deny",
                ["bash"] = "deny",
                ["webfetch"] = "deny",
            },
        };
        if (bridgeExe is not null)
        {
            config["mcp"] = new Dictionary<string, object>
            {
                ["servers"] = new Dictionary<string, object>
                {
                    ["company"] = new
                    {
                        type = "local",
                        command = new[] { bridgeExe },
                        environment = new Dictionary<string, string>
                        {
                            ["AGENTCOMPANY_WORKSPACE"] = workspace,
                            ["AGENTCOMPANY_PIPE"] = ApprovalPipe.DefaultPipeName,
                            ["AGENTCOMPANY_AGENT"] = Id,
                        },
                    },
                },
            };
        }
        File.WriteAllText(
            Path.Combine(workspace, "opencode.json"),
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), AgentsMd, new UTF8Encoding(false));

        // 同一份配置再写一份到 XDG 目录（.oc-config\opencode\opencode.json），由
        // ConfigureEnvironment 注入的 XDG_CONFIG_HOME 指过去。实测（2026-10-26）opencode 2.1.4
        // 的 project/workspace 解析在无 .git 的目录树上不稳定：同为 workspace\pipeline\task-N 的
        // cwd，有时解析为 task-N（加载项目级 opencode.json → MCP/deny 生效），有时向上解析为
        // Desktop\AgentCompany（配置全被忽略 → 无 company 工具、内置 write 直写逃逸）。全局配置
        // （XDG_CONFIG_HOME）必被加载，用它锚定；工作区根那份保留供人工检查与验收文件直证。
        var xdgConfigDir = Path.Combine(workspace, ".oc-config", "opencode");
        Directory.CreateDirectory(xdgConfigDir);
        File.WriteAllText(
            Path.Combine(xdgConfigDir, "opencode.json"),
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        // AGENTS.md 同理：project 解析飘走时工作区根的它读不到，global 位置（XDG 下）必读
        File.WriteAllText(Path.Combine(xdgConfigDir, "AGENTS.md"), AgentsMd, new UTF8Encoding(false));

        return (Arguments, new List<string>());
    }

    public void ConfigureEnvironment(System.Collections.Specialized.StringDictionary env, string workspace)
        => env["XDG_CONFIG_HOME"] = Path.Combine(workspace, ".oc-config");

    public void ParseLine(string line, ISessionSink sink)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            sink.Diag(trimmed);
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(trimmed);
        }
        catch (JsonException ex)
        {
            sink.Diag($"JSON 解析失败（{ex.Message}）：{Truncate(trimmed, 120)}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            {
                sink.Diag($"缺少 type 字段：{Truncate(trimmed, 120)}");
                return;
            }

            switch (typeEl.GetString())
            {
                case "step_start":
                    // 顶层 sessionID（无 init 事件，会话号在这里抓）
                    var sid = GetString(root, "sessionID");
                    if (!string.IsNullOrEmpty(sid))
                        sink.SetSession(sid);
                    break;

                case "text":
                    if (root.TryGetProperty("part", out var part) &&
                        part.TryGetProperty("text", out var textEl) &&
                        textEl.ValueKind == JsonValueKind.String)
                    {
                        var text = textEl.GetString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            sink.Text(text);
                            sink.SetReply(text); // 无 result 事件，最后一条 text 即最终回复
                        }
                    }
                    break;

                case "tool_use":
                    if (root.TryGetProperty("part", out var toolPart))
                        ParseToolUse(toolPart, sink);
                    break;

                case "step_finish":
                    if (root.TryGetProperty("part", out var finishPart) &&
                        finishPart.TryGetProperty("tokens", out var tokens) &&
                        tokens.ValueKind == JsonValueKind.Object)
                    {
                        long cacheRead = 0;
                        if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                            cacheRead = GetLong(cache, "read");
                        sink.AddUsage(GetLong(tokens, "input"), GetLong(tokens, "output"), cacheRead);
                    }
                    break;

                default:
                    // 未知 type：跳过并记诊断，绝不抛异常
                    sink.Diag($"未知 type={typeEl.GetString()}，已跳过：{Truncate(trimmed, 120)}");
                    break;
            }
        }
    }

    /// <summary>tool_use 展示：execute 展开内层 company.* 调用；其余直呼工具名的尝试（被 deny）标 ⛔。</summary>
    private static void ParseToolUse(JsonElement part, ISessionSink sink)
    {
        var tool = GetString(part, "tool") ?? "?";
        if (!part.TryGetProperty("state", out var state))
        {
            sink.Text($"🔧 {tool}");
            return;
        }
        var status = GetString(state, "status");

        if (tool == "execute")
        {
            // v2.1.4 的 MCP 调用走 JS 运行时，真实公司工具调用在 metadata.metadata.toolCalls 里
            if (state.TryGetProperty("metadata", out var m1) &&
                m1.ValueKind == JsonValueKind.Object &&
                m1.TryGetProperty("metadata", out var m2) &&
                m2.ValueKind == JsonValueKind.Object &&
                m2.TryGetProperty("toolCalls", out var calls) &&
                calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    var inner = GetString(call, "tool") ?? "?";
                    var arg = FirstInputString(call, "command", "path", "content");
                    sink.Text(arg is null
                        ? $"🔧 {inner}"
                        : $"🔧 {inner} → {Truncate(arg, 60)}");
                }
                return;
            }
            sink.Text($"🔧 execute（{status}）", dim: true);
            return;
        }

        if (status == "error")
        {
            // deny 掉的内置工具被强试时的形态（实测：No tool named "…" is currently available）
            var error = GetString(state, "error");
            sink.Text($"🔧 {tool} ⛔ {Truncate(error, 80)}");
            return;
        }
        sink.Text($"🔧 {tool}");
    }

    private static string? FirstInputString(JsonElement call, params string[] keys)
    {
        if (!call.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var key in keys)
        {
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string Truncate(string? s, int max) =>
        s is null ? string.Empty : s.Length <= max ? s : s[..max] + "…";
}

// ==================== crush ====================

/// <summary>
/// crush 适配器（轮 7，纯文本评审员，本机实测 v0.98.1）：
///  - 命令：cmd /c crush run --quiet（npm 垫片同 claude 须过 cmd；--quiet 只藏 spinner）；
///  - 任务文本走 stdin UTF-8（中文实测无损），输出为纯文本（无 JSON、无事件流、无 token 数据）；
///  - 不接公司 MCP 工具（官方 stdio MCP 的 JSON schema 文档不全，设计决策：crush 只用内置只读工具）；
///  - 计量：token 记 0，预算只走墙钟路（BudgetService 已有）；
///  - 权限封锁（.crushrc，项目级优先于全局，实测生效）：deny 全部写类与执行类工具
///    （deny = 从可用工具列表移除，强试报"不在可用工具列表"），allow 只读类；
///    .crushrc 与 CRUSH.md 写入工作区根且保留不删（便于排查）；
///  - 结果 = 全部 stdout 去首尾空白，由宿主代写成 review.md（受信操作，不走 ALLOWED_WRITES）。
/// </summary>
public sealed class CrushAdapter : IAgentAdapter
{
    public string Id => "crush";
    public string DisplayName => "crush";
    public string DefaultWorkspaceFolder => "crush";

    // 轮 8 可移植：crush 命令头可被设置表 crush_path 覆盖（默认按 PATH）
    private static string Arguments => $"/c {CliPaths.Command("crush")} run --quiet";

    /// <summary>
    /// 权限封锁名单（2026-10-29 本机实测定稿）：bash/edit/write 是基础写执行类；
    /// multiedit 是实测发现的绕道（deny edit 挡不住它，须单列）；patch/unified_edit/todowrite
    /// 同为写类；download 可从 URL 落盘文件；lsp_replace_symbol/lsp_rename 可改文件
    /// （实测穷尽绕道后补入）。余下工具（view/ls/grep/glob/fetch/agent 子代理等）均为只读。
    /// </summary>
    public const string DenyTools =
        "bash edit multiedit write patch todowrite unified_edit download lsp_replace_symbol lsp_rename";

    /// <summary>只读白名单（官方 README 示例语法：permissions allow &lt;tools&gt;）。</summary>
    public const string AllowTools = "view ls grep read glob";

    /// <summary>
    /// 行为约束（crush 自动读取项目根 CRUSH.md；v1.1 特性 A1：内容来源改 PromptStore，
    /// 外置 %APPDATA%\AgentCompany\prompts\crush.md 优先，带锚点校验与内置回退）。const → static 属性。
    /// </summary>
    private static string CrushMd => PromptStore.Get("crush");

    private readonly object _gate = new();
    private readonly StringBuilder _output = new();

    /// <summary>评审报告默认文件名（节点 write_targets 非空时由网关用其首个目标覆盖）。</summary>
    public const string DefaultReportName = "review.md";

    public (string Args, List<string> TempArtifacts) BuildArguments(string? bridgeExe, string workspace)
    {
        lock (_gate) _output.Clear();

        // .crushrc：项目级配置优先于全局（实测本目录 ./.crushrc 生效）；保留不删便于排查
        File.WriteAllText(Path.Combine(workspace, ".crushrc"),
            $"permissions deny {DenyTools}\npermissions allow {AllowTools}\n", new UTF8Encoding(false));
        // CRUSH.md：行为约束（crush 自动读取项目根 CRUSH.md，同 opencode 的 AGENTS.md）
        File.WriteAllText(Path.Combine(workspace, "CRUSH.md"), CrushMd, new UTF8Encoding(false));

        // crush 不接 MCP：忽略 bridgeExe，无临时文件
        return (Arguments, new List<string>());
    }

    /// <summary>纯文本输出：不上事件流（监视页显示占位文案），只累积供 FinalReply 取走。</summary>
    public void ParseLine(string line, ISessionSink sink)
    {
        if (string.IsNullOrEmpty(line))
            return;
        lock (_gate) _output.AppendLine(line);
    }

    /// <summary>全部 stdout 去首尾空白 = 最终评审报告。</summary>
    public string? FinalReply()
    {
        lock (_gate) return _output.ToString().Trim();
    }

    /// <summary>
    /// 报告整形（纯函数，单测直测）：剥掉包裹全文的单层 markdown 围栏（```markdown\n…\n```），
    /// 内部出现的围栏原样保留；空/纯空白输入返回空串（调用方据此跳过代写）。
    /// </summary>
    public static string ExtractReport(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return string.Empty;
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                var inner = text[(firstNewline + 1)..lastFence].Trim();
                // 内层还有非围栏内容才认为是"整体被包裹"，否则可能只是输出里恰有一行围栏
                if (inner.Length > 0)
                    return inner;
            }
        }
        return text;
    }
}
