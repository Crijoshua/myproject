# 冒烟：启动 exe → 导航到运行监视 → 读徽标/输入框/按钮 → 组织页徽标 → 设置页超时框 → 关闭
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = Join-Path $PSScriptRoot '..\publish\AgentCompany.exe'
$app = Start-Process $exe -PassThru
try {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    $root = $null
    $deadline = [DateTime]::Now.AddSeconds(30)
    while (-not $root -and [DateTime]::Now -lt $deadline) {
        $root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if (-not $root) { Start-Sleep -Milliseconds 400 }
    }
    if (-not $root) { throw '窗口未出现' }
    Write-Host "窗口: $($root.Current.Name)"

    function Find-ById([string]$id) {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    }
    function Find-ByName2([string]$name) {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
        $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    }

    foreach ($nav in '运行监视', '设置', '组织') {
        $item = Find-ByName2 $nav
        if (-not $item) { throw "nav 未找到: $nav" }
        Write-Host "nav $nav => $($item.Current.ControlType.ProgrammaticName)"
        $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 700
    }
    Write-Host ("org badge text: " + (Find-ById 'ClaudeBadge').Current.Name)
    Write-Host ("org model text: " + (Find-ById 'ClaudeModel').Current.Name)

    $item = Find-ByName2 '运行监视'
    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    Write-Host ("badge: " + (Find-ById 'StateBadge').Current.Name)
    Write-Host ("sessions: " + (Find-ById 'SessionCount').Current.Name)
    $ti = Find-ById 'TaskInput'
    Write-Host ("TaskInput ValuePattern: " + ($ti.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) -ne $null))
    $ti.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('冒烟测试输入：你好')
    Write-Host ("TaskInput now: " + $ti.Current.Name)
    Write-Host ("event list count: " + (Find-ById 'EventList').FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition).Count)
    $item = Find-ByName2 '设置'
    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    Write-Host ("timeout input: " + (Find-ById 'TimeoutInput').Current.Name)
    Write-Host 'SMOKE OK'
}
finally {
    if ($app -and -not $app.HasExited) { $app.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2; if (-not $app.HasExited) { $app.Kill() } }
}

