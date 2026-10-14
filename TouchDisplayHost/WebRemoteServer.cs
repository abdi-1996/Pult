using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace TouchDisplayHost;

/// <summary>
/// TouchDisplay Web Remote. /video and /control are deliberately separate so
/// video backpressure can never queue mouse/touch input behind a frame.
/// </summary>
internal sealed class WebRemoteServer
{
    public const int DefaultPort = 59435;
    private readonly int _port;
    private readonly string _pin;
    private readonly Action<string, bool> _status;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _lastInputTick;

    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders()
        .First(x => x.FormatID == ImageFormat.Jpeg.Guid);

    public WebRemoteServer(int port, string pin, Action<string, bool> status)
    {
        _port = port;
        _pin = NormalizePin(pin);
        _status = status;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
        _status($"Web Remote v4.3: http://PC-IP:{_port} • LAN / Tailscale", false);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
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
                client.SendBufferSize = 1024 * 1024;
                _ = HandleClientAsync(client, token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _status("Web Remote: " + ex.Message, true);
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
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IsAllowedRemote(remote.Address))
                    return;

                var stream = client.GetStream();
                var headerText = await ReadHttpHeaderAsync(stream, token);
                if (string.IsNullOrWhiteSpace(headerText)) return;
                var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0) return;
                var request = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (request.Length < 2 || !string.Equals(request[0], "GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHttpAsync(stream, "405 Method Not Allowed", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("GET only"), token);
                    return;
                }

                var target = request[1];
                var headers = ParseHeaders(lines.Skip(1));
                var isWebSocket = headers.TryGetValue("Upgrade", out var upgrade) &&
                                  string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase);
                if (isWebSocket)
                {
                    await HandleWebSocketAsync(stream, target, headers, remote.Address, token);
                    return;
                }

                var path = target.Split('?', 2)[0];
                if (path is "/" or "/index.html")
                    await WriteHttpAsync(stream, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(IndexHtml), token, "Cache-Control: no-store\r\n");
                else if (path == "/manifest.webmanifest")
                    await WriteHttpAsync(stream, "200 OK", "application/manifest+json; charset=utf-8", Encoding.UTF8.GetBytes(Manifest), token, "Cache-Control: no-cache\r\n");
                else if (path == "/icon.svg")
                    await WriteHttpAsync(stream, "200 OK", "image/svg+xml; charset=utf-8", Encoding.UTF8.GetBytes(IconSvg), token, "Cache-Control: public, max-age=86400\r\n");
                else
                    await WriteHttpAsync(stream, "404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not found"), token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (Exception ex) { _status("Web client: " + ex.Message, true); }
        }
    }

    private async Task HandleWebSocketAsync(NetworkStream stream, string target, Dictionary<string, string> headers, IPAddress remote, CancellationToken token)
    {
        var uri = new Uri("http://touchdisplay" + target);
        var query = ParseQuery(uri.Query);
        var supplied = query.TryGetValue("pin", out var p) ? NormalizePin(p) : string.Empty;
        if (!FixedPinEquals(supplied, _pin))
        {
            await WriteHttpAsync(stream, "401 Unauthorized", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Wrong PIN"), token);
            return;
        }

        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || string.IsNullOrWhiteSpace(key)) return;
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);

        if (uri.AbsolutePath == "/video")
        {
            _status($"Web video подключён: {remote}", false);
            await StreamVideoAsync(stream, remote, token);
        }
        else if (uri.AbsolutePath == "/control")
        {
            _status($"Web управление подключено: {remote}", false);
            await ReceiveControlAsync(stream, token);
        }
        else await SendWsFrameAsync(stream, 0x8, Array.Empty<byte>(), token);
    }

    private async Task StreamVideoAsync(NetworkStream stream, IPAddress remote, CancellationToken token)
    {
        var ai = new NvidiaAiPriorityMonitor();
        var aiTask = ai.RunAsync(token);
        var activity = new DesktopActivityDetector();
        var tailscale = IsTailscale(remote);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
                if (bounds.Width <= 0 || bounds.Height <= 0) { await Task.Delay(100, token); continue; }

                var aiLevel = ai.Level;
                var maxWidth = aiLevel >= 2 ? 960 : aiLevel == 1 ? 1280 : tailscale ? 1600 : 1920;
                var quality = aiLevel >= 2 ? 68L : aiLevel == 1 ? 76L : tailscale ? 82L : 86L;
                var fps = aiLevel >= 2 ? 18 : aiLevel == 1 ? 24 : tailscale ? 30 : 40;

                using var bitmap = CaptureScreen(bounds, maxWidth);
                if (!activity.ShouldEncode(bitmap, Interlocked.Read(ref _lastInputTick)))
                {
                    await Task.Delay(22, token);
                    continue;
                }

                using var ms = new MemoryStream(Math.Max(256 * 1024, bitmap.Width * bitmap.Height / 8));
                using (var ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                    bitmap.Save(ms, JpegCodec, ep);
                }

                if (!ms.TryGetBuffer(out var segment) || segment.Array is null) segment = new ArraySegment<byte>(ms.ToArray());
                await SendWsFrameAsync(stream, 0x2, segment.Array!, segment.Offset, (int)ms.Length, token);
                activity.MarkSent();

                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var remaining = (int)Math.Floor(1000.0 / fps - elapsed);
                if (remaining > 0) await Task.Delay(remaining, token); else await Task.Yield();
            }
        }
        finally { try { await aiTask; } catch { } }
    }

    private async Task ReceiveControlAsync(NetworkStream stream, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var frame = await ReadWsFrameAsync(stream, token);
            if (frame is null || frame.Value.Opcode == 0x8) return;
            if (frame.Value.Opcode == 0x9) { await SendWsFrameAsync(stream, 0xA, frame.Value.Payload, token); continue; }
            if (frame.Value.Opcode != 0x1) continue;

            try
            {
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(frame.Value.Payload));
                var root = doc.RootElement;
                if (!root.TryGetProperty("t", out var tn)) continue;
                var type = tn.GetString() ?? "";
                var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;

                if (type == "touch")
                {
                    var at = root.GetProperty("a").GetString() ?? "move";
                    var action = at == "down" ? (byte)0 : at == "move" ? (byte)1 : at == "up" ? (byte)2 : (byte)3;
                    var id = root.TryGetProperty("id", out var idn) ? idn.GetInt32() : 0;
                    TouchInjector.Inject(action, Math.Abs(id % 32), root.GetProperty("x").GetSingle(), root.GetProperty("y").GetSingle(), bounds);
                    MarkInput();
                }
                else if (type == "mousemove")
                {
                    MouseInjector.MoveRelative(root.GetProperty("dx").GetSingle(), root.GetProperty("dy").GetSingle(), bounds);
                    MarkInput();
                }
                else if (type == "button")
                {
                    MouseInjector.Button((byte)Math.Clamp(root.TryGetProperty("b", out var b) ? b.GetInt32() : 0, 0, 1),
                                         (byte)Math.Clamp(root.TryGetProperty("a", out var a) ? a.GetInt32() : 2, 0, 2));
                    MarkInput();
                }
                else if (type == "wheel")
                {
                    MouseInjector.Wheel(Math.Clamp(root.GetProperty("d").GetInt32(), -960, 960));
                    MarkInput();
                }
                else if (type == "text")
                {
                    if (root.TryGetProperty("v", out var v)) WebKeyboardInjector.TypeText(v.GetString() ?? "");
                    MarkInput();
                }
                else if (type == "key")
                {
                    if (root.TryGetProperty("v", out var v)) WebKeyboardInjector.PressKey(v.GetString() ?? "");
                    MarkInput();
                }
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
    }

    private void MarkInput() => Interlocked.Exchange(ref _lastInputTick, Environment.TickCount64);

    private static Bitmap CaptureScreen(Rectangle bounds, int maxWidth)
    {
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(source)) g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        if (source.Width <= maxWidth) return new Bitmap(source);
        var scale = maxWidth / (double)source.Width;
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

    private static bool IsAllowedRemote(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 169 && b[1] == 254) return true;
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static bool IsTailscale(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static string NormalizePin(string value)
    {
        var sb = new StringBuilder(6);
        foreach (var ch in value)
        {
            if (!char.IsDigit(ch)) continue;
            var n = char.GetNumericValue(ch);
            if (n >= 0 && n <= 9 && Math.Floor(n) == n) sb.Append((char)('0' + (int)n));
            if (sb.Length == 6) break;
        }
        return sb.ToString();
    }

    private static bool FixedPinEquals(string a, string b)
    {
        var aa = Encoding.ASCII.GetBytes(a); var bb = Encoding.ASCII.GetBytes(b);
        return aa.Length == bb.Length && CryptographicOperations.FixedTimeEquals(aa, bb);
    }

    private static Dictionary<string, string> ParseHeaders(IEnumerable<string> lines)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines) { var i = line.IndexOf(':'); if (i > 0) r[line[..i].Trim()] = line[(i + 1)..].Trim(); }
        return r;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = pair.Split('=', 2);
            r[Uri.UnescapeDataString(p[0])] = p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace("+", " ")) : "";
        }
        return r;
    }

    private static async Task<string> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var data = new List<byte>(2048); var one = new byte[1];
        while (data.Count < 16 * 1024)
        {
            var n = await stream.ReadAsync(one, token); if (n == 0) break; data.Add(one[0]); var c = data.Count;
            if (c >= 4 && data[c - 4] == 13 && data[c - 3] == 10 && data[c - 2] == 13 && data[c - 1] == 10) break;
        }
        return Encoding.ASCII.GetString(data.ToArray());
    }

    private static async Task WriteHttpAsync(NetworkStream stream, string status, string contentType, byte[] body, CancellationToken token, string extraHeaders = "")
    {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\n" + extraHeaders + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token); await stream.WriteAsync(body, token);
    }

    private readonly record struct WsFrame(byte Opcode, byte[] Payload);

    private static async Task<WsFrame?> ReadWsFrameAsync(NetworkStream stream, CancellationToken token)
    {
        var first = new byte[2]; if (!await ReadExactOrFalseAsync(stream, first, token)) return null;
        var opcode = (byte)(first[0] & 0x0F); var masked = (first[1] & 0x80) != 0; ulong length = (ulong)(first[1] & 0x7F);
        if (length == 126) { var e = new byte[2]; if (!await ReadExactOrFalseAsync(stream, e, token)) return null; length = (ulong)((e[0] << 8) | e[1]); }
        else if (length == 127) { var e = new byte[8]; if (!await ReadExactOrFalseAsync(stream, e, token)) return null; length = 0; for (var i = 0; i < 8; i++) length = (length << 8) | e[i]; }
        if (length > 256 * 1024) return null;
        byte[]? mask = null; if (masked) { mask = new byte[4]; if (!await ReadExactOrFalseAsync(stream, mask, token)) return null; }
        var payload = new byte[(int)length]; if (payload.Length > 0 && !await ReadExactOrFalseAsync(stream, payload, token)) return null;
        if (mask is not null) for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];
        return new WsFrame(opcode, payload);
    }

    private static async Task<bool> ReadExactOrFalseAsync(NetworkStream stream, Memory<byte> memory, CancellationToken token)
    {
        var offset = 0; while (offset < memory.Length) { var n = await stream.ReadAsync(memory[offset..], token); if (n == 0) return false; offset += n; } return true;
    }

    private static Task SendWsFrameAsync(NetworkStream stream, byte opcode, byte[] payload, CancellationToken token) => SendWsFrameAsync(stream, opcode, payload, 0, payload.Length, token);

    private static async Task SendWsFrameAsync(NetworkStream stream, byte opcode, byte[] payload, int offset, int count, CancellationToken token)
    {
        var header = new byte[10]; var h = 0; header[h++] = (byte)(0x80 | (opcode & 0x0F));
        if (count < 126) header[h++] = (byte)count;
        else if (count <= ushort.MaxValue) { header[h++] = 126; header[h++] = (byte)(count >> 8); header[h++] = (byte)count; }
        else { header[h++] = 127; var len = (ulong)count; for (var i = 7; i >= 0; i--) header[h++] = (byte)(len >> (i * 8)); }
        await stream.WriteAsync(header.AsMemory(0, h), token); if (count > 0) await stream.WriteAsync(payload.AsMemory(offset, count), token);
    }

    private const string Manifest = """
{"name":"TouchDisplay Web","short_name":"TouchDisplay","start_url":"/","display":"standalone","background_color":"#000000","theme_color":"#000000","orientation":"any","icons":[{"src":"/icon.svg","sizes":"any","type":"image/svg+xml","purpose":"any maskable"}]}
""";

    private const string IconSvg = """
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512"><rect width="512" height="512" rx="112" fill="#080b10"/><rect x="72" y="96" width="368" height="260" rx="26" fill="none" stroke="#fff" stroke-width="24"/><path d="M176 414h160M256 356v58" stroke="#fff" stroke-width="24" stroke-linecap="round"/><circle cx="356" cy="292" r="42" fill="#fff"/></svg>
""";

    private const string IndexHtml = """
<!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover,user-scalable=no"><meta name="theme-color" content="#000"><meta name="apple-mobile-web-app-capable" content="yes"><meta name="apple-mobile-web-app-status-bar-style" content="black-translucent"><link rel="manifest" href="/manifest.webmanifest"><title>TouchDisplay Web</title>
<style>:root{color-scheme:dark;--g:rgba(12,14,18,.56)}*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#000;font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;touch-action:none}#stage{position:fixed;inset:0;background:#000;overflow:hidden}#bg,#screen{position:absolute;inset:0;width:100%;height:100%;pointer-events:none}#bg{object-fit:cover;filter:blur(28px) brightness(.42);transform:scale(1.05)}#screen{object-fit:contain}#hit{position:absolute;inset:0;touch-action:none}#login{position:fixed;inset:0;z-index:20;display:flex;align-items:center;justify-content:center;background:radial-gradient(circle at 50% 30%,#182030,#07090d 62%)}.card{width:min(390px,calc(100% - 38px));padding:26px;border:1px solid #ffffff20;border-radius:24px;background:#141820db;backdrop-filter:blur(24px);text-align:center}.logo{font-size:48px}.title{font-size:26px;font-weight:750;margin:10px 0 4px}.sub{font-size:13px;color:#aeb5c0;margin-bottom:20px}#pin{width:100%;height:54px;border:1px solid #ffffff20;border-radius:14px;background:#ffffff0c;color:white;font:600 23px ui-monospace,monospace;text-align:center;letter-spacing:5px;outline:none}#connect{width:100%;height:50px;margin-top:12px;border:0;border-radius:14px;background:#f4f5f7;color:#080b10;font-weight:750;font-size:15px}#error{min-height:20px;margin-top:10px;font-size:12px;color:#ffad78}#bar{position:fixed;z-index:10;left:50%;bottom:max(8px,env(safe-area-inset-bottom));transform:translateX(-50%);display:flex;gap:8px;padding:6px;background:#0003;border-radius:18px;transition:opacity .25s;opacity:.96}#bar.faded{opacity:.20}.btn{min-width:60px;height:42px;padding:0 13px;border:1px solid #ffffff15;border-radius:13px;background:var(--g);backdrop-filter:blur(14px);color:#fff;font-weight:720;font-size:13px}.btn:active{transform:scale(.95);background:#ffffff30}#mode{min-width:96px}.active{background:#ffffff35}#kbd{position:fixed;left:-1000px;bottom:0;width:1px;height:1px;opacity:.01}#hint{position:fixed;top:max(8px,env(safe-area-inset-top));left:50%;transform:translateX(-50%);z-index:9;padding:6px 10px;border-radius:10px;background:#0007;color:#fff9;font-size:11px;opacity:0;transition:opacity .25s;pointer-events:none}#hint.show{opacity:1}</style></head><body>
<div id="stage"><img id="bg"><img id="screen"><div id="hit"></div></div><div id="bar"><button class="btn" id="mode">☝️ Сенсор</button><button class="btn" id="left">ЛКМ</button><button class="btn" id="right">ПКМ</button><button class="btn" id="keyboard">⌨️</button></div><input id="kbd" autocomplete="off" autocapitalize="off" spellcheck="false"><div id="hint"></div>
<div id="login"><div class="card"><div class="logo">🖥️</div><div class="title">TouchDisplay Web</div><div class="sub">Windows Remote • LAN / Tailscale</div><input id="pin" inputmode="numeric" maxlength="6" placeholder="6-значный PIN"><button id="connect">ПОДКЛЮЧИТЬСЯ</button><div id="error"></div></div></div>
<script>(()=>{const $=s=>document.querySelector(s),screen=$('#screen'),bg=$('#bg'),hit=$('#hit'),bar=$('#bar'),pin=$('#pin'),login=$('#login'),err=$('#error'),modeBtn=$('#mode'),kbd=$('#kbd'),hint=$('#hint');let video=null,control=null,mouseMode=localStorage.getItem('td.web.mouse')==='1',pointers=new Map(),tapStart=null,moved=false,lastUrl=null,fadeTimer=null;const wsBase=(location.protocol==='https:'?'wss://':'ws://')+location.host,showHint=t=>{hint.textContent=t;hint.classList.add('show');setTimeout(()=>hint.classList.remove('show'),900)},activity=()=>{bar.classList.remove('faded');clearTimeout(fadeTimer);fadeTimer=setTimeout(()=>bar.classList.add('faded'),2200)},send=o=>{if(control&&control.readyState===1)control.send(JSON.stringify(o))},setMode=()=>{modeBtn.textContent=mouseMode?'🖱️ Мышь':'☝️ Сенсор';modeBtn.classList.toggle('active',mouseMode);localStorage.setItem('td.web.mouse',mouseMode?'1':'0');activity()};setMode();activity();
function connect(){const p=pin.value.replace(/\D/g,'').slice(0,6);pin.value=p;if(p.length!==6){err.textContent='Введите 6 цифр';return}localStorage.setItem('td.web.pin',p);err.textContent='Подключение…';try{video?.close();control?.close()}catch{}const q='?pin='+encodeURIComponent(p);video=new WebSocket(wsBase+'/video'+q);video.binaryType='arraybuffer';control=new WebSocket(wsBase+'/control'+q);let a=false,b=false;const ready=()=>{if(a&&b){login.style.display='none';err.textContent='';showHint('Подключено')}};video.onopen=()=>{a=true;ready()};control.onopen=()=>{b=true;ready()};const fail=()=>{if(login.style.display!=='none')err.textContent='Проверь PIN и Host'};video.onerror=fail;control.onerror=fail;video.onclose=()=>{if(login.style.display==='none'){login.style.display='flex';err.textContent='Соединение потеряно'}};control.onclose=video.onclose;video.onmessage=e=>{const u=URL.createObjectURL(new Blob([e.data],{type:'image/jpeg'})),old=lastUrl;lastUrl=u;screen.src=u;bg.src=u;screen.onload=()=>{if(old)URL.revokeObjectURL(old)}}}$('#connect').onclick=connect;pin.addEventListener('input',()=>pin.value=pin.value.replace(/\D/g,'').slice(0,6));pin.addEventListener('keydown',e=>{if(e.key==='Enter')connect()});const saved=localStorage.getItem('td.web.pin');if(saved)pin.value=saved;
modeBtn.onclick=()=>{mouseMode=!mouseMode;pointers.clear();setMode();showHint(mouseMode?'Режим мыши':'Режим сенсора')};$('#left').onclick=()=>{send({t:'button',b:0,a:2});activity()};$('#right').onclick=()=>{send({t:'button',b:1,a:2});activity()};$('#keyboard').onclick=()=>{kbd.focus({preventScroll:true});activity()};
function imagePoint(x,y){const r=hit.getBoundingClientRect(),iw=screen.naturalWidth||r.width,ih=screen.naturalHeight||r.height,s=Math.min(r.width/iw,r.height/ih),dw=iw*s,dh=ih*s,l=(r.width-dw)/2,t=(r.height-dh)/2;return{x:Math.max(0,Math.min(1,(x-r.left-l)/Math.max(1,dw))),y:Math.max(0,Math.min(1,(y-r.top-t)/Math.max(1,dh)))}}hit.oncontextmenu=e=>e.preventDefault();hit.addEventListener('pointerdown',e=>{hit.setPointerCapture(e.pointerId);pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});tapStart={x:e.clientX,y:e.clientY,at:performance.now(),id:e.pointerId};moved=false;activity();if(!mouseMode){const p=imagePoint(e.clientX,e.clientY);send({t:'touch',a:'down',id:e.pointerId,x:p.x,y:p.y})}});
hit.addEventListener('pointermove',e=>{const prev=pointers.get(e.pointerId);if(!prev)return;const count=pointers.size,dx=e.clientX-prev.x,dy=e.clientY-prev.y;pointers.set(e.pointerId,{x:e.clientX,y:e.clientY});if(Math.hypot(dx,dy)>2)moved=true;if(mouseMode){if(count>=2){const d=Math.round(-dy*7);if(d)send({t:'wheel',d})}else send({t:'mousemove',dx:dx/Math.max(1,innerWidth),dy:dy/Math.max(1,innerHeight)})}else{const p=imagePoint(e.clientX,e.clientY);send({t:'touch',a:'move',id:e.pointerId,x:p.x,y:p.y})}});
function end(e,c=false){if(!pointers.has(e.pointerId))return;if(mouseMode){const elapsed=tapStart&&tapStart.id===e.pointerId?performance.now()-tapStart.at:999;if(pointers.size===1&&!moved&&elapsed<340)send({t:'button',b:0,a:2})}else{const p=imagePoint(e.clientX,e.clientY);send({t:'touch',a:c?'cancel':'up',id:e.pointerId,x:p.x,y:p.y})}pointers.delete(e.pointerId);if(!pointers.size){tapStart=null;moved=false}}hit.addEventListener('pointerup',e=>end(e,false));hit.addEventListener('pointercancel',e=>end(e,true));let hold=false;$('#left').addEventListener('pointerdown',e=>{if(e.pointerType!=='mouse'){hold=true;send({t:'button',b:0,a:0})}});$('#left').addEventListener('pointerup',()=>{if(hold){hold=false;send({t:'button',b:0,a:1})}});$('#left').addEventListener('pointercancel',()=>{if(hold){hold=false;send({t:'button',b:0,a:1})}});
kbd.addEventListener('input',()=>{if(kbd.value){send({t:'text',v:kbd.value});kbd.value=''}});kbd.addEventListener('keydown',e=>{if(['Enter','Backspace','Escape','Tab','ArrowUp','ArrowDown','ArrowLeft','ArrowRight','Delete'].includes(e.key)){send({t:'key',v:e.key});e.preventDefault()}})})();</script></body></html>
""";
}

internal static class WebKeyboardInjector
{
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
    public static void TypeText(string text)
    {
        foreach (var ch in (text ?? "").Take(4096)) { SendUnicode(ch, false); SendUnicode(ch, true); }
    }
    public static void PressKey(string key)
    {
        ushort vk = key switch { "Enter"=>0x0D, "Backspace"=>0x08, "Escape"=>0x1B, "Tab"=>0x09, "Delete"=>0x2E, "ArrowLeft"=>0x25, "ArrowUp"=>0x26, "ArrowRight"=>0x27, "ArrowDown"=>0x28, _=>0 };
        if (vk == 0) return; SendVirtual(vk, false); SendVirtual(vk, true);
    }
    private static void SendUnicode(char ch, bool up) => Send(new KEYBDINPUT { wScan=ch, dwFlags=KEYEVENTF_UNICODE | (up?KEYEVENTF_KEYUP:0) });
    private static void SendVirtual(ushort vk, bool up) => Send(new KEYBDINPUT { wVk=vk, dwFlags=up?KEYEVENTF_KEYUP:0 });
    private static void Send(KEYBDINPUT ki) { var input=new INPUT { type=INPUT_KEYBOARD, U=new InputUnion { ki=ki } }; _=SendInput(1,new[]{input},Marshal.SizeOf<INPUT>()); }
    [DllImport("user32.dll", SetLastError=true)] private static extern uint SendInput(uint nInputs,[In] INPUT[] pInputs,int cbSize);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
}
