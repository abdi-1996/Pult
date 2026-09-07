using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NAudio.Wave;

namespace TouchDisplayWindowsClient;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ClientForm());
    }
}

internal sealed class ClientForm : Form
{
    private const int VideoPort = 59432;
    private const int DiscoveryPort = 59431;
    private const int AudioPort = 59434;

    private readonly Panel _login = new();
    private readonly TextBox _password = new();
    private readonly TextBox _manualHost = new();
    private readonly Button _connect = new();
    private readonly Label _status = new();
    private readonly RemoteView _view = new();
    private readonly Panel _toolbar = new();
    private readonly Label _connectionLabel = new();

    private TcpClient? _videoClient;
    private NetworkStream? _videoStream;
    private TcpClient? _audioClient;
    private CancellationTokenSource? _sessionCts;
    private readonly object _sendLock = new();
    private bool _fullScreen;
    private FormBorderStyle _oldBorder;
    private Rectangle _oldBounds;
    private string _connectedHost = string.Empty;

    private readonly string _settingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TouchDisplayWindowsClient");

    public ClientForm()
    {
        Text = "TouchDisplay Windows Client v3";
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 18, 24);
        ForeColor = Color.White;
        KeyPreview = true;
        WindowState = FormWindowState.Maximized;

        BuildLogin();
        BuildRemoteUi();
        ShowLogin();

        FormClosing += (_, _) => Disconnect();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && _fullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        };
    }

    private void BuildLogin()
    {
        _login.Dock = DockStyle.Fill;
        _login.BackColor = Color.FromArgb(15, 18, 24);

        var box = new Panel
        {
            Width = 520,
            Height = 370,
            BackColor = Color.FromArgb(24, 28, 36)
        };
        _login.Controls.Add(box);
        _login.Resize += (_, _) =>
        {
            box.Left = Math.Max(20, (_login.ClientSize.Width - box.Width) / 2);
            box.Top = Math.Max(20, (_login.ClientSize.Height - box.Height) / 2);
        };

        var title = new Label
        {
            Text = "TouchDisplay",
            Font = new Font("Segoe UI", 26f, FontStyle.Regular),
            ForeColor = Color.White,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(20, 24),
            Size = new Size(480, 54)
        };
        box.Controls.Add(title);

        var sub = new Label
        {
            Text = "Подключение к домашнему ПК\nLAN / Tailscale • экран • звук • мышь",
            Font = new Font("Segoe UI", 11f),
            ForeColor = Color.Gainsboro,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(20, 82),
            Size = new Size(480, 52)
        };
        box.Controls.Add(sub);

        _password.PlaceholderText = "6-значный пароль из TouchDisplay Host";
        _password.MaxLength = 6;
        _password.UseSystemPasswordChar = true;
        _password.Font = new Font("Segoe UI", 13f);
        _password.Location = new Point(70, 150);
        _password.Size = new Size(380, 36);
        _password.TextAlign = HorizontalAlignment.Center;
        box.Controls.Add(_password);

        _manualHost.PlaceholderText = "Tailscale IP/имя (необязательно)";
        _manualHost.Font = new Font("Segoe UI", 11f);
        _manualHost.Location = new Point(70, 200);
        _manualHost.Size = new Size(380, 33);
        _manualHost.Text = LoadSavedTail();
        box.Controls.Add(_manualHost);

        _connect.Text = "ПОДКЛЮЧИТЬСЯ";
        _connect.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        _connect.Location = new Point(70, 250);
        _connect.Size = new Size(380, 44);
        _connect.Click += async (_, _) => await BeginConnectAsync();
        box.Controls.Add(_connect);

        _status.Text = "Введите пароль. В домашней сети ПК найдётся автоматически.";
        _status.Font = new Font("Segoe UI", 9.5f);
        _status.ForeColor = Color.Gray;
        _status.AutoSize = false;
        _status.TextAlign = ContentAlignment.MiddleCenter;
        _status.Location = new Point(40, 302);
        _status.Size = new Size(440, 48);
        box.Controls.Add(_status);

        Controls.Add(_login);
    }

    private void BuildRemoteUi()
    {
        _view.Dock = DockStyle.Fill;
        _view.Visible = false;
        _view.BackColor = Color.Black;
        _view.MouseDown += ViewMouseDown;
        _view.MouseMove += ViewMouseMove;
        _view.MouseUp += ViewMouseUp;
        _view.MouseWheel += ViewMouseWheel;
        Controls.Add(_view);

        _toolbar.Dock = DockStyle.Top;
        _toolbar.Height = 42;
        _toolbar.BackColor = Color.FromArgb(28, 32, 40);
        _toolbar.Visible = false;

        _connectionLabel.AutoSize = false;
        _connectionLabel.Location = new Point(12, 0);
        _connectionLabel.Size = new Size(520, 42);
        _connectionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _connectionLabel.ForeColor = Color.Gainsboro;
        _toolbar.Controls.Add(_connectionLabel);

        var full = new Button
        {
            Text = "Полный экран (F11)",
            Width = 150,
            Height = 30,
            Top = 6,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        var disconnect = new Button
        {
            Text = "Отключить",
            Width = 110,
            Height = 30,
            Top = 6,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _toolbar.Controls.Add(full);
        _toolbar.Controls.Add(disconnect);
        _toolbar.Resize += (_, _) =>
        {
            disconnect.Left = _toolbar.ClientSize.Width - disconnect.Width - 10;
            full.Left = disconnect.Left - full.Width - 8;
        };
        full.Click += (_, _) => ToggleFullScreen();
        disconnect.Click += (_, _) =>
        {
            Disconnect();
            ShowLogin("Отключено");
        };
        Controls.Add(_toolbar);
        _toolbar.BringToFront();
    }

    private async Task BeginConnectAsync()
    {
        var password = new string(_password.Text.Where(char.IsDigit).Take(6).ToArray());
        if (password.Length != 6)
        {
            SetStatus("Пароль должен состоять из 6 цифр", true);
            return;
        }

        _connect.Enabled = false;
        SetStatus("Поиск ПК…", false);

        try
        {
            Disconnect();
            var manual = _manualHost.Text.Trim();
            var target = await FindTargetAsync(password, manual);
            await OpenSessionAsync(target.host, target.port, password);
            _connectedHost = target.host;
            if (IsTailscale(target.host)) SaveTail(target.host);
            if (!string.IsNullOrWhiteSpace(target.tail)) SaveTail(target.tail!);
            ShowRemote(target.host);
        }
        catch (Exception ex)
        {
            Disconnect();
            SetStatus("Ошибка подключения: " + ex.Message, true);
        }
        finally
        {
            _connect.Enabled = true;
        }
    }

    private async Task<(string host, int port, string? tail)> FindTargetAsync(string password, string manual)
    {
        var candidates = new List<(string host, int port, string? tail)>();

        if (!string.IsNullOrWhiteSpace(manual))
        {
            candidates.Add((manual, VideoPort, manual));
        }
        else
        {
            var found = await DiscoverAsync(password);
            if (found is not null)
            {
                candidates.Add((found.Value.lan, found.Value.port, found.Value.tail));
                if (!string.IsNullOrWhiteSpace(found.Value.tail))
                    candidates.Add((found.Value.tail!, found.Value.port, found.Value.tail));
            }

            var savedTail = LoadSavedTail();
            if (!string.IsNullOrWhiteSpace(savedTail))
                candidates.Add((savedTail, VideoPort, savedTail));
        }

        if (candidates.Count == 0)
            throw new IOException("ПК не найден. Вне дома укажите Tailscale IP или имя ПК.");

        Exception? last = null;
        foreach (var candidate in candidates
                     .GroupBy(x => $"{x.host}:{x.port}")
                     .Select(x => x.First()))
        {
            try
            {
                using var probe = new TcpClient();
                probe.NoDelay = true;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await probe.ConnectAsync(candidate.host, candidate.port, timeout.Token);
                var stream = probe.GetStream();
                await AuthenticateVideoAsync(stream, password, timeout.Token);
                return candidate;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw last ?? new IOException("ПК недоступен");
    }

    private async Task OpenSessionAsync(string host, int port, string password)
    {
        _sessionCts = new CancellationTokenSource();
        var token = _sessionCts.Token;

        var client = new TcpClient
        {
            NoDelay = true,
            ReceiveBufferSize = 2 * 1024 * 1024,
            SendBufferSize = 128 * 1024
        };
        await client.ConnectAsync(host, port, token);
        var stream = client.GetStream();
        await AuthenticateVideoAsync(stream, password, token);

        _videoClient = client;
        _videoStream = stream;

        _ = Task.Run(() => VideoLoopAsync(stream, token), token);
        _ = Task.Run(() => AudioLoopAsync(host, password, token), token);
    }

    private static async Task AuthenticateVideoAsync(NetworkStream stream, string password, CancellationToken token)
    {
        var pass = Encoding.UTF8.GetBytes(password);
        var hello = new byte[8 + pass.Length];
        Encoding.ASCII.GetBytes("TD01").CopyTo(hello, 0);
        BinaryPrimitives.WriteInt32BigEndian(hello.AsSpan(4, 4), pass.Length);
        pass.CopyTo(hello, 8);
        await stream.WriteAsync(hello, token);
        await stream.FlushAsync(token);

        var one = new byte[1];
        await ReadExactAsync(stream, one, token);
        if (one[0] != 1) throw new UnauthorizedAccessException("Неверный пароль");
    }

    private async Task VideoLoopAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            var lenBuf = new byte[4];
            while (!token.IsCancellationRequested)
            {
                await ReadExactAsync(stream, lenBuf, token);
                var length = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                if (length <= 0 || length > 24_000_000)
                    throw new IOException("Некорректный видеокадр");

                var bytes = new byte[length];
                await ReadExactAsync(stream, bytes, token);
                using var ms = new MemoryStream(bytes, writable: false);
                using var temp = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
                var bitmap = new Bitmap(temp);
                BeginInvoke(() => _view.SetFrame(bitmap));
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            BeginInvoke(() =>
            {
                Disconnect();
                ShowLogin("Соединение потеряно: " + ex.Message);
            });
        }
    }

    private async Task AudioLoopAsync(string host, string password, CancellationToken token)
    {
        WaveOutEvent? output = null;
        try
        {
            var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 256 * 1024 };
            await client.ConnectAsync(host, AudioPort, token);
            _audioClient = client;
            var stream = client.GetStream();

            var pass = Encoding.UTF8.GetBytes(password);
            var hello = new byte[8 + pass.Length];
            Encoding.ASCII.GetBytes("TDA2").CopyTo(hello, 0);
            BinaryPrimitives.WriteInt32BigEndian(hello.AsSpan(4, 4), pass.Length);
            pass.CopyTo(hello, 8);
            await stream.WriteAsync(hello, token);
            await stream.FlushAsync(token);

            var one = new byte[1];
            await ReadExactAsync(stream, one, token);
            if (one[0] != 1) return;

            var intBuf = new byte[4];
            await ReadExactAsync(stream, intBuf, token);
            var sampleRate = BinaryPrimitives.ReadInt32BigEndian(intBuf);
            var fmt = new byte[2];
            await ReadExactAsync(stream, fmt, token);
            var channels = fmt[0];
            var bits = fmt[1];
            if (sampleRate <= 0 || channels is < 1 or > 2 || bits != 16) return;

            var provider = new BufferedWaveProvider(new WaveFormat(sampleRate, bits, channels))
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromMilliseconds(220)
            };
            output = new WaveOutEvent
            {
                DesiredLatency = 80,
                NumberOfBuffers = 3
            };
            output.Init(provider);
            output.Play();

            while (!token.IsCancellationRequested)
            {
                await ReadExactAsync(stream, intBuf, token);
                var length = BinaryPrimitives.ReadInt32BigEndian(intBuf);
                if (length <= 0 || length > 262_144) throw new IOException("Некорректный аудиопакет");
                var pcm = new byte[length];
                await ReadExactAsync(stream, pcm, token);
                provider.AddSamples(pcm, 0, pcm.Length);
            }
        }
        catch when (token.IsCancellationRequested)
        {
        }
        catch
        {
            // Audio is optional; video/control stays connected.
        }
        finally
        {
            try { output?.Stop(); } catch { }
            output?.Dispose();
        }
    }

    private async Task<(string lan, int port, string? tail)?> DiscoverAsync(string password)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.EnableBroadcast = true;
        udp.Client.ReceiveTimeout = 1300;
        var request = Encoding.UTF8.GetBytes("TDISCOVER17|" + password);
        await udp.SendAsync(request, request.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1400));
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(cts.Token);
                var text = Encoding.UTF8.GetString(result.Buffer).Trim();
                var parts = text.Split('|');
                if (parts.Length >= 4 && parts[0] == "TDHOST17" && int.TryParse(parts[1], out var port))
                {
                    var tail = string.IsNullOrWhiteSpace(parts[2]) ? null : parts[2];
                    return (result.RemoteEndPoint.Address.ToString(), port, tail);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        return null;
    }

    private void ViewMouseDown(object? sender, MouseEventArgs e)
    {
        if (!_view.TryNormalize(e.Location, out var nx, out var ny)) return;
        _view.Focus();
        if (e.Button == MouseButtons.Left)
            SendTouch(0, 0, nx, ny);
        else if (e.Button == MouseButtons.Right)
            SendRightClick(nx, ny);
    }

    private void ViewMouseMove(object? sender, MouseEventArgs e)
    {
        if ((e.Button & MouseButtons.Left) == 0) return;
        if (_view.TryNormalize(e.Location, out var nx, out var ny))
            SendTouch(1, 0, nx, ny);
    }

    private void ViewMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_view.TryNormalize(e.Location, out var nx, out var ny))
            SendTouch(2, 0, nx, ny);
    }

    private void ViewMouseWheel(object? sender, MouseEventArgs e)
    {
        if (!_view.TryNormalize(e.Location, out var nx, out var ny)) return;
        // Keep Host unchanged: emulate a short vertical touch gesture for wheel scrolling.
        var amount = e.Delta > 0 ? 0.10f : -0.10f;
        var endY = Math.Clamp(ny + amount, 0.02f, 0.98f);
        SendTouch(0, 7, nx, ny);
        SendTouch(1, 7, nx, (ny + endY) / 2f);
        SendTouch(1, 7, nx, endY);
        SendTouch(2, 7, nx, endY);
    }

    private void SendTouch(byte action, int pointerId, float nx, float ny)
    {
        var stream = _videoStream;
        if (stream is null) return;

        var packet = new byte[14];
        packet[0] = 0x10;
        packet[1] = action;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(2, 4), pointerId);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(6, 4), BitConverter.SingleToInt32Bits(nx));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(10, 4), BitConverter.SingleToInt32Bits(ny));
        WritePacket(stream, packet);
    }

    private void SendRightClick(float nx, float ny)
    {
        var stream = _videoStream;
        if (stream is null) return;

        var packet = new byte[9];
        packet[0] = 0x11;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1, 4), BitConverter.SingleToInt32Bits(nx));
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(5, 4), BitConverter.SingleToInt32Bits(ny));
        WritePacket(stream, packet);
    }

    private void WritePacket(NetworkStream stream, byte[] packet)
    {
        try
        {
            lock (_sendLock)
            {
                stream.Write(packet, 0, packet.Length);
                stream.Flush();
            }
        }
        catch
        {
        }
    }

    private void ShowRemote(string host)
    {
        _login.Visible = false;
        _view.Visible = true;
        _toolbar.Visible = true;
        _view.BringToFront();
        _toolbar.BringToFront();
        _connectionLabel.Text = $"Подключено: {host}   •   F11 — полный экран";
        ActiveControl = _view;
    }

    private void ShowLogin(string? message = null)
    {
        if (_fullScreen) ToggleFullScreen();
        _view.Visible = false;
        _toolbar.Visible = false;
        _login.Visible = true;
        _login.BringToFront();
        if (!string.IsNullOrWhiteSpace(message)) SetStatus(message, message.Contains("Ошибка") || message.Contains("потеряно"));
        ActiveControl = _password;
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.FromArgb(255, 180, 105) : Color.Gray;
    }

    private void ToggleFullScreen()
    {
        if (!_view.Visible) return;
        if (!_fullScreen)
        {
            _oldBorder = FormBorderStyle;
            _oldBounds = Bounds;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;
            _toolbar.Visible = false;
            _fullScreen = true;
        }
        else
        {
            FormBorderStyle = _oldBorder;
            Bounds = _oldBounds;
            WindowState = FormWindowState.Maximized;
            _toolbar.Visible = true;
            _toolbar.BringToFront();
            _fullScreen = false;
        }
    }

    private void Disconnect()
    {
        try { _sessionCts?.Cancel(); } catch { }
        try { _videoClient?.Close(); } catch { }
        try { _audioClient?.Close(); } catch { }
        _sessionCts?.Dispose();
        _sessionCts = null;
        _videoClient = null;
        _videoStream = null;
        _audioClient = null;
        _connectedHost = string.Empty;
        _view.ClearFrame();
    }

    private string LoadSavedTail()
    {
        try
        {
            var path = Path.Combine(_settingsDir, "tailscale.txt");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch { return string.Empty; }
    }

    private void SaveTail(string value)
    {
        if (!IsTailscale(value) && !value.Contains('.')) return;
        try
        {
            Directory.CreateDirectory(_settingsDir);
            File.WriteAllText(Path.Combine(_settingsDir, "tailscale.txt"), value.Trim());
        }
        catch { }
    }

    private static bool IsTailscale(string host)
    {
        if (!IPAddress.TryParse(host, out var ip)) return false;
        var b = ip.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new EndOfStreamException("Соединение закрыто ПК");
            offset += read;
        }
    }
}

internal sealed class RemoteView : Control
{
    private Bitmap? _frame;
    private RectangleF _imageRect;

    public RemoteView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        TabStop = true;
        Cursor = Cursors.Default;
    }

    public void SetFrame(Bitmap bitmap)
    {
        var old = _frame;
        _frame = bitmap;
        old?.Dispose();
        Invalidate();
    }

    public void ClearFrame()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ClearFrame);
            return;
        }
        var old = _frame;
        _frame = null;
        old?.Dispose();
        _imageRect = RectangleF.Empty;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.Black);
        var frame = _frame;
        if (frame is null || frame.Width <= 0 || frame.Height <= 0) return;

        var scale = Math.Min(ClientSize.Width / (float)frame.Width, ClientSize.Height / (float)frame.Height);
        var w = frame.Width * scale;
        var h = frame.Height * scale;
        var x = (ClientSize.Width - w) / 2f;
        var y = (ClientSize.Height - h) / 2f;
        _imageRect = new RectangleF(x, y, w, h);

        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        e.Graphics.DrawImage(frame, _imageRect);
    }

    public bool TryNormalize(Point point, out float nx, out float ny)
    {
        if (_imageRect.IsEmpty || !_imageRect.Contains(point))
        {
            nx = ny = 0;
            return false;
        }

        nx = Math.Clamp((point.X - _imageRect.Left) / _imageRect.Width, 0f, 1f);
        ny = Math.Clamp((point.Y - _imageRect.Top) / _imageRect.Height, 0f, 1f);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frame?.Dispose();
            _frame = null;
        }
        base.Dispose(disposing);
    }
}
