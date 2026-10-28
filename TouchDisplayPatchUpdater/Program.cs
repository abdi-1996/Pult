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
            var target = ChooseTargetFolder();
            if (target is null) return;

            var web = Path.Combine(target, "wwwroot");
            if (!Directory.Exists(web) || !File.Exists(Path.Combine(target, AppExe)))
            {
                MessageBox.Show(
                    "В выбранной папке не найдена версия TouchDisplay с отдельными файлами.\n\n" +
                    "Сначала должна быть установлена версия v1.2.",
                    "TouchDisplay Update v1.2.15",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                "Сохранить рабочий стол при сворачивании приложения?\n\n" +
                "Будут обновлены файлы Adaptive Display и интерфейса:\n" +
                "• TouchDisplayMobileWeb.dll\n" +
                "• wwwroot\\app.js\n" +
                "• wwwroot\\app.css\n" +
                "• wwwroot\\index.html\n\n" +
                "PIN, настройки и остальные файлы приложения не меняются.",
                "TouchDisplay Update v1.2.15",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information) != DialogResult.OK)
                return;

            StopApp(target);

            var backup = Path.Combine(target, "Backup", "ui-v1.2.15-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backup);

            PatchRootFile(target, backup, "TouchDisplayMobileWeb.dll", "TouchDisplayPatch.TouchDisplayMobileWeb.dll");
            PatchFile(web, backup, "app.js", "TouchDisplayPatch.app.js");
            PatchFile(web, backup, "app.css", "TouchDisplayPatch.app.css");
            PatchFile(web, backup, "index.html", "TouchDisplayPatch.index.html");

            var data = Path.Combine(target, "data");
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "version.txt"), "1.2.15");

            EnableAutoStart(target);

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
                "Готово.\n\n" +
                "Исправлено восстановление экрана после сворачивания. Режим монитора сохраняется 60 секунд при кратком обрыве управления; повторное подключение не переключает разрешение. Индикатор подключения исчезает после первого видеокадра.\n\n" +
                "На iPhone полностью закрой старую вкладку/PWA TouchDisplay и открой снова.",
                "TouchDisplay v1.2.15",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Не удалось установить обновление.\n\n" + ex.Message,
                "TouchDisplay Update v1.2.15",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string? ChooseTargetFolder()
    {
        var suggested = DetectInstalledFolder();

        using var form = new Form
        {
            Text = "TouchDisplay Update v1.2.15",
            ClientSize = new Size(660, 210),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(18, 23, 31),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f)
        };

        var title = new Label
        {
            Text = "Выберите папку установленного TouchDisplay",
            AutoSize = false,
            Location = new Point(24, 20),
            Size = new Size(610, 30),
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.White
        };

        var hint = new Label
        {
            Text = "Можно выбрать другое местоположение вручную. Обновление затронет только файлы интерфейса.",
            AutoSize = false,
            Location = new Point(24, 53),
            Size = new Size(610, 42),
            ForeColor = Color.FromArgb(175, 185, 198)
        };

        var pathBox = new TextBox
        {
            Location = new Point(24, 103),
            Size = new Size(500, 32),
            Text = suggested ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)
        };

        var browse = new Button
        {
            Text = "Обзор...",
            Location = new Point(536, 101),
            Size = new Size(100, 36)
        };

        var cancel = new Button
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Location = new Point(430, 157),
            Size = new Size(95, 36)
        };

        var update = new Button
        {
            Text = "Обновить",
            DialogResult = DialogResult.OK,
            Location = new Point(536, 157),
            Size = new Size(100, 36)
        };

        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Выберите папку, где установлен TouchDisplay",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
                SelectedPath = Directory.Exists(pathBox.Text) ? pathBox.Text : (suggested ?? string.Empty)
            };
            if (picker.ShowDialog(form) == DialogResult.OK)
                pathBox.Text = picker.SelectedPath;
        };

        form.Controls.AddRange(new Control[] { title, hint, pathBox, browse, cancel, update });
        form.AcceptButton = update;
        form.CancelButton = cancel;

        while (true)
        {
            if (form.ShowDialog() != DialogResult.OK)
                return null;

            var selected = pathBox.Text.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(selected))
            {
                MessageBox.Show(form, "Укажите папку TouchDisplay.", "TouchDisplay Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }

            try { selected = Path.GetFullPath(selected); }
            catch
            {
                MessageBox.Show(form, "Неверный путь к папке.", "TouchDisplay Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                continue;
            }

            if (!File.Exists(Path.Combine(selected, AppExe)) || !Directory.Exists(Path.Combine(selected, "wwwroot")))
            {
                MessageBox.Show(
                    form,
                    "В этой папке не найден TouchDisplay v1.2 с отдельными файлами.\n\n" +
                    "Нужны:\n" +
                    "• TouchDisplayMobileWeb.exe\n" +
                    "• папка wwwroot",
                    "TouchDisplay не найден",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                continue;
            }

            return selected;
        }
    }

    private static string? DetectInstalledFolder()
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

        return null;
    }

    private static void EnableAutoStart(string target)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("Не удалось включить автозапуск Windows.");

        var exe = Path.Combine(target, AppExe);
        key.SetValue(RunValue, "\"" + exe + "\" --autostart");
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

    private static void PatchRootFile(string targetRoot, string backupRoot, string name, string resourceName)
    {
        var destination = Path.Combine(targetRoot, name);
        var backup = Path.Combine(backupRoot, name);

        if (File.Exists(destination))
            File.Copy(destination, backup, true);

        var asm = Assembly.GetExecutingAssembly();
        using var source = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("В обновлении отсутствует " + name);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(output);
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
