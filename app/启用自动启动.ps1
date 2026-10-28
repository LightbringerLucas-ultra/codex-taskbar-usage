$widgetExecutable = Join-Path $PSScriptRoot 'CodexTaskbarUsage.exe'
if (-not (Test-Path -LiteralPath $widgetExecutable)) { throw '未找到 CodexTaskbarUsage.exe' }
$widgetService = New-Object -ComObject Schedule.Service
$widgetService.Connect()
$widgetFolder = $widgetService.GetFolder('\')
$widgetTask = $widgetService.NewTask(0)
$widgetTask.RegistrationInfo.Description = '独立运行外置大脑剩余，防止 Codex 关闭时连带结束。当前用户权限，登录后显示。'
$widgetTask.Settings.Enabled = $true
$widgetTask.Settings.AllowDemandStart = $true
$widgetTask.Settings.DisallowStartIfOnBatteries = $false
$widgetTask.Settings.StopIfGoingOnBatteries = $false
$widgetTask.Settings.ExecutionTimeLimit = 'PT0S'
$widgetTask.Settings.StartWhenAvailable = $true
$widgetTask.Settings.RestartCount = 3
$widgetTask.Settings.RestartInterval = 'PT1M'
$widgetTask.Settings.MultipleInstances = 2
$widgetTask.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$widgetTask.Principal.LogonType = 3
$widgetTask.Principal.RunLevel = 0
$widgetTrigger = $widgetTask.Triggers.Create(9)
$widgetTrigger.Enabled = $true
$widgetTrigger.UserId = $widgetTask.Principal.UserId
$widgetTrigger.Delay = 'PT5S'
$widgetAction = $widgetTask.Actions.Create(0)
$widgetAction.Path = $widgetExecutable
$widgetAction.Arguments = '--scheduled'
$widgetAction.WorkingDirectory = $PSScriptRoot
$null = $widgetFolder.RegisterTaskDefinition('外置大脑剩余', $widgetTask, 6, $widgetTask.Principal.UserId, $null, 3)
$widgetLegacyStartup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex 剩余用量.lnk'
if (Test-Path -LiteralPath $widgetLegacyStartup) {
    $widgetLegacyShell = New-Object -ComObject WScript.Shell
    $widgetLegacyLink = $widgetLegacyShell.CreateShortcut($widgetLegacyStartup)
    if ($widgetLegacyLink.TargetPath -eq $widgetExecutable) { Remove-Item -LiteralPath $widgetLegacyStartup }
}
