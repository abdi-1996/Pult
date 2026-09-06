$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'patch-v1.5.ps1')

$path = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $path -Raw

$text = $text.Replace('TouchDisplay Host v1.5', 'TouchDisplay Host v1.6')

# The "wrong password even though it is correct" case happens when an older
# TouchDisplayHost process is still listening on the same TCP/UDP ports. The new
# window then shows one password while Android is actually talking to the old
# process with another password. Before starting v1.6, close older host builds.
$usingMarker = 'using System.ComponentModel;'
if (-not $text.Contains($usingMarker)) { throw 'Using marker not found' }
$text = $text.Replace($usingMarker, "using System.ComponentModel;`r`nusing System.Diagnostics;")

$mainOld = @'
    private static void Main()
    {
        NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4));
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
'@

$mainNew = @'
    private static void Main()
    {
        ClosePreviousHosts();
        NativeMethods.SetProcessDpiAwarenessContext(new IntPtr(-4));
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void ClosePreviousHosts()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == current.Id)
                        continue;

                    var name = process.ProcessName;
                    if (!name.StartsWith("TouchDisplayHost", StringComparison.OrdinalIgnoreCase))
                        continue;

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(1500);
                }
                catch
                {
                    // If an old elevated process cannot be stopped, StartServer will
                    // show a clear port-in-use error instead of silently using it.
                }
                finally
                {
                    process.Dispose();
                }
            }

            Thread.Sleep(250);
        }
        catch
        {
        }
    }
'@

if (-not $text.Contains($mainOld)) { throw 'Program.Main patch target not found' }
$text = $text.Replace($mainOld, $mainNew)

# Make a port collision explicit so the password shown in this window can never
# be mistaken for the password of an older host that owns port 59432.
$text = $text.Replace(
    'SetStatus("Ошибка запуска: " + ex.Message, true);',
    'SetStatus("Ошибка запуска. Возможно, старая версия TouchDisplay всё ещё запущена: " + ex.Message, true);')

Set-Content -Path $path -Value $text -Encoding UTF8
Write-Host 'TouchDisplay Host v1.6 stale-host/password fix applied.'
