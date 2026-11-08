using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// DAG 校验器与波次调度（轮 6 任务书要求 ≥6 校验例 + ≥2 调度例）：
/// 节点数 1-5 / agent 白名单 / 依赖只引用更早节点（无环）/ write_targets 跨节点不重叠；
/// 调度 = 依赖满足 + 所有权空闲 + 并发 ≤2。
/// </summary>
public class DagSpecTests
{
    private static (string, string, string, string, IReadOnlyList<string>, IReadOnlyList<string>) Node(
        string id, string agent = "claude", string prompt = "做事",
        string[]? writes = null, string[]? deps = null) =>
        (id, $"{id} 标题", agent, prompt, writes ?? Array.Empty<string>(), deps ?? Array.Empty<string>());

    // ---- 校验器 ----

    [Fact]
    public void 校验_合法DAG_两员工两节点依赖且所有权分离()
    {
        var (nodes, errors) = DagValidator.Validate(new[]
        {
            Node("n1", "claude", writes: new[] { "index.html", "styles.css" }),
            Node("n2", "opencode", writes: new[] { "README.md" }, deps: new[] { "n1" }),
        });
        Assert.Empty(errors);
        Assert.NotNull(nodes);
        Assert.Equal(2, nodes!.Count);
        Assert.Equal(1, nodes[0].Seq);
        Assert.Equal(2, nodes[1].Seq);
        Assert.Equal("opencode", nodes[1].Agent);
    }

    [Fact]
    public void 校验_自依赖_报错()
    {
        var (_, errors) = DagValidator.Validate(new[] { Node("n1", deps: new[] { "n1" }) });
        Assert.Contains(errors, e => e.Contains("依赖自己"));
    }

    [Fact]
    public void 校验_依赖不存在的更晚节点_按悬空依赖报错()
    {
        // depends_on 只允许引用更早节点：n1 依赖 n2 时 seenIds 里还没有 n2
        var (_, errors) = DagValidator.Validate(new[]
        {
            Node("n1", deps: new[] { "n2" }),
            Node("n2"),
        });
        Assert.Contains(errors, e => e.Contains("不存在的节点"));
    }

    [Fact]
    public void 校验_编造的员工名_白名单拦下()
    {
        // 轮 7 起 crush 是合法员工，编造名换 "grok"
        var (_, errors) = DagValidator.Validate(new[] { Node("n1", agent: "grok") });
        Assert.Contains(errors, e => e.Contains("agent 非法") && e.Contains("grok"));
    }

    [Fact]
    public void 校验_同一文件归两个节点_所有权重叠报错()
    {
        var (_, errors) = DagValidator.Validate(new[]
        {
            Node("n1", writes: new[] { "README.md" }),
            Node("n2", writes: new[] { "readme.md" }), // 大小写与斜杠归一后仍冲突
        });
        Assert.Contains(errors, e => e.Contains("同时属于") && e.Contains("n1") && e.Contains("n2"));
    }

    [Fact]
    public void 校验_六个节点_超出上限报错()
    {
        var raw = Enumerable.Range(1, 6)
            .Select(i => Node($"n{i}"))
            .ToArray();
        var (_, errors) = DagValidator.Validate(raw);
        Assert.Contains(errors, e => e.Contains("超出 1-5"));
    }

    [Fact]
    public void 校验_空prompt报错()
    {
        var (_, errors) = DagValidator.Validate(new[] { Node("n1", prompt: " ") });
        Assert.Contains(errors, e => e.Contains("prompt 为空"));
    }

    // ---- 调度器 ----

    [Fact]
    public void 调度_链式依赖_一次只放行链头()
    {
        var nodes = new[]
        {
            Node("n1", writes: new[] { "a.txt" }),
            Node("n2", writes: new[] { "b.txt" }, deps: new[] { "n1" }),
            Node("n3", writes: new[] { "c.txt" }, deps: new[] { "n2" }),
        };
        var (dag, errors) = DagValidator.Validate(nodes);
        Assert.Empty(errors);
        var pending = dag!.ToList();

        var ready = DagScheduler.SelectReady(pending, new HashSet<string>(), new HashSet<string>(),
            dag!, runningCount: 0, maxConcurrent: 2);
        Assert.Equal(new[] { "n1" }, ready.Select(n => n.NodeId));

        // n1 完成后 n2 放行
        var done = new HashSet<string> { "n1" };
        ready = DagScheduler.SelectReady(pending.Skip(1).ToList(), done, new HashSet<string>(),
            dag!, runningCount: 0, maxConcurrent: 2);
        Assert.Equal(new[] { "n2" }, ready.Select(n => n.NodeId));
    }

    [Fact]
    public void 调度_三个独立节点_并发上限2只放行两个()
    {
        var nodes = new[]
        {
            Node("n1", agent: "claude", writes: new[] { "a.txt" }),
            Node("n2", agent: "opencode", writes: new[] { "b.txt" }),
            Node("n3", agent: "claude", writes: new[] { "c.txt" }),
        };
        var (dag, errors) = DagValidator.Validate(nodes);
        Assert.Empty(errors);

        var ready = DagScheduler.SelectReady(dag!.ToList(), new HashSet<string>(), new HashSet<string>(),
            dag, runningCount: 0, maxConcurrent: 2);
        Assert.Equal(new[] { "n1", "n2" }, ready.Select(n => n.NodeId)); // 按 seq 序填满 2 槽

        // n1 在跑时（占 1 槽）：n3 可补位
        ready = DagScheduler.SelectReady(new[] { dag[2] }, new HashSet<string>(),
            new HashSet<string> { "n1" }, dag, runningCount: 1, maxConcurrent: 2);
        Assert.Equal(new[] { "n3" }, ready.Select(n => n.NodeId));
    }

    [Fact]
    public void 调度_所有权被在跑节点占用_断言不满足不放行()
    {
        // 校验通过的 DAG 所有权本就不重叠：此断言是调度器的防御路径（防绕过校验的输入），
        // 测试直接构造 DagNode 绕过 Validate
        var dag = new[]
        {
            new DagNode(1, "n1", "n1", "claude", "p", new[] { "a.txt" }, Array.Empty<string>()),
            new DagNode(2, "n2", "n2", "opencode", "p", new[] { "b.txt" }, Array.Empty<string>()),
            new DagNode(3, "n3", "n3", "claude", "p", new[] { "a.txt" }, Array.Empty<string>()),
        };

        // n1 在跑占用 a.txt：n3 不能启动（依赖满足也没用），n2 照常
        var ready = DagScheduler.SelectReady(new[] { dag[1], dag[2] }, new HashSet<string>(),
            new HashSet<string> { "n1" }, dag, runningCount: 1, maxConcurrent: 2);
        Assert.Equal(new[] { "n2" }, ready.Select(n => n.NodeId));
    }

    [Fact]
    public void 调度_下游取消_传递闭包正确()
    {
        var (dag, errors) = DagValidator.Validate(new[]
        {
            Node("n1"),
            Node("n2", deps: new[] { "n1" }),
            Node("n3", deps: new[] { "n1" }),
            Node("n4", deps: new[] { "n3" }),
        });
        Assert.Empty(errors);
        var downstream = DagScheduler.DownstreamOf("n1", dag!);
        Assert.Equal(new HashSet<string> { "n2", "n3", "n4" }, downstream);
        Assert.Empty(DagScheduler.DownstreamOf("n4", dag!));
    }
}
