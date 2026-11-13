# ============================================================
# start-cmd.ps1 —— R2 迭代：手机可视化操作 CMD 命令行（Web 终端 ttyd）启动脚本
#
# 功能：
#   1. 从同目录 credentials.md 解析 Basic Auth 用户名/密码；
#   2. 启动 ..\tools\ttyd\ttyd.exe，监听 127.0.0.1:9801；
#   3. 客户端 shell 为子进程 pwsh.exe，进入终端后先执行
#      [Console]::OutputEncoding=[System.Text.Encoding]::UTF8 与 chcp 65001，
#      确保手机浏览器中中文输出无乱码；
#   4. 独立维护 cmd.pid / cmd.out.log / cmd.err.log 三个文件；
#   5. 启动前检查 cmd.pid 对应进程是否仍在运行，避免重复启动。
#
# 使用建议：本脚本为 UTF-8（无 BOM）编码，请使用 PowerShell 7+（pwsh）运行，
#           以保证中文提示正常显示。
# ============================================================

# ---------- 1. 路径定位（全部基于 $PSScriptRoot，不写死绝对路径） ----------
$scriptRoot = $PSScriptRoot
$credFile   = Join-Path $scriptRoot 'credentials.md'          # 凭据文件（同目录）
$pidFile    = Join-Path $scriptRoot 'cmd.pid'                 # ttyd 进程号文件
$outLog     = Join-Path $scriptRoot 'cmd.out.log'             # 标准输出日志
$errLog     = Join-Path $scriptRoot 'cmd.err.log'             # 错误日志
$ttydExe    = Join-Path $scriptRoot 'tools\ttyd\ttyd.exe'    # ttyd 主程序（交付-v2\tools\ttyd\，计划 2.0 约定位置）

# ---------- 2. 防重复启动：检查 cmd.pid 对应进程是否仍在运行 ----------
if (Test-Path -LiteralPath $pidFile) {
    $oldRaw = Get-Content -LiteralPath $pidFile -ErrorAction SilentlyContinue | Select-Object -First 1
    $oldPid = 0
    if ([int]::TryParse("$oldRaw".Trim(), [ref]$oldPid) -and $oldPid -gt 0) {
        $oldProc = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
        if ($null -ne $oldProc) {
            if ($oldProc.ProcessName -ieq 'ttyd') {
                # pid 文件有效且 ttyd 仍在运行：中文提示并跳过启动
                Write-Host "检测到 ttyd 已在运行（PID: $oldPid），跳过启动；如需重启请先运行 stop-cmd.ps1。"
                exit 0
            }
            # 进程号被其他程序复用，说明 pid 文件是残留的
            Write-Host "cmd.pid 中的 PID $oldPid 已被其他进程（$($oldProc.ProcessName)）占用，视为残留文件并清理。"
        }
    }
    # pid 文件不存在有效进程号：清理残留文件后继续启动
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
}

# ---------- 3. 解析 credentials.md 中的 Basic Auth 凭据 ----------
# 文件内约定格式（与 credentials.md 保持一致，键必须顶格、整行只有键与值）：
#   ttyd_user: <用户名>
#   ttyd_pass: <密码>
$basicUser = $null
$basicPass = $null

if (-not (Test-Path -LiteralPath $credFile)) {
    Write-Host "错误：未找到凭据文件（$credFile），无法启动 ttyd。"
    exit 1
}

# 逐行按 "键: 值" 解析，取第一次出现的有效键；-Encoding UTF8 保证特殊字符读取正确
foreach ($line in @(Get-Content -LiteralPath $credFile -Encoding UTF8)) {
    if ($null -eq $basicUser -and $line -match '^ttyd_user\s*:\s*(\S+)\s*$') {
        $basicUser = $Matches[1]
    }
    elseif ($null -eq $basicPass -and $line -match '^ttyd_pass\s*:\s*(\S+)\s*$') {
        $basicPass = $Matches[1]
    }
}

if ([string]::IsNullOrWhiteSpace($basicUser) -or [string]::IsNullOrWhiteSpace($basicPass)) {
    Write-Host '错误：未能从 credentials.md 解析出 ttyd_user / ttyd_pass，请检查文件格式（键必须顶格书写）。'
    exit 1
}

# ---------- 4. 检查 ttyd 主程序与 pwsh 是否可用 ----------
if (-not (Test-Path -LiteralPath $ttydExe)) {
    Write-Host "错误：未找到 ttyd 主程序：$ttydExe"
    exit 1
}

# 定位客户端 shell：优先 pwsh.exe（PowerShell 7+），未安装时回退 powershell.exe（Windows 内置）
# （本机修复：无 pwsh 时不再硬性退出，回退 5.1 同样执行 UTF-8 初始化）
$shellCmd = Get-Command -Name 'pwsh.exe' -ErrorAction SilentlyContinue
if ($null -eq $shellCmd) {
    $shellCmd = Get-Command -Name 'powershell.exe' -ErrorAction SilentlyContinue
    if ($null -eq $shellCmd) {
        Write-Host '错误：未找到 pwsh.exe 或 powershell.exe，无法启动终端。'
        exit 1
    }
    Write-Host '提示：未安装 PowerShell 7（pwsh），已回退使用 Windows PowerShell 5.1。'
}
$pwshExe = $shellCmd.Source

# ---------- 5. 组装 ttyd 启动参数 ----------
# 客户端 shell 为 pwsh 子进程；进入终端后先执行：
#   [Console]::OutputEncoding=[System.Text.Encoding]::UTF8  —— 控制台输出编码切到 UTF-8
#   chcp 65001                                              —— 代码页切到 UTF-8（中文无乱码）
$clientInit = '[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; chcp 65001 | Out-Null'

# 手工拼好带引号的参数串，避免不同版本 PowerShell 对 -ArgumentList 的引号处理差异；
# -W 表示允许手机端向终端写入输入（可交互）；-c 的 Basic Auth 凭据来自 credentials.md。
$argString = '-i 127.0.0.1 -p 9801 -c "{0}:{1}" -W "{2}" -NoExit -Command "{3}"' -f $basicUser, $basicPass, $pwshExe, $clientInit

# ---------- 6. 启动 ttyd（隐藏窗口），标准输出/错误分别重定向到独立日志 ----------
$proc = Start-Process -FilePath $ttydExe `
                      -ArgumentList $argString `
                      -WorkingDirectory $scriptRoot `
                      -WindowStyle Hidden `
                      -RedirectStandardOutput $outLog `
                      -RedirectStandardError $errLog `
                      -PassThru

if ($null -eq $proc) {
    Write-Host '错误：ttyd 启动失败，请查看 cmd.err.log。'
    exit 1
}

# ---------- 7. 记录 pid 并确认进程未立即退出 ----------
Set-Content -LiteralPath $pidFile -Value $proc.Id
Start-Sleep -Milliseconds 800
$proc.Refresh()
if ($proc.HasExited) {
    Write-Host "错误：ttyd 启动后立即退出（PID: $($proc.Id)），请查看日志：$errLog 与 $outLog"
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
    exit 1
}

Write-Host "ttyd 已启动（PID: $($proc.Id)），监听 127.0.0.1:9801。"
Write-Host "访问地址：http://127.0.0.1:9801/  （Basic Auth 用户名：$basicUser）"
Write-Host "日志文件：$outLog（标准输出）/ $errLog（错误）"

# ---------- 8. 启动 ttyd 网关（9802）：解决 iOS Safari 不在 WS 升级带 Basic 凭据的问题 ----------
$gwPidFile = Join-Path $scriptRoot 'gateway.pid'
$gwOutLog  = Join-Path $scriptRoot 'gateway.out.log'
$gwErrLog  = Join-Path $scriptRoot 'gateway.err.log'
$gwJs      = Join-Path $scriptRoot 'ws-gateway.js'
$nodeCmd   = Get-Command -Name 'node.exe' -ErrorAction SilentlyContinue
if ($null -eq $nodeCmd) {
    Write-Host '错误：未找到 node.exe，网关无法启动（门户页同依赖 node）。'
    exit 1
}
$gwOld = Get-Content -LiteralPath $gwPidFile -ErrorAction SilentlyContinue | Select-Object -First 1
$gwOldPid = 0
if ([int]::TryParse("$gwOld".Trim(), [ref]$gwOldPid) -and $gwOldPid -gt 0) {
    $gwProc = Get-Process -Id $gwOldPid -ErrorAction SilentlyContinue
    if ($null -ne $gwProc -and $gwProc.ProcessName -ieq 'node') {
        Write-Host "检测到网关已在运行（PID: $gwOldPid），跳过启动。"
        exit 0
    }
    Remove-Item -LiteralPath $gwPidFile -Force -ErrorAction SilentlyContinue
}
$gw = Start-Process -FilePath $nodeCmd.Source `
                    -ArgumentList ('"' + $gwJs + '"') `
                    -WorkingDirectory $scriptRoot `
                    -WindowStyle Hidden `
                    -RedirectStandardOutput $gwOutLog `
                    -RedirectStandardError $gwErrLog `
                    -PassThru
Set-Content -LiteralPath $gwPidFile -Value $gw.Id
Start-Sleep -Milliseconds 800
$gw.Refresh()
if ($gw.HasExited) {
    Write-Host "错误：网关启动后立即退出（PID: $($gw.Id)），请查看 $gwErrLog"
    Remove-Item -LiteralPath $gwPidFile -Force -ErrorAction SilentlyContinue
    exit 1
}
Write-Host "网关已启动（PID: $($gw.Id)），监听 127.0.0.1:9802 → 9801（iOS Safari WebSocket 兼容）。"
exit 0
