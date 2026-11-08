using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AgentCompany.Desktop.Services;

/// <summary>
/// 提示词外置存储（v1.1 特性 A1/A2/A4）：统一提供四份提示词文本，"外置覆盖 + 安全回退"。
///  - 键→文件：planner→planner.md、claude→claude.md、opencode→opencode.md、crush→crush.md，
///    目录 %APPDATA%\AgentCompany\prompts\（本类只读不建目录——设置页负责生成，删除目录=恢复内置）；
///  - 三态：① 文件不存在 → 内置默认（等价 v1.0 行为）；② 文件存在但空/全空白/读取异常（含坏字节
///    解码失败）/锚点校验失败 → 内置默认 + 诊断警告；③ 合法 → 外置内容。绝不因外置文件坏而抛到调用方；
///  - 编码 UTF-8（StreamReader 检测，容忍 BOM；坏字节按状态 ② 回退）；
///  - 锚点校验（A2，防改坏）：格式契约（planner 的 JSON 骨架）+ 安全契约（员工"只走公司工具/只读"），
///    清单集中在 <see cref="AnchorTable"/>，单测直测；
///  - 进程内缓存解析结果；设置页保存后调 <see cref="InvalidateCache"/> 失效重载（各处临时文件写出口径不变）；
///  - 每次解析记 diag.log 一行（A4 留痕）：键、来源（built-in/external/fallback+原因）、SHA-256 前 8 位、
///    行数——排障可复现"当时用的是哪份词"。
/// </summary>
public static class PromptStore
{
    // ---- 内置默认（v1.0 四处 const 原文逐字搬入，外置文件不存在/非法时的最后防线） ----

    /// <summary>
    /// CEO 规划官系统提示词原文（原 CeoPlanner.PlannerSystemPrompt const；含轮 7 crush 评审岗、
    /// 工程样板归属、"≥2 节点须两员工分工"引导规则——宿主侧 EnsureTwoAgents 兜底）。
    /// v1.1 B4 追加末句：员工忙闲信息仅供拆解参考（stdin 里的【员工忙闲】节不改变依赖结构）。
    /// </summary>
    private const string PlannerDefault =
        """
        你是"小公司"的首席执行官(CEO)，把用户需求拆解成员工可执行的任务DAG。员工：claude（擅长复杂实现）、opencode（主力执行）、crush（测试评审岗：只读代码与产物，不写文件，最终输出中文评审报告，报告由系统自动代存为 review.md，不接公司工具）。只输出一个 JSON 对象，不要任何其他文字、解释或 markdown 围栏。格式：{"nodes":[{"id":"n1","title":"简短标题","agent":"claude或opencode或crush","prompt":"给该员工的完整任务指令（中文、自包含，员工看不到原始需求）","write_targets":["相对路径"],"depends_on":["前置节点id，无则空数组"]}]}。规则：节点总数1-5；agent 只能是 claude、opencode 或 crush；每个文件只能属于一个节点的 write_targets；工程样板与构建文件（如 __init__.py、package.json、pyproject.toml、requirements.txt、Makefile、各类配置/清单文件）同样是文件，必须纳入相应节点的 write_targets（归创建该工程的节点所有），漏分配会导致员工建不了工程骨架；depends_on 只能引用更早的节点，禁止循环；prompt 必须自包含；节点数≥2 时至少两名员工各承担一个节点（复杂实现优先给 claude，其余给 opencode）；凡需求涉及产出代码或交付物，最后一个节点应是 crush 评审节点（depends_on 指向实现节点，write_targets 为 ["review.md"]，prompt 要求输出含优点/问题/建议的中文评审报告）；crush 节点的 prompt 里不要要求它写文件或执行命令。员工忙闲信息仅供拆解参考：忙碌员工的节点照常安排，依赖结构不变。
        """;

    /// <summary>claude 员工追加系统提示原文（原 ClaudeAdapter.SystemPrompt const，经 --append-system-prompt-file 传入）。</summary>
    private const string ClaudeDefault =
        """
        你是"小公司"里的员工 claude，在宿主管控的工作区内干活，必须遵守：
        1. 读写文件一律使用 company 工具：file_read（读）、file_list（列目录）、file_write（写），路径一律用工作区内相对路径。
        2. 执行命令一律使用 company.terminal。dir、ls、git status、git diff、git add、git commit、npm test、npm run build、dotnet build、dotnet test、node -v 在白名单内可直接执行；其余命令（如 npm install）需要宿主审批，用户可能拒绝。
        3. 工具返回 SANDBOX_DENIED（路径越界）或 DENIED（命令被拒）时，不要原样重试同一路径或命令；改用别的方法（工作区内相对路径、白名单内命令），或直接向用户说明原因并结束当前步骤。
        4. 你的工作目录就是工作区根，所有产物都放在工作区内。
        """;

    /// <summary>opencode 行为约束原文（原 OpencodeAdapter.AgentsMd const，写工作区 AGENTS.md + XDG 双写）。</summary>
    private const string OpencodeDefault =
        """
        你是"小公司"里的员工 opencode，在宿主管控的工作区内干活，必须遵守：

        1. 读写文件一律使用 company 工具：file_read（读）、file_list（列目录）、file_write（写），路径一律用工作区内相对路径；不要使用内置的 write/edit 文件工具。
        2. 执行命令一律使用 company.terminal。dir、ls、git status、git diff、git add、git commit、npm test、npm run build、dotnet build、dotnet test、node -v 在白名单内可直接执行；其余命令（如 npm install）需要宿主审批，用户可能拒绝。
        3. 工具返回 SANDBOX_DENIED（路径越界）或 DENIED（命令被拒）时，不要原样重试同一路径或命令；改用别的方法（工作区内相对路径、白名单内命令），或直接向用户说明原因并结束当前步骤。
        4. 你的工作目录就是工作区根，所有产物都放在工作区内。
        """;

    /// <summary>crush 行为约束原文（原 CrushAdapter.CrushMd const，写工作区 CRUSH.md）。</summary>
    private const string CrushDefault =
        """
        你是"小公司"里的测试评审岗 crush，在宿主管控的工作区内做只读评审，必须遵守：

        1. 你没有写文件与执行命令的能力（已被 .crushrc 封锁）：只能用 view、ls、grep、glob 等只读工具阅读工作区内的代码与产物，不要尝试任何写文件或执行命令的工具（会被拒绝）。
        2. 你的最终回复就是评审报告：用简体中文，包含 总体评价 / 优点 / 问题与风险 / 改进建议 四个部分，直接输出报告全文；不要说"我已写入文件"之类的话（写文件由宿主代劳）。
        3. 若无法完成评审（如产物缺失），在报告里明确说明原因。
        """;

    /// <summary>
    /// 锚点校验表（A2，键→必含子串，全部命中外置文件才合法）：
    /// planner 是格式契约（缺一个 CEO 拆解必挂，"nodes" 连引号一起匹配防止把 JSON 根字段改没）；
    /// 其余三个是安全契约（约束词可润色，但不许把"只走公司工具/只读"的安全语义改没）。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AnchorTable { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["planner"] = new[] { "\"nodes\"", "depends_on", "write_targets" },
            ["claude"] = new[] { "company 工具", "file_write" },
            ["opencode"] = new[] { "company 工具", "file_write" },
            ["crush"] = new[] { "只读" },
        };

    private static readonly object _gate = new();
    private static readonly Dictionary<string, string> _cache = new();
    /// <summary>数据根目录注入（null = 默认 %APPDATA%\AgentCompany；仿 CliPaths.Init/Reset 的可测性口径）。</summary>
    private static string? _baseDirOverride;

    /// <summary>
    /// 取提示词（三态 + 进程内缓存）。未知键抛 ArgumentException（调用方都是固定四键）。
    /// 任何外置文件问题都在这里消化为"回退内置"，绝不抛给调用方。
    /// </summary>
    public static string Get(string key)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }
        var text = Resolve(key);
        lock (_gate) { _cache[key] = text; }
        return text;
    }

    /// <summary>内置默认文本（最后防线；设置页"恢复默认"展示与单测对照用）。</summary>
    public static string BuiltIn(string key) => key switch
    {
        "planner" => PlannerDefault,
        "claude" => ClaudeDefault,
        "opencode" => OpencodeDefault,
        "crush" => CrushDefault,
        _ => throw new ArgumentException($"未知提示词键：{key}"),
    };

    /// <summary>失效缓存（设置页保存外置文件后调用；测试隔离也用）。</summary>
    public static void InvalidateCache()
    {
        lock (_gate) { _cache.Clear(); }
    }

    /// <summary>
    /// 注入数据根目录（单测隔离用；null 恢复默认 %APPDATA%\AgentCompany）。
    /// 注入后 prompts\ 与 diag.log 全部落在该目录下，行为与默认完全一致——测试不碰真实 %APPDATA%。
    /// </summary>
    public static void SetBaseDirForTests(string? dir)
    {
        lock (_gate)
        {
            _baseDirOverride = dir;
            _cache.Clear(); // 目录变了，旧解析结果全部作废
        }
    }

    // ---- 三态解析（每次解析记一行诊断） ----

    private static string Resolve(string key)
    {
        var builtIn = BuiltIn(key); // 未知键在此抛出，后面都是合法键
        var fileName = FileName(key);
        var file = Path.Combine(PromptsDir(), fileName);

        byte[]? bytes = null;
        try
        {
            if (File.Exists(file))
                bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex)
        {
            return Fallback(key, builtIn, $"读取异常：{ex.Message}");
        }

        if (bytes is null)
        {
            // 状态①：文件不存在 → 内置默认（等价 v1.0 行为，常规留痕不算警告）
            Diag($"提示词 {key}：built-in（外置文件不存在 {fileName}）sha256={ShortHash(builtIn)} 行数={LineCount(builtIn)}");
            return builtIn;
        }

        string text;
        try
        {
            text = DecodeUtf8(bytes);
        }
        catch (Exception ex)
        {
            return Fallback(key, builtIn, $"解码失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(text))
            return Fallback(key, builtIn, "文件为空或全空白");

        var missing = MissingAnchors(key, text);
        if (missing.Count > 0)
            return Fallback(key, builtIn, $"锚点校验失败（缺少 {string.Join("、", missing)}）");

        // 状态③：合法 → 外置内容
        Diag($"提示词 {key}：external（{fileName}）sha256={ShortHash(text)} 行数={LineCount(text)}");
        return text;
    }

    /// <summary>状态②：存在但非法 → 内置默认 + 诊断警告（大声留痕但不拒绝启动）。</summary>
    private static string Fallback(string key, string builtIn, string reason)
    {
        Diag($"提示词 {key}：fallback→built-in（{reason}）sha256={ShortHash(builtIn)} 行数={LineCount(builtIn)}");
        return builtIn;
    }

    /// <summary>UTF-8 解码（StreamReader 检测并剥 BOM；坏字节抛 DecoderFallbackException，由调用方按状态②回退）。</summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        using var reader = new StreamReader(
            new MemoryStream(bytes),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static List<string> MissingAnchors(string key, string text)
    {
        var missing = new List<string>();
        foreach (var anchor in AnchorTable[key])
            if (!text.Contains(anchor, StringComparison.Ordinal))
                missing.Add(anchor);
        return missing;
    }

    private static string FileName(string key) => key + ".md";

    /// <summary>数据根目录：注入优先，默认 %APPDATA%\AgentCompany（prompts\ 与 diag.log 都在这里）。</summary>
    private static string BaseDir() => _baseDirOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentCompany");

    private static string PromptsDir() => Path.Combine(BaseDir(), "prompts");

    /// <summary>内容 SHA-256 前 8 位（A4 留痕：排障可对出"当时用的是哪份词"）。</summary>
    private static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8].ToLowerInvariant();

    private static int LineCount(string text) =>
        text.Length == 0 ? 0 : text.Split('\n').Length;

    /// <summary>诊断留痕：追加 baseDir\diag.log（格式仿 CeoPlanner.Diag；失败不影响取词）。</summary>
    private static void Diag(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(BaseDir(), "diag.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响取词
        }
    }
}
