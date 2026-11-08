using System.IO;
using AgentCompany.Desktop.Services;
using Xunit;

namespace AgentCompany.Tests;

/// <summary>轮 8 可移植性：CLI 路径覆盖解析、工作区根解析、备份快照、自检判定。</summary>
public sealed class PortabilityTests : IDisposable
{
    public PortabilityTests()
    {
        CliPaths.Reset();
        WorkspaceLayout.Reset();
    }

    public void Dispose()
    {
        CliPaths.Reset();
        WorkspaceLayout.Reset();
    }

    // ---- CliPaths ----

    [Fact]
    public void 无覆盖_命令头为员工名_走PATH()
    {
        CliPaths.Init(_ => null);
        Assert.Equal("claude", CliPaths.Command("claude"));
        Assert.Null(CliPaths.RawOverride("opencode"));
    }

    [Fact]
    public void 覆盖路径含空格_自动加引号_无空格不加()
    {
        CliPaths.Init(a => a == "claude" ? @"D:\tools dir\claude.cmd" : a == "opencode" ? @"D:\opencode.cmd" : null);
        Assert.Equal(@"""D:\tools dir\claude.cmd""", CliPaths.Command("claude"));
        Assert.Equal(@"D:\opencode.cmd", CliPaths.Command("opencode"));
    }

    [Fact]
    public void 覆盖为空白_视为无覆盖()
    {
        CliPaths.Init(_ => "   ");
        Assert.Null(CliPaths.RawOverride("crush"));
        Assert.Equal("crush", CliPaths.Command("crush"));
    }

    [Fact]
    public void 适配器命令头接覆盖_claude参数含引号路径()
    {
        CliPaths.Init(a => a == "claude" ? @"E:\bin\my claude.cmd" : null);
        var adapter = EmployeeRegistry.Create("claude");
        var (args, _) = adapter.BuildArguments(null, Path.GetTempPath());
        Assert.StartsWith("/c \"E:\\bin\\my claude.cmd\" -p --output-format stream-json --verbose", args);
    }

    // ---- WorkspaceLayout ----

    [Fact]
    public void 工作区根_默认为主程序目录下workspace()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "workspace"), WorkspaceLayout.Root());
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "workspace", "pipeline", "task-7"),
            WorkspaceLayout.PipelineWorkspace(7));
    }

    [Fact]
    public void 工作区根_覆盖项优先_设置页改完即生效()
    {
        WorkspaceLayout.Init(() => @"D:\AgentCompanyPortable\ws");
        Assert.Equal(@"D:\AgentCompanyPortable\ws", WorkspaceLayout.Root());
        Assert.Equal(@"D:\AgentCompanyPortable\ws\MEMORY.md", WorkspaceLayout.CompanyMemoryPath());
        Assert.Equal(@"D:\AgentCompanyPortable\ws\claude", WorkspaceLayout.AgentWorkspace("claude"));
    }

    // ---- 备份 ----

    [Fact]
    public void 状态库备份_生成带数据的可用快照()
    {
        var src = Path.Combine(Path.GetTempPath(), $"ac-port-{Guid.NewGuid():N}.db");
        var dst = Path.Combine(Path.GetTempPath(), $"ac-port-backup-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(src);
            db.EnsureCreated();
            db.SetSetting("theme", "dark");
            db.BackupTo(dst);

            Assert.True(File.Exists(dst));
            var backup = new Database(dst); // 快照可独立打开
            Assert.Equal("dark", backup.GetSetting("theme"));
        }
        finally
        {
            try { File.Delete(src); } catch { /* 清理尽力 */ }
            try { File.Delete(dst); } catch { /* 清理尽力 */ }
        }
    }

    // ---- SelfCheck 判定 ----

    [Fact]
    public void 自检_未跑过员工不可用_覆盖存在即可用_覆盖缺失不可用()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"ac-selfcheck-{Guid.NewGuid():N}.db");
        try
        {
            var db = new Database(dbPath);
            db.EnsureCreated();
            var check = new SelfCheckService();

            Assert.False(check.IsAgentAvailable("claude")); // 未检过 = 不可用（宁可误拒）

            var real = Path.Combine(Path.GetTempPath(), "ac-probe-real.cmd");
            File.WriteAllText(real, "@echo off");
            CliPaths.Init(a => a == "claude" ? real : a == "crush" ? @"X:\不存在\crush-x.cmd" : null);

            var items = check.Run(db);
            Assert.True(check.IsAgentAvailable("claude"));            // 覆盖项文件存在
            Assert.False(check.IsAgentAvailable("crush"));            // 覆盖项文件缺失
            Assert.Contains(items, i => i.Key == "workspace" && i.Ok); // 临时目录必可写
            Assert.Contains(items, i => i.Key == "sqlite" && i.Ok);
            Assert.Contains(items, i => i.Key == "bridge");            // 断言存在该项（Ok 与否取决于运行目录）
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* 清理尽力 */ }
        }
    }
}
