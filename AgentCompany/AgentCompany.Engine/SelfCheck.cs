using System.Diagnostics;
using System.IO;

namespace AgentCompany.Desktop.Services;

/// <summary>一项自检结果（轮 8）：Key 供程序判定，Title/Detail 供设置页展示。</summary>
public sealed record CheckItem(string Key, string Title, bool Ok, string Detail);

/// <summary>
/// 启动自检（轮 8 成品化）：三员工 CLI 可达（覆盖项存在 / PATH 可寻）、McpBridge 就位、
/// 工作区可写、SQLite 可用。App 启动跑一次并留结果；设置页[重新自检]重跑。
/// 员工不可用时派活闸门（AgentCliProcessHost.AvailabilityGate / MonitorPage 派活按钮）据此拒绝。
/// </summary>
public sealed class SelfCheckService
{
    /// <summary>满编员工（与 DagValidator.AllowedAgents 同序）。</summary>
    public static readonly string[] Agents = { "claude", "opencode", "crush" };

    private readonly List<CheckItem> _last = new();

    /// <summary>最近一次自检结果（Run 之前为空）。</summary>
    public IReadOnlyList<CheckItem> Last => _last;

    /// <summary>全部通过（空结果视为未通过——还没检过不能算好）。</summary>
    public bool AllOk => _last.Count > 0 && _last.All(i => i.Ok);

    /// <summary>员工 CLI 是否可用（未检过 = 不可用，宁可误拒不可漏跑）。</summary>
    public bool IsAgentAvailable(string agent) =>
        _last.FirstOrDefault(i => i.Key == $"cli:{agent}") is { Ok: true };

    /// <summary>跑一轮全量自检并覆盖缓存结果。</summary>
    public List<CheckItem> Run(Database db)
    {
        var items = new List<CheckItem>();
        // v1.1 B5.2（轮 8 已知问题 3）：三员工 where.exe 探测改并行——串行最坏 3×5s 拖慢启动，
        // 并行后总耗时≈单次；按索引回填保持结果顺序/口径/文案全部不变（宿主可用性判定同源）。
        var cliItems = new CheckItem[Agents.Length];
        Parallel.For(0, Agents.Length, i => cliItems[i] = CheckCli(Agents[i]));
        items.AddRange(cliItems);

        var bridgeExe = Path.Combine(AppContext.BaseDirectory, "McpBridge", "AgentCompany.McpBridge.exe");
        items.Add(new CheckItem("bridge", "McpBridge（公司工具与审批门）", File.Exists(bridgeExe),
            File.Exists(bridgeExe) ? bridgeExe : $"未找到 {bridgeExe}：用 发布.bat 重新发布"));

        items.Add(CheckWorkspace());

        try
        {
            var text = db.StatusText(); // 走一次真实查询，连不上会抛
            items.Add(new CheckItem("sqlite", "SQLite 状态库", true, $"{db.DbPath}（{text}）"));
        }
        catch (Exception ex)
        {
            items.Add(new CheckItem("sqlite", "SQLite 状态库", false, ex.Message));
        }

        _last.Clear();
        _last.AddRange(items);
        return items;
    }

    private static CheckItem CheckCli(string agent)
    {
        var o = CliPaths.RawOverride(agent);
        if (o is not null)
        {
            var ok = File.Exists(o);
            return new CheckItem($"cli:{agent}", $"{agent} CLI",
                ok, ok ? $"覆盖路径 {o}" : $"覆盖路径不存在：{o}（清掉覆盖走 PATH，或改正确路径）");
        }
        var found = WhereOnPath(agent);
        return new CheckItem($"cli:{agent}", $"{agent} CLI",
            found is not null, found ?? $"PATH 上找不到 {agent}：npm 装好后重开本程序，或在设置页填路径覆盖");
    }

    /// <summary>where.exe 查 PATH（覆盖项没设时用它判定；返回首个命中路径或 null）。</summary>
    public static string? WhereOnPath(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe", name)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
                return null;
            var first = p.StandardOutput.ReadLine();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(first) ? first.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static CheckItem CheckWorkspace()
    {
        try
        {
            var root = WorkspaceLayout.Root();
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new CheckItem("workspace", "工作区可写", true, root);
        }
        catch (Exception ex)
        {
            return new CheckItem("workspace", "工作区可写", false, ex.Message);
        }
    }
}
