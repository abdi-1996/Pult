using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace TouchDisplayMobileWeb;

internal static class NetworkInfo
{
    public static string BuildAddressText(int port)
    {
        var all = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct()
            .ToList();

        var tail = all.FirstOrDefault(IsTailscale);
        var lan = all.FirstOrDefault(a => !IsTailscale(a) && !a.ToString().StartsWith("169.254."));
        var lines = new List<string>();
        if (lan is not null) lines.Add($"LAN: http://{lan}:{port}");
        if (tail is not null) lines.Add($"Tailscale: http://{tail}:{port}");
        if (lines.Count == 0) lines.Add($"Local: http://127.0.0.1:{port}");
        return string.Join(Environment.NewLine, lines);
    }

    public static string? FindTailscale()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsTailscale(a))
            ?.ToString();
    }

    public static string? FindLan()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork &&
                                 !IPAddress.IsLoopback(a) &&
                                 !IsTailscale(a) &&
                                 !a.ToString().StartsWith("169.254."))
            ?.ToString();
    }

    public static bool IsAllowedRemote(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        if (b[0] == 10) return true;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        if (b[0] == 192 && b[1] == 168) return true;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
        return false;
    }

    public static bool IsTailscale(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }
}

internal static class MouseController
{
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint Wheel = 0x0800;

    public static void MoveRelative(float dx, float dy, Rectangle bounds)
    {
        if (!float.IsFinite(dx) || !float.IsFinite(dy)) return;
        dx = Math.Clamp(dx, -1f, 1f);
        dy = Math.Clamp(dy, -1f, 1f);
        var cur = Cursor.Position;
        var mag = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var gain = mag > 0.035f ? 1.6f : mag > 0.012f ? 1.3f : 1.05f;
        var x = Math.Clamp(cur.X + (int)Math.Round(dx * bounds.Width * gain), bounds.Left, bounds.Right - 1);
        var y = Math.Clamp(cur.Y + (int)Math.Round(dy * bounds.Height * gain), bounds.Top, bounds.Bottom - 1);
        NativeMethods.SetCursorPos(x, y);
    }

    public static void Button(int button, int action)
    {
        var down = button == 1 ? RightDown : LeftDown;
        var up = button == 1 ? RightUp : LeftUp;
        if (action == 0) NativeMethods.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        else if (action == 1) NativeMethods.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        else
        {
            NativeMethods.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            NativeMethods.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        }
    }

    public static void WheelDelta(int delta)
    {
        delta = Math.Clamp(delta, -960, 960);
        if (delta != 0) NativeMethods.mouse_event(Wheel, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }
}

internal static class KeyboardController
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;

    public static void TypeText(string value)
    {
        foreach (var ch in (value ?? string.Empty).Take(4096))
        {
            SendUnicode(ch, false);
            SendUnicode(ch, true);
        }
    }

    public static void Press(string key)
    {
        ushort vk = key switch
        {
            "Enter" => 0x0D,
            "Backspace" => 0x08,
            "Escape" => 0x1B,
            "Tab" => 0x09,
            "Delete" => 0x2E,
            "ArrowLeft" => 0x25,
            "ArrowUp" => 0x26,
            "ArrowRight" => 0x27,
            "ArrowDown" => 0x28,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            _ => 0
        };
        if (vk == 0) return;
        SendVirtual(vk, false);
        SendVirtual(vk, true);
    }

    private static void SendUnicode(char ch, bool up) =>
        Send(new KEYBDINPUT { wScan = ch, dwFlags = Unicode | (up ? KeyUp : 0) });

    private static void SendVirtual(ushort vk, bool up) =>
        Send(new KEYBDINPUT { wVk = vk, dwFlags = up ? KeyUp : 0 });

    private static void Send(KEYBDINPUT ki)
    {
        var input = new INPUT { type = InputKeyboard, U = new InputUnion { ki = ki } };
        _ = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
}

internal static class TouchController
{
    private sealed class Contact
    {
        public uint NativeId;
        public int X;
        public int Y;
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<int, Contact> Active = new();
    private static bool _ready;

    public static void EnsureInitialized()
    {
        if (_ready) return;
        _ready = NativeMethods.InitializeTouchInjection(32, 3);
    }

    public static void Reset()
    {
        lock (Sync) Active.Clear();
    }

    public static void Inject(string action, int pointerId, float nx, float ny, Rectangle bounds)
    {
        EnsureInitialized();
        if (!_ready || !float.IsFinite(nx) || !float.IsFinite(ny)) return;

        nx = Math.Clamp(nx, 0, 1);
        ny = Math.Clamp(ny, 0, 1);
        var x = Math.Clamp(bounds.Left + (int)Math.Round(nx * Math.Max(1, bounds.Width - 1)), bounds.Left, bounds.Right - 1);
        var y = Math.Clamp(bounds.Top + (int)Math.Round(ny * Math.Max(1, bounds.Height - 1)), bounds.Top, bounds.Bottom - 1);

        lock (Sync)
        {
            if (action == "down")
            {
                Active[pointerId] = new Contact { NativeId = (uint)Math.Clamp(pointerId, 0, 255), X = x, Y = y };
                InjectFrame(pointerId, PointerFlags.Down | PointerFlags.InRange | PointerFlags.InContact);
                return;
            }

            if (!Active.TryGetValue(pointerId, out var c)) return;
            c.X = x; c.Y = y;

            if (action == "move")
            {
                InjectAllUpdates();
                return;
            }

            InjectAllUpdates();
            InjectFrame(pointerId, PointerFlags.Up | (action == "cancel" ? PointerFlags.Canceled : PointerFlags.None));
            Active.Remove(pointerId);
        }
    }

    private static void InjectFrame(int changedId, PointerFlags changedFlags)
    {
        var frame = Active.Select(kv =>
            Build(kv.Value, kv.Key == changedId ? changedFlags : PointerFlags.Update | PointerFlags.InRange | PointerFlags.InContact))
            .ToArray();
        Inject(frame);
    }

    private static void InjectAllUpdates()
    {
        if (Active.Count == 0) return;
        Inject(Active.Values.Select(c => Build(c, PointerFlags.Update | PointerFlags.InRange | PointerFlags.InContact)).ToArray());
    }

    private static void Inject(POINTER_TOUCH_INFO[] frame)
    {
        if (frame.Length == 0) return;
        for (var i = 0; i < 3; i++)
        {
            if (NativeMethods.InjectTouchInput((uint)frame.Length, frame)) return;
            Thread.Sleep(1);
        }
    }

    private static POINTER_TOUCH_INFO Build(Contact c, PointerFlags flags)
    {
        var rect = new RECT { left = c.X - 2, top = c.Y - 2, right = c.X + 2, bottom = c.Y + 2 };
        return new POINTER_TOUCH_INFO
        {
            pointerInfo = new POINTER_INFO
            {
                pointerType = 2,
                pointerId = c.NativeId,
                pointerFlags = flags,
                ptPixelLocation = new POINT { X = c.X, Y = c.Y }
            },
            touchMask = 0x0001 | 0x0002,
            rcContact = rect,
            orientation = 90
        };
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] internal static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint nInputs, [In] KeyboardController.INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool InjectTouchInput(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0), In] POINTER_TOUCH_INFO[] contacts);
}

[Flags]
internal enum PointerFlags : uint
{
    None = 0, InRange = 0x2, InContact = 0x4, Primary = 0x2000,
    Canceled = 0x8000, Down = 0x10000, Update = 0x20000, Up = 0x40000
}

[StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X; public int Y; }
[StructLayout(LayoutKind.Sequential)] internal struct RECT { public int left, top, right, bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_INFO
{
    public uint pointerType, pointerId, frameId;
    public PointerFlags pointerFlags;
    public IntPtr sourceDevice, hwndTarget;
    public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
    public uint dwTime, historyCount;
    public int inputData;
    public uint dwKeyStates;
    public ulong performanceCount;
    public uint buttonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTER_TOUCH_INFO
{
    public POINTER_INFO pointerInfo;
    public uint touchFlags, touchMask;
    public RECT rcContact, rcContactRaw;
    public uint orientation, pressure;
}
