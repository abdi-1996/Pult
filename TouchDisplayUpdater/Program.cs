using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace TouchDisplayUpdater;

internal static class Program
{
    private const string AppExeName = "TouchDisplayMobileWeb.exe";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "TouchDisplayMobileWeb";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var target = ResolveTargetFolder();
            if (string.IsNullOrWhiteSpace(target))
                return;

            var answer = MessageBox.Show(
                "TouchDisplay будет обновлён до v1.2.\n\n" +
                "Настройки, PIN и автозапуск сохранятся.\n" +
                "Старые файлы будут сохранены в папке Backup.\n\n" +
                "Папка:\n" + target,
                "TouchDisplay Update v1.2",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);

            if (answer != DialogResult.OK)
                return;

            UpdateApp(target);

            MessageBox.Show(
                "Обновление завершено.\n\n" +
                "TouchDisplay v1.2 теперь хранится как обычное приложение со своими файлами.\n" +
                "В дальнейшем можно обновлять только изменённые файлы, а не собирать всё приложение заново.",
                "TouchDisplay обновлён",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Обновление не выполнено. Старые данные не удалены.\n\n" + ex.Message,
                "TouchDisplay Update",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string? ResolveTargetFolder()
    {
        var fromRun = ReadExistingAutoStartPath();
        if (!string.IsNullOrWhiteSpace(fromRun))
        {
            var dir = Path.GetDirectoryName(fromRun);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }

        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.EnumerateFiles(here, "TouchDisplay*.exe", SearchOption.TopDirectoryOnly)
            .Any(f => !string.Equals(Path.GetFileName(f), Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase)))
            return here;

        using var picker = new FolderBrowserDialog
        {
            Description = "Выберите папку, где находится текущий TouchDisplay",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        return picker.ShowDialog() == DialogResult.OK ? picker.SelectedPath : null;
    }

    private static string? ReadExistingAutoStartPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var value = key?.GetValue(RunValue) as string;
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Trim().Trim('"');
        }
        catch { return null; }
    }

    private static void UpdateApp(string target)
    {
        target = Path.GetFullPath(target);
        Directory.CreateDirectory(target);

        StopTouchDisplayProcesses(target);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupRoot = Path.Combine(target, "Backup", "before-v1.2-" + stamp);
        var tempRoot = Path.Combine(Path.GetTempPath(), "TouchDisplayUpdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(backupRoot);

        var payloadPath = Path.Combine(tempRoot, "payload.zip");
        ExtractEmbeddedPayload(payloadPath);

        var unpacked = Path.Combine(tempRoot, "payload");
        ZipFile.ExtractToDirectory(payloadPath, unpacked, true);

        var newExe = Path.Combine(unpacked, AppExeName);
        var newWeb = Path.Combine(unpacked, "wwwroot", "index.html");
        if (!File.Exists(newExe) || !File.Exists(newWeb))
            throw new InvalidOperationException("Файл обновления повреждён: отсутствуют файлы приложения.");

        var payloadFiles = Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(unpacked, f))
            .ToArray();

        var oldStandalone = Directory.EnumerateFiles(target, "TouchDisplay-Mobile-Web-v*.exe", SearchOption.TopDirectoryOnly)
            .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(Environment.ProcessPath ?? ""), StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var file in oldStandalone)
        {
            var dest = Path.Combine(backupRoot, Path.GetFileName(file));
            File.Copy(file, dest, true);
        }

        foreach (var relative in payloadFiles)
        {
            var current = Path.Combine(target, relative);
            if (!File.Exists(current)) continue;

            var backup = Path.Combine(backupRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(current, backup, true);
        }

        var copied = new List<string>();
        try
        {
            foreach (var relative in payloadFiles)
            {
                var src = Path.Combine(unpacked, relative);
                var dst = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
                copied.Add(relative);
            }

            WriteVersionFile(target);
            UpdateAutoStart(target);
            ConfigureFirewall(target);

            var installedExe = Path.Combine(target, AppExeName);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = installedExe,
                WorkingDirectory = target,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Не удалось запустить обновлённый TouchDisplay.");

            Thread.Sleep(2200);
            if (process.HasExited)
                throw new InvalidOperationException("Новая версия закрылась сразу после запуска. Выполняется откат.");
        }
        catch
        {
            foreach (var relative in copied.AsEnumerable().Reverse())
            {
                var dst = Path.Combine(target, relative);
                var backup = Path.Combine(backupRoot, relative);
                try
                {
                    if (File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(backup, dst, true);
                    }
                    else if (File.Exists(dst))
                    {
                        File.Delete(dst);
                    }
                }
                catch { }
            }

            var restored = oldStandalone.FirstOrDefault();
            if (restored is not null)
            {
                var backup = Path.Combine(backupRoot, Path.GetFileName(restored));
                if (File.Exists(backup))
                {
                    try { File.Copy(backup, restored, true); } catch { }
                }
            }

            throw;
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static void ExtractEmbeddedPayload(string destination)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var input = asm.GetManifestResourceStream("TouchDisplay.payload.zip")
            ?? throw new InvalidOperationException("В обновлении отсутствует payload.");
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static void StopTouchDisplayProcesses(string target)
    {
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == me) continue;
                var file = p.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(file)) continue;

                var sameFolder = string.Equals(
                    Path.GetFullPath(Path.GetDirectoryName(file)!),
                    Path.GetFullPath(target),
                    StringComparison.OrdinalIgnoreCase);

                var name = Path.GetFileName(file);
                if (!sameFolder || !name.StartsWith("TouchDisplay", StringComparison.OrdinalIgnoreCase))
                    continue;

                p.CloseMainWindow();
                if (!p.WaitForExit(1200))
                {
                    p.Kill(true);
                    p.WaitForExit(2500);
                }
            }
            catch { }
        }
    }

    private static void WriteVersionFile(string target)
    {
        var dataDir = Path.Combine(target, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "version.txt"), "1.2");
    }

    private static void UpdateAutoStart(string target)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("Не удалось обновить автозапуск.");

        var exe = Path.Combine(target, AppExeName);
        key.SetValue(RunValue, "\"" + exe + "\"");
    }

    private static void ConfigureFirewall(string target)
    {
        var exe = Path.Combine(target, AppExeName);
        RunNetsh("advfirewall firewall delete rule name=\"TouchDisplay Mobile Web\"");
        RunNetsh("advfirewall firewall add rule name=\"TouchDisplay Mobile Web\" dir=in action=allow program=\"" + exe + "\" enable=yes profile=private,domain");
    }

    private static void RunNetsh(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            p?.WaitForExit(5000);
        }
        catch { }
    }
}
