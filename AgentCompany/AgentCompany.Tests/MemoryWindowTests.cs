using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// v1.1 特性 A3：公司记忆滚动窗口纯函数 TailMemory——尾部 N 行（≤0 = 全文 = v1.0 行为）、
/// 单行超 maxLine 截断加 …、null/空/全空白返回 null（既有语义）。
/// 直测 internal 纯函数（仿 StepStatusPhrase 的单测口径），不碰磁盘。
/// </summary>
public class MemoryWindowTests
{
    [Fact]
    public void 空输入_null或空白_返回null不截断()
    {
        Assert.Null(A2aGateway.TailMemory(null, 40, 500).Text);
        Assert.Null(A2aGateway.TailMemory("", 40, 500).Text);
        Assert.Null(A2aGateway.TailMemory("  \r\n \t \r\n", 40, 500).Text);
        Assert.False(A2aGateway.TailMemory(null, 40, 500).Truncated);
    }

    [Fact]
    public void 行数小于窗口_全文保留()
    {
        // 混合 CRLF/LF 输入：行内容原样保留，分隔统一回填 Environment.NewLine
        var (text, truncated) = A2aGateway.TailMemory("l1\r\nl2\nl3", 40, 500);
        Assert.False(truncated);
        Assert.Equal(string.Join(Environment.NewLine, new[] { "l1", "l2", "l3" }), text);
    }

    [Fact]
    public void 行数超过窗口_只取尾部N行()
    {
        var lines = Enumerable.Range(1, 5).Select(i => $"第{i}条记忆").ToList();
        var (text, truncated) = A2aGateway.TailMemory(string.Join("\n", lines), 2, 500);
        Assert.True(truncated);
        Assert.Equal(string.Join(Environment.NewLine, new[] { "第4条记忆", "第5条记忆" }), text);
    }

    [Fact]
    public void 单行超500字符_截断加省略号_恰好500不截()
    {
        var (cut, truncated) = A2aGateway.TailMemory(new string('甲', 600), 0, 500);
        Assert.True(truncated);
        Assert.Equal(new string('甲', 500) + "…", cut);

        var (exact, exactTruncated) = A2aGateway.TailMemory(new string('甲', 500), 0, 500);
        Assert.False(exactTruncated);
        Assert.Equal(new string('甲', 500), exact);
    }

    [Fact]
    public void 窗口0_全文不限_等价v10行为()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"第{i}行").ToList();
        var (text, truncated) = A2aGateway.TailMemory(string.Join("\n", lines) + "\n", 0, 500);
        Assert.False(truncated);
        Assert.Equal(string.Join(Environment.NewLine, lines), text); // 末尾换行不计行不入文
    }

    [Fact]
    public void 末尾空行_不计行也不入文()
    {
        var (text, truncated) = A2aGateway.TailMemory("a\nb\n\n\n", 40, 500);
        Assert.False(truncated);
        Assert.Equal(string.Join(Environment.NewLine, new[] { "a", "b" }), text);
    }

    [Fact]
    public void 窗口截断与单行截断_叠加生效()
    {
        var lines = new List<string> { new string('x', 600), new string('y', 600), "短行" };
        var (text, truncated) = A2aGateway.TailMemory(string.Join("\n", lines), 2, 500);
        Assert.True(truncated);
        Assert.Equal(
            string.Join(Environment.NewLine, new[] { new string('y', 500) + "…", "短行" }),
            text);
    }
}
