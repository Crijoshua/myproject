using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AgentCompany.Desktop.Services;

/// <summary>一次 CEO 拆解的结果（网关落库/预览用）。</summary>
public sealed record CeoOutcome(
    bool Success,
    List<DagNode>? Nodes,
    List<string> Errors,
    long Tokens,
    string RawReply);

/// <summary>
/// CEO 规划器（轮 6）：claude 换装规划模式，把一句话需求拆成任务 DAG。
/// 实测约束（任务书照抄）：
///  - 本机 --json-schema 失效（模型找不到 StructuredOutput 工具、自创格式、编员工名）→ 禁用，
///    结构化输出走三保险：严格系统提示词 + 宿主提取器 + JSON 解析与 DAG 校验；
///  - 校验失败把错误拼回重试 1 次；再失败任务标"拆解失败"，绝不无限重试；
///  - 调用参数：cmd /c claude -p --system-prompt-file &lt;规划官提示词UTF-8文件&gt; --restricted
///    --settings &lt;用户settings.json&gt;（新版 --restricted 不读用户级配置，须显式补传，见 RestrictedSettingsArgs）
///    --output-format json --verbose，不带 mcp-config（CEO 不用公司工具），需求经 stdin UTF-8；
///  - token 用量取 result 的 usage。
/// </summary>
public sealed class CeoPlanner : IDisposable
{
    /// <summary>
    /// 规划官系统提示词（任务书模板原文 + 追加规则；运行时生成 UTF-8 文件经 --system-prompt-file 传入）。
    /// 追加的"≥2 节点须两员工分工"：实测（2026-10-28 验收第 3 跑）模型心情好时全塞给 opencode，
    /// 验收 ① 的"分给两员工"不能赌心情——提示词先引导，宿主侧 EnsureTwoAgents 兜底。
    /// 轮 7：员工清单加 crush（测试评审岗）；评审节点提示它写清"输出中文评审报告"（宿主代存）。
    /// v1.1 特性 A1：内容来源改 PromptStore（外置 %APPDATA%\AgentCompany\prompts\planner.md 优先，
    /// 带锚点校验与内置回退；内置原文已逐字搬入 PromptStore）。名字与可见性保持不变，
    /// 唯一放宽：const → static 属性。
    /// </summary>
    public static string PlannerSystemPrompt => PromptStore.Get("planner");

    private readonly Database _db;
    private readonly IEventBus _bus;
    private readonly object _gate = new();
    private Process? _process;
    private CancellationTokenSource? _watchdog;
    private bool _running;
    private TaskCompletionSource<int> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsRunning { get { lock (_gate) return _running; } }

    public CeoPlanner(Database db, IEventBus bus)
    {
        _db = db;
        _bus = bus;
    }

    /// <summary>
    /// 拆解一条需求（含重试一次）：组装 stdin（需求 + 公司记忆）→ CEO 调用 → 提取 → 校验。
    /// 失败时 Errors 非空，任务由调用方标"拆解失败"。
    /// v1.1 B4：agentStatusText = 三员工忙闲快照（网关拆解前采集），拼进 stdin 尾部
    /// 【员工忙闲】节（可选参数零破坏——ComposeStdin 签名与既有测试调用不动）。
    /// </summary>
    public async Task<CeoOutcome> PlanAsync(int taskId, string demand, string? memoryText, string? agentStatusText = null)
    {
        // 轮 8：CEO 用的 claude 不可用直接判拆解失败（带明确原因），不烧一次注定失败的进程启动
        if (!AgentCliProcessHost.AvailabilityGate("claude"))
        {
            Diag("CEO 拆解拒绝启动：claude CLI 自检未通过（设置页检查路径覆盖后[重新自检]）");
            return new CeoOutcome(false, null,
                new List<string> { "claude CLI 不可用（自检未通过）：请到设置页检查路径覆盖并[重新自检]" }, 0, "");
        }

        var stdin1 = ComposeStdin(demand, memoryText) + AgentStatusSection(agentStatusText);
        // ⑥ 验收锚点：诊断日志可见 CEO 的 stdin 里包含 MEMORY.md 内容
        Diag($"CEO 拆解 stdin（任务#{taskId}）：{Truncate(stdin1, 400)}");

        var (exit1, stdout1) = await RunOnceAsync(taskId, stdin1);
        var attempt1 = ParseAndValidate(stdout1, exit1);
        if (attempt1.Success)
            return attempt1 with { Tokens = EnvelopeTokens(stdout1) }; // RawReply 已是信封内 result 文本

        // 校验失败：错误拼回重试 1 次（仅一次，绝不无限重试）
        var feedback = string.Join("；", attempt1.Errors);
        Diag($"CEO 拆解第 1 次输出不合格（{Truncate(feedback, 200)}），带错误反馈重试 1 次");
        var stdin2 = stdin1 + "\n\n【上次输出未通过校验，错误：" + feedback + "。请严格只输出一个 JSON 对象，不要任何其他文字或围栏】";
        var (exit2, stdout2) = await RunOnceAsync(taskId, stdin2);
        var attempt2 = ParseAndValidate(stdout2, exit2);
        // 两次调用都入账（预算口径：含 CEO 调用；失败重试的 token 不能白嫖）
        return attempt2 with { Tokens = EnvelopeTokens(stdout1) + EnvelopeTokens(stdout2) };
    }

    /// <summary>
    /// 宿主保底：≥2 节点却只用了单一员工时，把"最重"的节点（write_targets 最多，平手取
    /// prompt 更长，再平手取更早）调给缺席员工。只改归属不动结构（write_targets/依赖/顺序
    /// 全保留）；返回留痕说明（null = 无需调整）。轮 7 缺席者按白名单序取（claude→opencode→crush）。
    /// </summary>
    public static (List<DagNode> Nodes, string? Note) EnsureTwoAgents(IReadOnlyList<DagNode> nodes)
    {
        if (nodes.Count < 2)
            return (nodes.ToList(), null);
        var used = nodes.Select(n => n.Agent).Distinct(StringComparer.Ordinal).ToList();
        if (used.Count >= 2)
            return (nodes.ToList(), null);

        var missing = DagValidator.AllowedAgents.First(a => !used.Contains(a));
        var pick = nodes
            .OrderByDescending(n => n.WriteTargets.Count)
            .ThenByDescending(n => n.Prompt.Length)
            .ThenBy(n => n.Seq)
            .First();
        var adjusted = nodes.Select(n => n.NodeId == pick.NodeId ? n with { Agent = missing } : n).ToList();
        return (adjusted, $"{pick.NodeId} 由 {pick.Agent} 调整为 {missing}（宿主保底：≥2 节点须至少两名员工分工）");
    }

    /// <summary>
    /// 宿主保底（v1.1 B3 负载感知再平衡，PlanAsync 在 EnsureTwoAgents 之后调用）：
    /// ≥3 节点且某员工承担 &gt;2/3 节点时，把最重的多余节点（write_targets 最多，平手取
    /// prompt 更长，再平手取 seq 更早）依次调给空闲员工（白名单序 claude→opencode→crush），
    /// 直到没有员工超过 2/3。只改 Agent 归属，不动 write_targets/依赖/顺序；返回留痕说明
    /// （null = 无需调整；多条调整以「；」相连，文案格式与 <see cref="EnsureTwoAgents"/> 相同）。
    /// </summary>
    public static (List<DagNode> Nodes, string? Note) EnsureBalanced(IReadOnlyList<DagNode> nodes)
    {
        if (nodes.Count < 3)
            return (nodes.ToList(), null);

        var adjusted = nodes.ToList();
        var notes = new List<string>();
        while (true)
        {
            var counts = adjusted.GroupBy(n => n.Agent, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var (over, count) = counts.MaxBy(kv => kv.Value);
            if (count * 3 <= nodes.Count * 2)
                break; // 最重员工 ≤ 2/3：平衡达成（或从未失衡）
            var idle = DagValidator.AllowedAgents.FirstOrDefault(a => !counts.ContainsKey(a));
            if (idle is null)
                break; // 无空闲员工可调（防御：≤5 节点三员工全上场不会超 2/3）

            var pick = adjusted.Where(n => n.Agent == over)
                .OrderByDescending(n => n.WriteTargets.Count)
                .ThenByDescending(n => n.Prompt.Length)
                .ThenBy(n => n.Seq)
                .First();
            adjusted[adjusted.FindIndex(n => n.NodeId == pick.NodeId)] = pick with { Agent = idle };
            notes.Add($"{pick.NodeId} 由 {over} 调整为 {idle}（宿主保底：单一员工承担超过 2/3 节点，负载再平衡）");
        }
        return (adjusted, notes.Count == 0 ? null : string.Join("；", notes));
    }

    /// <summary>stdin 组装：【用户需求】+ 原文 +【公司记忆 MEMORY.md】+ 内容（记忆为空则省略）。</summary>
    public static string ComposeStdin(string demand, string? memoryText) =>
        string.IsNullOrWhiteSpace(memoryText)
            ? $"【用户需求】\n{demand.Trim()}"
            : $"【用户需求】\n{demand.Trim()}\n\n【公司记忆 MEMORY.md】\n{memoryText.Trim()}";

    /// <summary>
    /// 【员工忙闲】节（v1.1 B4，纯函数 internal 供单测）：忙闲快照拼在 stdin 最尾；
    /// 快照为空/全空白省略该节。不改 ComposeStdin（既有测试调用零破坏）。
    /// </summary>
    internal static string AgentStatusSection(string? agentStatusText) =>
        string.IsNullOrWhiteSpace(agentStatusText)
            ? ""
            : $"\n\n【员工忙闲】\n{agentStatusText.Trim()}";

    /// <summary>
    /// 三保险第二层：宿主提取器。先剥 ```json 围栏，否则取首个 { 到最后一个 }。
    /// 提不出返回 null（第三层 JSON 解析会给出可读错误）。
    /// </summary>
    public static string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            // ```json\n{...}\n``` → 去首行围栏与尾围栏
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
                text = text[(firstNewline + 1)..];
            var fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
                text = text[..fence];
            text = text.Trim();
        }
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last <= first)
            return null;
        return text[first..(last + 1)];
    }

    /// <summary>三保险第三层：JSON 解析 + 字段读取 + DAG 校验（agent 白名单在这里兜住编造的员工名）。</summary>
    public static CeoOutcome ParseAndValidate(string stdout, int exitCode)
    {
        var raw = ExtractEnvelopeResult(stdout);
        var errors = new List<string>();
        if (exitCode != 0)
            errors.Add($"claude 退出码 {exitCode}");
        if (string.IsNullOrWhiteSpace(raw))
        {
            errors.Add("模型没有输出任何内容");
            return new CeoOutcome(false, null, errors, 0, raw ?? "");
        }

        var json = ExtractJson(raw);
        if (json is null)
        {
            errors.Add($"输出里找不到 JSON 对象（原文：{Truncate(raw, 120)}）");
            return new CeoOutcome(false, null, errors, 0, raw);
        }

        List<(string, string, string, string, IReadOnlyList<string>, IReadOnlyList<string>)> nodesRaw;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("nodes", out var nodesEl) || nodesEl.ValueKind != JsonValueKind.Array)
                return Bad($"JSON 根对象缺少 nodes 数组（原文：{Truncate(json, 120)}）", raw);

            nodesRaw = new();
            foreach (var n in nodesEl.EnumerateArray())
            {
                if (n.ValueKind != JsonValueKind.Object)
                    return Bad($"nodes 里有非对象条目（原文：{Truncate(json, 120)}）", raw);
                nodesRaw.Add((
                    Str(n, "id") ?? "",
                    Str(n, "title") ?? "",
                    Str(n, "agent") ?? "",
                    Str(n, "prompt") ?? "",
                    StrArray(n, "write_targets"),
                    StrArray(n, "depends_on")));
            }
        }
        catch (JsonException ex)
        {
            return Bad($"JSON 解析失败（{ex.Message}；原文：{Truncate(json, 160)}）", raw);
        }

        var (nodes, validateErrors) = DagValidator.Validate(nodesRaw);
        if (nodes is null || errors.Count > 0)
            return new CeoOutcome(false, null, errors.Concat(validateErrors).ToList(), 0, raw);
        return new CeoOutcome(true, nodes, errors, 0, raw);

        CeoOutcome Bad(string message, string rawText) =>
            new(false, null, errors.Append(message).ToList(), 0, rawText);
    }

    /// <summary>
    /// claude -p --output-format json --verbose 的 stdout 实测不是单信封：--verbose 会在 result
    /// 信封前先来一行 system/init 信封（两段 JSON，甚至成数组）。逐行找含 result 字段的 JSON 对象；
    /// 整体解析作兜底（无 --verbose 时单行信封照走）；都失败整段当模型原文（提取器自己会找 {...}）。
    /// </summary>
    public static (string Text, long Tokens) ParseEnvelope(string stdout)
    {
        // 策略一：逐行解析，取含字符串 result 字段的那行信封
        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (ReadEnvelope(doc.RootElement) is { } hit)
                    return hit;
            }
            catch (JsonException)
            {
                // 诊断/中断行不是完整 JSON：跳过
            }
        }
        // 策略二：整体解析（单信封原文，或 [{init},{result}] 数组形态）
        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;
            if (ReadEnvelope(root) is { } single)
                return single;
            if (root.ValueKind == JsonValueKind.Array)
                foreach (var el in root.EnumerateArray())
                    if (ReadEnvelope(el) is { } item)
                        return item;
        }
        catch
        {
            // 信封解析失败：整段当模型原文（提取器自己会找 {...}）
        }
        return (stdout, 0);
    }

    private static (string Text, long Tokens)? ReadEnvelope(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        if (!el.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.String)
            return null;
        long tokens = 0;
        if (el.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            tokens = GetLong(u, "input_tokens") + GetLong(u, "output_tokens");
        return (r.GetString() ?? "", tokens);
    }

    private static string ExtractEnvelopeResult(string stdout)
    {
        var (text, _) = ParseEnvelope(stdout);
        return text;
    }

    /// <summary>提取器可见的 token 用量（信封 usage；解析失败为 0，由调用方记录）。</summary>
    public static long EnvelopeTokens(string stdout)
    {
        var (_, tokens) = ParseEnvelope(stdout);
        return tokens;
    }

    // ---- 进程托管（cmd /c claude，stdin UTF-8，Job Object 陪葬 + journal 记账 + 看门狗） ----

    private async Task<(int ExitCode, string Stdout)> RunOnceAsync(int taskId, string stdin)
    {
        var promptFile = Path.Combine(Path.GetTempPath(), "AgentCompany",
            $"ceo-prompt-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(promptFile)!);
        await File.WriteAllTextAsync(promptFile, PlannerSystemPrompt, new UTF8Encoding(false));
        try
        {
            return await RunProcessAsync(taskId, stdin, promptFile);
        }
        finally
        {
            try { File.Delete(promptFile); } catch { /* 临时文件删不掉无碍 */ }
        }
    }

    /// <summary>
    /// claude ≥2.1.314 起 --restricted 忽略用户级/项目级/本地 settings 文件（实测 2026-10-30：
    /// apiKeySource=none、模型还原成官方名，输出"Not logged in · Please run /login"），但
    /// --settings 显式传入仍生效——把 %USERPROFILE%\.claude\settings.json（智谱端点/token/
    /// 模型映射的 env 块）原样补传恢复鉴权。文件不存在返回空串（走官方 OAuth 登录的机器不受影响）。
    /// </summary>
    private static string RestrictedSettingsArgs()
    {
        var userSettings = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
        return File.Exists(userSettings) ? $" --settings \"{userSettings}\"" : "";
    }

    private async Task<(int ExitCode, string Stdout)> RunProcessAsync(int taskId, string stdin, string promptFile)
    {
        // 轮 8 可移植：CEO 的 claude 命令头同样走覆盖项/PATH
        // claude ≥2.1.314：--restricted 忽略用户级 settings（鉴权/端点/模型全丢 → Not logged in），
        // --settings 显式补传用户 settings.json 恢复（见 RestrictedSettingsArgs）
        var arguments = $"/c {CliPaths.Command("claude")} -p --system-prompt-file \"{promptFile}\" --restricted{RestrictedSettingsArgs()} --output-format json --verbose";
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        var stdoutBuilder = new StringBuilder();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_gate) stdoutBuilder.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Diag("[stderr] " + e.Data); };
        proc.Exited += (_, _) =>
        {
            try { proc.WaitForExit(); } catch { /* 已退出 */ }
            tcs.TrySetResult(SafeExitCode(proc));
        };

        lock (_gate)
        {
            if (_running)
                throw new InvalidOperationException("CEO 规划已在运行（单实例）");
            _running = true;
            _process = proc;
            _done = tcs;
        }

        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.AgentStatus, "CEO 拆解中"));
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.Task,
            $"🧠 任务#{taskId} CEO 拆解中…"));

        try
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Diag($"CEO 进程启动失败：{ex.Message}");
            lock (_gate) { _running = false; _process = null; }
            return (-1, "");
        }

        try { JobObject.Assign(proc); } catch (Exception ex) { Diag($"CEO Job 挂入失败（{ex.Message}）：依赖启动清扫兜底"); }
        long journalRow = 0;
        try { journalRow = _db.InsertJournal(taskId, null, "claude", proc.Id); }
        catch (Exception ex) { Diag($"CEO process_journal 记账失败：{ex.Message}"); }

        StartWatchdog(proc);

        // 需求经 stdin UTF-8 传入（中文不进命令行，防 GBK 乱码）
        try
        {
            await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            Diag($"CEO stdin 写入失败：{ex.Message}");
        }

        var exitCode = await tcs.Task;
        _watchdog?.Cancel();

        if (journalRow != 0)
        {
            try { _db.MarkJournalCleaned(journalRow); } catch { /* 销账失败留给清扫 */ }
        }

        string stdout;
        lock (_gate)
        {
            stdout = stdoutBuilder.ToString();
            _running = false;
            _process = null;
        }
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.AgentStatus, "空闲"));
        try { proc.Dispose(); } catch { }
        return (exitCode, stdout);
    }

    /// <summary>看门狗：墙钟超时杀树（审批等待暂停规则不适用——CEO 不走审批门）。</summary>
    private void StartWatchdog(Process proc)
    {
        var timeout = 180;
        if (int.TryParse(_db.GetSetting(AgentCliProcessHost.TimeoutSettingKey), out var v) && v >= 1)
            timeout = v;
        var cts = new CancellationTokenSource();
        lock (_gate) _watchdog = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < timeout; i++)
                    await Task.Delay(1000, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Diag($"CEO 看门狗：超过 {timeout}s，终止进程树");
            KillTree(proc);
        });
    }

    /// <summary>手动终止（优雅退出 / 熔断用）。</summary>
    public void Cancel()
    {
        Process? proc;
        lock (_gate)
        {
            if (!_running || _process is null)
                return;
            proc = _process;
        }
        Diag("CEO 手动终止：杀进程树");
        KillTree(proc);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false })
                KillTree(_process);
        }
    }

    private static void KillTree(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskkill", $"/PID {proc.Id} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit(5000);
            }
            catch { /* 双兜底都失败：进程自会退出 */ }
        }
    }

    private static int SafeExitCode(Process proc)
    {
        try { return proc.ExitCode; } catch { return -1; }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static List<string> StrArray(JsonElement el, string name)
    {
        var list = new List<string>();
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    list.Add(s);
        return list;
    }

    private static string Truncate(string? s, int max) =>
        s is null ? "" : s.Length <= max ? s : s[..max] + "…";

    private void Diag(string message)
    {
        _bus.Publish(new AppEvent(DateTime.Now, EventCategories.AgentDiag, message, Agent: "claude"));
        try
        {
            // 与 AgentCliProcessHost 同一落盘口径（⑥ 验收：诊断日志可见 CEO stdin 含公司记忆）
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AgentCompany", "diag.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响拆解
        }
    }
}
