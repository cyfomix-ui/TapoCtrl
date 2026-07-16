param([int]$Port = 8900)

# Run this from an elevated / Administrator PowerShell.
$ErrorActionPreference = 'Stop'

$ruleName = "TapoCtrl Web Server $Port"
try { Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue } catch {}
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port | Out-Null

Write-Host "TapoCtrl WebServer firewall was configured for TCP port $Port"
Write-Host "v0.0.63 uses TcpListener, so netsh http urlacl is no longer required."
Write-Host "Restart TapoCtrl after running this script."
