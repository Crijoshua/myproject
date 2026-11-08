using System.Text;
using AgentCompany.Core;
using AgentCompany.McpBridge;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

// stdio MCP Server（服务器名 company）。stdout 是协议通道，任何日志只能走 stderr / 文件。

// claude→bridge 的 file_write 中文内容、bridge→claude 的文件内容都必须按 UTF-8 收发，
// 显式设置控制台编码（zh-CN 控制台默认 OEM/GBK，不设会乱码）。
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

var workspace = Environment.GetEnvironmentVariable("AGENTCOMPANY_WORKSPACE") ?? "";
var pipeName = Environment.GetEnvironmentVariable("AGENTCOMPANY_PIPE") ?? ApprovalPipe.DefaultPipeName;

ToolEnv.Init(workspace, pipeName, Environment.GetEnvironmentVariable("AGENTCOMPANY_AGENT"));
BridgeLog.Info($"bridge 启动 pid={Environment.ProcessId} workspace={workspace} pipe={pipeName}");

var services = new ServiceCollection();

// 只加空 Logger 工厂（不注册任何 Provider）：stdio 是协议通道，绝不能被日志污染
services.AddLogging();

services.AddMcpServer(o => o.ServerInfo = new()
{
    Name = "company",
    Title = "小公司工具",
    Version = "1.1.1",
})
.WithStdioServerTransport()
.WithToolsFromAssembly(typeof(CompanyTools).Assembly);

using var provider = services.BuildServiceProvider();
var server = provider.GetRequiredService<McpServer>();
await server.RunAsync(); // stdin 关闭（claude 退出）即返回

BridgeLog.Info("bridge 退出");
