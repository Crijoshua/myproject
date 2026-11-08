using AgentCompany.Desktop.Services;

namespace AgentCompany.Tests;

/// <summary>
/// v1.1 特性 B 调度与分工（M3 任务书要求 ≥8 例）：
///  - B1 节点并发上限钳制（1–4，默认 2）；
///  - B2 员工多样性优先 + 同员工串行（SelectReady serializePerAgent=true），
///    依赖/所有权/下游取消语义不变，黄金对照与 v1.0（serializePerAgent=false）逐位一致；
///  - B3 EnsureBalanced 负载感知再平衡（≥3 节点且单一员工 &gt;2/3 才触发，只改归属）；
///  - B4 忙闲快照文本与【员工忙闲】节拼接。
/// </summary>
public class SchedulerV11Tests
{
    private static DagNode Node(int seq, string id, string agent = "claude", string prompt = "做事",
        string[]? writes = null, string[]? deps = null) =>
        new(seq, id, id, agent, prompt, writes ?? Array.Empty<string>(), deps ?? Array.Empty<string>());

    private static List<DagNode> Validate(params (string id, string agent, string[]? writes, string[]? deps)[] raw) =>
        DagValidator.Validate(raw.Select(r => (r.id, r.id, r.agent, "做事",
            (IReadOnlyList<string>)(r.writes ?? Array.Empty<string>()),
            (IReadOnlyList<string>)(r.deps ?? Array.Empty<string>()))).ToList()).Nodes!;

    // ---- B1：并发上限钳制 ----

    [Fact]
    public void B1_钳制_非法与过小回落默认2_过大钳到4()
    {
        Assert.Equal(2, A2aGateway.ClampNodeParallelism(null));
        Assert.Equal(2, A2aGateway.ClampNodeParallelism(""));
        Assert.Equal(2, A2aGateway.ClampNodeParallelism("0"));
        Assert.Equal(2, A2aGateway.ClampNodeParallelism("abc"));
        Assert.Equal(1, A2aGateway.ClampNodeParallelism("1"));
        Assert.Equal(3, A2aGateway.ClampNodeParallelism("3"));
        Assert.Equal(4, A2aGateway.ClampNodeParallelism("4"));
        Assert.Equal(4, A2aGateway.ClampNodeParallelism("9")); // 引擎侧钳边；设置页非法输入回默认 2 不落库
    }

    [Fact]
    public void B1_调度_上限1_只启动seq最小者()
    {
        var dag = Validate(
            ("n1", "claude", new[] { "a.txt" }, null),
            ("n2", "opencode", new[] { "b.txt" }, null));
        var ready = DagScheduler.SelectReady(dag, new HashSet<string>(), new HashSet<string>(),
            dag, runningCount: 0, maxConcurrent: 1, serializePerAgent: true);
        Assert.Equal(new[] { "n1" }, ready.Select(n => n.NodeId)); // 上限 1：DAG 严格串行
    }

    [Fact]
    public void B1_调度_上限2_三员工三节点_按seq取前二()
    {
        var dag = Validate(
            ("n1", "claude", new[] { "a.txt" }, null),
            ("n2", "opencode", new[] { "b.txt" }, null),
            ("n3", "crush", new[] { "review.md" }, null));
        var ready = DagScheduler.SelectReady(dag, new HashSet<string>(), new HashSet<string>(),
            dag, runningCount: 0, maxConcurrent: 2, serializePerAgent: true);
        Assert.Equal(new[] { "n1", "n2" }, ready.Select(n => n.NodeId));
    }

    [Fact]
    public void B1_调度_上限4_同员工串行不因上限放宽()
    {
        // 四节点三员工（claude 两个）：上限 4 也只起 3 个——同员工第二节点的槽位让给串行等待
        var dag = Validate(
            ("n1", "claude", new[] { "a.txt" }, null),
            ("n2", "opencode", new[] { "b.txt" }, null),
            ("n3", "crush", new[] { "review.md" }, null),
            ("n4", "claude", new[] { "c.txt" }, null));
        var ready = DagScheduler.SelectReady(dag, new HashSet<string>(), new HashSet<string>(),
            dag, runningCount: 0, maxConcurrent: 4, serializePerAgent: true);
        Assert.Equal(new[] { "n1", "n2", "n3" }, ready.Select(n => n.NodeId));
    }

    // ---- B2：员工多样性优先 + 同员工串行 ----

    [Fact]
    public void B2_多样性优先_同员工两节点与空闲员工并存_先起空闲员工的节点()
    {
        // v1.0 问题复现位：n1/n2 同属 claude 时 v1.0 会并行起两个 claude，opencode 闲着
        var dag = Validate(
            ("n1", "claude", new[] { "a.txt" }, null),
            ("n2", "claude", new[] { "b.txt" }, null),
            ("n3", "opencode", new[] { "c.txt" }, null));
        var ready = DagScheduler.SelectReady(dag, new HashSet<string>(), new HashSet<string>(),
            dag, runningCount: 0, maxConcurrent: 2, serializePerAgent: true);
        // 同员工取 seq 最小（n1），另一槽给空闲员工 opencode（n3），n2 串行等待
        Assert.Equal(new[] { "n1", "n3" }, ready.Select(n => n.NodeId));
    }

    [Fact]
    public void B2_同员工串行_在跑时挡住同员工_完成后才放行()
    {
        var dag = Validate(
            ("n1", "claude", new[] { "a.txt" }, null),
            ("n2", "claude", new[] { "b.txt" }, null));

        // 第一轮：只启动 n1
        var ready = DagScheduler.SelectReady(dag, new HashSet<string>(), new HashSet<string>(),
            dag, runningCount: 0, maxConcurrent: 2, serializePerAgent: true);
        Assert.Equal(new[] { "n1" }, ready.Select(n => n.NodeId));

        // n1 在跑：n2 同员工被挡（哪怕槽位空着）
        ready = DagScheduler.SelectReady(new[] { dag[1] }, new HashSet<string>(),
            new HashSet<string> { "n1" }, dag, runningCount: 1, maxConcurrent: 2, serializePerAgent: true);
        Assert.Empty(ready);

        // n1 完成（退出在跑表）：n2 才可启动
        ready = DagScheduler.SelectReady(new[] { dag[1] }, new HashSet<string> { "n1" },
            new HashSet<string>(), dag, runningCount: 0, maxConcurrent: 2, serializePerAgent: true);
        Assert.Equal(new[] { "n2" }, ready.Select(n => n.NodeId));
    }

    [Fact]
    public void B2_所有权冲突仍挡_与员工规则叠加()
    {
        // 防御路径：绕过校验构造所有权重叠（同 DagSpecTests 口径），n1 在跑占用 a.txt
        var dag = new[]
        {
            Node(1, "n1", "opencode", writes: new[] { "a.txt" }),
            Node(2, "n2", "crush", writes: new[] { "a.txt" }), // 与在跑的 n1 撞文件
            Node(3, "n3", "claude", writes: new[] { "b.txt" }),
        };
        var ready = DagScheduler.SelectReady(new[] { dag[1], dag[2] }, new HashSet<string>(),
            new HashSet<string> { "n1" }, dag, runningCount: 1, maxConcurrent: 2, serializePerAgent: true);
        Assert.Equal(new[] { "n3" }, ready.Select(n => n.NodeId)); // n2 被所有权挡，n3 照常
    }

    [Fact]
    public void B2_依赖不满足仍不启动()
    {
        var dag = Validate(
            ("n1", "claude", new[] { "a.txt" }, null),
            ("n2", "opencode", new[] { "b.txt" }, new[] { "n1" }),
            ("n3", "crush", new[] { "review.md" }, null));
        var ready = DagScheduler.SelectReady(dag, new HashSet<string>(), new HashSet<string>(),
            dag, runningCount: 0, maxConcurrent: 3, serializePerAgent: true);
        Assert.Equal(new[] { "n1", "n3" }, ready.Select(n => n.NodeId)); // n2 等依赖
    }

    // ---- B2：黄金对照（v1.0 已验收场景不变形） ----

    [Fact]
    public void B2_黄金对照_两节点两员工_启动集合与顺序与旧版完全一致()
    {
        foreach (var (first, second) in new[] { ("claude", "opencode"), ("opencode", "claude") })
        {
            var dag = Validate(
                ("n1", first, new[] { "a.txt" }, null),
                ("n2", second, new[] { "b.txt" }, null));
            var v10 = DagScheduler.SelectReady(dag, new HashSet<string>(), new HashSet<string>(),
                dag, runningCount: 0, maxConcurrent: 2, serializePerAgent: false);
            var v11 = DagScheduler.SelectReady(dag, new HashSet<string>(), new HashSet<string>(),
                dag, runningCount: 0, maxConcurrent: 2, serializePerAgent: true);
            Assert.Equal(new[] { "n1", "n2" }, v10.Select(n => n.NodeId));
            Assert.Equal(v10.Select(n => n.NodeId), v11.Select(n => n.NodeId)); // 逐位一致
        }
    }

    [Fact]
    public void B2_黄金对照_三节点经典场景_波次推进与旧版一致()
    {
        // 6.3 边界 #6：claude 实现 + opencode 辅助 + crush 评审（链式依赖）
        var dag = Validate(
            ("n1", "claude", new[] { "a.txt" }, null),
            ("n2", "opencode", new[] { "b.txt" }, new[] { "n1" }),
            ("n3", "crush", new[] { "review.md" }, new[] { "n1", "n2" }));

        // 波次一：只有 n1
        var v11 = DagScheduler.SelectReady(dag, new HashSet<string>(), new HashSet<string>(),
            dag, runningCount: 0, maxConcurrent: 2, serializePerAgent: true);
        var v10 = DagScheduler.SelectReady(dag, new HashSet<string>(), new HashSet<string>(),
            dag, runningCount: 0, maxConcurrent: 2, serializePerAgent: false);
        Assert.Equal(new[] { "n1" }, v10.Select(n => n.NodeId));
        Assert.Equal(v10.Select(n => n.NodeId), v11.Select(n => n.NodeId));

        // 波次二：n1 done → n2（n3 还差 n2）
        var doneIds = new HashSet<string> { "n1" };
        var rest = dag.Skip(1).ToList();
        v11 = DagScheduler.SelectReady(rest, doneIds, new HashSet<string>(), dag, 0, 2, serializePerAgent: true);
        v10 = DagScheduler.SelectReady(rest, doneIds, new HashSet<string>(), dag, 0, 2, serializePerAgent: false);
        Assert.Equal(new[] { "n2" }, v10.Select(n => n.NodeId));
        Assert.Equal(v10.Select(n => n.NodeId), v11.Select(n => n.NodeId));

        // 波次三：n2 done → n3
        doneIds.Add("n2");
        v11 = DagScheduler.SelectReady(new[] { dag[2] }, doneIds, new HashSet<string>(), dag, 0, 2, serializePerAgent: true);
        Assert.Equal(new[] { "n3" }, v11.Select(n => n.NodeId));
    }

    [Fact]
    public void B2_下游取消语义不变_闭包照旧()
    {
        var dag = Validate(
            ("n1", "claude", new[] { "a.txt" }, null),
            ("n2", "opencode", new[] { "b.txt" }, new[] { "n1" }),
            ("n3", "crush", new[] { "review.md" }, new[] { "n1" }),
            ("n4", "opencode", new[] { "d.txt" }, new[] { "n3" }));
        var downstream = DagScheduler.DownstreamOf("n1", dag);
        Assert.Equal(new HashSet<string> { "n2", "n3", "n4" }, downstream);
        Assert.Empty(DagScheduler.DownstreamOf("n4", dag));
    }

    // ---- B3：EnsureBalanced 负载感知再平衡 ----

    private static DagNode Node2(int seq, string id, string agent, int writes, int promptLen = 10) =>
        new(seq, id, id, agent, new string('p', promptLen),
            Enumerable.Range(0, writes).Select(i => $"{id}-{i}.txt").ToList(), Array.Empty<string>());

    [Fact]
    public void B3_五节点四比一_最重节点调给空闲crush_只改归属()
    {
        // 4 claude + 1 opencode：claude 占 4/5 > 2/3 → 最重的 claude 节点（write_targets 最多）调给空闲的 crush
        var nodes = new List<DagNode>
        {
            Node2(1, "n1", "claude", writes: 1),
            Node2(2, "n2", "claude", writes: 3), // 最重
            Node2(3, "n3", "claude", writes: 2),
            Node2(4, "n4", "claude", writes: 2, promptLen: 5), // 与 n3 平手 writes，prompt 更短
            Node2(5, "n5", "opencode", writes: 1),
        };
        var (adjusted, note) = CeoPlanner.EnsureBalanced(nodes);
        Assert.NotNull(note);
        Assert.Contains("n2 由 claude 调整为 crush", note);
        Assert.Equal("crush", adjusted.Single(n => n.NodeId == "n2").Agent);
        // 分布 3:1:1，无员工再超 2/3；结构不动（write_targets/依赖/顺序全保留，其余归属不变）
        Assert.Equal(3, adjusted.Count(n => n.Agent == "claude"));
        Assert.Equal(1, adjusted.Count(n => n.Agent == "opencode"));
        Assert.Equal(1, adjusted.Count(n => n.Agent == "crush"));
        Assert.All(adjusted.Where(n => n.NodeId != "n2"), n => Assert.Equal(
            nodes.First(o => o.NodeId == n.NodeId).Agent, n.Agent));
        Assert.Equal(3, adjusted.Single(n => n.NodeId == "n2").WriteTargets.Count);
        Assert.Equal(nodes.Select(n => n.NodeId), adjusted.Select(n => n.NodeId)); // 顺序不动
    }

    [Fact]
    public void B3_不超过三分之二_不触发()
    {
        var balanced = new List<DagNode> // 3:2（3/5 未超 2/3）
        {
            Node2(1, "n1", "claude", writes: 1),
            Node2(2, "n2", "claude", writes: 1),
            Node2(3, "n3", "claude", writes: 1),
            Node2(4, "n4", "opencode", writes: 1),
            Node2(5, "n5", "opencode", writes: 1),
        };
        var (adjusted, note) = CeoPlanner.EnsureBalanced(balanced);
        Assert.Null(note);
        Assert.Equal(balanced, adjusted);

        var three = new List<DagNode> // 2:1（2/3 恰好等于 2/3，不大于 → 不触发）
        {
            Node2(1, "n1", "claude", writes: 2),
            Node2(2, "n2", "claude", writes: 1),
            Node2(3, "n3", "opencode", writes: 1),
        };
        var (adjusted3, note3) = CeoPlanner.EnsureBalanced(three);
        Assert.Null(note3);
        Assert.Equal(three, adjusted3);
    }

    [Fact]
    public void B3_两节点与不足三节点_原样返回()
    {
        var two = new List<DagNode> { Node2(1, "n1", "claude", 1), Node2(2, "n2", "claude", 1) };
        var (adjusted, note) = CeoPlanner.EnsureBalanced(two);
        Assert.Null(note);
        Assert.Equal(two, adjusted);
    }

    [Fact]
    public void B3_白名单序_空闲员工按claudeopencodecrush取()
    {
        // 4 opencode + 1 crush：空闲 = claude（白名单序第一个）→ 调给 claude 而非别人
        var nodes = new List<DagNode>
        {
            Node2(1, "n1", "opencode", writes: 1),
            Node2(2, "n2", "opencode", writes: 3),
            Node2(3, "n3", "opencode", writes: 1),
            Node2(4, "n4", "opencode", writes: 1),
            Node2(5, "n5", "crush", writes: 1),
        };
        var (adjusted, note) = CeoPlanner.EnsureBalanced(nodes);
        Assert.NotNull(note);
        Assert.Contains("n2 由 opencode 调整为 claude", note);
        Assert.Equal("claude", adjusted.Single(n => n.NodeId == "n2").Agent);
    }

    [Fact]
    public void B3_先保底再平衡_单员工五节点_EnsureTwoAgents后EnsureBalanced()
    {
        // CEO 全塞 opencode（心情差）：保底调最重给 claude → 4:1 仍偏斜 → 再平衡调给空闲 crush → 3:1:1
        var nodes = new List<DagNode>
        {
            Node2(1, "n1", "opencode", writes: 1),
            Node2(2, "n2", "opencode", writes: 4),
            Node2(3, "n3", "opencode", writes: 3),
            Node2(4, "n4", "opencode", writes: 2),
            Node2(5, "n5", "opencode", writes: 1),
        };
        var (afterTwo, twoNote) = CeoPlanner.EnsureTwoAgents(nodes);
        Assert.NotNull(twoNote);
        Assert.Equal("claude", afterTwo.Single(n => n.NodeId == "n2").Agent); // 最重给 claude

        var (final, balanceNote) = CeoPlanner.EnsureBalanced(afterTwo);
        Assert.NotNull(balanceNote);
        Assert.Equal(3, final.Count(n => n.Agent == "opencode"));
        Assert.Equal(1, final.Count(n => n.Agent == "claude"));
        Assert.Equal(1, final.Count(n => n.Agent == "crush"));
    }

    // ---- B4：忙闲快照 ----

    [Fact]
    public void B4_忙闲表_忙碌带任务号_空闲为一行()
    {
        var text = A2aGateway.FormatAgentStatus(new List<(string, int?)>
        {
            ("claude", 3),
            ("opencode", null),
            ("crush", 7),
        });
        Assert.Equal("claude:忙碌(任务#3)\nopencode:空闲\ncrush:忙碌(任务#7)", text);
    }

    [Fact]
    public void B4_员工忙闲节_拼在尾部_空快照省略()
    {
        Assert.Equal("\n\n【员工忙闲】\nclaude:空闲", CeoPlanner.AgentStatusSection("claude:空闲"));
        Assert.Equal("", CeoPlanner.AgentStatusSection(null));
        Assert.Equal("", CeoPlanner.AgentStatusSection("  "));
    }
}
