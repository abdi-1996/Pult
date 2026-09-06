$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v1.7.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v1.7', 'TouchDisplay Host v2.0')

# Audio server runs on a separate low-latency TCP channel so touch/video never wait on audio.
$fieldMarker = '    private UdpClient? _discovery;'
$fieldReplacement = "    private UdpClient? _discovery;`r`n    private AudioLoopbackServer? _audio;"
if (-not $text.Contains($fieldMarker)) { throw 'v2 audio field marker not found' }
$text = $text.Replace($fieldMarker, $fieldReplacement)

$startMarker = '        StartDiscovery(_cts.Token);'
$startReplacement = "        StartDiscovery(_cts.Token);`r`n        _audio = new AudioLoopbackServer(59434, _pin, _status);`r`n        _audio.Start();"
if (-not $text.Contains($startMarker)) { throw 'v2 audio start marker not found' }
$text = $text.Replace($startMarker, $startReplacement)

$stopMarker = '        try { _discovery?.Dispose(); } catch { }'
$stopReplacement = "        try { _discovery?.Dispose(); } catch { }`r`n        try { _audio?.Stop(); } catch { }`r`n        _audio = null;"
if (-not $text.Contains($stopMarker)) { throw 'v2 audio stop marker not found' }
$text = $text.Replace($stopMarker, $stopReplacement)

# Prefer a non-primary Windows display. When TouchDisplay IDD is installed this is the real Display 2.
$oldBounds = '        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;'
$newBounds = '        var bounds = GetTargetScreenBounds();'
$count = ([regex]::Matches($text, [regex]::Escape($oldBounds))).Count
if ($count -lt 2) { throw "v2 target screen markers not found ($count)" }
$text = $text.Replace($oldBounds, $newBounds)

$streamMarker = '    private static async Task StreamFramesAsync(NetworkStream stream, CancellationToken token)'
$helper = @'
    private static Rectangle GetTargetScreenBounds()
    {
        var secondary = Screen.AllScreens.FirstOrDefault(s => !s.Primary);
        return secondary?.Bounds ?? Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
    }

'@
if (-not $text.Contains($streamMarker)) { throw 'v2 target screen helper insertion marker not found' }
$text = $text.Replace($streamMarker, $helper + $streamMarker)

$text = $text.Replace(
    'SetStatus($"Готов v1.7. Введите на планшете 6-значный пароль. ПК определяется по паролю.");',
    'SetStatus($"Готов v2.0 • Display 2: {(Screen.AllScreens.Length > 1 ? "найден" : "пока нет")} • Audio: 59434");')

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v2.0 Display2/audio patch applied.'
