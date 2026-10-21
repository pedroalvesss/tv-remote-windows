param([string]$Exe)
# Mede o TVRemote: memória em repouso, CPU em repouso e com o painel aberto, e quanto o painel leva para aparecer.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
Stop-Process -Name TVRemote -Force -ErrorAction SilentlyContinue
Start-Sleep 1
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $Exe -ArgumentList '--open' -PassThru
$cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id)
$win = $null
while (-not $win -and $sw.ElapsedMilliseconds -lt 15000) { $win = $A::RootElement.FindFirst($T::Children, $cond); if (-not $win) { Start-Sleep -Milliseconds 20 } }
"startup to visible panel: $($sw.ElapsedMilliseconds) ms"
Start-Sleep 8
$p.Refresh(); $c0 = $p.TotalProcessorTime
Start-Sleep 10
$p.Refresh(); "cpu with panel open (10 s): $([int]($p.TotalProcessorTime - $c0).TotalMilliseconds) ms"
"memory with panel open: working set $([int]($p.WorkingSet64/1MB)) MB, private $([int]($p.PrivateMemorySize64/1MB)) MB"
# fecha o painel tirando o foco dele
$shell = New-Object -ComObject WScript.Shell
$shell.AppActivate((Get-Process explorer | Select-Object -First 1).Id) | Out-Null
Start-Sleep 3
$p.Refresh(); $c0 = $p.TotalProcessorTime
Start-Sleep 10
$p.Refresh(); "cpu hidden (10 s): $([int]($p.TotalProcessorTime - $c0).TotalMilliseconds) ms"
"memory hidden: working set $([int]($p.WorkingSet64/1MB)) MB, private $([int]($p.PrivateMemorySize64/1MB)) MB"
