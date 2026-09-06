$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v1.4.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v1.4', 'TouchDisplay Host v1.5')

# Increase stream quality while keeping stale-frame dropping on Android.
$text = $text.Replace('const int maxWidth = 1920;', 'const int maxWidth = 2560;')
$text = $text.Replace('const int frameDelayMs = 33;', 'const int frameDelayMs = 25;')
$text = $text.Replace('const long jpegQuality = 72L;', 'const long jpegQuality = 82L;')
$text = $text.Replace('new MemoryStream(1024 * 1024)', 'new MemoryStream(2 * 1024 * 1024)')
$text = $text.Replace('client.SendBufferSize = 512 * 1024;', 'client.SendBufferSize = 1024 * 1024;')

# Add right-click packet support (0x11 + normalized X/Y floats).
$oldReceive = @'
            var type = await NetIo.ReadByteAsync(stream, token);
            if (type != 0x10) throw new IOException("Неизвестный пакет");
            await NetIo.ReadExactAsync(stream, packet, token);

            var action = packet[0];
            var pointerId = NetIo.Int32FromBigEndian(packet.AsSpan(1, 4));
            var nx = NetIo.SingleFromBigEndian(packet.AsSpan(5, 4));
            var ny = NetIo.SingleFromBigEndian(packet.AsSpan(9, 4));
            var result = TouchInjector.Inject(action, pointerId, nx, ny, bounds);
'@
$newReceive = @'
            var type = await NetIo.ReadByteAsync(stream, token);
            if (type == 0x11)
            {
                var mousePacket = new byte[8];
                await NetIo.ReadExactAsync(stream, mousePacket, token);
                var mouseX = NetIo.SingleFromBigEndian(mousePacket.AsSpan(0, 4));
                var mouseY = NetIo.SingleFromBigEndian(mousePacket.AsSpan(4, 4));
                MouseInjector.RightClick(mouseX, mouseY, bounds);
                continue;
            }

            if (type != 0x10) throw new IOException("Неизвестный пакет");
            await NetIo.ReadExactAsync(stream, packet, token);

            var action = packet[0];
            var pointerId = NetIo.Int32FromBigEndian(packet.AsSpan(1, 4));
            var nx = NetIo.SingleFromBigEndian(packet.AsSpan(5, 4));
            var ny = NetIo.SingleFromBigEndian(packet.AsSpan(9, 4));
            var result = TouchInjector.Inject(action, pointerId, nx, ny, bounds);
'@
if (-not $text.Contains($oldReceive)) { throw 'ReceiveInput right-click patch target not found' }
$text = $text.Replace($oldReceive, $newReceive)

$nativeMarker = 'internal static class NativeMethods'
$mouseClass = @'
internal static class MouseInjector
{
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    public static void RightClick(float nx, float ny, Rectangle bounds)
    {
        if (float.IsNaN(nx) || float.IsNaN(ny)) return;
        nx = Math.Clamp(nx, 0f, 1f);
        ny = Math.Clamp(ny, 0f, 1f);
        var x = bounds.Left + (int)Math.Round(nx * Math.Max(1, bounds.Width - 1));
        var y = bounds.Top + (int)Math.Round(ny * Math.Max(1, bounds.Height - 1));
        NativeMethods.SetCursorPos(x, y);
        NativeMethods.mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
        NativeMethods.mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
    }
}

'@
if (-not $text.Contains($nativeMarker)) { throw 'Native marker not found' }
$text = $text.Replace($nativeMarker, $mouseClass + $nativeMarker)

$setDpi = @'
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
'@
$setDpiPlus = @'
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
'@
if (-not $text.Contains($setDpi)) { throw 'Native PInvoke patch target not found' }
$text = $text.Replace($setDpi, $setDpiPlus)

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v1.5 patch applied.'
