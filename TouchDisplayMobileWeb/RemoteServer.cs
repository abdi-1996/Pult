using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TouchDisplayMobileWeb;

internal sealed class RemoteServer
{
    private readonly int _port;
    private readonly string _pin;
    private readonly Action<string, bool> _status;
    private readonly SystemStats _stats = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);

    public RemoteServer(int port, string pin, Action<string, bool> status)
    {
        _port = port;
        _pin = NormalizePin(pin);
        _status = status;
    }

    public void Start()
    {
        TouchController.EnsureInitialized();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start(32);
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        TouchController.Reset();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(token);
                client.NoDelay = true;
                client.ReceiveBufferSize = 128 * 1024;
                client.SendBufferSize = 2 * 1024 * 1024;
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _status("Web server: " + ex.Message, true);
                try { await Task.Delay(300, token); } catch { }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint ep || !NetworkInfo.IsAllowedRemote(ep.Address))
                    return;

                using var stream = client.GetStream();
                var req = await HttpRequest.ReadAsync(stream, token);
                if (req is null) return;

                if (req.Headers.TryGetValue("Upgrade", out var upgrade) &&
                    upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWebSocketAsync(stream, req, ep.Address, token);
                    return;
                }

                await HandleHttpAsync(stream, req, token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (Exception ex)
            {
                _status("Web client: " + ex.Message, true);
            }
        }
    }

    private async Task HandleHttpAsync(NetworkStream stream, HttpRequest req, CancellationToken token)
    {
        if (!string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await HttpResponse.TextAsync(stream, 405, "Method Not Allowed", token);
            return;
        }

        var path = req.Path;
        if (path.StartsWith("/api/", StringComparison.Ordinal))
        {
            if (!IsAuthorized(req))
            {
                await HttpResponse.JsonAsync(stream, 401, new { ok = false, error = "Неверный PIN" }, token);
                return;
            }

            switch (path)
            {
                case "/api/status":
                    await SendStatusAsync(stream, token);
                    return;
                case "/api/files":
                    await SendFilesAsync(stream, req, token);
                    return;
                case "/api/recent":
                    await SendRecentAsync(stream, token);
                    return;
                case "/api/file":
                    await SendFileAsync(stream, req, token);
                    return;
                default:
                    await HttpResponse.JsonAsync(stream, 404, new { ok = false, error = "Not found" }, token);
                    return;
            }
        }

        if (path is "/" or "/index.html")
        {
            await SendStaticAsync(stream, "index.html", "text/html; charset=utf-8", token);
            return;
        }
        if (path == "/app.css")
        {
            await SendStaticAsync(stream, "app.css", "text/css; charset=utf-8", token);
            return;
        }
        if (path == "/app.js")
        {
            await SendStaticAsync(stream, "app.js", "application/javascript; charset=utf-8", token);
            return;
        }
        if (path == "/manifest.webmanifest")
        {
            await SendStaticAsync(stream, "manifest.webmanifest", "application/manifest+json; charset=utf-8", token);
            return;
        }
        if (path == "/icon.svg")
        {
            await SendStaticAsync(stream, "icon.svg", "image/svg+xml; charset=utf-8", token);
            return;
        }

        await HttpResponse.TextAsync(stream, 404, "Not found", token);
    }

    private bool IsAuthorized(HttpRequest req)
    {
        var supplied = req.Query.TryGetValue("pin", out var q) ? q :
            req.Headers.TryGetValue("X-TD-PIN", out var h) ? h : string.Empty;
        supplied = NormalizePin(supplied);
        var a = Encoding.ASCII.GetBytes(supplied);
        var b = Encoding.ASCII.GetBytes(_pin);
        return a.Length == b.Length && a.Length == 6 && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private async Task SendStatusAsync(NetworkStream stream, CancellationToken token)
    {
        var screen = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        var s = _stats.Read();
        await HttpResponse.JsonAsync(stream, 200, new
        {
            ok = true,
            pc = Environment.MachineName,
            user = Environment.UserName,
            lan = NetworkInfo.FindLan(),
            tailscale = NetworkInfo.FindTailscale(),
            width = screen.Width,
            height = screen.Height,
            gpu = new { percent = Math.Round(s.GpuPercent), memory = Math.Round(s.GpuMemoryPercent), name = s.GpuName },
            ram = new { percent = Math.Round(s.RamPercent), used = Math.Round(s.RamUsedGb, 1), total = Math.Round(s.RamTotalGb, 1) },
            network = new { down = Math.Round(s.DownMbps, 1), up = Math.Round(s.UpMbps, 1) },
            version = "1.2.5"
        }, token);
    }

    private static async Task SendFilesAsync(NetworkStream stream, HttpRequest req, CancellationToken token)
    {
        var requested = req.Query.TryGetValue("path", out var p) ? p : "";
        string current;
        if (string.IsNullOrWhiteSpace(requested))
            current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else
            current = Path.GetFullPath(requested);

        if (!Directory.Exists(current))
        {
            await HttpResponse.JsonAsync(stream, 404, new { ok = false, error = "Папка не найдена" }, token);
            return;
        }

        var items = new List<object>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(current).Take(300))
            {
                var di = new DirectoryInfo(dir);
                items.Add(new
                {
                    name = di.Name,
                    path = di.FullName,
                    directory = true,
                    size = 0L,
                    modified = di.LastWriteTime
                });
            }
            foreach (var file in Directory.EnumerateFiles(current).Take(Math.Max(0, 500 - items.Count)))
            {
                var fi = new FileInfo(file);
                items.Add(new
                {
                    name = fi.Name,
                    path = fi.FullName,
                    directory = false,
                    size = fi.Length,
                    modified = fi.LastWriteTime
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            await HttpResponse.JsonAsync(stream, 403, new { ok = false, error = "Нет доступа к папке" }, token);
            return;
        }

        var parent = Directory.GetParent(current)?.FullName;
        await HttpResponse.JsonAsync(stream, 200, new
        {
            ok = true,
            path = current,
            parent,
            items
        }, token);
    }

    private static async Task SendRecentAsync(NetworkStream stream, CancellationToken token)
    {
        var dirs = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos")
        };

        var comfyCandidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ComfyUI", "output"),
            @"C:\ComfyUI\output",
            @"C:\ComfyUI_windows_portable\ComfyUI\output"
        };
        dirs.AddRange(comfyCandidates.Where(Directory.Exists));

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".mp4", ".mov", ".mkv", ".json" };

        var files = new List<FileInfo>();
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
                    .Where(f => wanted.Contains(Path.GetExtension(f)))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(16));
            }
            catch { }
        }

        var items = files
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(18)
            .Select(fi => new
            {
                name = fi.Name,
                path = fi.FullName,
                size = fi.Length,
                modified = fi.LastWriteTime,
                image = new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" }.Contains(fi.Extension, StringComparer.OrdinalIgnoreCase),
                video = new[] { ".mp4", ".mov", ".mkv" }.Contains(fi.Extension, StringComparer.OrdinalIgnoreCase)
            })
            .ToArray();

        await HttpResponse.JsonAsync(stream, 200, new { ok = true, items }, token);
    }

    private static async Task SendFileAsync(NetworkStream stream, HttpRequest req, CancellationToken token)
    {
        if (!req.Query.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            await HttpResponse.TextAsync(stream, 400, "Missing path", token);
            return;
        }

        string full;
        try { full = Path.GetFullPath(path); }
        catch
        {
            await HttpResponse.TextAsync(stream, 400, "Invalid path", token);
            return;
        }

        if (!File.Exists(full))
        {
            await HttpResponse.TextAsync(stream, 404, "File not found", token);
            return;
        }

        var fi = new FileInfo(full);
        var type = MimeFor(fi.Extension);
        var disposition = $"inline; filename*=UTF-8''{Uri.EscapeDataString(fi.Name)}";
        await HttpResponse.FileAsync(stream, full, type, disposition, token);
    }

    private static string MimeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".json" => "application/json",
        ".txt" or ".log" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };

    private async Task SendStaticAsync(NetworkStream stream, string suffix, string contentType, CancellationToken token)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        var path = Path.GetFullPath(Path.Combine(root, suffix.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            await HttpResponse.TextAsync(stream, 404, "Resource missing", token);
            return;
        }

        var extra = suffix is "index.html" or "app.js" or "app.css"
            ? "Cache-Control: no-store\r\n"
            : "Cache-Control: public, max-age=3600\r\n";
        var bytes = await File.ReadAllBytesAsync(path, token);
        await HttpResponse.BytesAsync(stream, 200, contentType, bytes, token, extra);
    }

    private async Task HandleWebSocketAsync(NetworkStream stream, HttpRequest req, IPAddress remote, CancellationToken token)
    {
        if (!IsAuthorized(req))
        {
            await HttpResponse.TextAsync(stream, 401, "Wrong PIN", token);
            return;
        }

        if (!req.Headers.TryGetValue("Sec-WebSocket-Key", out var key)) return;
        var accept = Convert.ToBase64String(SHA1.HashData(
            Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));

        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);

        if (req.Path == "/ws/video")
        {
            _status($"iPhone/Web video: {remote}", false);
            await StreamVideoAsync(stream, remote, token);
        }
        else if (req.Path == "/ws/control")
        {
            _status($"iPhone/Web управление: {remote}", false);
            TouchController.Reset();
            using var adaptiveDisplay = new AdaptiveDisplaySession(_status);
            await ReceiveControlAsync(stream, adaptiveDisplay, token);
        }
        else
        {
            await WebSocketFrames.SendAsync(stream, 0x8, Array.Empty<byte>(), token);
        }
    }

    private static async Task StreamVideoAsync(NetworkStream stream, IPAddress remote, CancellationToken token)
    {
        var tailscale = NetworkInfo.IsTailscale(remote);
        var maxWidth = tailscale ? 1440 : 1920;
        var quality = tailscale ? 78L : 84L;
        var fps = tailscale ? 24 : 30;

        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
            using var bitmap = Capture(bounds, maxWidth);
            using var ms = new MemoryStream(Math.Max(256 * 1024, bitmap.Width * bitmap.Height / 8));
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bitmap.Save(ms, JpegCodec, ep);
            }

            if (!ms.TryGetBuffer(out var segment) || segment.Array is null)
                segment = new ArraySegment<byte>(ms.ToArray());

            await WebSocketFrames.SendAsync(stream, 0x2, segment.Array!, segment.Offset, (int)ms.Length, token);

            var delay = Math.Max(1, (1000 / fps) - (int)(Environment.TickCount64 - started));
            await Task.Delay(delay, token);
        }
    }

    private static Bitmap Capture(Rectangle bounds, int maxWidth)
    {
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(source))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        if (source.Width <= maxWidth) return new Bitmap(source);

        var scale = maxWidth / (double)source.Width;
        var w = Math.Max(2, ((int)Math.Round(source.Width * scale)) & ~1);
        var h = Math.Max(2, ((int)Math.Round(source.Height * scale)) & ~1);
        var target = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var tg = Graphics.FromImage(target);
        tg.CompositingQuality = CompositingQuality.HighSpeed;
        tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
        tg.SmoothingMode = SmoothingMode.HighSpeed;
        tg.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        tg.DrawImage(source, new Rectangle(0, 0, w, h));
        return target;
    }

    private static async Task ReceiveControlAsync(NetworkStream stream, AdaptiveDisplaySession adaptiveDisplay, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var frame = await WebSocketFrames.ReadAsync(stream, token);
            if (frame is null || frame.Value.Opcode == 0x8) return;
            if (frame.Value.Opcode == 0x9)
            {
                await WebSocketFrames.SendAsync(stream, 0xA, frame.Value.Payload, token);
                continue;
            }
            if (frame.Value.Opcode != 0x1) continue;

            try
            {
                using var doc = JsonDocument.Parse(frame.Value.Payload);
                var root = doc.RootElement;
                var t = root.GetProperty("t").GetString() ?? "";
                var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;

                switch (t)
                {
                    case "profile":
                        adaptiveDisplay.ApplyForClient(
                            root.TryGetProperty("w", out var pw) ? pw.GetInt32() : 0,
                            root.TryGetProperty("h", out var ph) ? ph.GetInt32() : 0,
                            root.TryGetProperty("dpr", out var pd) ? pd.GetDouble() : 1.0);
                        break;
                    case "touch":
                        TouchController.Inject(
                            root.GetProperty("a").GetString() ?? "move",
                            root.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                            root.GetProperty("x").GetSingle(),
                            root.GetProperty("y").GetSingle(),
                            bounds);
                        break;
                    case "mousemove":
                        MouseController.MoveRelative(root.GetProperty("dx").GetSingle(), root.GetProperty("dy").GetSingle(), bounds);
                        break;
                    case "button":
                        MouseController.Button(
                            root.TryGetProperty("b", out var b) ? b.GetInt32() : 0,
                            root.TryGetProperty("a", out var a) ? a.GetInt32() : 2);
                        break;
                    case "wheel":
                        MouseController.WheelDelta(root.GetProperty("d").GetInt32());
                        break;
                    case "text":
                        KeyboardController.TypeText(root.GetProperty("v").GetString() ?? "");
                        break;
                    case "key":
                        KeyboardController.Press(root.GetProperty("v").GetString() ?? "");
                        break;
                }
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
    }

    private static string NormalizePin(string value)
    {
        var chars = value.Where(char.IsDigit).Take(6).ToArray();
        return new string(chars);
    }
}

internal sealed class HttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required Dictionary<string, string> Query { get; init; }

    public static async Task<HttpRequest?> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>(2048);
        var one = new byte[1];
        while (bytes.Count < 32 * 1024)
        {
            var n = await stream.ReadAsync(one, token);
            if (n == 0) break;
            bytes.Add(one[0]);
            var c = bytes.Count;
            if (c >= 4 && bytes[c - 4] == 13 && bytes[c - 3] == 10 && bytes[c - 2] == 13 && bytes[c - 1] == 10)
                break;
        }

        if (bytes.Count == 0) return null;
        var text = Encoding.ASCII.GetString(bytes.ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;
        var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length < 2) return null;

        var target = first[1];
        var qm = target.IndexOf('?');
        var path = qm >= 0 ? target[..qm] : target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (qm >= 0)
        {
            foreach (var pair in target[(qm + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = pair.Split('=', 2);
                query[Uri.UnescapeDataString(p[0])] = p.Length > 1
                    ? Uri.UnescapeDataString(p[1].Replace("+", " "))
                    : "";
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }

        return new HttpRequest
        {
            Method = first[0],
            Path = path,
            Headers = headers,
            Query = query
        };
    }
}

internal static class HttpResponse
{
    public static async Task TextAsync(NetworkStream stream, int status, string text, CancellationToken token)
    {
        await BytesAsync(stream, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), token);
    }

    public static async Task JsonAsync(NetworkStream stream, int status, object value, CancellationToken token)
    {
        await BytesAsync(stream, status, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value), token, "Cache-Control: no-store\r\n");
    }

    public static async Task BytesAsync(NetworkStream stream, int status, string contentType, byte[] bytes, CancellationToken token, string extra = "")
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "OK"
        };
        var head =
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bytes.LongLength}\r\n" +
            "Connection: close\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            extra +
            "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token);
        await stream.WriteAsync(bytes, token);
    }

    public static async Task FileAsync(NetworkStream stream, string path, string contentType, string disposition, CancellationToken token)
    {
        var fi = new FileInfo(path);
        var head =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {fi.Length}\r\n" +
            $"Content-Disposition: {disposition}\r\n" +
            "Cache-Control: private, max-age=30\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await fs.CopyToAsync(stream, 128 * 1024, token);
    }
}

internal readonly record struct WsFrame(byte Opcode, byte[] Payload);

internal static class WebSocketFrames
{
    public static async Task<WsFrame?> ReadAsync(NetworkStream stream, CancellationToken token)
    {
        var first = new byte[2];
        if (!await ReadExactAsync(stream, first, token)) return null;

        var opcode = (byte)(first[0] & 0x0F);
        var masked = (first[1] & 0x80) != 0;
        ulong length = (ulong)(first[1] & 0x7F);

        if (length == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactAsync(stream, ext, token)) return null;
            length = (ulong)((ext[0] << 8) | ext[1]);
        }
        else if (length == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactAsync(stream, ext, token)) return null;
            length = 0;
            for (var i = 0; i < 8; i++) length = (length << 8) | ext[i];
        }

        if (length > 512 * 1024) return null;

        byte[]? mask = null;
        if (masked)
        {
            mask = new byte[4];
            if (!await ReadExactAsync(stream, mask, token)) return null;
        }

        var payload = new byte[(int)length];
        if (payload.Length > 0 && !await ReadExactAsync(stream, payload, token)) return null;
        if (mask is not null)
            for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

        return new WsFrame(opcode, payload);
    }

    public static Task SendAsync(NetworkStream stream, byte opcode, byte[] payload, CancellationToken token) =>
        SendAsync(stream, opcode, payload, 0, payload.Length, token);

    public static async Task SendAsync(NetworkStream stream, byte opcode, byte[] payload, int offset, int count, CancellationToken token)
    {
        var h = new byte[10];
        var n = 0;
        h[n++] = (byte)(0x80 | opcode);
        if (count < 126) h[n++] = (byte)count;
        else if (count <= ushort.MaxValue)
        {
            h[n++] = 126; h[n++] = (byte)(count >> 8); h[n++] = (byte)count;
        }
        else
        {
            h[n++] = 127;
            var len = (ulong)count;
            for (var i = 7; i >= 0; i--) h[n++] = (byte)(len >> (i * 8));
        }

        await stream.WriteAsync(h.AsMemory(0, n), token);
        if (count > 0) await stream.WriteAsync(payload.AsMemory(offset, count), token);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[offset..], token);
            if (n == 0) return false;
            offset += n;
        }
        return true;
    }
}
