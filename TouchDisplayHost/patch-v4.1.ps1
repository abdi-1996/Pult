$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v4.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v4.0', 'TouchDisplay Host v4.1')
$text = $text.Replace(
    'Готов v4.0 • Low Latency UDP • AI Priority • Touch + Audio • LAN / Tailscale',
    'Готов v4.1 • UDP + FEC • Smart Bitrate • AI Priority • 10ms Audio • Tailscale')

$oldProtocol = @'
                var protocol = Encoding.ASCII.GetString(magic);
                var lowLatencyV4 = string.Equals(protocol, "TD04", StringComparison.Ordinal);
                if (!lowLatencyV4 && !string.Equals(protocol, "TD01", StringComparison.Ordinal)) return;
'@
$newProtocol = @'
                var protocol = Encoding.ASCII.GetString(magic);
                var lowLatencyV5 = string.Equals(protocol, "TD05", StringComparison.Ordinal);
                var lowLatencyV4 = lowLatencyV5 || string.Equals(protocol, "TD04", StringComparison.Ordinal);
                if (!lowLatencyV4 && !string.Equals(protocol, "TD01", StringComparison.Ordinal)) return;
'@
if (-not $text.Contains($oldProtocol)) { throw 'v4.1 protocol target not found' }
$text = $text.Replace($oldProtocol, $newProtocol, 1)

$oldStreamer = 'LowLatencyUdpStreamer.StreamAsync(client, udpVideoPort, displayProfile, _status, linked.Token)'
$newStreamer = 'LowLatencyUdpStreamer.StreamAsync(client, udpVideoPort, displayProfile, _status, linked.Token, lowLatencyV5)'
if (-not $text.Contains($oldStreamer)) { throw 'v4.1 streamer target not found' }
$text = $text.Replace($oldStreamer, $newStreamer, 1)

# Remote input immediately wakes full-rate video so cursor/touch feedback never waits
# for the static-desktop power saver.
$oldRight = @'
                MouseInjector.RightClick(mouseX, mouseY, bounds);
                continue;
'@
$newRight = @'
                profile.MarkInteraction();
                MouseInjector.RightClick(mouseX, mouseY, bounds);
                continue;
'@
if (-not $text.Contains($oldRight)) { throw 'v4.1 right-click activity target not found' }
$text = $text.Replace($oldRight, $newRight, 1)

$injectNeedle = '            var result = TouchInjector.Inject(action, pointerId, nx, ny, bounds);'
$injectReplacement = "            profile.MarkInteraction();`r`n            var result = TouchInjector.Inject(action, pointerId, nx, ny, bounds);"
if (-not $text.Contains($injectNeedle)) { throw 'v4.1 touch activity target not found' }
$text = $text.Replace($injectNeedle, $injectReplacement, 1)

$oldProfileTail = @'
    public void ReportFeedback(int clientFps, int lossPermille, int decodeMs)
    {
        Interlocked.Exchange(ref _clientReportedFps, Math.Clamp(clientFps, 0, 240));
        Interlocked.Exchange(ref _lossPermille, Math.Clamp(lossPermille, 0, 1000));
        Interlocked.Exchange(ref _decodeMs, Math.Clamp(decodeMs, 0, 1000));
        Interlocked.Exchange(ref _hasFeedback, 1);
    }
}
'@
$newProfileTail = @'
    private long _lastInteractionTick;
    public long LastInteractionTick => Interlocked.Read(ref _lastInteractionTick);

    public void ReportFeedback(int clientFps, int lossPermille, int decodeMs)
    {
        Interlocked.Exchange(ref _clientReportedFps, Math.Clamp(clientFps, 0, 240));
        Interlocked.Exchange(ref _lossPermille, Math.Clamp(lossPermille, 0, 1000));
        Interlocked.Exchange(ref _decodeMs, Math.Clamp(decodeMs, 0, 1000));
        Interlocked.Exchange(ref _hasFeedback, 1);
    }

    public void MarkInteraction()
    {
        Interlocked.Exchange(ref _lastInteractionTick, Environment.TickCount64);
    }
}
'@
if (-not $text.Contains($oldProfileTail)) { throw 'v4.1 profile interaction target not found' }
$text = $text.Replace($oldProfileTail, $newProfileTail, 1)

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v4.1 FEC + Smart Bitrate + interaction priority patch applied.'
