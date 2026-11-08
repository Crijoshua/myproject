using System.Text.Json;

namespace AgentCompany.Desktop.Services;

/// <summary>CEO 拆出的一个节点（= task_steps 一行，seq 为拓扑序下标+1）。</summary>
public sealed record DagNode(
    int Seq,
    string NodeId,
    string Title,
    string Agent,
    string Prompt,
    IReadOnlyList<string> WriteTargets,
    IReadOnlyList<string> DependsOn);

/// <summary>
/// DAG 校验与波次调度（轮 6；轮 7 员工白名单加 crush）。规则全部来自任务书：
/// 节点 1-5、agent 白名单（claude|opencode|crush，模型会编名字）、无环、依赖只引用更早节点、
/// write_targets 跨节点不重叠；调度 = 依赖满足 + 所有权空闲 + 并发上限内即启动。
/// </summary>
public static class DagValidator
{
    public static readonly string[] AllowedAgents = { "claude", "opencode", "crush" };
    public const int MaxNodes = 5;

    /// <summary>校验 CEO 输出的原始节点表（顺序即声明顺序，depends_on 只允许引用更早节点）。</summary>
    /// <returns>(按序号化后的节点, 错误列表)；错误非空即不可用。</returns>
    public static (List<DagNode>? Nodes, List<string> Errors) Validate(
        IReadOnlyList<(string NodeId, string Title, string Agent, string Prompt, IReadOnlyList<string> Writes, IReadOnlyList<string> Depends)> raw)
    {
        var errors = new List<string>();
        if (raw.Count is < 1 or > MaxNodes)
            errors.Add($"节点数量 {raw.Count} 超出 1-{MaxNodes}");

        var nodes = new List<DagNode>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var fileOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 文件 → 节点id

        for (var i = 0; i < raw.Count; i++)
        {
            var (nodeId, title, agent, prompt, writes, depends) = raw[i];
            var seq = i + 1;

            if (string.IsNullOrWhiteSpace(nodeId) || !seenIds.Add(nodeId))
                errors.Add($"节点{seq} id 重复或为空：{nodeId}");
            if (!AllowedAgents.Contains(agent))
                errors.Add($"节点{seq} agent 非法：{agent}（只能是 claude、opencode 或 crush）");
            if (string.IsNullOrWhiteSpace(prompt))
                errors.Add($"节点{seq} prompt 为空（必须自包含）");

            foreach (var dep in depends)
            {
                if (!seenIds.Contains(dep))
                    errors.Add($"节点{seq} 依赖了不存在的节点：{dep}");
                // depends_on 引用更早节点：seenIds 此时只含 0..i-1，dep 命中即必为更早
            }

            foreach (var file in writes)
            {
                var norm = NormalizePath(file);
                if (norm.Length == 0)
                {
                    errors.Add($"节点{seq} write_targets 含空路径");
                    continue;
                }
                if (fileOwner.TryGetValue(norm, out var owner))
                    errors.Add($"文件 {file} 同时属于 {owner} 和 {nodeId}（一个文件只能归一个节点）");
                else
                    fileOwner[norm] = nodeId;
            }

            nodes.Add(new DagNode(seq, nodeId, title?.Trim() ?? nodeId, agent, prompt?.Trim() ?? "",
                writes.Select(NormalizePath).Where(p => p.Length > 0).ToList(),
                depends.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToList()));
        }

        // 无环：depends_on 只引用更早节点 ⇒ 按声明顺序天然无环；上面已用 seenIds 强制。
        // 补一层防御：自依赖。
        foreach (var n in nodes)
            if (n.DependsOn.Contains(n.NodeId, StringComparer.Ordinal))
                errors.Add($"节点{n.Seq} 依赖自己");

        return (errors.Count == 0 ? nodes : null, errors);
    }

    /// <summary>路径归一（所有权与 Bridge 侧判定用同一口径）：反斜杠→斜杠、去首尾空白与 ./。</summary>
    public static string NormalizePath(string path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/');
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        return p.Trim('/');
    }
}

/// <summary>
/// 波次调度（轮 6；v1.1 B2 员工多样性优先 + 同员工串行）：依赖满足 + 文件所有权空闲 +
/// 并发 ≤ 上限 即可启动。
/// </summary>
public static class DagScheduler
{
    /// <summary>
    /// 从待跑节点中挑出本轮可启动的（按 seq 序）：依赖全部 done 且 write_targets 不与
    /// 在跑节点冲突，凑够 (上限 - 在跑数) 个为止。
    /// v1.1 B2（serializePerAgent = true，网关调用点启用）：实现方式为"按 seq 扫描 + 跳过
    /// 忙碌员工"——该员工已在跑或本轮已挑过节点时跳过其后同员工节点（等下一轮在位释放
    /// 后放行），效果等价于"员工多样性优先"（空闲员工的就绪节点不会被前面同员工的就绪
    /// 节点占位挤掉），而非按空闲度排序的实现；同一员工同时最多 1 个在跑节点（本任务内
    /// 自动串行化），提高真并行度、避免同员工 CLI 限流自竞争。
    /// false = v1.0 行为（默认参数零破坏：既有调度测试与黄金对照的回归口径）。
    /// 依赖、所有权、失败止损、下游取消语义两态完全一致，一概不动。
    /// </summary>
    public static List<DagNode> SelectReady(
        IReadOnlyList<DagNode> pending,
        IReadOnlySet<string> doneNodeIds,
        IReadOnlySet<string> runningNodeIds,
        IReadOnlyList<DagNode> allNodes,
        int runningCount,
        int maxConcurrent,
        bool serializePerAgent = false)
    {
        var busyFiles = new HashSet<string>(
            allNodes.Where(n => runningNodeIds.Contains(n.NodeId))
                .SelectMany(n => n.WriteTargets), StringComparer.OrdinalIgnoreCase);
        var busyAgents = new HashSet<string>(
            allNodes.Where(n => runningNodeIds.Contains(n.NodeId)).Select(n => n.Agent),
            StringComparer.Ordinal);

        var slots = maxConcurrent - runningCount;
        var ready = new List<DagNode>();
        foreach (var node in pending.OrderBy(n => n.Seq))
        {
            if (slots <= 0)
                break;
            if (!node.DependsOn.All(d => doneNodeIds.Contains(d)))
                continue;
            if (node.WriteTargets.Any(f => busyFiles.Contains(f)))
                continue; // 所有权被在跑节点占用：断言不满足，等下一轮
            if (serializePerAgent && busyAgents.Contains(node.Agent))
                continue; // 该员工已有在跑/本轮已挑节点：串行化，让位给空闲员工，等下一轮
            ready.Add(node);
            slots--;
            if (serializePerAgent)
                busyAgents.Add(node.Agent); // 本轮该员工已挑一个：后续同员工节点等下一轮
            foreach (var f in node.WriteTargets)
                busyFiles.Add(f);
        }
        return ready;
    }

    /// <summary>某节点的全部下游（直接或间接依赖它），失败时一并取消。</summary>
    public static HashSet<string> DownstreamOf(string nodeId, IReadOnlyList<DagNode> allNodes)
    {
        var dependents = allNodes
            .Where(n => n.DependsOn.Contains(nodeId, StringComparer.Ordinal))
            .Select(n => n.NodeId)
            .ToList();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (dependents.Count > 0)
        {
            var next = new List<string>();
            foreach (var id in dependents)
                if (result.Add(id))
                    next.AddRange(allNodes.Where(n => n.DependsOn.Contains(id, StringComparer.Ordinal))
                        .Select(n => n.NodeId));
            dependents = next;
        }
        return result;
    }
}
