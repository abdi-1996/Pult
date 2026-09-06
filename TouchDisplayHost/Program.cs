using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace TouchDisplayHost;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4));
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly Label _status = new();
    private readonly Label _addresses = new();
    private readonly Label _pin = new();
    private readonly NumericUpDown _port = new();
    private readonly Button _toggle = new();
    private TouchServer? _server;
    private readonly string _pinCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public MainForm()
    {
        Text = "TouchDisplay Host v1";
        ClientSize = new Size(600, 390);
        MinimumSize = new Size(600, 390);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 21, 28);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        var title = MakeLabel("TouchDisplay Host", 24f, FontStyle.Bold);
        title.Location = new Point(26, 22);
        title.Size = new Size(540, 42);
        Controls.Add(title);

        var sub = MakeLabel("Android-планшет → настоящий Windows Touch", 11f, FontStyle.Regular, Color.Gainsboro);
        sub.Location = new Point(28, 68);
        sub.Size = new Size(540, 30);
        Controls.Add(sub);

        _addresses.Location = new Point(28, 112);
        _addresses.Size = new Size(540, 64);
        _addresses.ForeColor = Color.LightGray;
        _addresses.Text = BuildAddressText();
        Controls.Add(_addresses);

        var pinTitle = MakeLabel("PIN для планшета:", 11f, FontStyle.Regular, Color.LightGray);
        pinTitle.Location = new Point(28, 190);
        pinTitle.Size = new Size(180, 28);
        Controls.Add(pinTitle);

        _pin.Text = _pinCode;
        _pin.Font = new Font("Consolas", 20f, FontStyle.Bold);
        _pin.ForeColor = Color.White;
        _pin.Location = new Point(210, 184);
        _pin.Size = new Size(140, 40);
        Controls.Add(_pin);

        var portTitle = MakeLabel("Порт:", 11f, FontStyle.Regular, Color.LightGray);
        portTitle.Location = new Point(375, 190);
        portTitle.Size = new Size(55, 28);
        Controls.Add(portTitle);

        _port.Minimum = 1024;
        _port.Maximum = 65535;
        _port.Value = 59432;
        _port.Location = new Point(432, 188);
        _port.Size = new Size(120, 30);
        Controls.Add(_port);

        _toggle.Text = "ОСТАНОВИТЬ";
        _toggle.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _toggle.Location = new Point(28, 246);
        _toggle.Size = new Size(524, 48);
        _toggle.Click += (_, _) => ToggleServer();
        Controls.Add(_toggle);

        _status.Text = "Запуск…";
        _status.Location = new Point(28, 315);
        _status.Size = new Size(524, 42);
        _status.ForeColor = Color.FromArgb(110, 220, 150);
        Controls.Add(_status);

        Shown += (_, _) => StartServer();
        FormClosing += (_, _) => _server?.Stop();
    }

    private static Label MakeLabel(string text, float size, FontStyle style, Color? color = null) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", size, style),
        ForeColor = color ?? Color.White,
        BackColor = Color.Transparent
    };

    private void ToggleServer()
    {
        if (_server is null)
            StartServer();
        else
            StopServer();
    }

    private void StartServer()
    {
        try
        {
            var port = (int)_port.Value;
            _server = new TouchServer(port, _pinCode, SetStatus);
            _server.Start();
            _port.Enabled = false;
            _toggle.Text = "ОСТАНОВИТЬ";
            SetStatus($"Готов. Откройте TouchDisplay на планшете. Порт {port}.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка запуска: " + ex.Message, true);
            _server = null;
        }
    }

    private void StopServer()
    {
        _server?.Stop();
        _server = null;
        _port.Enabled = true;
        _toggle.Text = "ЗАПУСТИТЬ";
        SetStatus("Сервер остановлен.", true);
    }

    private void SetStatus(string text, bool warning = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text, warning));
            return;
        }
        _status.ForeColor = warning ? Color.FromArgb(255, 185, 110) : Color.FromArgb(110, 220, 150);
        _status.Text = text;
    }

    private static string BuildAddressText()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct()
            .ToList();

        var tail = all.FirstOrDefault(IsTailscale);
        var local = all.FirstOrDefault(a => !IsTailscale(a));
        var lines = new List<string>();
        if (local is not null) lines.Add("LAN: " + local);
        if (tail is not null) lines.Add("Tailscale: " + tail);
        if (lines.Count == 0) lines.Add("IP: сеть не найдена");
        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsTailscale(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}

internal sealed class TouchServer
{
    private readonly int _port;
    private readonly string _pin;
    private readonly Action<string, bool> _status;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);

    public TouchServer(int port, string pin, Action<string, bool> status)
    {
        _port = port;
        _pin = pin;
        _status = status;
    }

    public void Start()
    {
        if (!TouchInjector.Initialize())
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Touch API недоступен");

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
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
                _status("Ошибка сети: " + ex.Message, true);
                await Task.Delay(500, token);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        {
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "клиент";
            try
            {
                var stream = client.GetStream();
                var magic = new byte[4];
                await NetIo.ReadExactAsync(stream, magic, serverToken);
                if (Encoding.ASCII.GetString(magic) != "TD01") return;

                var pinLength = await NetIo.ReadInt32BigEndianAsync(stream, serverToken);
                if (pinLength < 1 || pinLength > 32) return;
                var pinBytes = new byte[pinLength];
                await NetIo.ReadExactAsync(stream, pinBytes, serverToken);
                var suppliedPin = Encoding.UTF8.GetString(pinBytes);
                var ok = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(suppliedPin),
                    Encoding.UTF8.GetBytes(_pin));
                await stream.WriteAsync(new byte[] { ok ? (byte)1 : (byte)0 }, serverToken);
                await stream.FlushAsync(serverToken);
                if (!ok)
                {
                    _status($"Отклонён неверный PIN: {remote}", true);
                    return;
                }

                _status($"Планшет подключён: {remote}", false);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                var send = StreamFramesAsync(stream, linked.Token);
                var receive = ReceiveInputAsync(stream, linked.Token);
                await Task.WhenAny(send, receive);
                linked.Cancel();
                try { await Task.WhenAll(send, receive); } catch { }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                _status("Клиент: " + ex.Message, true);
            }
            finally
            {
                if (!serverToken.IsCancellationRequested)
                    _status("Планшет отключён. Ожидание подключения…", false);
            }
        }
    }

    private static async Task StreamFramesAsync(NetworkStream stream, CancellationToken token)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        const int maxWidth = 1600;
        const int frameDelayMs = 50;
        const long jpegQuality = 58L;

        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            using var bitmap = CaptureScreen(bounds, maxWidth);
            using var ms = new MemoryStream(512 * 1024);
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

    private static async Task ReceiveInputAsync(NetworkStream stream, CancellationToken token)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        var packet = new byte[13];

        while (!token.IsCancellationRequested)
        {
            var type = await NetIo.ReadByteAsync(stream, token);
            if (type != 0x10) throw new IOException("Неизвестный пакет");
            await NetIo.ReadExactAsync(stream, packet, token);

            var action = packet[0];
            var pointerId = NetIo.Int32FromBigEndian(packet.AsSpan(1, 4));
            var nx = NetIo.SingleFromBigEndian(packet.AsSpan(5, 4));
            var ny = NetIo.SingleFromBigEndian(packet.AsSpan(9, 4));
            TouchInjector.Inject(action, pointerId, nx, ny, bounds);
        }
    }
}

internal static class NetIo
{
    public static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    public static async Task<byte> ReadByteAsync(Stream stream, CancellationToken token)
    {
        var b = new byte[1];
        await ReadExactAsync(stream, b, token);
        return b[0];
    }

    public static async Task<int> ReadInt32BigEndianAsync(Stream stream, CancellationToken token)
    {
        var b = new byte[4];
        await ReadExactAsync(stream, b, token);
        return Int32FromBigEndian(b);
    }

    public static int Int32FromBigEndian(ReadOnlySpan<byte> b) =>
        (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];

    public static float SingleFromBigEndian(ReadOnlySpan<byte> b)
    {
        Span<byte> native = stackalloc byte[4];
        if (BitConverter.IsLittleEndian)
        {
            native[0] = b[3]; native[1] = b[2]; native[2] = b[1]; native[3] = b[0];
        }
        else
        {
            b.CopyTo(native);
        }
        return BitConverter.ToSingle(native);
    }

    public static async Task WriteInt32BigEndianAsync(Stream stream, int value, CancellationToken token)
    {
        var b = new byte[]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        };
        await stream.WriteAsync(b, token);
    }
}

internal static class TouchInjector
{
    private static bool _initialized;

    public static bool Initialize()
    {
        if (_initialized) return true;
        _initialized = NativeMethods.InitializeTouchInjection(32, 3);
        return _initialized;
    }

    public static void Inject(byte action, int androidPointerId, float nx, float ny, Rectangle bounds)
    {
        if (!_initialized) return;
        if (float.IsNaN(nx) || float.IsNaN(ny)) return;

        nx = Math.Clamp(nx, 0f, 1f);
        ny = Math.Clamp(ny, 0f, 1f);
        var x = bounds.Left + (int)Math.Round(nx * Math.Max(1, bounds.Width - 1));
        var y = bounds.Top + (int)Math.Round(ny * Math.Max(1, bounds.Height - 1));

        PointerFlags flags = action switch
        {
            0 => PointerFlags.Down | PointerFlags.InRange | PointerFlags.InContact,
            1 => PointerFlags.Update | PointerFlags.InRange | PointerFlags.InContact,
            2 => PointerFlags.Up,
            3 => PointerFlags.Up | PointerFlags.Canceled,
            _ => PointerFlags.Update | PointerFlags.InRange | PointerFlags.InContact
        };

        if (androidPointerId == 0) flags |= PointerFlags.Primary;
        var contact = new Rect { Left = x - 2, Top = y - 2, Right = x + 2, Bottom = y + 2 };
        var info = new PointerTouchInfo
        {
            PointerInfo = new PointerInfo
            {
                PointerType = PointerInputType.Touch,
                PointerId = (uint)Math.Clamp(androidPointerId + 1, 1, 255),
                PointerFlags = flags,
                PtPixelLocation = new PointNative { X = x, Y = y }
            },
            TouchFlags = 0,
            TouchMask = TouchMask.ContactArea | TouchMask.Orientation | TouchMask.Pressure,
            RcContact = contact,
            RcContactRaw = contact,
            Orientation = 90,
            Pressure = action is 2 or 3 ? 0u : 32000u
        };

        if (!NativeMethods.InjectTouchInput(1, new[] { info }))
            _ = Marshal.GetLastWin32Error();
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}

internal enum PointerInputType : uint
{
    Pointer = 1,
    Touch = 2,
    Pen = 3,
    Mouse = 4,
    TouchPad = 5
}

[Flags]
internal enum PointerFlags : uint
{
    None = 0x00000000,
    New = 0x00000001,
    InRange = 0x00000002,
    InContact = 0x00000004,
    Primary = 0x00002000,
    Confidence = 0x00004000,
    Canceled = 0x00008000,
    Down = 0x00010000,
    Update = 0x00020000,
    Up = 0x00040000
}

[Flags]
internal enum TouchMask : uint
{
    None = 0,
    ContactArea = 0x0001,
    Orientation = 0x0002,
    Pressure = 0x0004
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointNative
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointerInfo
{
    public PointerInputType PointerType;
    public uint PointerId;
    public uint FrameId;
    public PointerFlags PointerFlags;
    public IntPtr SourceDevice;
    public IntPtr HwndTarget;
    public PointNative PtPixelLocation;
    public PointNative PtHimetricLocation;
    public PointNative PtPixelLocationRaw;
    public PointNative PtHimetricLocationRaw;
    public uint DwTime;
    public uint HistoryCount;
    public int InputData;
    public uint DwKeyStates;
    public ulong PerformanceCount;
    public uint ButtonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointerTouchInfo
{
    public PointerInfo PointerInfo;
    public uint TouchFlags;
    public TouchMask TouchMask;
    public Rect RcContact;
    public Rect RcContactRaw;
    public uint Orientation;
    public uint Pressure;
}
