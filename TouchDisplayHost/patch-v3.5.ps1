$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v3.1.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v3.1', 'TouchDisplay Host v3.5')
$text = $text.Replace('Готов v3.1 • Adaptive iPhone/iPad • Screen + Touch + Audio • LAN / Tailscale', 'Готов v3.5 • Auto Portrait iPhone/iPad • Screen + Touch + Audio • LAN / Tailscale')

# Give each connected adaptive client a display-orientation session. The original
# Windows display mode is restored automatically when the client disconnects.
$oldTasks = @'
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                var displayProfile = new ClientDisplayProfile();
                var send = StreamFramesAsync(stream, displayProfile, linked.Token);
                var receive = ReceiveInputAsync(stream, displayProfile, linked.Token);
'@
$newTasks = @'
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                var displayProfile = new ClientDisplayProfile();
                using var displayRotation = new DisplayRotationSession(_status);
                var send = StreamFramesAsync(stream, displayProfile, linked.Token);
                var receive = ReceiveInputAsync(stream, displayProfile, displayRotation, linked.Token);
'@
if (-not $text.Contains($oldTasks)) { throw 'v3.5 client rotation task target not found' }
$text = $text.Replace($oldTasks, $newTasks)

# Screen bounds can change after Windows rotates, so do not cache them for the
# whole streaming session.
$oldStreamStart = @'
    private static async Task StreamFramesAsync(NetworkStream stream, ClientDisplayProfile profile, CancellationToken token)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;

        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
'@
$newStreamStart = @'
    private static async Task StreamFramesAsync(NetworkStream stream, ClientDisplayProfile profile, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var started = Environment.TickCount64;
            var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
'@
if (-not $text.Contains($oldStreamStart)) { throw 'v3.5 dynamic stream bounds target not found' }
$text = $text.Replace($oldStreamStart, $newStreamStart)

$oldReceiveSig = '    private async Task ReceiveInputAsync(NetworkStream stream, ClientDisplayProfile profile, CancellationToken token)'
$newReceiveSig = '    private async Task ReceiveInputAsync(NetworkStream stream, ClientDisplayProfile profile, DisplayRotationSession displayRotation, CancellationToken token)'
if (-not $text.Contains($oldReceiveSig)) { throw 'v3.5 receive signature target not found' }
$text = $text.Replace($oldReceiveSig, $newReceiveSig)

$oldReceiveStart = @'
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
        var packet = new byte[13];

        while (!token.IsCancellationRequested)
        {
            var type = await NetIo.ReadByteAsync(stream, token);
'@
$newReceiveStart = @'
    {
        var packet = new byte[13];

        while (!token.IsCancellationRequested)
        {
            var type = await NetIo.ReadByteAsync(stream, token);
            var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
'@
if (-not $text.Contains($oldReceiveStart)) { throw 'v3.5 dynamic input bounds target not found' }
$text = $text.Replace($oldReceiveStart, $newReceiveStart, 1)

$oldProfileUpdate = @'
                profile.Update(width, height, quality, fps);
                continue;
'@
$newProfileUpdate = @'
                profile.Update(width, height, quality, fps);
                displayRotation.ApplyForClient(width, height);
                continue;
'@
if (-not $text.Contains($oldProfileUpdate)) { throw 'v3.5 portrait profile target not found' }
$text = $text.Replace($oldProfileUpdate, $newProfileUpdate, 1)

$marker = 'internal sealed class ClientDisplayProfile'
$rotationClass = @'
internal sealed class DisplayRotationSession : IDisposable
{
    private readonly Action<string, bool> _status;
    private readonly object _sync = new();
    private string? _deviceName;
    private DisplayModeNative.DevMode _original;
    private bool _hasOriginal;
    private bool _changedByUs;
    private bool _disposed;

    public DisplayRotationSession(Action<string, bool> status)
    {
        _status = status;
    }

    public void ApplyForClient(int clientWidth, int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0) return;
        lock (_sync)
        {
            if (_disposed) return;
            var wantsPortrait = clientHeight > clientWidth;
            if (wantsPortrait)
                EnsurePortrait();
            else if (_changedByUs)
                RestoreCore(showStatus: true);
        }
    }

    private void EnsurePortrait()
    {
        var screen = Screen.PrimaryScreen;
        if (screen is null) return;

        var device = screen.DeviceName;
        var mode = DisplayModeNative.CreateMode();
        if (!DisplayModeNative.EnumDisplaySettings(device, DisplayModeNative.ENUM_CURRENT_SETTINGS, ref mode))
        {
            _status("Не удалось прочитать режим основного дисплея. Ориентация не изменена.", true);
            return;
        }

        // Already portrait: nothing to change and nothing to restore later.
        if (mode.dmPelsHeight > mode.dmPelsWidth || mode.dmDisplayOrientation == DisplayModeNative.DMDO_90 || mode.dmDisplayOrientation == DisplayModeNative.DMDO_270)
            return;

        if (!_hasOriginal)
        {
            _deviceName = device;
            _original = mode;
            _hasOriginal = true;
        }

        var rotated = mode;
        rotated.dmSize = (short)Marshal.SizeOf<DisplayModeNative.DevMode>();
        rotated.dmFields |= DisplayModeNative.DM_DISPLAYORIENTATION | DisplayModeNative.DM_PELSWIDTH | DisplayModeNative.DM_PELSHEIGHT;
        rotated.dmDisplayOrientation = DisplayModeNative.DMDO_90;

        // Windows requires width/height to be swapped when rotating by 90/270°.
        (rotated.dmPelsWidth, rotated.dmPelsHeight) = (rotated.dmPelsHeight, rotated.dmPelsWidth);

        var result = DisplayModeNative.ChangeDisplaySettingsEx(device, ref rotated, IntPtr.Zero, 0, IntPtr.Zero);
        if (result == DisplayModeNative.DISP_CHANGE_SUCCESSFUL)
        {
            _changedByUs = true;
            _status($"iPhone подключён вертикально • Windows: {rotated.dmPelsWidth}×{rotated.dmPelsHeight}, книжная ориентация", false);
            Thread.Sleep(250); // let WinForms/desktop bounds refresh before the next capture
        }
        else
        {
            _status($"Windows не разрешил книжную ориентацию (код {result}). Поток продолжит работать без смены режима.", true);
        }
    }

    private void RestoreCore(bool showStatus)
    {
        if (!_changedByUs || !_hasOriginal || string.IsNullOrWhiteSpace(_deviceName)) return;
        var original = _original;
        original.dmSize = (short)Marshal.SizeOf<DisplayModeNative.DevMode>();
        var result = DisplayModeNative.ChangeDisplaySettingsEx(_deviceName, ref original, IntPtr.Zero, 0, IntPtr.Zero);
        if (result == DisplayModeNative.DISP_CHANGE_SUCCESSFUL)
        {
            _changedByUs = false;
            if (showStatus)
                _status("Windows вернул исходную ориентацию дисплея.", false);
            Thread.Sleep(200);
        }
        else if (showStatus)
        {
            _status($"Не удалось вернуть исходную ориентацию Windows (код {result}).", true);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            RestoreCore(showStatus: false);
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

    public static DevMode CreateMode()
    {
        var mode = new DevMode
        {
            dmDeviceName = new string('\0', 32),
            dmFormName = new string('\0', 32),
            dmSize = (short)Marshal.SizeOf<DevMode>()
        };
        return mode;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DevMode lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);
}

'@
if (-not $text.Contains($marker)) { throw 'v3.5 rotation class marker not found' }
$text = $text.Replace($marker, $rotationClass + $marker, 1)

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v3.5 automatic portrait rotation patch applied.'
