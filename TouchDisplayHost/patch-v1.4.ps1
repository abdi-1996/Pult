$ErrorActionPreference = 'Stop'

# Apply the stable v1.3 touch-injection fixes first.
& (Join-Path $PSScriptRoot 'patch-v1.2.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v1.3', 'TouchDisplay Host v1.4')
$text = $text.Replace('PIN для планшета:', 'Пароль:')
$text = $text.Replace('Отклонён неверный PIN:', 'Неверный пароль:')

# Keep one stable password between Host restarts so Android only asks for one thing.
$oldPassword = 'private readonly string _pinCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");'
$newPassword = 'private readonly string _pinCode = LoadOrCreatePassword();'
if (-not $text.Contains($oldPassword)) { throw 'Password field patch target not found' }
$text = $text.Replace($oldPassword, $newPassword)

$constructorMarker = '    public MainForm()'
$passwordMethod = @'
    private static string LoadOrCreatePassword()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TouchDisplay");
            Directory.CreateDirectory(dir);
            var passwordPath = Path.Combine(dir, "password.txt");

            if (File.Exists(passwordPath))
            {
                var existing = File.ReadAllText(passwordPath).Trim();
                if (existing.Length >= 4 && existing.Length <= 32)
                    return existing;
            }

            var created = RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
            File.WriteAllText(passwordPath, created);
            return created;
        }
        catch
        {
            return RandomNumberGenerator.GetInt32(100000, 1000000).ToString("D6");
        }
    }

'@
if (-not $text.Contains($constructorMarker)) { throw 'MainForm constructor marker not found' }
$text = $text.Replace($constructorMarker, $passwordMethod + $constructorMarker)

# Discovery service: Android broadcasts one packet and receives the PC address + Tailscale address.
$listenerField = '    private TcpListener? _listener;'
$discoveryField = "    private TcpListener? _listener;`r`n    private UdpClient? _discovery;"
if (-not $text.Contains($listenerField)) { throw 'Listener field marker not found' }
$text = $text.Replace($listenerField, $discoveryField)

$startMarker = '        _ = AcceptLoopAsync(_cts.Token);'
$startReplacement = "        _ = AcceptLoopAsync(_cts.Token);`r`n        StartDiscovery(_cts.Token);"
if (-not $text.Contains($startMarker)) { throw 'Start discovery marker not found' }
$text = $text.Replace($startMarker, $startReplacement)

$stopMarker = '        try { _listener?.Stop(); } catch { }'
$stopReplacement = "        try { _listener?.Stop(); } catch { }`r`n        try { _discovery?.Dispose(); } catch { }`r`n        _discovery = null;"
if (-not $text.Contains($stopMarker)) { throw 'Stop discovery marker not found' }
$text = $text.Replace($stopMarker, $stopReplacement)

$acceptMarker = '    private async Task AcceptLoopAsync(CancellationToken token)'
$discoveryMethods = @'
    private void StartDiscovery(CancellationToken token)
    {
        try
        {
            _discovery = new UdpClient(new IPEndPoint(IPAddress.Any, 59431));
            _discovery.EnableBroadcast = true;
            _ = DiscoveryLoopAsync(token);
        }
        catch (Exception ex)
        {
            _status("Автопоиск недоступен: " + ex.Message, true);
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _discovery is not null)
        {
            try
            {
                var result = await _discovery.ReceiveAsync(token);
                var request = Encoding.ASCII.GetString(result.Buffer).Trim();
                if (!string.Equals(request, "TDISCOVER14", StringComparison.Ordinal))
                    continue;

                var tailscale = FindTailscaleAddress() ?? string.Empty;
                var response = Encoding.UTF8.GetBytes(
                    $"TDHOST14|{_port}|{tailscale}|{Environment.MachineName}");
                await _discovery.SendAsync(response, response.Length, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException)
            {
                if (token.IsCancellationRequested) break;
            }
            catch
            {
                if (token.IsCancellationRequested) break;
            }
        }
    }

    private static string? FindTailscaleAddress()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .FirstOrDefault(a =>
            {
                var b = a.GetAddressBytes();
                return b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
            })
            ?.ToString();
    }

'@
if (-not $text.Contains($acceptMarker)) { throw 'Discovery method insertion marker not found' }
$text = $text.Replace($acceptMarker, $discoveryMethods + $acceptMarker)

# Better image quality while keeping the stream responsive.
$text = $text.Replace('const int maxWidth = 1600;', 'const int maxWidth = 1920;')
$text = $text.Replace('const int frameDelayMs = 50;', 'const int frameDelayMs = 33;')
$text = $text.Replace('const long jpegQuality = 58L;', 'const long jpegQuality = 72L;')
$text = $text.Replace('new MemoryStream(512 * 1024)', 'new MemoryStream(1024 * 1024)')
$text = $text.Replace('client.SendBufferSize = 1024 * 1024;', 'client.SendBufferSize = 512 * 1024;')

# Friendlier host status for the new single-password flow.
$text = $text.Replace(
    'SetStatus($"Готов. Откройте TouchDisplay на планшете. Порт {port}.");',
    'SetStatus($"Готов. На планшете введите только пароль. Автопоиск включён.");')

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v1.4 patch applied.'
