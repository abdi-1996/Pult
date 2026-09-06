$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v1.7.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v1.7', 'TouchDisplay Host v2.1')

# Audio runs on its own low-latency TCP channel so screen/touch never wait on sound.
$fieldMarker = '    private UdpClient? _discovery;'
$fieldReplacement = "    private UdpClient? _discovery;`r`n    private AudioLoopbackServer? _audio;"
if (-not $text.Contains($fieldMarker)) { throw 'v2.1 audio field marker not found' }
$text = $text.Replace($fieldMarker, $fieldReplacement)

$startMarker = '        StartDiscovery(_cts.Token);'
$startReplacement = @'
        StartDiscovery(_cts.Token);
        try
        {
            _audio = new AudioLoopbackServer(59434, _pin, _status);
            _audio.Start();
        }
        catch (Exception ex)
        {
            _audio = null;
            _status("Audio недоступно: " + ex.Message, true);
        }
'@
if (-not $text.Contains($startMarker)) { throw 'v2.1 audio start marker not found' }
$text = $text.Replace($startMarker, $startReplacement)

$stopMarker = '        try { _discovery?.Dispose(); } catch { }'
$stopReplacement = "        try { _discovery?.Dispose(); } catch { }`r`n        try { _audio?.Stop(); } catch { }`r`n        _audio = null;"
if (-not $text.Contains($stopMarker)) { throw 'v2.1 audio stop marker not found' }
$text = $text.Replace($stopMarker, $stopReplacement)

# Keep streaming the primary desktop. Display 2/IDD is intentionally removed.
$text = $text.Replace(
    'SetStatus($"Готов v1.7. Введите на планшете 6-значный пароль. ПК определяется по паролю.");',
    'SetStatus($"Готов v2.1 • Screen + Touch + Audio • LAN/Tailscale • Audio port 59434");')

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v2.1 audio + Tailscale patch applied.'
