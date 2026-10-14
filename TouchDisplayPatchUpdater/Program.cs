using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace TouchDisplayPatchUpdater;

internal static class Program
{
    private const string AppExe = "TouchDisplayMobileWeb.exe";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "TouchDisplayMobileWeb";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            var target = FindTargetFolder();
            if (target is null) return;

            var web = Path.Combine(target, "wwwroot");
            if (!Directory.Exists(web) || !File.Exists(Path.Combine(target, AppExe)))
            {
                MessageBox.Show(
                    "В выбранной папке не найдена версия TouchDisplay с отдельными файлами.\n\n" +
                    "Сначала должна быть установлена версия v1.2.",
                    "TouchDisplay Update v1.2.1",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                "Исправить неработающие вкладки и кнопки TouchDisplay?\n\n" +
                "Будут обновлены только файлы интерфейса:\n" +
                "• wwwroot\\app.js\n" +
                "• wwwroot\\app.css\n" +
                "• wwwroot\\index.html\n\n" +
                "Остальные файлы приложения и настройки не меняются.",
                "TouchDisplay Update v1.2.1",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK)
                return;

            StopApp(target);

            var backup = Path.Combine(target, "Backup", "ui-v1.2.1-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backup);

            PatchFile(web, backup, "app.js", "TouchDisplayPatch.app.js");
            PatchFile(web, backup, "app.css", "TouchDisplayPatch.app.css");
            PatchFile(web, backup, "index.html", "TouchDisplayPatch.index.html");

            var data = Path.Combine(target, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "version.txt"), "1.2.1");

            var exe = Path.Combine(target, AppExe);
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = target,
                UseShellExecute = true
            });

            if (p is null)
                throw new InvalidOperationException("Не удалось запустить TouchDisplay после обновления.");

            Thread.Sleep(1800);
            if (p.HasExited)
                throw new InvalidOperationException("TouchDisplay закрылся сразу после запуска.");

            MessageBox.Show(
                "Готово. Исправлены вкладки и кнопки.\n\n" +
                "На iPhone полностью закрой старую вкладку/PWA TouchDisplay и открой снова.",
                "TouchDisplay v1.2.1",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Не удалось установить обновление.\n\n" + ex.Message,
                "TouchDisplay Update v1.2.1",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string? FindTargetFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var value = key?.GetValue(RunValue) as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                var exe = value.Trim().Trim('"');
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrWhiteSpace(dir) &&
                    File.Exists(Path.Combine(dir, AppExe)) &&
                    Directory.Exists(Path.Combine(dir, "wwwroot")))
                    return dir;
            }
        }
        catch { }

        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(here, AppExe)) && Directory.Exists(Path.Combine(here, "wwwroot")))
            return here;

        using var picker = new FolderBrowserDialog
        {
            Description = "Выберите папку TouchDisplay v1.2",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        return picker.ShowDialog() == DialogResult.OK ? picker.SelectedPath : null;
    }

    private static void StopApp(string target)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is null) continue;
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
                                   Path.GetFullPath(target),
                                   StringComparison.OrdinalIgnoreCase))
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

    private static void PatchFile(string webRoot, string backupRoot, string name, string resourceName)
    {
        var destination = Path.Combine(webRoot, name);
        var backup = Path.Combine(backupRoot, name);

        if (File.Exists(destination))
            File.Copy(destination, backup, true);

        var asm = Assembly.GetExecutingAssembly();
        using var source = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("В обновлении отсутствует " + name);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(output);
    }
}
