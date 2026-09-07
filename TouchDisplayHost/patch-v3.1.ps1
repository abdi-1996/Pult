$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v3.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v3.0', 'TouchDisplay Host v3.1')
$text = $text.Replace('Готов v3.0 • Android / iPhone / iPad • Screen + Touch + Audio • LAN / Tailscale', 'Готов v3.1 • Adaptive iPhone/iPad • Screen + Touch + Audio • LAN / Tailscale')

$oldTasks = @'
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                var send = StreamFramesAsync(stream, linked.Token);
                var receive = ReceiveInputAsync(stream, linked.Token);
'@
$newTasks = @'
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                var displayProfile = new ClientDisplayProfile();
                var send = StreamFramesAsync(stream, displayProfile, linked.Token);
                var receive = ReceiveInputAsync(stream, displayProfile, linked.Token);
'@
if (-not $text.Contains($oldTasks)) { throw 'v3.1 task patch target not found' }
$text = $text.Replace($oldTasks, $newTasks)

$oldStream = @'
    private static async Task StreamFramesAsync(NetworkStream stream, CancellationToken token)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        const int maxWidth = 2560;
        const int frameDelayMs = 25;
        const long jpegQuality = 82L;

        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            using var bitmap = CaptureScreen(bounds, maxWidth);
            using var ms = new MemoryStream(2 * 1024 * 1024);
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
            bitmap.Save(ms, JpegCodec, encoderParams);
            var bytes = ms.ToArray();
            await NetIo.WriteInt32BigEndianAsync(stream, bytes.Length, token);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);

            var spent = (int)(Environment.TickCount64 - started);
            var delay = Math.Max(1, frameDelayMs - spent);
            await Task.Delay(delay, token);
        }
    }

    private static Bitmap CaptureScreen(Rectangle bounds, int maxWidth)
    {
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(source))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        if (source.Width <= maxWidth)
            return new Bitmap(source);

        var scale = maxWidth / (double)source.Width;
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var target = new Bitmap(maxWidth, h, PixelFormat.Format24bppRgb);
        using var tg = Graphics.FromImage(target);
        tg.CompositingQuality = CompositingQuality.HighSpeed;
        tg.InterpolationMode = InterpolationMode.Bilinear;
        tg.SmoothingMode = SmoothingMode.HighSpeed;
        tg.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        tg.DrawImage(source, new Rectangle(0, 0, target.Width, target.Height));
        return target;
    }
'@
$newStream = @'
    private static async Task StreamFramesAsync(NetworkStream stream, ClientDisplayProfile profile, CancellationToken token)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;

        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            var maxWidth = profile.Width > 0 ? Math.Clamp(profile.Width, 960, 3072) : 2560;
            var maxHeight = profile.Height > 0 ? Math.Clamp(profile.Height, 540, 2048) : 1600;
            var frameDelayMs = Math.Max(16, 1000 / Math.Clamp(profile.Fps, 20, 45));
            var jpegQuality = (long)Math.Clamp(profile.Quality, 68, 90);

            using var bitmap = CaptureScreen(bounds, maxWidth, maxHeight);
            using var ms = new MemoryStream(2 * 1024 * 1024);
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
            bitmap.Save(ms, JpegCodec, encoderParams);
            var bytes = ms.ToArray();
            await NetIo.WriteInt32BigEndianAsync(stream, bytes.Length, token);
            await stream.WriteAsync(bytes, token);
            await stream.FlushAsync(token);

            var spent = (int)(Environment.TickCount64 - started);
            var delay = Math.Max(1, frameDelayMs - spent);
            await Task.Delay(delay, token);
        }
    }

    private static Bitmap CaptureScreen(Rectangle bounds, int maxWidth, int maxHeight)
    {
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(source))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)source.Width, maxHeight / (double)source.Height));
        if (scale >= 0.999)
            return new Bitmap(source);

        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var target = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var tg = Graphics.FromImage(target);
        tg.CompositingQuality = CompositingQuality.HighSpeed;
        tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
        tg.SmoothingMode = SmoothingMode.HighSpeed;
        tg.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        tg.DrawImage(source, new Rectangle(0, 0, target.Width, target.Height));
        return target;
    }
'@
if (-not $text.Contains($oldStream)) { throw 'v3.1 stream patch target not found' }
$text = $text.Replace($oldStream, $newStream)

$oldReceiveSig = '    private static async Task ReceiveInputAsync(NetworkStream stream, CancellationToken token)'
$newReceiveSig = '    private static async Task ReceiveInputAsync(NetworkStream stream, ClientDisplayProfile profile, CancellationToken token)'
if (-not $text.Contains($oldReceiveSig)) { throw 'v3.1 receive signature target not found' }
$text = $text.Replace($oldReceiveSig, $newReceiveSig)

$oldType = @'
            var type = await NetIo.ReadByteAsync(stream, token);
            if (type == 0x11)
'@
$newType = @'
            var type = await NetIo.ReadByteAsync(stream, token);
            if (type == 0x12)
            {
                var displayPacket = new byte[10];
                await NetIo.ReadExactAsync(stream, displayPacket, token);
                var width = NetIo.Int32FromBigEndian(displayPacket.AsSpan(0, 4));
                var height = NetIo.Int32FromBigEndian(displayPacket.AsSpan(4, 4));
                var quality = displayPacket[8];
                var fps = displayPacket[9];
                profile.Update(width, height, quality, fps);
                continue;
            }

            if (type == 0x11)
'@
if (-not $text.Contains($oldType)) { throw 'v3.1 display packet insertion target not found' }
$text = $text.Replace($oldType, $newType)

$marker = 'internal static class NetIo'
$profileClass = @'
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
if (-not $text.Contains($marker)) { throw 'v3.1 profile marker not found' }
$text = $text.Replace($marker, $profileClass + $marker)

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v3.1 adaptive client display patch applied.'
