$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v4.1.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v4.1', 'TouchDisplay Host v4.2')
$text = $text.Replace(
    'Готов v4.1 • UDP + FEC • Smart Bitrate • AI Priority • 10ms Audio • Tailscale',
    'Готов v4.2 • Clean UI • Mouse Mode • UDP + FEC • AI Priority • Tailscale')

$oldType = @'
            var type = await NetIo.ReadByteAsync(stream, token);
            var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;
            if (type == 0x21)
'@
$newType = @'
            var type = await NetIo.ReadByteAsync(stream, token);
            var bounds = Screen.PrimaryScreen?.Bounds ?? SystemInformation.VirtualScreen;

            // v4.2 mouse mode. Input stays on the authenticated low-latency TCP
            // control channel, completely separate from video.
            if (type == 0x30)
            {
                var movePacket = new byte[8];
                await NetIo.ReadExactAsync(stream, movePacket, token);
                var dx = NetIo.SingleFromBigEndian(movePacket.AsSpan(0, 4));
                var dy = NetIo.SingleFromBigEndian(movePacket.AsSpan(4, 4));
                profile.MarkInteraction();
                MouseInjector.MoveRelative(dx, dy, bounds);
                continue;
            }

            if (type == 0x31)
            {
                var buttonPacket = new byte[2];
                await NetIo.ReadExactAsync(stream, buttonPacket, token);
                profile.MarkInteraction();
                MouseInjector.Button(buttonPacket[0], buttonPacket[1]);
                continue;
            }

            if (type == 0x32)
            {
                var wheelPacket = new byte[4];
                await NetIo.ReadExactAsync(stream, wheelPacket, token);
                var delta = NetIo.Int32FromBigEndian(wheelPacket);
                profile.MarkInteraction();
                MouseInjector.Wheel(delta);
                continue;
            }

            if (type == 0x21)
'@
if (-not $text.Contains($oldType)) { throw 'v4.2 input protocol target not found' }
$text = $text.Replace($oldType, $newType, 1)

$newMouse = @'
internal static class MouseInjector
{
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

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

    public static void MoveRelative(float dx, float dy, Rectangle bounds)
    {
        if (float.IsNaN(dx) || float.IsNaN(dy) || float.IsInfinity(dx) || float.IsInfinity(dy)) return;
        dx = Math.Clamp(dx, -1f, 1f);
        dy = Math.Clamp(dy, -1f, 1f);

        var current = Cursor.Position;

        // dx/dy are normalized to the iPhone viewport. A small acceleration gives
        // a laptop-trackpad feel while still allowing pixel-precise slow movement.
        var magnitude = Math.Max(Math.Abs(dx), Math.Abs(dy));
        var gain = magnitude > 0.035f ? 1.55 : magnitude > 0.012f ? 1.30 : 1.05;
        var x = current.X + (int)Math.Round(dx * bounds.Width * gain);
        var y = current.Y + (int)Math.Round(dy * bounds.Height * gain);
        x = Math.Clamp(x, bounds.Left, bounds.Right - 1);
        y = Math.Clamp(y, bounds.Top, bounds.Bottom - 1);
        NativeMethods.SetCursorPos(x, y);
    }

    // button: 0 left, 1 right. action: 0 down, 1 up, 2 click.
    public static void Button(byte button, byte action)
    {
        var down = button == 1 ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
        var up = button == 1 ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;

        if (action == 0)
            NativeMethods.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        else if (action == 1)
            NativeMethods.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        else
        {
            NativeMethods.mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            NativeMethods.mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        }
    }

    public static void Wheel(int delta)
    {
        delta = Math.Clamp(delta, -960, 960);
        if (delta == 0) return;
        NativeMethods.mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }
}

'@

$pattern = '(?s)internal static class MouseInjector\s*\{.*?(?=internal static class NativeMethods)'
if (-not [regex]::IsMatch($text, $pattern)) { throw 'v4.2 MouseInjector target not found' }
$text = [regex]::Replace($text, $pattern, $newMouse, 1)

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v4.2 Clean UI + Mouse Mode protocol patch applied.'
