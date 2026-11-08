using System.Text.Json;
using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// CEO 输出的三保险提取与信封解析（轮 6 任务书要求 ≥4 例）：
/// 宿主提取器剥围栏/抓首尾大括号；claude -p --output-format json 的 result 信封；
/// 校验失败给可读错误（拼回重试用）。
/// </summary>
public class CeoPlannerTests
{
    private const string GoodDag =
        """{"nodes":[{"id":"n1","title":"页面","agent":"claude","prompt":"写网页","write_targets":["index.html"],"depends_on":[]}]}""";

    // ---- 提取器 ----

    [Fact]
    public void 提取_裸JSON_原样返回()
    {
        Assert.Equal(GoodDag, CeoPlanner.ExtractJson(GoodDag));
    }

    [Fact]
    public void 提取_markdown围栏_剥掉围栏()
    {
        var fenced = "```json\n" + GoodDag + "\n```";
        Assert.Equal(GoodDag, CeoPlanner.ExtractJson(fenced));
    }

    [Fact]
    public void 提取_带前后言_抓首个左括号到最后右括号()
    {
        var chatty = "好的，这是拆解结果：\n" + GoodDag + "\n希望对你有帮助。";
        Assert.Equal(GoodDag, CeoPlanner.ExtractJson(chatty));
    }

    [Fact]
    public void 提取_完全没有JSON_返回null()
    {
        Assert.Null(CeoPlanner.ExtractJson("抱歉，我无法完成这个请求。"));
        Assert.Null(CeoPlanner.ExtractJson(""));
    }

    // ---- 信封 + 解析校验 ----

    private static string Envelope(string result, long inputTokens = 100, long outputTokens = 50) =>
        JsonSerializer.Serialize(new { type = "result", result, usage = new { input_tokens = inputTokens, output_tokens = outputTokens } });

    [Fact]
    public void 解析_标准信封_取result文本并产出节点()
    {
        var outcome = CeoPlanner.ParseAndValidate(Envelope(GoodDag), exitCode: 0);
        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Nodes);
        Assert.Single(outcome.Nodes!);
        Assert.Equal("n1", outcome.Nodes![0].NodeId);
        Assert.Equal("claude", outcome.Nodes[0].Agent);
        Assert.Equal(new[] { "index.html" }, outcome.Nodes[0].WriteTargets);
        Assert.Empty(outcome.Nodes[0].DependsOn);
    }

    [Fact]
    public void 解析_信封内的围栏输出_照样提到()
    {
        var fenced = "```json\n" + GoodDag + "\n```";
        var outcome = CeoPlanner.ParseAndValidate(Envelope(fenced), exitCode: 0);
        Assert.True(outcome.Success);
        Assert.Single(outcome.Nodes!);
    }

    [Fact]
    public void 解析_坏JSON_给可读错误供重试()
    {
        var outcome = CeoPlanner.ParseAndValidate(Envelope("{nodes: [}}, 不是合法 JSON"), exitCode: 0);
        Assert.False(outcome.Success);
        Assert.Contains(outcome.Errors, e => e.Contains("JSON 解析失败"));
    }

    [Fact]
    public void 解析_编造员工名_校验层拦下()
    {
        // 轮 7 起 crush 是合法员工，编造名换 "grok"
        var bad = """{"nodes":[{"id":"n1","title":"t","agent":"grok","prompt":"p","write_targets":[],"depends_on":[]}]}""";
        var outcome = CeoPlanner.ParseAndValidate(Envelope(bad), exitCode: 0);
        Assert.False(outcome.Success);
        Assert.Contains(outcome.Errors, e => e.Contains("agent 非法"));
    }

    [Fact]
    public void 解析_非零退出码_记入错误()
    {
        var outcome = CeoPlanner.ParseAndValidate(Envelope(GoodDag), exitCode: 2);
        Assert.False(outcome.Success);
        Assert.Contains(outcome.Errors, e => e.Contains("退出码 2"));
    }

    [Fact]
    public void 信封_token用量_从usage取()
    {
        var (text, tokens) = CeoPlanner.ParseEnvelope(Envelope(GoodDag, inputTokens: 120, outputTokens: 80));
        Assert.Equal(GoodDag, text);
        Assert.Equal(200, tokens);
    }

    // ---- stdin 组装 ----

    [Fact]
    public void stdin_有公司记忆_拼入MEMORY段落()
    {
        var stdin = CeoPlanner.ComposeStdin("做一个网页", "2026-10-27 #1 夜间模式 · 产物：index.html");
        Assert.Contains("【用户需求】", stdin);
        Assert.Contains("做一个网页", stdin);
        Assert.Contains("【公司记忆 MEMORY.md】", stdin);
        Assert.Contains("夜间模式", stdin);
    }

    [Fact]
    public void stdin_无公司记忆_省略记忆段落()
    {
        var stdin = CeoPlanner.ComposeStdin("做一个网页", null);
        Assert.Contains("【用户需求】", stdin);
        Assert.DoesNotContain("【公司记忆 MEMORY.md】", stdin);
    }

    // ---- 两员工分工保底（验收 ①c：分给两员工不能赌模型心情） ----

    private static DagNode Node(int seq, string id, string agent, int writes, int promptLen = 10, params string[] depends) =>
        new(seq, id, id, agent, new string('p', promptLen),
            Enumerable.Range(0, writes).Select(i => $"{id}-{i}.txt").ToList(), depends);

    [Fact]
    public void 保底_单一员工多节点_最重节点调给缺席员工()
    {
        var nodes = new List<DagNode>
        {
            Node(1, "n1", "opencode", writes: 1),
            Node(2, "n2", "opencode", writes: 3, depends: "n1"),
            Node(3, "n3", "opencode", writes: 2, depends: "n1"),
        };
        var (adjusted, note) = CeoPlanner.EnsureTwoAgents(nodes);
        Assert.NotNull(note);
        Assert.Contains("n2", note);
        Assert.Equal("claude", adjusted.Single(n => n.NodeId == "n2").Agent);
        // 结构不动：其余归属、写目标、依赖全保留
        Assert.All(adjusted.Where(n => n.NodeId != "n2"), n => Assert.Equal("opencode", n.Agent));
        Assert.Equal(3, adjusted.Single(n => n.NodeId == "n2").WriteTargets.Count);
        Assert.Equal(new[] { "n1" }, adjusted.Single(n => n.NodeId == "n2").DependsOn);
    }

    [Fact]
    public void 保底_已两员工_原样返回()
    {
        var nodes = new List<DagNode>
        {
            Node(1, "n1", "claude", writes: 2),
            Node(2, "n2", "opencode", writes: 1, depends: "n1"),
        };
        var (adjusted, note) = CeoPlanner.EnsureTwoAgents(nodes);
        Assert.Null(note);
        Assert.Equal(nodes, adjusted);
    }

    [Fact]
    public void 保底_单节点_不调整()
    {
        var nodes = new List<DagNode> { Node(1, "n1", "opencode", writes: 1) };
        var (adjusted, note) = CeoPlanner.EnsureTwoAgents(nodes);
        Assert.Null(note);
        Assert.Single(adjusted);
        Assert.Equal("opencode", adjusted[0].Agent);
    }
}
