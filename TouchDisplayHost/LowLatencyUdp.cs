using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Windows.Forms;

namespace TouchDisplayHost;

internal static class LowLatencyUdpStreamer
{
    // Keep packets below common WireGuard/Tailscale MTUs so one lost IP fragment
    // never destroys a whole large UDP datagram.
    public const int HeaderSize = 20;
    public const int PayloadSize = 1160;
    private static readonly byte[] Magic = { (byte)'T', (byte)'D', (byte)'U', (byte)'4' };
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders()
        .First(x => x.FormatID == ImageFormat.Jpeg.Guid);

    public static async Task StreamAsync(
        TcpClient controlClient,
        int udpPort,
        ClientDisplayProfile profile,
        Action<string, bool> status,
        CancellationToken token)
    {
        if (controlClient.Client.RemoteEndPoint is not IPEndPoint tcpRemote)
            throw new IOException("Не удалось определить адрес клиента для UDP video");

        var target = new IPEndPoint(tcpRemote.Address, udpPort);
        using var udp = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        udp.SendBufferSize = 256 * 1024;

        var ai = new NvidiaAiPriorityMonitor();
        var aiTask = ai.RunAsync(token);
        var frameId = 1u;
        var packet = new byte[HeaderSize + PayloadSize];
        Buffer.BlockCopy(Magic, 0, packet, 0, Magic.Length);

        status($"Low Latency v4 • UDP video → {target.Address}:{target.Port} • AI Priority", false);

        try
        {
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    await Task.Delay(100, token);
                    continue;
                }

                var tuning = ComputeTuning(profile, ai.Level, tcpRemote.Address);
                using var bitmap = CaptureScreen(bounds, tuning.MaxWidth, tuning.MaxHeight);
                using var ms = new MemoryStream(Math.Max(256 * 1024, bitmap.Width * bitmap.Height / 6));
                using (var encoderParams = new EncoderParameters(1))
                {
                    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)tuning.Quality);
                    bitmap.Save(ms, JpegCodec, encoderParams);
                }

                if (!ms.TryGetBuffer(out var jpeg) || jpeg.Array is null)
                    jpeg = new ArraySegment<byte>(ms.ToArray());

                SendFrame(udp, target, packet, frameId++, jpeg.Array!, jpeg.Offset, (int)ms.Length);

                var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var targetMs = 1000.0 / Math.Max(1, tuning.Fps);
                var delayMs = (int)Math.Floor(targetMs - elapsedMs);
                if (delayMs > 0)
                    await Task.Delay(delayMs, token);
                else
                    await Task.Yield();
            }
        }
        finally
        {
            try { await aiTask; } catch { }
        }
    }

    private static StreamTuning ComputeTuning(ClientDisplayProfile profile, int aiLevel, IPAddress remote)
    {
        var requestedWidth = profile.Width > 0 ? Math.Clamp(profile.Width, 640, 3200) : 1920;
        var requestedHeight = profile.Height > 0 ? Math.Clamp(profile.Height, 640, 3200) : 1080;
        var quality = Math.Clamp(profile.Quality, 76, 92);
        var fps = Math.Clamp(profile.Fps, 30, 60);
        var scale = 1.0;

        // Network feedback from the iPhone. UDP always prefers a fresh frame over
        // preserving an old frame, so under loss we reduce bytes before reducing touch responsiveness.
        var loss = profile.LossPermille;
        var decodeMs = profile.DecodeMs;
        var reportedFps = profile.ClientReportedFps;
        if (loss >= 80 || decodeMs >= 28 || (reportedFps > 0 && reportedFps < fps * 0.55))
        {
            scale = 0.68;
            quality = Math.Min(quality, 76);
            fps = Math.Min(fps, 30);
        }
        else if (loss >= 30 || decodeMs >= 18 || (reportedFps > 0 && reportedFps < fps * 0.78))
        {
            scale = 0.82;
            quality = Math.Min(quality, 82);
            fps = Math.Min(fps, 45);
        }
        else if (loss >= 10 || decodeMs >= 13)
        {
            scale = 0.92;
            quality = Math.Min(quality, 86);
            fps = Math.Min(fps, 55);
        }

        // AI Priority: if RTX/CUDA is busy, TouchDisplay backs off first. JPEG
        // encoding itself stays on CPU, so this mainly cuts desktop capture and
        // memory traffic while a model is generating.
        if (aiLevel >= 2)
        {
            scale = Math.Min(scale, 0.72);
            quality = Math.Min(quality, 78);
            fps = Math.Min(fps, 30);
        }
        else if (aiLevel == 1)
        {
            scale = Math.Min(scale, 0.88);
            quality = Math.Min(quality, 84);
            fps = Math.Min(fps, 45);
        }

        // A Tailscale address often means the session is outside the home LAN.
        // Do not force low quality; just use a slightly safer starting ceiling
        // until feedback proves the route can sustain more.
        if (IsTailscale(remote) && profile.HasFeedback == false)
        {
            quality = Math.Min(quality, 86);
            fps = Math.Min(fps, 50);
        }

        return new StreamTuning(
            Math.Max(640, (int)Math.Round(requestedWidth * scale)),
            Math.Max(640, (int)Math.Round(requestedHeight * scale)),
            quality,
            fps);
    }

    private static bool IsTailscale(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static Bitmap CaptureScreen(Rectangle bounds, int maxWidth, int maxHeight)
    {
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(source))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)source.Width, maxHeight / (double)source.Height));
        if (scale >= 0.999)
            return new Bitmap(source);

        var width = Math.Max(2, ((int)Math.Round(source.Width * scale)) & ~1);
        var height = Math.Max(2, ((int)Math.Round(source.Height * scale)) & ~1);
        var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var tg = Graphics.FromImage(target);
        tg.CompositingQuality = CompositingQuality.HighSpeed;
        tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
        tg.SmoothingMode = SmoothingMode.HighSpeed;
        tg.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        tg.DrawImage(source, new Rectangle(0, 0, width, height));
        return target;
    }

    private static void SendFrame(
        Socket socket,
        EndPoint target,
        byte[] packet,
        uint frameId,
        byte[] jpeg,
        int jpegOffset,
        int jpegLength)
    {
        var chunkCount = (jpegLength + PayloadSize - 1) / PayloadSize;
        if (chunkCount <= 0 || chunkCount > ushort.MaxValue)
            return;

        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), frameId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), (ushort)chunkCount);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), jpegLength);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), unchecked((uint)Environment.TickCount));

        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var sourceOffset = chunk * PayloadSize;
            var payload = Math.Min(PayloadSize, jpegLength - sourceOffset);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), (ushort)chunk);
            Buffer.BlockCopy(jpeg, jpegOffset + sourceOffset, packet, HeaderSize, payload);
            try
            {
                socket.SendTo(packet, 0, HeaderSize + payload, SocketFlags.None, target);
            }
            catch (SocketException)
            {
                // UDP loss is expected on a congested route. The next frame is
                // more useful than retrying an old fragment.
            }
        }
    }

    private readonly record struct StreamTuning(int MaxWidth, int MaxHeight, int Quality, int Fps);
}

internal sealed class NvidiaAiPriorityMonitor
{
    private int _level;
    public int Level => Volatile.Read(ref _level);

    public async Task RunAsync(CancellationToken token)
    {
        var unsupported = false;
        while (!token.IsCancellationRequested)
        {
            if (!unsupported)
            {
                try
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "nvidia-smi.exe",
                            Arguments = "--query-gpu=utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };
                    if (!process.Start())
                        throw new InvalidOperationException();

                    var lineTask = process.StandardOutput.ReadLineAsync();
                    await process.WaitForExitAsync(token);
                    var line = await lineTask;
                    if (TryParse(line, out var utilization, out var memoryPercent))
                    {
                        var next = utilization >= 96 || memoryPercent >= 94 ? 2
                            : utilization >= 84 || memoryPercent >= 86 ? 1
                            : 0;
                        Volatile.Write(ref _level, next);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch
                {
                    // Non-NVIDIA machines simply stay at full stream quality.
                    unsupported = true;
                    Volatile.Write(ref _level, 0);
                }
            }

            try { await Task.Delay(unsupported ? 15000 : 2000, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static bool TryParse(string? line, out double utilization, out double memoryPercent)
    {
        utilization = 0;
        memoryPercent = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var p = line.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length < 3) return false;
        if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out utilization)) return false;
        if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var used)) return false;
        if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var total) || total <= 0) return false;
        memoryPercent = used * 100.0 / total;
        return true;
    }
}
