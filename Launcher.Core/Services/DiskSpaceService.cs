using Launcher.App.Models;
using Launcher.App.Platform;

namespace Launcher.App.Services;

public static class DiskSpaceService
{
    // Базовый «вес» ваниль-клиента 1.20.1 + библиотек + ассетов + Java JRE + библиотек Forge.
    // Эти данные качаются всегда и занимают примерно столько (оценка по реальной установке).
    private const long BaseGameFootprintBytes = 1600L * 1024 * 1024;   // ~1.6 ГБ
    // Рабочий запас на временные файлы распаковки и логи.
    private const long WorkingHeadroomBytes = 512L * 1024 * 1024;      // ~0.5 ГБ
    // Во сколько раз распакованный модпак больше zip (моды-jar уже сжаты, растёт умеренно).
    private const double ExtractedMultiplier = 1.3;
    // Если размер архива неизвестен (нет в манифесте) — берём осторожную оценку.
    private const long UnknownArchiveFallbackBytes = 2L * 1024 * 1024 * 1024; // ~2 ГБ

    public static DiskSpaceCheckResult CheckInstallSpace(string installRoot, ModpackManifest? manifest)
    {
        var fullRoot = LauncherPaths.ExpandFull(installRoot);
        var mountPoint = ResolveMountPoint(fullRoot);
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            // Раздел не определился — это не повод запрещать установку: проверка места
            // подстраховывает, а не решает, можно ли играть.
            return new DiskSpaceCheckResult(true, 0, 0, $"Свободное место на диске определить не удалось ({fullRoot}).");
        }

        long available;
        try
        {
            available = new DriveInfo(mountPoint).AvailableFreeSpace;
        }
        catch (Exception exception)
        {
            return new DiskSpaceCheckResult(true, 0, 0, $"Свободное место на диске определить не удалось: {exception.Message}");
        }

        return Evaluate(mountPoint, available, manifest);
    }

    /// <summary>
    /// Оценка по уже известному объёму свободного места. Вынесена отдельно, чтобы проверять
    /// решения без настоящего диска.
    /// </summary>
    public static DiskSpaceCheckResult Evaluate(string driveName, long availableBytes, ModpackManifest? manifest)
    {
        // Ноль означает «файловая система не ответила», а не «диск полон». У игрока на Linux
        // (саппорт-лог 11.08) свободное место читалось как 0,0 KB, и лаунчер отказывался ставить
        // сборку на диск, где места хватало. Отрицательные значения бывают у сетевых томов.
        if (availableBytes <= 0)
        {
            return new DiskSpaceCheckResult(true, 0, 0, $"Свободное место на диске {driveName} определить не удалось — продолжаем без проверки.");
        }

        var archiveSize = manifest?.Modpack.ArchiveSize ?? 0;
        var forgeSize = manifest?.Runtime.ForgeInstallerSize ?? 0;

        // Реальная оценка пикового использования диска при установке:
        //   zip модпака в кэше + распакованный модпак + forge-installer + базовый клиент/Java/ассеты + запас.
        var modpackBytes = archiveSize > 0 ? archiveSize : UnknownArchiveFallbackBytes;
        var extractedBytes = (long)(modpackBytes * ExtractedMultiplier);
        var requiredBytes = modpackBytes + extractedBytes + forgeSize + BaseGameFootprintBytes + WorkingHeadroomBytes;

        if (availableBytes < requiredBytes)
        {
            return new DiskSpaceCheckResult(
                false,
                availableBytes,
                requiredBytes,
                $"Недостаточно места на диске {driveName}. Свободно {FormatBytes(availableBytes)}, нужно минимум {FormatBytes(requiredBytes)}.");
        }

        return new DiskSpaceCheckResult(true, availableBytes, requiredBytes, $"Свободно {FormatBytes(availableBytes)} на диске {driveName}.");
    }

    /// <summary>
    /// Точка монтирования, на которой реально лежит путь.
    /// </summary>
    /// <remarks>
    /// На Windows это корень диска (<c>C:\</c>) — как и было. На Unix корень пути ВСЕГДА <c>/</c>,
    /// поэтому старый код мерил свободное место не там: у игрока сборка стояла в <c>/home/…</c>,
    /// а проверялся <c>/</c>. Отдельный раздел под <c>/home</c> — обычное дело, и цифры расходятся
    /// на порядки. Ищем самую длинную точку монтирования, которая является префиксом пути.
    /// </remarks>
    public static string ResolveMountPoint(string fullPath)
    {
        if (HostPlatform.IsWindows)
        {
            return Path.GetPathRoot(fullPath) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return "/";
        }

        var normalized = fullPath.Replace('\\', '/');
        var best = "/";

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                var mount = drive.RootDirectory.FullName.Replace('\\', '/');
                if (mount.Length > 1 && mount.EndsWith('/'))
                {
                    mount = mount.TrimEnd('/');
                }

                // Именно префикс каталога, а не строки: /home не должен «поймать» /homework.
                var isPrefix = normalized == mount ||
                               normalized.StartsWith(mount == "/" ? "/" : mount + "/", StringComparison.Ordinal);
                if (isPrefix && mount.Length > best.Length)
                {
                    best = mount;
                }
            }
        }
        catch
        {
            // Перечисление томов может упасть в урезанном окружении (контейнер, песочница) —
            // тогда меряем корень, это лучше, чем отказать игроку в установке.
        }

        return best;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.0} MB";
        }

        return $"{bytes / 1024d:0.0} KB";
    }
}

public sealed record DiskSpaceCheckResult(bool IsOk, long AvailableBytes, long RequiredBytes, string Message);
