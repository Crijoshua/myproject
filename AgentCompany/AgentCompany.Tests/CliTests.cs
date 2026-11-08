using System.IO;
using System.IO.Pipes;
using System.Text;
using AgentCompany.Core;
using AgentCompany.Desktop.Services;
using Xunit;

namespace AgentCompany.Tests;

/// <summary>
/// M4 特性 D（CEO 终端入口）单测：FolderSnapshot 排除/深度/条目/路径校验、
/// TerminalApprovalGate.Decide（y/n/超时，注入假钟不真等）、CliArgs 参数解析、
/// CliPeek 50KB 上限、PipeProbe 管道占用探测（S6 互斥的判定层）。
/// </summary>
public class CliTests : IDisposable
{
    private readonly string _root;

    public CliTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录清理失败无碍 */ }
    }

    private string Abs(string rel) => Path.Combine(_root, rel);

    private void Touch(string rel)
    {
        var full = Abs(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }

    // ---- FolderSnapshot ----

    [Fact]
    public void FolderSnapshot_排除目录不出现()
    {
        foreach (var d in new[] { "bin", "obj", "node_modules", ".git", ".oc-config", ".crush", ".vs", "packages" })
            Touch(Path.Combine(d, "f.txt"));
        Touch("src/keep.txt");
        Touch("Bin/lower.txt"); // 排除规则 OrdinalIgnoreCase

        var (tree, _, _) = FolderSnapshot.RenderTree(_root);

        foreach (var d in new[] { "bin", "node_modules", ".git", ".oc-config", ".crush", "Bin" })
            Assert.DoesNotContain($"{d}", tree);
        Assert.Contains("keep.txt", tree);
    }

    [Fact]
    public void FolderSnapshot_深度超过3不展开()
    {
        Touch("a/b/c/deep.txt");   // 第 4 层文件（root=a/b/c/deep.txt 深度 4）
        Touch("a/b/inside.txt");   // 第 3 层：可见

        var (tree, truncated, _) = FolderSnapshot.RenderTree(_root, maxDepth: 3);

        Assert.Contains("inside.txt", tree);
        Assert.DoesNotContain("deep.txt", tree);
        Assert.False(truncated); // 深度截断静默，不算条目截断
    }

    [Fact]
    public void FolderSnapshot_条目超过500截断且置Truncated()
    {
        // 501 个文件：超过 maxEntries=500，截断留痕 + Truncated=true
        var dir = Abs("many");
        Directory.CreateDirectory(dir);
        for (var i = 0; i <= 500; i++)
            File.WriteAllText(Path.Combine(dir, $"f{i:D3}.txt"), "x");

        var (tree, truncated, entries) = FolderSnapshot.RenderTree(_root);

        Assert.True(truncated);
        Assert.Equal(500, entries);
        Assert.Contains("…(截断,共 500 项)", tree);
    }

    [Fact]
    public void FolderSnapshot_相对路径拒绝()
    {
        var ex = Assert.Throws<ArgumentException>(() => FolderSnapshot.RenderTree("some/relative/path"));
        Assert.Contains("绝对路径", ex.Message);
    }

    [Fact]
    public void FolderSnapshot_盘相对路径拒绝_联审v12()
    {
        // "C:foo" 的 IsPathRooted 是 true（盘相对路径），v1.2 前会被当绝对路径放行、
        // 按该盘当前目录解析——现要求完整根（IsPathFullyRooted：盘符+目录或 UNC）
        var ex = Assert.Throws<ArgumentException>(() => FolderSnapshot.RenderTree("C:foo"));
        Assert.Contains("完整绝对路径", ex.Message);
    }

    [Fact]
    public void FolderSnapshot_不存在路径拒绝()
    {
        var missing = Path.Combine(_root, "no-such-dir");
        var ex = Assert.Throws<ArgumentException>(() => FolderSnapshot.RenderTree(missing));
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public void FolderSnapshot_树形为相对路径且计入目录条目()
    {
        Touch("sub/inner.txt");

        var (tree, truncated, entries) = FolderSnapshot.RenderTree(_root);

        Assert.False(truncated);
        Assert.Equal(2, entries); // 目录 sub + 文件 inner.txt 都计条目
        Assert.StartsWith(_root, tree);
        Assert.Contains("└─ sub", tree);
        Assert.Contains("└─ inner.txt", tree);
        Assert.DoesNotContain(Abs("sub"), tree); // 条目是相对名，不是绝对路径
    }

    // ---- TerminalApprovalGate.Decide（注入读写与假钟，不真等 115s） ----

    private static readonly ApprovalRequest Req =
        new("id-1234", "opencode", "terminal", "npm install left-pad", "实现登录页");

    private static List<string> Sink() => new();

    [Fact]
    public void Decide_y批准()
    {
        var writes = Sink();
        var t0 = new DateTime(2026, 11, 3, 12, 0, 0);

        var d = TerminalApprovalGate.Decide(Req, 115, _ => "y", () => t0, writes.Add);

        Assert.True(d.Approved);
        Assert.Equal("approved", d.Decision);
        Assert.Null(d.Note);
        Assert.Contains(writes, w => w.Contains("npm install left-pad")); // 完整命令必须可见
        Assert.Contains(writes, w => w.Contains("实现登录页"));           // 来源任务可见
        Assert.Contains(writes, w => w.Contains("剩余 115s"));
    }

    [Fact]
    public void Decide_n拒绝()
    {
        var d = TerminalApprovalGate.Decide(Req, 115, _ => "n", () => new DateTime(2026, 11, 3, 12, 0, 0), Sink().Add);

        Assert.False(d.Approved);
        Assert.Equal("denied", d.Decision);
        Assert.Null(d.Note);
    }

    [Fact]
    public void Decide_大小写不敏感()
    {
        var t0 = new DateTime(2026, 11, 3, 12, 0, 0);
        Assert.True(TerminalApprovalGate.Decide(Req, 115, _ => "Y", () => t0, Sink().Add).Approved);
        Assert.False(TerminalApprovalGate.Decide(Req, 115, _ => "N ", () => t0, Sink().Add).Approved);
    }

    [Fact]
    public void Decide_超时按拒绝且note为timeout()
    {
        // 假钟：第一次取时刻即已过 deadline（注入 120s 步进，不真等）
        var t0 = new DateTime(2026, 11, 3, 12, 0, 0);
        var calls = 0;
        DateTime Clock() => t0.AddSeconds(Interlocked.Increment(ref calls) * 120);

        var d = TerminalApprovalGate.Decide(Req, 115, _ => (string?)null, Clock, Sink().Add);

        Assert.False(d.Approved);
        Assert.Equal("timeout", d.Decision);
        Assert.Equal("timeout", d.Note); // 与 GUI 落库口径一致（note=timeout）
    }

    [Fact]
    public void Decide_无效输入重提示后仍可批准()
    {
        var writes = Sink();
        var queue = new Queue<string?>(new[] { "what", "y" });
        var t0 = new DateTime(2026, 11, 3, 12, 0, 0);

        var d = TerminalApprovalGate.Decide(Req, 115, _ => queue.Dequeue(), () => t0, writes.Add);

        Assert.True(d.Approved);
        Assert.Contains(writes, w => w.Contains("请输入 y 或 n"));
    }

    // ---- CliArgs 参数解析 ----

    [Fact]
    public void CliArgs_folder带路径()
    {
        var o = CliArgs.Parse(new[] { "--folder", @"C:\repo\x" });
        Assert.Null(o.Error);
        Assert.Equal(@"C:\repo\x", o.Folder);
        Assert.False(o.ShowVersion);
    }

    [Fact]
    public void CliArgs_version与help()
    {
        Assert.True(CliArgs.Parse(new[] { "--version" }).ShowVersion);
        var help = CliArgs.Parse(new[] { "--help" });
        Assert.True(help.ShowHelp);
        Assert.Null(help.Error);
    }

    [Fact]
    public void CliArgs_未知参数报错并列出用法()
    {
        var o = CliArgs.Parse(new[] { "--bogus" });
        Assert.NotNull(o.Error);
        Assert.Contains("未知参数：--bogus", o.Error);
        Assert.Contains("用法", o.Error);
    }

    [Fact]
    public void CliArgs_folder缺参数报错()
    {
        var o = CliArgs.Parse(new[] { "--folder" });
        Assert.NotNull(o.Error);
        Assert.Contains("缺少路径参数", o.Error);
    }

    // ---- CliPeek（/peek 代读 50KB 上限） ----

    [Fact]
    public void Peek_超过50KB截断留痕()
    {
        var big = Abs("big.log");
        File.WriteAllText(big, new string('a', CliPeek.MaxBytes + 1024)); // 51KB ASCII

        var (content, truncated, error) = CliPeek.ReadFile(_root, "big.log");

        Assert.Null(error);
        Assert.True(truncated);
        Assert.Equal(CliPeek.MaxBytes, Encoding.UTF8.GetByteCount(content));
    }

    [Fact]
    public void Peek_小文件全文且不截断()
    {
        File.WriteAllText(Abs("readme.md"), "# 标题\n正文");

        var (content, truncated, error) = CliPeek.ReadFile(_root, "readme.md");

        Assert.Null(error);
        Assert.False(truncated);
        Assert.Contains("# 标题", content);
    }

    [Fact]
    public void Peek_绝对路径与越界与不存在都报错()
    {
        Assert.NotNull(CliPeek.ReadFile(_root, @"C:\Windows\win.ini").Error);          // 绝对路径拒绝
        Assert.NotNull(CliPeek.ReadFile(_root, "../escape.txt").Error);                 // .. 逃逸拒绝
        Assert.NotNull(CliPeek.ReadFile(_root, "no-such-file.txt").Error);              // 不存在
        Assert.NotNull(CliPeek.ReadFile(_root, "").Error);                              // 空参数提示用法
    }

    [Fact]
    public void Peek_兄弟目录同前缀不越界_联审m234()
    {
        // 形态：root=...\proj，../proj2/x.txt 解析为 ...\proj2\x.txt——
        // 旧实现 StartsWith(rootFull) 前缀误命中放行；修复后必须按"相等或 root+分隔符前缀"拒绝
        var parent = Path.GetDirectoryName(_root.TrimEnd(Path.DirectorySeparatorChar))!;
        var rootName = Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar));      // cli-tests-<guid>
        var sibling = Path.Combine(parent, rootName + "2");                               // 同前缀兄弟目录
        Directory.CreateDirectory(sibling);
        try
        {
            File.WriteAllText(Path.Combine(sibling, "secret.txt"), "不该被读到");
            // 从 root 出发用相对路径指到兄弟目录
            var rel = "../" + rootName + "2/secret.txt";
            var (content, _, error) = CliPeek.ReadFile(_root, rel);
            Assert.NotNull(error);                      // 必须拒绝
            Assert.NotEqual("不该被读到", content);      // 且绝不返回内容
        }
        finally
        {
            try { Directory.Delete(sibling, recursive: true); } catch { /* 清理失败无碍 */ }
        }
    }

    // ---- PipeProbe（单宿主互斥的判定层；用独立管道名避免与真实桌面版互串） ----

    [Fact]
    public void PipeProbe_有服务端在听则报占用()
    {
        var name = "AgentCompany.Test.Probe." + Guid.NewGuid().ToString("N");
        try
        {
            Assert.False(PipeProbe.IsOccupied(name, 300));

            using var server = new NamedPipeServerStream(name, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var waiting = Task.Run(() => server.WaitForConnection());
            Thread.Sleep(200); // named pipe 创建即进入 listening，稍候探测稳定

            Assert.True(PipeProbe.IsOccupied(name, 500)); // CLI 启动探测：连得上 = 被占 → 退出码 2
            if (waiting.IsCompleted)
                server.Disconnect();
        }
        finally
        {
            // 探测连接若被服务端接走，释放句柄即可
        }
    }
}
