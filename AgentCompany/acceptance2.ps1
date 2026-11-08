# UIA acceptance test - Round 2 (claude integration)  (run from repo root)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = Join-Path $PSScriptRoot 'publish\AgentCompany.exe'
$script:pass = 0; $script:fail = 0

function Check($name, $cond) {
    if ($cond) { Write-Output "PASS  $name"; $script:pass++ }
    else       { Write-Output "FAIL  $name"; $script:fail++ }
}

function Start-App {
    $p = Start-Process -FilePath $exe -PassThru
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $p.Refresh()
        if ($p.HasExited) { throw "app exited early" }
        if ($p.MainWindowHandle -ne 0) { break }
    }
    Start-Sleep -Seconds 2
    $wnd = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
    if (-not $wnd) { throw 'main window not found' }
    return @{ Proc = $p; Wnd = $wnd }
}

function Find-ByName($wnd, $controlType, $name) {
    $t = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    $all = $wnd.FindAll([System.Windows.Automation.TreeScope]::Descendants, $t)
    foreach ($el in $all) { if ($el.Current.Name -eq $name) { return $el } }
    return $null
}

function Set-Text($el, $text) {
    ($el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($text)
}

function Click($el) {
    ($el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Select-Nav($wnd, $name) {
    $items = $wnd.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    foreach ($it in $items) {
        if ($it.Current.Name -eq $name) {
            ($it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            return $true
        }
    }
    return $false
}

function Get-State($wnd) {
    $el = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Text) '状态徽标'
    if ($el) { return $el.Current.Name } else { return '' }
}

function Get-EventCount($wnd) {
    $lists = $wnd.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)))
    foreach ($l in $lists) {
        if ($l.Current.Name -like '*事件流*') {
            return @($l.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))).Count
        }
    }
    return -1
}

function Get-EventLines($wnd) {
    $lines = @()
    $lists = $wnd.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)))
    foreach ($l in $lists) {
        if ($l.Current.Name -like '*事件流*') {
            $items = $l.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
            foreach ($it in $items) { $lines += $it.Current.Name }
        }
    }
    return $lines
}

function Wait-State($wnd, $target, $timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 800
        if ((Get-State $wnd) -eq $target) { return $true }
    }
    return ((Get-State $wnd) -eq $target)
}

function Dispatch($wnd, $task) {
    Select-Nav $wnd '运行监视' | Out-Null
    Start-Sleep -Milliseconds 600
    $input = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Edit) '任务输入'
    Set-Text $input $task
    Start-Sleep -Milliseconds 300
    $btn = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Button) '派活'
    $btn.SetFocus()
    Start-Sleep -Milliseconds 200
    Click $btn
}

function Node-Processes {
    return @(Get-Process node, claude -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
}

# ============ 准备：启动 ============
$app = Start-App
$wnd = $app.Wnd
$baseNodes = Node-Processes

# ============ ① 简单任务：实时性 + 摘要 ============
Dispatch $wnd '你好，用一句话自我介绍'
$cnt1 = Get-EventCount $wnd
$growth = 0; $last = $cnt1; $sawRunning = $false
$deadline = (Get-Date).AddSeconds(240)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $st = Get-State $wnd
    if ($st -eq '运行中') { $sawRunning = $true }
    $c = Get-EventCount $wnd
    if ($c -gt $last) { $growth++; $last = $c }
    if ($st -eq '空闲') { break }
}
Start-Sleep -Seconds 1
$reply = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Text) '最终回复'
$usage = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Text) '用量'
$replyText = if ($reply) { $reply.Current.Name } else { '' }
$usageText = if ($usage) { $usage.Current.Name } else { '' }
Check "[1] realtime growth (>=2 increments, got $growth)" ($growth -ge 2 -and $sawRunning)
Check "[1] reply shown" ($replyText.Length -gt 2 -and $replyText -ne '—')
Check "[1] usage shown (tokens+time)" ($usageText -match '输入 \d+.*输出 \d+.*缓存读 \d+.*用时 [\d.]+s')
Write-Output "      reply: $replyText"
Write-Output "      usage: $usageText"

# ============ ③ 组织页联动（已完成 → 空闲）============
Select-Nav $wnd '组织' | Out-Null
Start-Sleep -Milliseconds 800
$badge = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Text) 'claude状态'
$model = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Text) 'claude模型'
Check "[3] org badge idle after finish" ($badge.Current.Name -eq '空闲')
Check "[3] org card shows model" ($model.Current.Name -match 'glm-5\.3')

# ============ ② 工具调用 + 中文无乱码 ============
Dispatch $wnd '列出当前目录的文件名'
$deadline = (Get-Date).AddSeconds(240)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    if ((Get-State $wnd) -eq '空闲') { break }
}
Start-Sleep -Seconds 1
$lines = Get-EventLines $wnd
$hasTool = @($lines | Where-Object { $_ -match '🔧' }).Count -gt 0
$hasMojibake = @($lines | Where-Object { $_ -match 'ï¿½|锟斤|烫烫' }).Count -gt 0
Check "[2] tool_use line present" $hasTool
Check "[2] no mojibake in stream" (-not $hasMojibake)

# ============ ③（运行中联动）+ ④ 终止 ============
Dispatch $wnd '请从 1 慢慢数到 30，每个数字之间停顿几秒'
Start-Sleep -Seconds 12
Select-Nav $wnd '组织' | Out-Null
Start-Sleep -Milliseconds 800
$badge = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Text) 'claude状态'
Check "[3] org badge running during task" ($badge.Current.Name -eq '运行中')
Select-Nav $wnd '运行监视' | Out-Null
Start-Sleep -Milliseconds 600
$cancel = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Button) '终止'
Click $cancel
$ok = Wait-State $wnd '已终止' 20
Check "[4] cancelled state" $ok
Start-Sleep -Seconds 3
$left = @(Node-Processes | Where-Object { $baseNodes -notcontains $_ })
Check "[4] no orphan node/claude processes (left=$($left.Count))" ($left.Count -eq 0)

# ============ ⑤ 超时 10s ============
Select-Nav $wnd '设置' | Out-Null
Start-Sleep -Milliseconds 600
$ti = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Edit) '任务超时'
Set-Text $ti '10'
Start-Sleep -Milliseconds 300
# 触发失焦保存：点别的导航
Select-Nav $wnd '运行监视' | Out-Null
Start-Sleep -Milliseconds 600
Dispatch $wnd '请从 1 慢慢数到 50，每个数字之间停顿几秒'
$ok = Wait-State $wnd '超时' 30
Check "[5] timeout state" $ok
Start-Sleep -Seconds 2
$left = @(Node-Processes | Where-Object { $baseNodes -notcontains $_ })
Check "[5] no orphan after timeout" ($left.Count -eq 0)
# 恢复超时 180
Select-Nav $wnd '设置' | Out-Null
Start-Sleep -Milliseconds 600
$ti = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Edit) '任务超时'
Set-Text $ti '180'
Start-Sleep -Milliseconds 300
Select-Nav $wnd '运行监视' | Out-Null

# ============ ⑥ 历史会话重启仍在 ============
$cnt = Find-ByName $wnd ([System.Windows.Automation.ControlType]::Text) '历史会话'
$before = $cnt.Current.Name
$app.Proc.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 2
try { Stop-Process -Id $app.Proc.Id -Force -ErrorAction SilentlyContinue } catch {}
$app2 = Start-App
$wnd2 = $app2.Wnd
Select-Nav $wnd2 '运行监视' | Out-Null
Start-Sleep -Milliseconds 800
$cnt2 = Find-ByName $wnd2 ([System.Windows.Automation.ControlType]::Text) '历史会话'
Check "[6] session count survives restart ($before -> $($cnt2.Current.Name))" ($cnt2.Current.Name -eq $before -and $before -match '[1-9]')

Stop-Process -Id $app2.Proc.Id -Force
Write-Output "RESULT pass=$pass fail=$fail"
