using System.IO;
using Launcher.App.Platform;

namespace Launcher.App.Configuration;

/// <summary>
/// Проверяет, безопасна ли выбранная папка установки. Защищает от катастроф вроде установки в корень
/// диска (cleanBeforeInstall стирал бы файлы пользователя) и в системные/личные папки, а также от
/// OneDrive (синхронизация ломает и тормозит файлы игры).
/// </summary>
public static class InstallPathValidator
{
    /// <summary>Возвращает null, если путь безопасен, иначе понятную причину отказа.</summary>
    public static string? Validate(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null; // пусто = папка по умолчанию, это нормально
        }

        string fullPath;
        try
        {
            fullPath = LauncherPaths.ExpandFull(rawPath.Trim());
        }
        catch
        {
            return "Некорректный путь к папке установки.";
        }

        var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (normalized.Length <= 2 || normalized.Equals(pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            return HostPlatform.IsWindows
                ? "Нельзя устанавливать в корень диска (например D:\\) — это рискует файлами на диске. " +
                  "Укажи отдельную папку, например D:\\BL-modern."
                : "Нельзя устанавливать в корень файловой системы — это рискует файлами на диске. " +
                  "Укажи отдельную папку, например ~/BL-modern.";
        }

        // Системные каталоги Unix: установка в них либо потребует прав root, либо повредит систему.
        if (!HostPlatform.IsWindows)
        {
            foreach (var systemRoot in ProtectedUnixRoots)
            {
                if (normalized.Equals(systemRoot, StringComparison.Ordinal) ||
                    normalized.StartsWith(systemRoot + "/", StringComparison.Ordinal))
                {
                    return $"Нельзя устанавливать в системный каталог ({systemRoot}). " +
                           "Выбери папку в своём домашнем каталоге, например ~/BL-modern.";
                }
            }
        }

        foreach (var folder in ProtectedSpecialFolders)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) &&
                normalized.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return HostPlatform.IsWindows
                    ? "Нельзя устанавливать прямо в системную или личную папку (рабочий стол, документы, Windows, " +
                      "Program Files и т.п.). Создай для сборки отдельную подпапку."
                    : "Нельзя устанавливать прямо в домашний каталог или в системную папку. " +
                      "Создай для сборки отдельную подпапку, например ~/BL-modern.";
            }
        }

        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(oneDrive))
        {
            try
            {
                var oneDriveRoot = Path.GetFullPath(oneDrive).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (normalized.Equals(oneDriveRoot, StringComparison.OrdinalIgnoreCase) ||
                    normalized.StartsWith(oneDriveRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return "Эта папка синхронизируется с OneDrive — облако будет ломать и тормозить файлы игры. " +
                           "Выбери папку вне OneDrive (например D:\\BL-modern).";
                }
            }
            catch
            {
                // OneDrive-путь не распарсился — пропускаем эту проверку.
            }
        }

        return null;
    }

    /// <summary>
    /// Каталоги Linux и macOS, куда игровую сборку класть нельзя. <c>/Applications</c> и
    /// <c>/System</c> относятся к macOS, остальные общие для Unix.
    /// </summary>
    private static readonly string[] ProtectedUnixRoots =
    [
        "/bin", "/sbin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/proc", "/sys",
        "/usr", "/var", "/System", "/Applications", "/Library"
    ];

    private static readonly Environment.SpecialFolder[] ProtectedSpecialFolders =
    [
        Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.DesktopDirectory,
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.ApplicationData,
        Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.CommonApplicationData
    ];
}
