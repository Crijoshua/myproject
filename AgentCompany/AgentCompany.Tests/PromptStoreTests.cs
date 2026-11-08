using System.Text;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// v1.1 特性 A1/A2：提示词外置三态（①不存在→内置；②空/坏字节/锚点缺→回退内置；③合法→外置）、
/// BOM 容忍、缓存与失效、四键独立、锚点表口径、诊断留痕。
/// 全部经 PromptStore.SetBaseDirForTests 注入临时数据根目录（prompts\ 与 diag.log 都落在里面），
/// finally 恢复默认——不碰真实 %APPDATA%。
/// </summary>
public class PromptStoreTests
{
    /// <summary>合法外置样本（各键锚点全部命中）。</summary>
    private const string ValidPlanner = "外置规划词：{\"nodes\":[…]} 含 depends_on 与 write_targets 骨架";
    private const string ValidClaude = "外置员工词：必须走 company 工具与 file_write";

    /// <summary>临时数据根目录（等价 %APPDATA%\AgentCompany 的注入替身）。</summary>
    private sealed class BaseDir : IDisposable
    {
        public string Dir { get; }
        public string Prompts { get; }

        public BaseDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), "AgentCompany-prompt-tests", Guid.NewGuid().ToString("N"));
            Prompts = Path.Combine(Dir, "prompts");
            Directory.CreateDirectory(Prompts);
            PromptStore.SetBaseDirForTests(Dir); // 注入即清缓存
        }

        /// <summary>键对应的外置文件路径（键→&lt;键&gt;.md）。</summary>
        public string FileOf(string key) => Path.Combine(Prompts, key + ".md");

        public string DiagLog => Path.Combine(Dir, "diag.log");

        public void Dispose()
        {
            PromptStore.SetBaseDirForTests(null); // 恢复默认
            try { Directory.Delete(Dir, recursive: true); } catch { /* 清理失败无碍 */ }
        }
    }

    /// <summary>取一次"新鲜"解析（先失效缓存再 Get；隔离并行用例可能写入的缓存条目）。</summary>
    private static string GetFresh(string key)
    {
        PromptStore.InvalidateCache();
        return PromptStore.Get(key);
    }

    // ---- 状态①：文件不存在 → 内置默认（等价 v1.0 行为） ----

    [Fact]
    public void 外置不存在_四键全走内置并留痕()
    {
        using var env = new BaseDir();
        Assert.Equal(PromptStore.BuiltIn("planner"), GetFresh("planner"));
        Assert.Equal(PromptStore.BuiltIn("claude"), GetFresh("claude"));
        Assert.Equal(PromptStore.BuiltIn("opencode"), GetFresh("opencode"));
        Assert.Equal(PromptStore.BuiltIn("crush"), GetFresh("crush"));

        var diag = File.ReadAllText(env.DiagLog);
        Assert.Contains("built-in", diag); // A4 留痕：来源可复现
        Assert.Contains("sha256=", diag);
    }

    // ---- 状态②：文件存在但非法 → 回退内置 + 诊断警告 ----

    [Fact]
    public void 外置为空文件_回退内置()
    {
        using var env = new BaseDir();
        File.WriteAllText(env.FileOf("planner"), "");
        Assert.Equal(PromptStore.BuiltIn("planner"), GetFresh("planner"));
        Assert.Contains("fallback", File.ReadAllText(env.DiagLog));
    }

    [Fact]
    public void 外置纯空白_回退内置()
    {
        using var env = new BaseDir();
        File.WriteAllText(env.FileOf("claude"), "   \r\n\t \r\n\u3000");
        Assert.Equal(PromptStore.BuiltIn("claude"), GetFresh("claude"));
    }

    [Fact]
    public void 外置坏字节_回退内置且不抛()
    {
        using var env = new BaseDir();
        // 乱序坏字节（非 BOM 开头）：0xFF/0xFE/0x00 混在合法 ASCII 中间，UTF-8 解码必失败
        System.IO.File.WriteAllBytes(env.FileOf("planner"),
            new byte[] { 0x61, 0x62, 0x63, 0xFF, 0xFE, 0x00, 0x78 });
        var text = GetFresh("planner");
        Assert.Equal(PromptStore.BuiltIn("planner"), text);
        Assert.Contains("fallback", File.ReadAllText(env.DiagLog));
    }

    [Fact]
    public void 锚点_planner缺nodes_拒绝外置()
    {
        using var env = new BaseDir();
        // depends_on 与 write_targets 还在，唯独没有 "nodes"（连引号的 JSON 根字段）
        File.WriteAllText(env.FileOf("planner"), "坏规划词：只剩 depends_on 与 write_targets 骨架");
        Assert.Equal(PromptStore.BuiltIn("planner"), GetFresh("planner"));
    }

    [Fact]
    public void 锚点_claude缺file_write_拒绝外置()
    {
        using var env = new BaseDir();
        // company 工具还在、唯独没有 file_write：证明是安全锚点（而非其他原因）拒绝的
        File.WriteAllText(env.FileOf("claude"), "员工词：company 工具还在但写文件入口没了");
        Assert.Equal(PromptStore.BuiltIn("claude"), GetFresh("claude"));
    }

    [Fact]
    public void 锚点_opencode缺company工具_拒绝外置()
    {
        using var env = new BaseDir();
        File.WriteAllText(env.FileOf("opencode"), "员工词：file_write 还在但不提公司工具了");
        Assert.Equal(PromptStore.BuiltIn("opencode"), GetFresh("opencode"));
    }

    [Fact]
    public void 锚点_crush缺只读_拒绝外置()
    {
        using var env = new BaseDir();
        File.WriteAllText(env.FileOf("crush"), "评审词：安全语义被改没了");
        Assert.Equal(PromptStore.BuiltIn("crush"), GetFresh("crush"));
    }

    // ---- 状态③：合法 → 外置内容生效 ----

    [Fact]
    public void 外置带BOM_可读且生效()
    {
        using var env = new BaseDir();
        File.WriteAllText(env.FileOf("claude"), ValidClaude,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.Equal(ValidClaude, GetFresh("claude"));
    }

    [Fact]
    public void 外置合法_生效且与文件内容一致()
    {
        using var env = new BaseDir();
        File.WriteAllText(env.FileOf("claude"), ValidClaude, new UTF8Encoding(false));
        Assert.Equal(ValidClaude, GetFresh("claude"));

        var diag = File.ReadAllText(env.DiagLog);
        Assert.Contains("external", diag); // A4 留痕
        Assert.Contains("行数=", diag);
    }

    // ---- 缓存（进程内缓存解析结果；设置页保存后 InvalidateCache 失效重载） ----

    [Fact]
    public void 缓存_进程内生效_InvalidateCache后改动可见()
    {
        using var env = new BaseDir();
        File.WriteAllText(env.FileOf("claude"), ValidClaude, new UTF8Encoding(false));
        PromptStore.InvalidateCache();
        Assert.Equal(ValidClaude, PromptStore.Get("claude"));

        // 文件改动后、未失效缓存前：仍读到旧解析（缓存命中）
        File.WriteAllText(env.FileOf("claude"),
            ValidClaude + "\n第二版：company 工具 + file_write", new UTF8Encoding(false));
        Assert.Equal(ValidClaude, PromptStore.Get("claude"));

        // 设置页保存后的失效口径：重读见到新文件
        PromptStore.InvalidateCache();
        var second = PromptStore.Get("claude");
        Assert.NotEqual(ValidClaude, second);
        Assert.Contains("第二版", second);
    }

    // ---- 四键互不影响 ----

    [Fact]
    public void 四键独立_仅claude外置其余内置()
    {
        using var env = new BaseDir();
        File.WriteAllText(env.FileOf("claude"), ValidClaude, new UTF8Encoding(false));
        PromptStore.InvalidateCache();
        Assert.Equal(ValidClaude, PromptStore.Get("claude"));                 // 唯一外置生效
        Assert.Equal(PromptStore.BuiltIn("planner"), PromptStore.Get("planner"));
        Assert.Equal(PromptStore.BuiltIn("opencode"), PromptStore.Get("opencode"));
        Assert.Equal(PromptStore.BuiltIn("crush"), PromptStore.Get("crush"));
    }

    // ---- 锚点表（只读，口径单测直测） ----

    [Fact]
    public void AnchorTable_四键锚点与规格一致()
    {
        var table = PromptStore.AnchorTable;
        Assert.Equal(new[] { "claude", "crush", "opencode", "planner" },
            table.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(new[] { "\"nodes\"", "depends_on", "write_targets" }, table["planner"]);
        Assert.Equal(new[] { "company 工具", "file_write" }, table["claude"]);
        Assert.Equal(new[] { "company 工具", "file_write" }, table["opencode"]);
        Assert.Equal(new[] { "只读" }, table["crush"]);
    }

    [Fact]
    public void 内置默认_全部通过自身锚点校验()
    {
        // 安全网：内置词自己必须永远过锚点（否则回退失去意义）
        foreach (var (key, anchors) in PromptStore.AnchorTable)
            foreach (var anchor in anchors)
                Assert.Contains(anchor, PromptStore.BuiltIn(key));
    }
}
