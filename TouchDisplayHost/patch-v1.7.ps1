$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v1.6.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v1.6', 'TouchDisplay Host v1.7')

# Password-aware discovery. Only the Host whose 6-digit password matches will reply.
$oldDiscovery = @'
                var result = await _discovery.ReceiveAsync(token);
                var request = Encoding.ASCII.GetString(result.Buffer).Trim();
                if (!string.Equals(request, "TDISCOVER14", StringComparison.Ordinal))
                    continue;

                var tailscale = FindTailscaleAddress() ?? string.Empty;
                var response = Encoding.UTF8.GetBytes(
                    $"TDHOST14|{_port}|{tailscale}|{Environment.MachineName}");
                await _discovery.SendAsync(response, response.Length, result.RemoteEndPoint);
'@

$newDiscovery = @'
                var result = await _discovery.ReceiveAsync(token);
                var request = Encoding.UTF8.GetString(result.Buffer).Trim();
                var parts = request.Split('|', 2);
                if (parts.Length != 2 || !string.Equals(parts[0], "TDISCOVER17", StringComparison.Ordinal))
                    continue;

                var discoveryPassword = NormalizePassword(parts[1]);
                if (!string.Equals(discoveryPassword, NormalizePassword(_pin), StringComparison.Ordinal))
                    continue;

                var tailscale = FindTailscaleAddress() ?? string.Empty;
                var response = Encoding.UTF8.GetBytes(
                    $"TDHOST17|{_port}|{tailscale}|{Environment.MachineName}");
                await _discovery.SendAsync(response, response.Length, result.RemoteEndPoint);
'@
if (-not $text.Contains($oldDiscovery)) { throw 'v1.7 discovery patch target not found' }
$text = $text.Replace($oldDiscovery, $newDiscovery)

# Normalize any localized numeric characters to ASCII before comparing passwords.
$findTailMarker = '    private static string? FindTailscaleAddress()'
$normalizeMethod = @'
    private static string NormalizePassword(string value)
    {
        var sb = new StringBuilder();
        foreach (var ch in value.Trim())
        {
            if (!char.IsDigit(ch))
                continue;
            var numeric = char.GetNumericValue(ch);
            if (numeric >= 0 && numeric <= 9 && Math.Floor(numeric) == numeric)
                sb.Append((char)('0' + (int)numeric));
        }
        return sb.ToString();
    }

'@
if (-not $text.Contains($findTailMarker)) { throw 'v1.7 normalize insertion target not found' }
$text = $text.Replace($findTailMarker, $normalizeMethod + $findTailMarker)

# Use the same normalization for the TCP authentication itself.
$oldAuth = @'
                var suppliedPin = Encoding.UTF8.GetString(pinBytes);
                var ok = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(suppliedPin),
                    Encoding.UTF8.GetBytes(_pin));
'@
$newAuth = @'
                var suppliedPin = Encoding.UTF8.GetString(pinBytes);
                var ok = string.Equals(
                    NormalizePassword(suppliedPin),
                    NormalizePassword(_pin),
                    StringComparison.Ordinal);
'@
if (-not $text.Contains($oldAuth)) { throw 'v1.7 auth patch target not found' }
$text = $text.Replace($oldAuth, $newAuth)

$text = $text.Replace(
    'SetStatus($"Готов. На планшете введите только пароль. Автопоиск включён.");',
    'SetStatus($"Готов v1.7. Введите на планшете 6-значный пароль. ПК определяется по паролю.");')

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v1.7 password-aware discovery patch applied.'
