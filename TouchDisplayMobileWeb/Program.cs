using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TouchDisplayMobileWeb;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4));
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, "TouchDisplayMobileWeb.Singleton", out var first);
        if (!first)
        {
            MessageBox.Show("TouchDisplay Mobile Web уже запущен.", "TouchDisplay", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new HostForm());
    }
}

internal sealed class HostForm : Form
{
    private const int WebPort = 59435;
    private readonly Label _status = new();
    private readonly Label _pinLabel = new();
    private readonly Label _addresses = new();
    private readonly CheckBox _autoStart = new();
    private readonly Button _openWeb = new();
    private readonly Button _firewall = new();
    private RemoteServer? _server;
    private readonly string _pin;

    public HostForm()
    {
        _pin = SettingsStore.LoadOrCreatePin();

        Text = "TouchDisplay Mobile Web v1.0";
        ClientSize = new Size(640, 455);
        MinimumSize = new Size(640, 455);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 16, 23);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);

        var title = Label("TouchDisplay Mobile Web", 24, FontStyle.Bold);
        title.Location = new Point(28, 22);
        title.Size = new Size(575, 42);
        Controls.Add(title);

        var sub = Label("Отдельная веб-версия • iPhone / Android / Windows • Tailscale", 10.5f, FontStyle.Regular, Color.FromArgb(170, 180, 194));
        sub.Location = new Point(30, 68);
        sub.Size = new Size(575, 28);
        Controls.Add(sub);

        var pinTitle = Label("PIN:", 11, FontStyle.Regular, Color.FromArgb(175, 185, 200));
        pinTitle.Location = new Point(30, 116);
        pinTitle.Size = new Size(60, 28);
        Controls.Add(pinTitle);

        _pinLabel.Text = _pin;
        _pinLabel.Font = new Font("Consolas", 23, FontStyle.Bold);
        _pinLabel.ForeColor = Color.White;
        _pinLabel.Location = new Point(88, 107);
        _pinLabel.Size = new Size(180, 42);
        Controls.Add(_pinLabel);

        _addresses.Location = new Point(30, 165);
        _addresses.Size = new Size(575, 86);
        _addresses.ForeColor = Color.FromArgb(190, 202, 220);
        _addresses.Text = NetworkInfo.BuildAddressText(WebPort);
        Controls.Add(_addresses);

        _openWeb.Text = "ОТКРЫТЬ WEB";
        _openWeb.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        _openWeb.Location = new Point(30, 270);
        _openWeb.Size = new Size(270, 44);
        _openWeb.Click += (_, _) => OpenLocalWeb();
        Controls.Add(_openWeb);

        _firewall.Text = "НАСТРОИТЬ FIREWALL";
        _firewall.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        _firewall.Location = new Point(315, 270);
        _firewall.Size = new Size(290, 44);
        _firewall.Click += (_, _) => ConfigureFirewall();
        Controls.Add(_firewall);

        _autoStart.Text = "Запускать TouchDisplay автоматически вместе с Windows";
        _autoStart.Location = new Point(31, 330);
        _autoStart.Size = new Size(520, 30);
        _autoStart.Checked = SettingsStore.IsAutoStartEnabled();
        _autoStart.CheckedChanged += (_, _) =>
        {
            try
            {
                SettingsStore.SetAutoStart(_autoStart.Checked);
                SetStatus(_autoStart.Checked ? "Автозапуск включён." : "Автозапуск выключен.");
            }
            catch (Exception ex)
            {
                SetStatus("Не удалось изменить автозапуск: " + ex.Message, true);
            }
        };
        Controls.Add(_autoStart);

        _status.Location = new Point(30, 380);
        _status.Size = new Size(575, 52);
        _status.ForeColor = Color.FromArgb(105, 225, 155);
        Controls.Add(_status);

        Shown += (_, _) =>
        {
            if (!SettingsStore.HasAutoStartPreference())
            {
                try
                {
                    SettingsStore.SetAutoStart(true);
                    _autoStart.Checked = true;
                }
                catch { }
            }
            StartServer();
        };

        FormClosing += (_, _) => _server?.Stop();
    }

    private static Label Label(string text, float size, FontStyle style, Color? color = null) => new()
    {
        Text = text,
        Font = new Font("Segoe UI", size, style),
        ForeColor = color ?? Color.White,
        BackColor = Color.Transparent
    };

    private void StartServer()
    {
        try
        {
            _server = new RemoteServer(WebPort, _pin, SetStatus);
            _server.Start();
            SetStatus($"Готов • Web порт {WebPort} • PIN {_pin} • LAN/Tailscale");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка запуска: " + ex.Message, true);
        }
    }

    private void OpenLocalWeb()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://127.0.0.1:{WebPort}",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось открыть браузер: " + ex.Message, true);
        }
    }

    private void ConfigureFirewall()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall add rule name=\"TouchDisplay Mobile Web\" dir=in action=allow program=\"{exe}\" enable=yes profile=private,domain",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(8000);
            SetStatus("Firewall: правило добавлено. Если Windows спросила разрешение — подтвердите.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetStatus("Настройка Firewall отменена.", true);
        }
        catch (Exception ex)
        {
            SetStatus("Firewall: " + ex.Message, true);
        }
    }

    private void SetStatus(string text, bool warning = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text, warning));
            return;
        }
        _status.ForeColor = warning ? Color.FromArgb(255, 185, 110) : Color.FromArgb(105, 225, 155);
        _status.Text = text;
    }
}

internal static class SettingsStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TouchDisplayMobileWeb";

    private static string Dir
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TouchDisplayMobileWeb");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string PinPath => Path.Combine(Dir, "pin.txt");
    private static string PrefPath => Path.Combine(Dir, "autostart.pref");

    public static string LoadOrCreatePin()
    {
        try
        {
            if (File.Exists(PinPath))
            {
                var p = new string(File.ReadAllText(PinPath).Where(char.IsDigit).Take(6).ToArray());
                if (p.Length == 6) return p;
            }
            var pin = Random.Shared.Next(100000, 1000000).ToString("D6");
            File.WriteAllText(PinPath, pin);
            return pin;
        }
        catch
        {
            return Random.Shared.Next(100000, 1000000).ToString("D6");
        }
    }

    public static bool HasAutoStartPreference() => File.Exists(PrefPath);

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
    }

    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true) ?? throw new InvalidOperationException("Registry недоступен");
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            key.SetValue(AppName, $"\"{exe}\"");
        }
        else key.DeleteValue(AppName, false);
        File.WriteAllText(PrefPath, enabled ? "1" : "0");
    }
}
