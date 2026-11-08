using System.Text;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// 轮 7 crush 结果处理：空输出 / 超长文本 / markdown 围栏整形 + 启动前封锁文件投放 + FinalReply 累积。
/// ExtractReport 与 BuildArguments 全部纯逻辑/临时目录，不真调 crush CLI。
/// </summary>
public class CrushResultTests
{
    private static string TempWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AgentCompany-tests",
            "crush-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void 空输出_ExtractReport返回空串()
    {
        Assert.Equal("", CrushAdapter.ExtractReport(null));
        Assert.Equal("", CrushAdapter.ExtractReport(""));
        Assert.Equal("", CrushAdapter.ExtractReport("   \r\n  \n")); // 纯空白同样视为无报告
    }

    [Fact]
    public void 超长文本_原样保留不截断()
    {
        var part = new string('评', 1000);
        var longText = string.Join("\n", Enumerable.Range(1, 120).Select(i => $"## 第{i}节\n{part}"));
        Assert.True(longText.Length > 100_000);

        var report = CrushAdapter.ExtractReport(longText);
        Assert.Equal(longText, report); // 评审报告多长都不截断（代写全文落 review.md）
        Assert.DoesNotContain("…", report);
    }

    [Fact]
    public void 外层围栏_剥掉单层包裹_内层围栏保留()
    {
        var body = "## 评审报告\n总体评价：良好。\n\n示例代码：\n```js\nconsole.log(1)\n```";
        var wrapped = "```markdown\n" + body + "\n```";

        Assert.Equal(body, CrushAdapter.ExtractReport(wrapped));     // 整体被包裹 → 剥掉外层
        Assert.Equal(body, CrushAdapter.ExtractReport(body));        // 无围栏 → 原样
        Assert.Equal(body, CrushAdapter.ExtractReport("  \n" + wrapped + "\n\n")); // 首尾空白先去掉再判

        // 恰好一行围栏开头但没有整体包裹：不误剥
        var loneFence = "```\n只有开头围栏没有结尾";
        Assert.Equal(loneFence, CrushAdapter.ExtractReport(loneFence));
    }

    [Fact]
    public void BuildArguments_投放crushrc与CRUSH_md_且不接MCP()
    {
        var workspace = TempWorkspace();
        try
        {
            var adapter = new CrushAdapter();
            var (args, temps) = adapter.BuildArguments(bridgeExe: @"C:\fake\McpBridge.exe", workspace);

            Assert.Equal("/c crush run --quiet", args);      // 无头调用，禁 --yolo（黑名单另测宿主审计）
            Assert.Empty(temps);                              // 不生成 mcp-config：crush 不接 MCP

            var rc = File.ReadAllText(Path.Combine(workspace, ".crushrc"));
            Assert.Contains("permissions deny", rc);
            Assert.Contains("bash", rc);                      // 执行类封锁
            Assert.Contains("multiedit", rc);                 // 实测绕道工具必须单列
            Assert.Contains("edit", rc);
            Assert.Contains("write", rc);
            Assert.Contains("permissions allow view ls grep read glob", rc); // 只读白名单

            Assert.True(File.Exists(Path.Combine(workspace, "CRUSH.md")));    // 行为约束（保留不删）
            var crushMd = File.ReadAllText(Path.Combine(workspace, "CRUSH.md"));
            Assert.Contains("评审报告", crushMd);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void FinalReply_累积全部stdout_重新BuildArguments清空()
    {
        var adapter = new CrushAdapter();
        var sink = new NopSink();

        adapter.ParseLine("# 评审报告", sink);
        adapter.ParseLine("", sink);                            // 空行跳过但仍保留段落结构
        adapter.ParseLine("总体评价：通过。", sink);

        var reply = adapter.FinalReply()!;
        Assert.StartsWith("# 评审报告", reply);
        Assert.EndsWith("总体评价：通过。", reply);
        Assert.Contains("\n", reply);                           // 多行累积不是一行拼接

        // 一次新任务：BuildArguments 重置累积，上一轮报告不能串台
        var workspace = TempWorkspace();
        try
        {
            adapter.BuildArguments(null, workspace);
            Assert.Equal("", adapter.FinalReply());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class NopSink : ISessionSink
    {
        public string AgentId => "test";
        public long TotalInput => 0;
        public long TotalOutput => 0;
        public long TotalCacheRead => 0;
        public void Text(string message, bool dim = false) { }
        public void Diag(string message) { }
        public void SetSession(string? sessionId) { }
        public void SetModel(string? model) { }
        public void SetReply(string? text) { }
        public void AddUsage(long inputTokens, long outputTokens, long cacheReadTokens) { }
        public void SetThinking(long estimatedTokens) { }
        public void SetDuration(long milliseconds) { }
    }
}
