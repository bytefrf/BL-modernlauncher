using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Launcher.App.Platform;

namespace Launcher.App.Services;

/// <summary>
/// Создаёт ярлык лаунчера на рабочем столе. Цель — текущий исполняемый файл, он самообновляется,
/// поэтому ярлык остаётся актуальным.
/// </summary>
/// <remarks>
/// Форма ярлыка своя у каждой ОС: <c>.lnk</c> через WScript.Shell на Windows, <c>.desktop</c>
/// по спецификации freedesktop на Linux, символическая ссылка на бандл (или <c>.command</c>) на macOS.
/// </remarks>
public static class DesktopShortcutService
{
    public static string Create(string shortcutName)
    {
        var exePath = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Не удалось определить путь к лаунчеру.");

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
        {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        if (string.IsNullOrWhiteSpace(desktop))
        {
            // На Linux эти папки берутся из XDG, а он бывает не настроен — ровно так же,
            // как GetFolderPath(ApplicationData) вернул пустоту на живой Ubuntu.
            // Без фолбэка кнопка «Ярлык на рабочий стол» просто ругалась бы.
            desktop = Path.Combine(LauncherPaths.GetHomeRoot(), "Desktop");
        }

        Directory.CreateDirectory(desktop);

        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(shortcutName) ? "BL-modern TFGM" : shortcutName);
        var workingDirectory = Path.GetDirectoryName(exePath) ?? desktop;

        if (HostPlatform.IsWindows)
        {
            return CreateWindowsShortcut(Path.Combine(desktop, safeName + ".lnk"), exePath, workingDirectory);
        }

        return HostPlatform.IsMacOS
            ? CreateMacShortcut(desktop, safeName, exePath, workingDirectory)
            : CreateLinuxShortcut(desktop, safeName, exePath, workingDirectory);
    }

    [SupportedOSPlatform("windows")]
    private static string CreateWindowsShortcut(string shortcutPath, string exePath, string workingDirectory)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell недоступен в системе — ярлык создать нельзя.");

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Не удалось создать WScript.Shell.");
            var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath])
                ?? throw new InvalidOperationException("Не удалось создать объект ярлыка.");
            var shortcutType = shortcut.GetType();

            void Set(string property, object value) =>
                shortcutType.InvokeMember(property, BindingFlags.SetProperty, null, shortcut, [value]);

            Set("TargetPath", exePath);
            Set("WorkingDirectory", workingDirectory);
            Set("IconLocation", exePath + ",0");
            Set("Description", "BL-modern TFGM Launcher");
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

            return shortcutPath;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    /// <summary>
    /// Linux: файл <c>.desktop</c> по спецификации freedesktop. Он обязан быть исполняемым,
    /// иначе рабочий стол покажет его как текстовый файл и откажется запускать.
    /// </summary>
    private static string CreateLinuxShortcut(string desktop, string safeName, string exePath, string workingDirectory)
    {
        var shortcutPath = Path.Combine(desktop, safeName + ".desktop");
        var icon = Path.Combine(workingDirectory, "launcher.png");

        var content = string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Version=1.0",
            $"Name={safeName}",
            "Comment=BL-modern TFGM Launcher",
            // Кавычки обязательны: путь установки вполне может содержать пробелы.
            $"Exec=\"{exePath}\"",
            $"Path={workingDirectory}",
            File.Exists(icon) ? $"Icon={icon}" : "Icon=applications-games",
            "Terminal=false",
            "Categories=Game;",
            string.Empty);

        File.WriteAllText(shortcutPath, content);
        MakeExecutable(shortcutPath);

        // GNOME с некоторых версий запускает только ярлыки, помеченные как доверенные.
        TryMarkTrusted(shortcutPath);
        return shortcutPath;
    }

    /// <summary>
    /// macOS: если лаунчер запущен из бандла <c>.app</c>, кладём на рабочий стол ссылку на бандл —
    /// это ровно то, что делает пользователь вручную. Иначе создаём исполняемый <c>.command</c>.
    /// </summary>
    private static string CreateMacShortcut(string desktop, string safeName, string exePath, string workingDirectory)
    {
        var bundle = FindAppBundle(exePath);
        if (bundle is not null)
        {
            var linkPath = Path.Combine(desktop, safeName + ".app");
            if (File.Exists(linkPath) || Directory.Exists(linkPath))
            {
                // Ссылку удаляем как файл: удалять как каталог опасно — уйдёт содержимое бандла.
                File.Delete(linkPath);
            }

            File.CreateSymbolicLink(linkPath, bundle);
            return linkPath;
        }

        var shortcutPath = Path.Combine(desktop, safeName + ".command");
        var content = string.Join('\n',
            "#!/bin/sh",
            $"cd {QuoteForShell(workingDirectory)}",
            $"exec {QuoteForShell(exePath)}",
            string.Empty);

        File.WriteAllText(shortcutPath, content);
        MakeExecutable(shortcutPath);
        return shortcutPath;
    }

    /// <summary>
    /// Поднимается от исполняемого файла вверх до каталога <c>*.app</c>, если он есть.
    /// </summary>
    private static string? FindAppBundle(string exePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(exePath));
        while (!string.IsNullOrEmpty(directory))
        {
            if (directory.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static void MakeExecutable(string path)
    {
        if (HostPlatform.IsWindows)
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Права могут не выставиться на нестандартной ФС — ярлык всё равно создан.
        }
    }

    /// <summary>
    /// Ставит ярлыку метку доверия GNOME. Отсутствие gio — не ошибка, на других средах метка не нужна.
    /// </summary>
    private static void TryMarkTrusted(string shortcutPath)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "gio",
                ArgumentList = { "set", shortcutPath, "metadata::trusted", "true" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit(3000);
        }
        catch
        {
        }
    }

    private static string QuoteForShell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, ' ');
        }

        return name.Trim();
    }
}
