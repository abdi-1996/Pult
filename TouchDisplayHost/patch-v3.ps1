$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v2.0.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw
$text = $text.Replace('TouchDisplay Host v2.1', 'TouchDisplay Host v3.0')
$text = $text.Replace('Android-планшет → настоящий Windows Touch', 'Android • iPhone • iPad → Screen • Touch • Audio')
$text = $text.Replace('PIN для планшета:', 'Пароль для устройств:')
$text = $text.Replace('Готов v2.1 • Screen + Touch + Audio • LAN/Tailscale • Audio port 59434', 'Готов v3.0 • Android / iPhone / iPad • Screen + Touch + Audio • LAN / Tailscale')
Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v3.0 universal patch applied.'
