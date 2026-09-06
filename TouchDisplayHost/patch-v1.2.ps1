$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$oldLan = 'var local = all.FirstOrDefault(a => !IsTailscale(a));'
$newLan = 'var local = all.FirstOrDefault(a => !IsTailscale(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));'
if (-not $text.Contains($oldLan)) { throw 'LAN patch target not found' }
$text = $text.Replace($oldLan, $newLan)
$text = $text.Replace('TouchDisplay Host v1', 'TouchDisplay Host v1.3')

$text = $text.Replace(
    'private CancellationTokenSource? _cts;',
    "private CancellationTokenSource? _cts;`r`n    private long _touchPackets;`r`n    private long _touchErrors;"
)

$text = $text.Replace(
    'private static async Task ReceiveInputAsync(NetworkStream stream, CancellationToken token)',
    'private async Task ReceiveInputAsync(NetworkStream stream, CancellationToken token)'
)

$oldInjectCall = '            TouchInjector.Inject(action, pointerId, nx, ny, bounds);'
$newInjectCall = @'
            var result = TouchInjector.Inject(action, pointerId, nx, ny, bounds);
            if (result.Success)
            {
                var count = Interlocked.Increment(ref _touchPackets);
                if (count == 1 || count % 60 == 0)
                    _status($"Touch OK: {count} событий", false);
            }
            else
            {
                var errors = Interlocked.Increment(ref _touchErrors);
                _status($"Touch ошибка {result.ErrorCode} (событий: {_touchPackets}, ошибок: {errors}). Соединение сохранено.", true);
            }
'@
if (-not $text.Contains($oldInjectCall)) { throw 'Touch call patch target not found' }
$text = $text.Replace($oldInjectCall, $newInjectCall.TrimEnd())

$oldConnected = '                _status($"Планшет подключён: {remote}", false);'
$newConnected = @'
                TouchInjector.Reset();
                _touchPackets = 0;
                _touchErrors = 0;
                _status($"Планшет подключён: {remote}. Коснитесь экрана для проверки Touch.", false);
'@
if (-not $text.Contains($oldConnected)) { throw 'Connected status patch target not found' }
$text = $text.Replace($oldConnected, $newConnected.TrimEnd())

$newInjector = @'
internal readonly record struct TouchInjectResult(bool Success, int ErrorCode)
{
    public static TouchInjectResult Ok => new(true, 0);
    public static TouchInjectResult Fail(int code) => new(false, code);
}

internal static class TouchInjector
{
    private sealed class ContactState
    {
        public uint NativeId { get; init; }
        public int X { get; set; }
        public int Y { get; set; }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<int, ContactState> Active = new();
    private static bool _initialized;

    public static bool Initialize()
    {
        if (_initialized) return true;
        _initialized = NativeMethods.InitializeTouchInjection(32, 3);
        return _initialized;
    }

    public static void Reset()
    {
        lock (Sync)
        {
            Active.Clear();
        }
    }

    public static TouchInjectResult Inject(byte action, int androidPointerId, float nx, float ny, Rectangle bounds)
    {
        if (!_initialized)
            return TouchInjectResult.Fail(10000);
        if (float.IsNaN(nx) || float.IsNaN(ny))
            return TouchInjectResult.Ok;

        nx = Math.Clamp(nx, 0f, 1f);
        ny = Math.Clamp(ny, 0f, 1f);
        var x = bounds.Left + (int)Math.Round(nx * Math.Max(1, bounds.Width - 1));
        var y = bounds.Top + (int)Math.Round(ny * Math.Max(1, bounds.Height - 1));

        // Keep the contact strictly inside the desktop. InjectTouchInput rejects
        // coordinates outside the desktop with ERROR_INVALID_PARAMETER.
        x = Math.Clamp(x, bounds.Left, bounds.Right - 1);
        y = Math.Clamp(y, bounds.Top, bounds.Bottom - 1);

        lock (Sync)
        {
            if (action == 0)
            {
                var nativeId = (uint)Math.Clamp(androidPointerId, 0, 255);
                var state = new ContactState { NativeId = nativeId, X = x, Y = y };
                Active[androidPointerId] = state;

                var frame = new List<PointerTouchInfo>(Active.Count);
                foreach (var pair in Active)
                {
                    var s = pair.Value;
                    var flags = pair.Key == androidPointerId
                        ? PointerFlags.Down | PointerFlags.InRange | PointerFlags.InContact
                        : PointerFlags.Update | PointerFlags.InRange | PointerFlags.InContact;
                    frame.Add(BuildInfo(s, flags));
                }
                return InjectFrame(frame);
            }

            if (!Active.TryGetValue(androidPointerId, out var current))
                return TouchInjectResult.Ok;

            if (action == 1)
            {
                current.X = x;
                current.Y = y;
                return InjectAllUpdates();
            }

            if (action == 2 || action == 3)
            {
                // UP must use the exact same location as the preceding UPDATE.
                // Android can report a slightly different final coordinate, so
                // first update the contact to the final point.
                current.X = x;
                current.Y = y;
                var updateResult = InjectAllUpdates();
                if (!updateResult.Success)
                    return updateResult;

                var frame = new List<PointerTouchInfo>(Active.Count);
                foreach (var pair in Active)
                {
                    var s = pair.Value;
                    var flags = pair.Key == androidPointerId
                        ? PointerFlags.Up | (action == 3 ? PointerFlags.Canceled : PointerFlags.None)
                        : PointerFlags.Update | PointerFlags.InRange | PointerFlags.InContact;
                    frame.Add(BuildInfo(s, flags));
                }

                var upResult = InjectFrame(frame);
                if (upResult.Success)
                    Active.Remove(androidPointerId);
                return upResult;
            }

            return TouchInjectResult.Ok;
        }
    }

    private static TouchInjectResult InjectAllUpdates()
    {
        if (Active.Count == 0) return TouchInjectResult.Ok;
        var frame = Active.Values
            .Select(s => BuildInfo(s, PointerFlags.Update | PointerFlags.InRange | PointerFlags.InContact))
            .ToList();
        return InjectFrame(frame);
    }

    private static TouchInjectResult InjectFrame(List<PointerTouchInfo> contacts)
    {
        if (contacts.Count == 0) return TouchInjectResult.Ok;

        // Windows may return ERROR_NOT_READY when two injection calls are closer
        // than 0.1 ms. Retry the identical frame instead of tearing down the session.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.InjectTouchInput((uint)contacts.Count, contacts.ToArray()))
                return TouchInjectResult.Ok;

            var error = Marshal.GetLastWin32Error();
            if (error != 21) // ERROR_NOT_READY
                return TouchInjectResult.Fail(error);
            Thread.Sleep(1);
        }

        return TouchInjectResult.Fail(21);
    }

    private static PointerTouchInfo BuildInfo(ContactState state, PointerFlags flags)
    {
        // Chromium Remote Desktop only supplies contact area + orientation unless
        // real pressure is available. This avoids optional-field validation issues.
        var contact = new Rect
        {
            Left = state.X - 2,
            Top = state.Y - 2,
            Right = state.X + 2,
            Bottom = state.Y + 2
        };

        return new PointerTouchInfo
        {
            PointerInfo = new PointerInfo
            {
                PointerType = PointerInputType.Touch,
                PointerId = state.NativeId,
                PointerFlags = flags,
                PtPixelLocation = new PointNative { X = state.X, Y = state.Y }
            },
            TouchFlags = 0,
            TouchMask = TouchMask.ContactArea | TouchMask.Orientation,
            RcContact = contact,
            RcContactRaw = new Rect(),
            Orientation = 90,
            Pressure = 0
        };
    }
}

'@

$pattern = '(?s)internal static class TouchInjector\s*\{.*?(?=internal static class NativeMethods)'
if (-not [regex]::IsMatch($text, $pattern)) { throw 'TouchInjector patch target not found' }
$text = [regex]::Replace($text, $pattern, $newInjector, 1)

# Make array marshalling explicit for the native API.
$oldPInvoke = 'public static extern bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);'
$newPInvoke = 'public static extern bool InjectTouchInput(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0), In] PointerTouchInfo[] contacts);'
if ($text.Contains($oldPInvoke))
{
    $text = $text.Replace($oldPInvoke, $newPInvoke)
}

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v1.3 patch applied.'
