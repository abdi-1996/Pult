$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$oldLan = 'var local = all.FirstOrDefault(a => !IsTailscale(a));'
$newLan = 'var local = all.FirstOrDefault(a => !IsTailscale(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal));'
if (-not $text.Contains($oldLan)) { throw 'LAN patch target not found' }
$text = $text.Replace($oldLan, $newLan)
$text = $text.Replace('TouchDisplay Host v1', 'TouchDisplay Host v1.2')

$text = $text.Replace(
    'private CancellationTokenSource? _cts;',
    "private CancellationTokenSource? _cts;`r`n    private long _touchPackets;"
)

$text = $text.Replace(
    'private static async Task ReceiveInputAsync(NetworkStream stream, CancellationToken token)',
    'private async Task ReceiveInputAsync(NetworkStream stream, CancellationToken token)'
)

$oldInjectCall = '            TouchInjector.Inject(action, pointerId, nx, ny, bounds);'
$newInjectCall = @'
            try
            {
                TouchInjector.Inject(action, pointerId, nx, ny, bounds);
                var count = Interlocked.Increment(ref _touchPackets);
                if (count == 1 || count % 60 == 0)
                    _status($"Touch OK: {count} событий", false);
            }
            catch (Win32Exception ex)
            {
                _status($"Touch API ошибка {ex.NativeErrorCode}: {ex.Message}", true);
                throw;
            }
'@
if (-not $text.Contains($oldInjectCall)) { throw 'Touch call patch target not found' }
$text = $text.Replace($oldInjectCall, $newInjectCall.TrimEnd())

$oldConnected = '                _status($"Планшет подключён: {remote}", false);'
$newConnected = @'
                TouchInjector.Reset();
                _touchPackets = 0;
                _status($"Планшет подключён: {remote}. Коснитесь экрана для проверки Touch.", false);
'@
if (-not $text.Contains($oldConnected)) { throw 'Connected status patch target not found' }
$text = $text.Replace($oldConnected, $newConnected.TrimEnd())

$newInjector = @'
internal static class TouchInjector
{
    private sealed class ContactState
    {
        public uint NativeId { get; init; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool IsNew { get; set; }
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<int, ContactState> Active = new();
    private static bool _initialized;
    private static uint _primaryNativeId;

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
            if (_initialized && Active.Count > 0)
            {
                try
                {
                    var cancel = Active.Values
                        .Select(s => BuildInfo(s, PointerFlags.Up | PointerFlags.Canceled, s.NativeId == _primaryNativeId))
                        .ToArray();
                    NativeMethods.InjectTouchInput((uint)cancel.Length, cancel);
                }
                catch { }
            }
            Active.Clear();
            _primaryNativeId = 0;
        }
    }

    public static void Inject(byte action, int androidPointerId, float nx, float ny, Rectangle bounds)
    {
        if (!_initialized) throw new InvalidOperationException("Touch injection is not initialized");
        if (float.IsNaN(nx) || float.IsNaN(ny)) return;

        nx = Math.Clamp(nx, 0f, 1f);
        ny = Math.Clamp(ny, 0f, 1f);
        var x = bounds.Left + (int)Math.Round(nx * Math.Max(1, bounds.Width - 1));
        var y = bounds.Top + (int)Math.Round(ny * Math.Max(1, bounds.Height - 1));

        lock (Sync)
        {
            if (action == 0)
            {
                var nativeId = (uint)Math.Clamp(androidPointerId + 1, 1, 255);
                var state = new ContactState { NativeId = nativeId, X = x, Y = y, IsNew = true };
                Active[androidPointerId] = state;
                if (_primaryNativeId == 0) _primaryNativeId = nativeId;
                InjectCurrentFrame(null, false);
                foreach (var item in Active.Values) item.IsNew = false;
                return;
            }

            if (!Active.TryGetValue(androidPointerId, out var current))
                return;

            if (action == 1)
            {
                current.X = x;
                current.Y = y;
                InjectCurrentFrame(null, false);
                return;
            }

            if (action == 2 || action == 3)
            {
                // Windows requires UP at the same point as the previous UPDATE.
                InjectCurrentFrame(null, false);
                InjectCurrentFrame(androidPointerId, action == 3);
                var removedNativeId = current.NativeId;
                Active.Remove(androidPointerId);
                if (removedNativeId == _primaryNativeId)
                    _primaryNativeId = Active.Values.FirstOrDefault()?.NativeId ?? 0;
            }
        }
    }

    private static void InjectCurrentFrame(int? endingAndroidId, bool canceled)
    {
        if (Active.Count == 0) return;
        var contacts = new List<PointerTouchInfo>(Active.Count);

        foreach (var pair in Active)
        {
            var state = pair.Value;
            PointerFlags flags;
            if (endingAndroidId.HasValue && pair.Key == endingAndroidId.Value)
                flags = PointerFlags.Up | (canceled ? PointerFlags.Canceled : PointerFlags.None);
            else if (state.IsNew)
                flags = PointerFlags.Down | PointerFlags.InRange | PointerFlags.InContact;
            else
                flags = PointerFlags.Update | PointerFlags.InRange | PointerFlags.InContact;

            contacts.Add(BuildInfo(state, flags, state.NativeId == _primaryNativeId));
        }

        if (!NativeMethods.InjectTouchInput((uint)contacts.Count, contacts.ToArray()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InjectTouchInput failed");
    }

    private static PointerTouchInfo BuildInfo(ContactState state, PointerFlags flags, bool primary)
    {
        if (primary) flags |= PointerFlags.Primary;
        flags |= PointerFlags.Confidence;

        var contact = new Rect
        {
            Left = state.X - 3,
            Top = state.Y - 3,
            Right = state.X + 3,
            Bottom = state.Y + 3
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
            TouchMask = TouchMask.ContactArea | TouchMask.Orientation | TouchMask.Pressure,
            RcContact = contact,
            RcContactRaw = contact,
            Orientation = 90,
            Pressure = 512
        };
    }
}

'@

$pattern = '(?s)internal static class TouchInjector\s*\{.*?(?=internal static class NativeMethods)'
if (-not [regex]::IsMatch($text, $pattern)) { throw 'TouchInjector patch target not found' }
$text = [regex]::Replace($text, $pattern, $newInjector, 1)

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v1.2 patch applied.'
