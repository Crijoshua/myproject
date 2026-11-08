using System.Diagnostics;
using AgentCompany.Core;

namespace AgentCompany.McpBridge;

/// <summary>
/// terminal 的实际执行器：cmd.exe /c、工作目录=工作区根、UTF-8 输出、
/// 单命令 120 秒超时（杀进程树）、返回 stdout+stderr+exit code（合计截断 8000 字符）。
///
/// 轮 3 实测坑（验收 ⑤ 踩的，勿回退）：
///  1. 必须重定向子进程 stdin 并立即关闭——否则 cmd/npm 继承 bridge 的 stdin
///     （那是 claude↔bridge 的 MCP 协议通道），等 stdin 的工具会永久挂死；
///  2. 进程退出 ≠ 输出管道关闭：npm 链条的孙进程可能握着 stdout 写句柄不放，
///     ReadToEnd/CopyToAsync 会永远 await。排空必须带时限，超时强杀整棵树、拿已收字节走人；
///  3. kill/超时/完成三种结局都必须落日志（排障时"没有日志"本身就是线索）。
/// </summary>
internal static class TerminalRunner
{
    private const int TimeoutSeconds = 120;
    private const int DrainGraceSeconds = 5;   // 进程退出后给输出管道的排空窗口
    private const int KillGraceSeconds = 3;    // 杀树后等管道收尾的窗口
    private const int MaxOutputChars = 8000;

    public static async Task<string> RunAsync(string workspace, string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // chcp 65001：让跟随控制台码页的子进程按 UTF-8 写（cmd 自身消息恒为 OEM，由 OutputDecoder 兜底）
            Arguments = "/c chcp 65001 >nul & " + command,
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,   // 不继承 MCP 协议 stdin
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi)!;
        proc.StandardInput.Close(); // 立即 EOF：防子进程等 stdin

        // 双泵带兜底：管道被杀/被孤儿句柄拖住时保留已收字节，绝不抛出
        var stdoutBuf = new MemoryStream();
        var stderrBuf = new MemoryStream();
        var pumping = Task.WhenAll(
            Pump(proc.StandardOutput.BaseStream, stdoutBuf),
            Pump(proc.StandardError.BaseStream, stderrBuf));

        var exited = await Task.Run(() => proc.WaitForExit(TimeoutSeconds * 1000));
        var timedOut = !exited;

        if (!timedOut)
        {
            // 进程已退，但孙进程可能还握着管道写句柄：限时排空，拖过窗口就强杀整树
            if (await Task.WhenAny(pumping, Task.Delay(TimeSpan.FromSeconds(DrainGraceSeconds))) != pumping)
            {
                timedOut = true;
                BridgeLog.Info($"terminal 排空超时（孙进程握着管道），强杀进程树：{command}");
                TryKillTree(proc);
                await Task.WhenAny(pumping, Task.Delay(TimeSpan.FromSeconds(KillGraceSeconds)));
            }
        }
        else
        {
            BridgeLog.Info($"terminal 超时({TimeoutSeconds}s)，强杀进程树：{command}");
            TryKillTree(proc);
            await Task.WhenAny(pumping, Task.Delay(TimeSpan.FromSeconds(KillGraceSeconds)));
        }

        var stdout = OutputDecoder.Decode(stdoutBuf.ToArray());
        var stderr = OutputDecoder.Decode(stderrBuf.ToArray());
        int exitCode;
        try { exitCode = proc.ExitCode; }
        catch { exitCode = -1; }

        if (timedOut)
        {
            return Truncate($"[命令超过 {TimeoutSeconds}s 或输出管道未关闭，已被强制终止]\n{stdout}\n{stderr}\nexit code: {exitCode}");
        }

        BridgeLog.Info($"terminal 完成 exit={exitCode} 用时={sw.ElapsedMilliseconds}ms：{command}");
        return Truncate($"$ {command}\n{stdout}\n{stderr}\nexit code: {exitCode} · 用时 {sw.Elapsed.TotalSeconds:0.0}s");
    }

    private static async Task Pump(Stream source, MemoryStream destination)
    {
        try
        {
            await source.CopyToAsync(destination);
        }
        catch
        {
            // 管道被杀/句柄被关：保留已收字节即可
        }
    }

    private static void TryKillTree(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            BridgeLog.Info($"Kill(entireProcessTree) 失败（{ex.Message}），回退 taskkill");
            try
            {
                Process.Start(new ProcessStartInfo("taskkill", $"/PID {proc.Id} /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })?.WaitForExit(5000);
            }
            catch (Exception ex2)
            {
                BridgeLog.Info($"taskkill 回退也失败：{ex2.Message}");
            }
        }
    }

    private static string Truncate(string s)
    {
        s = s.Replace("\r\n", "\n");
        return s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + "\n…（输出超长，已截断）";
    }
}
