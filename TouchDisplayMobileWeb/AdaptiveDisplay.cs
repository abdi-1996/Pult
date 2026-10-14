using System.Runtime.InteropServices;

namespace TouchDisplayMobileWeb;

internal sealed class AdaptiveDisplaySession : IDisposable
{
    private readonly Action<string, bool> _status;
    private readonly object _sync = new();
    private string? _deviceName;
    private DisplayModeNative.DevMode _original;
    private bool _hasOriginal;
    private bool _changedByUs;
    private bool _disposed;
    private int _lastClientWidth;
    private int _lastClientHeight;

    public AdaptiveDisplaySession(Action<string, bool> status)
    {
        _status = status;
    }

    public void ApplyForClient(int clientWidth, int clientHeight, double dpr)
    {
        if (clientWidth < 100 || clientHeight < 100) return;

        lock (_sync)
        {
            if (_disposed) return;

            // Ignore tiny Safari viewport changes. Orientation / real size changes still apply.
            var oldPortrait = _lastClientHeight > _lastClientWidth;
            var newPortrait = clientHeight > clientWidth;
            var oldRatio = _lastClientWidth > 0 && _lastClientHeight > 0
                ? _lastClientWidth / (double)_lastClientHeight : 0;
            var newRatio = clientWidth / (double)clientHeight;
            if (_lastClientWidth > 0 && oldPortrait == newPortrait && Math.Abs(oldRatio - newRatio) < 0.025)
                return;

            _lastClientWidth = clientWidth;
            _lastClientHeight = clientHeight;

            var screen = Screen.PrimaryScreen;
            if (screen is null) return;

            var device = screen.DeviceName;
            var current = DisplayModeNative.CreateMode();
            if (!DisplayModeNative.EnumDisplaySettings(device, DisplayModeNative.ENUM_CURRENT_SETTINGS, ref current))
            {
                _status("Adaptive Display: не удалось прочитать режим монитора.", true);
                return;
            }

            if (!_hasOriginal)
            {
                _deviceName = device;
                _original = current;
                _hasOriginal = true;
            }

            var wantsPortrait = clientHeight > clientWidth;
            var targetPxW = Math.Max(1.0, clientWidth * Math.Max(1.0, dpr));
            var targetPxH = Math.Max(1.0, clientHeight * Math.Max(1.0, dpr));
            var targetRatio = targetPxW / targetPxH;

            var best = FindBestMode(device, wantsPortrait, targetRatio, targetPxW, targetPxH);
            if (best is null)
            {
                _status("Adaptive Display: подходящий режим монитора не найден.", true);
                return;
            }

            var mode = best.Value.Mode;
            var longSide = Math.Max(mode.dmPelsWidth, mode.dmPelsHeight);
            var shortSide = Math.Min(mode.dmPelsWidth, mode.dmPelsHeight);

            mode.dmSize = (short)Marshal.SizeOf<DisplayModeNative.DevMode>();
            mode.dmFields |= DisplayModeNative.DM_DISPLAYORIENTATION |
                             DisplayModeNative.DM_PELSWIDTH |
                             DisplayModeNative.DM_PELSHEIGHT;

            var originalPortrait = IsPortrait(_original);
            if (wantsPortrait)
            {
                mode.dmDisplayOrientation = originalPortrait
                    ? _original.dmDisplayOrientation
                    : DisplayModeNative.DMDO_90;
                mode.dmPelsWidth = shortSide;
                mode.dmPelsHeight = longSide;
            }
            else
            {
                mode.dmDisplayOrientation = originalPortrait
                    ? DisplayModeNative.DMDO_DEFAULT
                    : _original.dmDisplayOrientation;
                mode.dmPelsWidth = longSide;
                mode.dmPelsHeight = shortSide;
            }

            // Already at effectively the same mode.
            if (current.dmPelsWidth == mode.dmPelsWidth &&
                current.dmPelsHeight == mode.dmPelsHeight &&
                current.dmDisplayOrientation == mode.dmDisplayOrientation)
                return;

            var result = DisplayModeNative.ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, 0, IntPtr.Zero);
            if (result == DisplayModeNative.DISP_CHANGE_SUCCESSFUL)
            {
                _changedByUs = true;
                _status($"Adaptive Display • телефон {clientWidth}×{clientHeight} • Windows {mode.dmPelsWidth}×{mode.dmPelsHeight} • без искажений", false);
                Thread.Sleep(300);
            }
            else
            {
                _status($"Adaptive Display: Windows не применил режим (код {result}). Экран останется с сохранением пропорций.", true);
            }
        }
    }

    private static (DisplayModeNative.DevMode Mode, double Score)? FindBestMode(
        string device,
        bool portrait,
        double targetRatio,
        double targetPxW,
        double targetPxH)
    {
        (DisplayModeNative.DevMode Mode, double Score)? best = null;

        for (var i = 0; i < 256; i++)
        {
            var m = DisplayModeNative.CreateMode();
            if (!DisplayModeNative.EnumDisplaySettings(device, i, ref m)) break;
            if (m.dmPelsWidth < 640 || m.dmPelsHeight < 480) continue;
            if (m.dmBitsPerPel > 0 && m.dmBitsPerPel < 24) continue;

            var longSide = Math.Max(m.dmPelsWidth, m.dmPelsHeight);
            var shortSide = Math.Min(m.dmPelsWidth, m.dmPelsHeight);
            var w = portrait ? shortSide : longSide;
            var h = portrait ? longSide : shortSide;
            var ratio = w / (double)h;

            // Aspect ratio matters most. Among equally-shaped modes prefer one
            // closest to the phone's physical pixel size, normally the highest useful mode.
            var aspectError = Math.Abs(Math.Log(Math.Max(0.01, ratio) / Math.Max(0.01, targetRatio)));
            var area = Math.Max(1.0, w * (double)h);
            var targetArea = Math.Max(1.0, targetPxW * targetPxH);
            var areaError = Math.Abs(Math.Log(area / targetArea));
            var score = aspectError * 100.0 + areaError * 0.35;

            if (best is null || score < best.Value.Score)
                best = (m, score);
        }

        return best;
    }

    private static bool IsPortrait(DisplayModeNative.DevMode mode) =>
        mode.dmDisplayOrientation is DisplayModeNative.DMDO_90 or DisplayModeNative.DMDO_270 ||
        mode.dmPelsHeight > mode.dmPelsWidth;

    public void Restore()
    {
        lock (_sync)
        {
            if (_disposed) return;
            RestoreCore();
            _lastClientWidth = 0;
            _lastClientHeight = 0;
        }
    }

    private void RestoreCore()
    {
        if (!_changedByUs || !_hasOriginal || string.IsNullOrWhiteSpace(_deviceName)) return;
        var original = _original;
        original.dmSize = (short)Marshal.SizeOf<DisplayModeNative.DevMode>();
        var result = DisplayModeNative.ChangeDisplaySettingsEx(_deviceName, ref original, IntPtr.Zero, 0, IntPtr.Zero);
        if (result == DisplayModeNative.DISP_CHANGE_SUCCESSFUL)
        {
            _changedByUs = false;
            Thread.Sleep(220);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            RestoreCore();
            _disposed = true;
        }
    }
}

internal static class DisplayModeNative
{
    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DISP_CHANGE_SUCCESSFUL = 0;
    public const int DMDO_DEFAULT = 0;
    public const int DMDO_90 = 1;
    public const int DMDO_180 = 2;
    public const int DMDO_270 = 3;
    public const int DM_DISPLAYORIENTATION = 0x00000080;
    public const int DM_PELSWIDTH = 0x00080000;
    public const int DM_PELSHEIGHT = 0x00100000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    public static DevMode CreateMode() => new()
    {
        dmDeviceName = new string('\0', 32),
        dmFormName = new string('\0', 32),
        dmSize = (short)Marshal.SizeOf<DevMode>()
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DevMode lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);
}
