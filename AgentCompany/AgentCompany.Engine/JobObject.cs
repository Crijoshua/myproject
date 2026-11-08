using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentCompany.Desktop.Services;

/// <summary>
/// Windows Job Object 陪葬机制（轮 5）：宿主启动的所有 CLI 子进程（cmd → node → claude/opencode →
/// McpBridge 整棵树）挂入同一个 Job，设 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE——宿主无论正常退出还是被
/// 任务管理器强杀，OS 关闭进程句柄即自动终结 Job 内全部进程，不给孤儿留窗口。
/// 注意：句柄刻意不关（App 生命周期内常驻）；正常退出路径另有 Dispose/taskkill 兜底。
/// </summary>
public static class JobObject
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly object Gate = new();
    private static IntPtr _handle;

    /// <summary>把进程挂入公司 Job（幂等建 Job；进程已退出时静默记日志）。</summary>
    public static void Assign(Process proc)
    {
        lock (Gate)
        {
            if (_handle == IntPtr.Zero)
            {
                _handle = CreateJobObject(IntPtr.Zero, null);
                if (_handle == IntPtr.Zero)
                    throw new InvalidOperationException($"CreateJobObject 失败：{Marshal.GetLastWin32Error()}");

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose,
                    },
                };
                if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation,
                        ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                    throw new InvalidOperationException($"SetInformationJobObject 失败：{Marshal.GetLastWin32Error()}");
            }

            // 子进程一旦入 Job，其后代自动继承；这里在 Start 后立即挂入（cmd 垫片到 spawn node 有
            // 数十 ms 量级，挂入在微秒级完成，竞态窗口极小；漏网者由 process_journal 启动清扫兜底）
            if (!AssignProcessToJobObject(_handle, proc.Handle))
                throw new InvalidOperationException($"AssignProcessToJobObject 失败：{Marshal.GetLastWin32Error()}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, uint infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
{
    public long PerProcessUserTimeLimit;
    public long PerJobUserTimeLimit;
    public uint LimitFlags;
    public nuint MinimumWorkingSetSize;
    public nuint MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public nuint Affinity;
    public uint PriorityClass;
    public uint SchedulingClass;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IO_COUNTERS
{
    public long ReadOperationCount;
    public long WriteOperationCount;
    public long OtherOperationCount;
    public long ReadTransferCount;
    public long WriteTransferCount;
    public long OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
{
    public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
    public IO_COUNTERS IoInfo;
    public nuint ProcessMemoryLimit;
    public nuint JobMemoryLimit;
    public nuint PeakProcessMemoryUsed;
    public nuint PeakJobMemoryUsed;
}

/// <summary>杂项 Win32 调用（轮 5：单实例激活已有窗口用）。轮 1.1 引擎下沉后跨程序集使用，改 public。</summary>
public static class NativeWin
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam,
        string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    /// <summary>
    /// 广播环境变量变更（WM_SETTINGCHANGE，lParam="Environment"）——v1.2 安装终端命令用：
    /// 注册表直写不触发 Explorer 环境刷新，不广播则"重开终端即可用"不成立（HWND_BROADCAST 0xFFFF）。
    /// </summary>
    public static void BroadcastEnvironmentChange()
    {
        const uint WmSettingChange = 0x001A;
        const uint SmtoAbortIfHung = 0x0002;
        _ = SendMessageTimeout((IntPtr)0xFFFF, WmSettingChange, UIntPtr.Zero, "Environment",
            SmtoAbortIfHung, 1000, out _);
    }

    /// <summary>把另一个 AgentCompany 实例的主窗口带到前台（最小化则先还原）。</summary>
    public static void ActivateOtherInstance()
    {
        try
        {
            var other = Process.GetProcessesByName("AgentCompany")
                .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
            if (other is null)
                return;
            var hwnd = other.MainWindowHandle;
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // 激活失败无碍：二次实例照样退出，用户自己点一下原窗口即可
        }
    }
}
