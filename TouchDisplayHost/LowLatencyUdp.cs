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
    // 1180-byte UDP payload (20-byte header + 1160-byte data) stays below the
    // usual WireGuard/Tailscale MTU and avoids IP fragmentation.
    public const int HeaderSize = 20;
    public const int PayloadSize = 1160;
    private const int FecGroupSize = 8;
    private static readonly byte[] MagicV4 = { (byte)'T', (byte)'D', (byte)'U', (byte)'4' };
    private static readonly byte[] MagicV5 = { (byte)'T', (byte)'D', (byte)'U', (byte)'5' };
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders()
        .First(x => x.FormatID == ImageFormat.Jpeg.Guid);

    // Compatibility overload for TD04 clients.
    public static Task StreamAsync(
        TcpClient controlClient,
        int udpPort,
        ClientDisplayProfile profile,
        Action<string, bool> status,
        CancellationToken token) =>
        StreamAsync(controlClient, udpPort, profile, status, token, useFec: false);

    public static async Task StreamAsync(
        TcpClient controlClient,
        int udpPort,
        ClientDisplayProfile profile,
        Action<string, bool> status,
        CancellationToken token,
        bool useFec)
    {
        if (controlClient.Client.RemoteEndPoint is not IPEndPoint tcpRemote)
            throw new IOException("Не удалось определить адрес клиента для UDP video");

        var target = new IPEndPoint(tcpRemote.Address, udpPort);
        using var udp = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        udp.SendBufferSize = 1024 * 1024;

        var ai = new NvidiaAiPriorityMonitor();
        var aiTask = ai.RunAsync(token);
        var budget = new AdaptiveBitrateController();
        var activity = new DesktopActivityDetector();
        var frameId = 1u;
        var packet = new byte[HeaderSize + PayloadSize];

        status(useFec
            ? $"Low Latency v4.1 • UDP + FEC → {target.Address}:{target.Port} • AI Priority"
            : $"Low Latency v4 • UDP video → {target.Address}:{target.Port} • AI Priority", false);

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

                var tuning = ComputeTuning(profile, ai.Level, tcpRemote.Address, budget.QualityBias);
                using var bitmap = CaptureScreen(bounds, tuning.MaxWidth, tuning.MaxHeight);

                // When the desktop is genuinely static, avoid wasting CPU on 60 identical
                // JPEG encodes. Remote input and cursor movement instantly wake full FPS,
                // and a forced refresh still happens every 250 ms.
                if (!activity.ShouldEncode(bitmap, profile.LastInteractionTick))
                {
                    await Task.Delay(24, token);
                    continue;
                }

                using var ms = new MemoryStream(Math.Max(256 * 1024, bitmap.Width * bitmap.Height / 6));
                using (var encoderParams = new EncoderParameters(1))
                {
                    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)tuning.Quality);
                    bitmap.Save(ms, JpegCodec, encoderParams);
                }

                if (!ms.TryGetBuffer(out var jpeg) || jpeg.Array is null)
                    jpeg = new ArraySegment<byte>(ms.ToArray());

                var jpegLength = (int)ms.Length;
                SendFrame(udp, target, packet, frameId++, jpeg.Array!, jpeg.Offset, jpegLength, useFec);
                budget.Observe(jpegLength, tuning.Fps, tuning.TargetMbps);
                activity.MarkSent();

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

    private static StreamTuning ComputeTuning(
        ClientDisplayProfile profile,
        int aiLevel,
        IPAddress remote,
        int qualityBias)
    {
        var requestedWidth = profile.Width > 0 ? Math.Clamp(profile.Width, 640, 3200) : 1920;
        var requestedHeight = profile.Height > 0 ? Math.Clamp(profile.Height, 640, 3200) : 1080;
        var quality = Math.Clamp(profile.Quality + qualityBias, 68, 92);
        var fps = Math.Clamp(profile.Fps, 30, 60);
        var scale = 1.0;
        var tailscale = IsTailscale(remote);
        var targetMbps = tailscale ? 20.0 : 45.0;

        // Client feedback: reduce video bytes before delay can accumulate.
        var loss = profile.LossPermille;
        var decodeMs = profile.DecodeMs;
        var reportedFps = profile.ClientReportedFps;
        if (loss >= 80 || decodeMs >= 28 || (reportedFps > 0 && reportedFps < fps * 0.55))
        {
            scale = 0.68;
            quality = Math.Min(quality, 74);
            fps = Math.Min(fps, 30);
            targetMbps = Math.Min(targetMbps, 10.0);
        }
        else if (loss >= 30 || decodeMs >= 18 || (reportedFps > 0 && reportedFps < fps * 0.78))
        {
            scale = 0.82;
            quality = Math.Min(quality, 80);
            fps = Math.Min(fps, 45);
            targetMbps = Math.Min(targetMbps, 16.0);
        }
        else if (loss >= 10 || decodeMs >= 13)
        {
            scale = 0.92;
            quality = Math.Min(quality, 85);
            fps = Math.Min(fps, 55);
            targetMbps = Math.Min(targetMbps, 24.0);
        }
        else if (tailscale && profile.HasFeedback && loss <= 5 && decodeMs <= 10 && reportedFps >= 48)
        {
            // Good direct Tailscale path: allow noticeably sharper text/UI.
            targetMbps = 32.0;
        }

        // AI Priority: TouchDisplay backs off first while CUDA/VRAM is busy.
        if (aiLevel >= 2)
        {
            scale = Math.Min(scale, 0.72);
            quality = Math.Min(quality, 77);
            fps = Math.Min(fps, 30);
            targetMbps = Math.Min(targetMbps, 10.0);
        }
        else if (aiLevel == 1)
        {
            scale = Math.Min(scale, 0.88);
            quality = Math.Min(quality, 83);
            fps = Math.Min(fps, 45);
            targetMbps = Math.Min(targetMbps, 18.0);
        }

        // If JPEG size keeps exceeding the route budget, lower resolution only after
        // quality has already been reduced. This keeps text sharp for as long as possible.
        if (qualityBias <= -14)
            scale = Math.Min(scale, 0.74);
        else if (qualityBias <= -9)
            scale = Math.Min(scale, 0.86);

        if (tailscale && !profile.HasFeedback)
        {
            quality = Math.Min(quality, 85);
            fps = Math.Min(fps, 50);
        }

        return new StreamTuning(
            Math.Max(640, (int)Math.Round(requestedWidth * scale)),
            Math.Max(640, (int)Math.Round(requestedHeight * scale)),
            quality,
            fps,
            targetMbps);
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
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

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
        int jpegLength,
        bool useFec)
    {
        var chunkCount = (jpegLength + PayloadSize - 1) / PayloadSize;
        if (chunkCount <= 0 || chunkCount > ushort.MaxValue)
            return;

        Buffer.BlockCopy(useFec ? MagicV5 : MagicV4, 0, packet, 0, 4);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), frameId);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), (ushort)chunkCount);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(12, 4), jpegLength);

        if (!useFec)
        {
            // Original TDU4 layout for v4.0 clients.
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16, 4), unchecked((uint)Environment.TickCount));
            for (var chunk = 0; chunk < chunkCount; chunk++)
            {
                var sourceOffset = chunk * PayloadSize;
                var payload = Math.Min(PayloadSize, jpegLength - sourceOffset);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), (ushort)chunk);
                Buffer.BlockCopy(jpeg, jpegOffset + sourceOffset, packet, HeaderSize, payload);
                TrySend(socket, target, packet, HeaderSize + payload);
            }
            return;
        }

        // TDU5: every 8 data packets get one XOR parity packet. One lost packet in
        // each group can be reconstructed without retransmission or extra latency.
        var parity = new byte[PayloadSize];
        var groupCount = (chunkCount + FecGroupSize - 1) / FecGroupSize;
        for (var group = 0; group < groupCount; group++)
        {
            Array.Clear(parity, 0, parity.Length);
            var first = group * FecGroupSize;
            var last = Math.Min(chunkCount, first + FecGroupSize);

            for (var chunk = first; chunk < last; chunk++)
            {
                var sourceOffset = chunk * PayloadSize;
                var payload = Math.Min(PayloadSize, jpegLength - sourceOffset);

                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), (ushort)chunk);
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16, 2), (ushort)group);
                packet[18] = 0; // data
                packet[19] = 0;
                Buffer.BlockCopy(jpeg, jpegOffset + sourceOffset, packet, HeaderSize, payload);

                for (var i = 0; i < payload; i++)
                    parity[i] ^= jpeg[jpegOffset + sourceOffset + i];

                TrySend(socket, target, packet, HeaderSize + payload);
            }

            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8, 2), (ushort)group);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(16, 2), (ushort)group);
            packet[18] = 1; // parity
            packet[19] = (byte)(last - first);
            Buffer.BlockCopy(parity, 0, packet, HeaderSize, PayloadSize);
            TrySend(socket, target, packet, HeaderSize + PayloadSize);
        }
    }

    private static void TrySend(Socket socket, EndPoint target, byte[] packet, int count)
    {
        try { socket.SendTo(packet, 0, count, SocketFlags.None, target); }
        catch (SocketException)
        {
            // Never retry an old video packet. FEC may recover it; otherwise the
            // next fresh frame is more useful than increasing latency.
        }
    }

    private readonly record struct StreamTuning(
        int MaxWidth,
        int MaxHeight,
        int Quality,
        int Fps,
        double TargetMbps);
}

internal sealed class AdaptiveBitrateController
{
    private double _emaMbps;
    private int _qualityBias;
    public int QualityBias => Volatile.Read(ref _qualityBias);

    public void Observe(int frameBytes, int fps, double targetMbps)
    {
        if (frameBytes <= 0 || fps <= 0 || targetMbps <= 0) return;
        var instantaneous = frameBytes * 8.0 * fps / 1_000_000.0;
        _emaMbps = _emaMbps <= 0 ? instantaneous : (_emaMbps * 0.84 + instantaneous * 0.16);

        var bias = _qualityBias;
        if (_emaMbps > targetMbps * 1.15)
            bias--;
        else if (_emaMbps < targetMbps * 0.70)
            bias++;

        Volatile.Write(ref _qualityBias, Math.Clamp(bias, -18, 2));
    }
}

internal sealed class DesktopActivityDetector
{
    private ulong _lastSignature;
    private long _lastSentTick;
    private bool _hasSignature;

    public bool ShouldEncode(Bitmap bitmap, long lastRemoteInteractionTick)
    {
        var now = Environment.TickCount64;
        var signature = QuickSignature(bitmap);
        var changed = !_hasSignature || signature != _lastSignature;
        _lastSignature = signature;
        _hasSignature = true;

        if (changed) return true;
        if (unchecked(now - lastRemoteInteractionTick) < 850) return true;
        if (unchecked(now - _lastSentTick) >= 250) return true;
        return false;
    }

    public void MarkSent() => _lastSentTick = Environment.TickCount64;

    private static unsafe ulong QuickSignature(Bitmap bitmap)
    {
        // FNV-1a over a 24x14 sample grid. It is intentionally approximate: the
        // periodic 250 ms refresh guarantees small UI changes are never frozen.
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            const ulong offset = 1469598103934665603UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            var basePtr = (byte*)data.Scan0;
            const int cols = 24;
            const int rows = 14;
            for (var y = 0; y < rows; y++)
            {
                var py = Math.Min(bitmap.Height - 1, y * Math.Max(1, bitmap.Height - 1) / Math.Max(1, rows - 1));
                var row = basePtr + py * data.Stride;
                for (var x = 0; x < cols; x++)
                {
                    var px = Math.Min(bitmap.Width - 1, x * Math.Max(1, bitmap.Width - 1) / Math.Max(1, cols - 1));
                    var p = row + px * 3;
                    hash ^= p[0]; hash *= prime;
                    hash ^= p[1]; hash *= prime;
                    hash ^= p[2]; hash *= prime;
                }
            }

            if (NativeCursor.TryGetPosition(out var cx, out var cy))
            {
                hash ^= unchecked((uint)cx); hash *= prime;
                hash ^= unchecked((uint)cy); hash *= prime;
            }
            return hash;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}

internal static class NativeCursor
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PointNative { public int X; public int Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointNative point);

    public static bool TryGetPosition(out int x, out int y)
    {
        if (GetCursorPos(out var p))
        {
            x = p.X; y = p.Y; return true;
        }
        x = 0; y = 0; return false;
    }
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
                    if (!process.Start()) throw new InvalidOperationException();

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
                    unsupported = true;
                    Volatile.Write(ref _level, 0);
                }
            }

            try { await Task.Delay(unsupported ? 15000 : 1500, token); }
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
