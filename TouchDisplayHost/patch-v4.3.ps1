$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v4.2.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v4.2', 'TouchDisplay Host v4.3 Web')
$text = $text.Replace(
    'Готов v4.2 • Clean UI • Mouse Mode • UDP + FEC • AI Priority • Tailscale',
    'Готов v4.3 • Native + Web Remote • Mouse / Touch • AI Priority • Tailscale')

$field = '    private TouchServer? _server;'
$fieldNew = @'
    private TouchServer? _server;
    private WebRemoteServer? _webServer;
    private readonly Button _openWeb = new();
'@
if (-not $text.Contains($field)) { throw 'v4.3 MainForm field target not found' }
$text = $text.Replace($field, $fieldNew.TrimEnd(), 1)

$text = $text.Replace('ClientSize = new Size(600, 390);', 'ClientSize = new Size(600, 450);')
$text = $text.Replace('MinimumSize = new Size(600, 390);', 'MinimumSize = new Size(600, 450);')

$statusMarker = @'
        _status.Text = "Запуск…";
        _status.Location = new Point(28, 315);
        _status.Size = new Size(524, 42);
'@
$statusReplacement = @'
        _openWeb.Text = "ОТКРЫТЬ WEB НА ЭТОМ ПК";
        _openWeb.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _openWeb.Location = new Point(28, 304);
        _openWeb.Size = new Size(524, 40);
        _openWeb.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "http://127.0.0.1:59435",
                    UseShellExecute = true
                });
            }
            catch { }
        };
        Controls.Add(_openWeb);

        _status.Text = "Запуск…";
        _status.Location = new Point(28, 360);
        _status.Size = new Size(524, 58);
'@
if (-not $text.Contains($statusMarker)) { throw 'v4.3 status UI target not found' }
$text = $text.Replace($statusMarker, $statusReplacement, 1)

$closing = '        FormClosing += (_, _) => _server?.Stop();'
$closingNew = '        FormClosing += (_, _) => { _webServer?.Stop(); _server?.Stop(); };'
if (-not $text.Contains($closing)) { throw 'v4.3 close target not found' }
$text = $text.Replace($closing, $closingNew, 1)

$serverStart = '            _server.Start();'
$serverStartNew = @'
            _server.Start();
            try
            {
                _webServer = new WebRemoteServer(WebRemoteServer.DefaultPort, _pinCode, SetStatus);
                _webServer.Start();
            }
            catch (Exception webEx)
            {
                _webServer = null;
                SetStatus("Native готов, Web недоступен: " + webEx.Message, true);
            }
'@
if (-not $text.Contains($serverStart)) { throw 'v4.3 server start target not found' }
$text = $text.Replace($serverStart, $serverStartNew, 1)

$stop = @'
        _server?.Stop();
        _server = null;
'@
$stopNew = @'
        _webServer?.Stop();
        _webServer = null;
        _server?.Stop();
        _server = null;
'@
if (-not $text.Contains($stop)) { throw 'v4.3 stop target not found' }
$text = $text.Replace($stop, $stopNew, 1)

$lanLine = '        if (local is not null) lines.Add("LAN: " + local);'
$lanNew = @'
        if (local is not null)
        {
            lines.Add("LAN: " + local);
            lines.Add($"Web LAN: http://{local}:{WebRemoteServer.DefaultPort}");
        }
'@
if (-not $text.Contains($lanLine)) { throw 'v4.3 LAN address target not found' }
$text = $text.Replace($lanLine, $lanNew, 1)

$tailLine = '        if (tail is not null) lines.Add("Tailscale: " + tail);'
$tailNew = @'
        if (tail is not null)
        {
            lines.Add("Tailscale: " + tail);
            lines.Add($"Web Tailscale: http://{tail}:{WebRemoteServer.DefaultPort}");
        }
'@
if (-not $text.Contains($tailLine)) { throw 'v4.3 Tailscale address target not found' }
$text = $text.Replace($tailLine, $tailNew, 1)

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v4.3 Web Remote patch applied.'
