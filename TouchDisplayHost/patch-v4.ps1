$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v3.5.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v3.5', 'TouchDisplay Host v4.0')
$text = $text.Replace(
    'Готов v3.5 • Auto Portrait iPhone/iPad • Screen + Touch + Audio • LAN / Tailscale',
    'Готов v4.0 • Low Latency UDP • AI Priority • Touch + Audio • LAN / Tailscale')

# TD04 keeps the authenticated TCP socket for touch/control only and sends video
# over a latest-frame UDP path. Legacy TD01 clients remain compatible.
$oldMagic = @'
                var magic = new byte[4];
                await NetIo.ReadExactAsync(stream, magic, serverToken);
                if (Encoding.ASCII.GetString(magic) != "TD01") return;

                var pinLength = await NetIo.ReadInt32BigEndianAsync(stream, serverToken);
'@
$newMagic = @'
                var magic = new byte[4];
                await NetIo.ReadExactAsync(stream, magic, serverToken);
                var protocol = Encoding.ASCII.GetString(magic);
                var lowLatencyV4 = string.Equals(protocol, "TD04", StringComparison.Ordinal);
                if (!lowLatencyV4 && !string.Equals(protocol, "TD01", StringComparison.Ordinal)) return;

                var pinLength = await NetIo.ReadInt32BigEndianAsync(stream, serverToken);
'@
if (-not $text.Contains($oldMagic)) { throw 'v4 protocol magic target not found' }
$text = $text.Replace($oldMagic, $newMagic, 1)

$oldPinRead = @'
                var pinBytes = new byte[pinLength];
                await NetIo.ReadExactAsync(stream, pinBytes, serverToken);
                var suppliedPin = Encoding.UTF8.GetString(pinBytes);
'@
$newPinRead = @'
                var pinBytes = new byte[pinLength];
                await NetIo.ReadExactAsync(stream, pinBytes, serverToken);
                var udpVideoPort = 0;
                if (lowLatencyV4)
                {
                    udpVideoPort = await NetIo.ReadInt32BigEndianAsync(stream, serverToken);
                    if (udpVideoPort < 1024 || udpVideoPort > 65535) return;
                }
                var suppliedPin = Encoding.UTF8.GetString(pinBytes);
'@
if (-not $text.Contains($oldPinRead)) { throw 'v4 udp port handshake target not found' }
$text = $text.Replace($oldPinRead, $newPinRead, 1)

$oldTasks = @'
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                var displayProfile = new ClientDisplayProfile();
                using var displayRotation = new DisplayRotationSession(_status);
                var send = StreamFramesAsync(stream, displayProfile, linked.Token);
                var receive = ReceiveInputAsync(stream, displayProfile, displayRotation, linked.Token);
                await Task.WhenAny(send, receive);
'@
$newTasks = @'
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                var displayProfile = new ClientDisplayProfile();
                using var displayRotation = new DisplayRotationSession(_status);
                var send = lowLatencyV4
                    ? LowLatencyUdpStreamer.StreamAsync(client, udpVideoPort, displayProfile, _status, linked.Token)
                    : StreamFramesAsync(stream, displayProfile, linked.Token);
                var receive = ReceiveInputAsync(stream, displayProfile, displayRotation, linked.Token);
                await Task.WhenAny(send, receive);
'@
if (-not $text.Contains($oldTasks)) { throw 'v4 low latency task target not found' }
$text = $text.Replace($oldTasks, $newTasks, 1)

# Video feedback: [0x21][fps u16][loss permille u16][avg decode ms u16]
$oldTypeStart = @'
            var type = await NetIo.ReadByteAsync(stream, token);
            var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
            if (type == 0x12)
'@
$newTypeStart = @'
            var type = await NetIo.ReadByteAsync(stream, token);
            var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
            if (type == 0x21)
            {
                var feedback = new byte[6];
                await NetIo.ReadExactAsync(stream, feedback, token);
                var clientFps = (feedback[0] << 8) | feedback[1];
                var lossPermille = (feedback[2] << 8) | feedback[3];
                var decodeMs = (feedback[4] << 8) | feedback[5];
                profile.ReportFeedback(clientFps, lossPermille, decodeMs);
                continue;
            }

            if (type == 0x12)
'@
if (-not $text.Contains($oldTypeStart)) { throw 'v4 feedback packet target not found' }
$text = $text.Replace($oldTypeStart, $newTypeStart, 1)

$oldProfile = @'
internal sealed class ClientDisplayProfile
{
    private int _width;
    private int _height;
    private int _quality = 82;
    private int _fps = 40;

    public int Width => Volatile.Read(ref _width);
    public int Height => Volatile.Read(ref _height);
    public int Quality => Volatile.Read(ref _quality);
    public int Fps => Volatile.Read(ref _fps);

    public void Update(int width, int height, int quality, int fps)
    {
        Interlocked.Exchange(ref _width, Math.Clamp(width, 0, 4096));
        Interlocked.Exchange(ref _height, Math.Clamp(height, 0, 4096));
        Interlocked.Exchange(ref _quality, Math.Clamp(quality, 68, 90));
        Interlocked.Exchange(ref _fps, Math.Clamp(fps, 20, 45));
    }
}
'@
$newProfile = @'
internal sealed class ClientDisplayProfile
{
    private int _width;
    private int _height;
    private int _quality = 90;
    private int _fps = 60;
    private int _clientReportedFps = 60;
    private int _lossPermille;
    private int _decodeMs;
    private int _hasFeedback;

    public int Width => Volatile.Read(ref _width);
    public int Height => Volatile.Read(ref _height);
    public int Quality => Volatile.Read(ref _quality);
    public int Fps => Volatile.Read(ref _fps);
    public int ClientReportedFps => Volatile.Read(ref _clientReportedFps);
    public int LossPermille => Volatile.Read(ref _lossPermille);
    public int DecodeMs => Volatile.Read(ref _decodeMs);
    public bool HasFeedback => Volatile.Read(ref _hasFeedback) != 0;

    public void Update(int width, int height, int quality, int fps)
    {
        Interlocked.Exchange(ref _width, Math.Clamp(width, 0, 4096));
        Interlocked.Exchange(ref _height, Math.Clamp(height, 0, 4096));
        Interlocked.Exchange(ref _quality, Math.Clamp(quality, 68, 92));
        Interlocked.Exchange(ref _fps, Math.Clamp(fps, 20, 60));
    }

    public void ReportFeedback(int clientFps, int lossPermille, int decodeMs)
    {
        Interlocked.Exchange(ref _clientReportedFps, Math.Clamp(clientFps, 0, 240));
        Interlocked.Exchange(ref _lossPermille, Math.Clamp(lossPermille, 0, 1000));
        Interlocked.Exchange(ref _decodeMs, Math.Clamp(decodeMs, 0, 1000));
        Interlocked.Exchange(ref _hasFeedback, 1);
    }
}
'@
if (-not $text.Contains($oldProfile)) { throw 'v4 client profile target not found' }
$text = $text.Replace($oldProfile, $newProfile, 1)

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v4.0 low-latency UDP + AI Priority patch applied.'
