# UIA acceptance test for publish\AgentCompany.exe  (run from repo root)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = Join-Path $PSScriptRoot 'publish\AgentCompany.exe'
$db  = Join-Path $env:APPDATA 'AgentCompany\state.db'
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

function Find-Texts($wnd) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    return $wnd.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-Buttons($wnd) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    return $wnd.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-Radios($wnd) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)
    return $wnd.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
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

function Get-ThemeFromDb {
    $out = & dotnet run --project (Join-Path $env:TEMP 'dbcheck') --no-build 2>$null | Where-Object { $_ -like 'theme=*' }
    return ([string]$out).Substring(6)
}

# ---------- round 1: launch ----------
$app = Start-App
$wnd = $app.Wnd
$app.Proc.Refresh()
Check "[1] window title OK" ($app.Proc.MainWindowTitle -eq '小公司 · Agent 指挥中心')

$items = $wnd.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
$names = @($items | ForEach-Object { $_.Current.Name })
$expected = @('组织','任务看板','运行监视','审批中心','预算总览','设置')
$missing = @($expected | Where-Object { $names -notcontains $_ })
Check "[1] six nav items present" ($missing.Count -eq 0)

# ---------- round 2: click through all six ----------
$crash = $false
foreach ($n in $expected) {
    if (-not (Select-Nav $wnd $n)) { $crash = $true; break }
    Start-Sleep -Milliseconds 600
    $app.Proc.Refresh()
    if ($app.Proc.HasExited) { $crash = $true; break }
}
Check "[2] six pages switch without crash" (-not $crash)

# ---------- round 4/5: monitor page ----------
Select-Nav $wnd '运行监视' | Out-Null
Start-Sleep -Milliseconds 800
$texts = Find-Texts $wnd
$dbText = @($texts | Where-Object { $_.Current.Name -like '状态库 OK*' })
Check "[4] db status line" ($dbText.Count -ge 1 -and $dbText[0].Current.Name -eq '状态库 OK：tasks 0 / approvals 0 / sessions 0')
$navEvents = @($items | ForEach-Object { $_.Current.Name })  # stale list; re-query for event items
$items = $wnd.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
$evNames = @($items | ForEach-Object { $_.Current.Name })
$navCount = @($evNames | Where-Object { $_ -like '*导航 →*' }).Count
Check "[5] nav events in stream (count=$navCount)" ($navCount -ge 6)

# ---------- round 3: theme switch + persist ----------
Select-Nav $wnd '设置' | Out-Null
Start-Sleep -Milliseconds 800
$radios = Find-Radios $wnd
$dark = @($radios | Where-Object { $_.Current.Name -eq '暗色' })
Check "[3] dark radio exists" ($dark.Count -eq 1)
($dark[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
Start-Sleep -Milliseconds 800
$theme = Get-ThemeFromDb
Check "[3] theme=dark persisted (db=$theme)" ($theme -eq 'dark')
$app.Proc.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2
try { Stop-Process -Id $app.Proc.Id -Force -ErrorAction SilentlyContinue } catch {}

# restart, verify remembered
$app2 = Start-App
$wnd2 = $app2.Wnd
Select-Nav $wnd2 '设置' | Out-Null
Start-Sleep -Milliseconds 800
$radios2 = Find-Radios $wnd2
$darkSel = @($radios2 | Where-Object { $_.Current.Name -eq '暗色' -and $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected })
Check "[3] after restart dark still selected" ($darkSel.Count -eq 1)

# ---------- round 6: reset default via ContentDialog ----------
$buttons = Find-Buttons $wnd2
$resetBtn = @($buttons | Where-Object { $_.Current.Name -eq '恢复默认' })
Check "[6] reset button exists" ($resetBtn.Count -eq 1)
($resetBtn[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Start-Sleep -Milliseconds 1200
$buttons = Find-Buttons $wnd2
$resetAll = @($buttons | Where-Object { $_.Current.Name -eq '恢复默认' })
Check "[6] ContentDialog shown (2nd reset button=$($resetAll.Count))" ($resetAll.Count -ge 2)
if ($resetAll.Count -ge 2) {
    ($resetAll[1].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 800
}
$theme = Get-ThemeFromDb
Check "[6] theme back to system (db=$theme)" ($theme -eq 'system')
$radios3 = Find-Radios $wnd2
$sysSel = @($radios3 | Where-Object { $_.Current.Name -eq '跟随系统' -and $_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected })
Check "[6] follow-system radio selected" ($sysSel.Count -eq 1)

Stop-Process -Id $app2.Proc.Id -Force
Write-Output "RESULT pass=$pass fail=$fail"
