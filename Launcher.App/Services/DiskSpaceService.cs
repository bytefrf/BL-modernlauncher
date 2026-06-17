using Launcher.App.Models;

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
        var fullRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(installRoot));
        var rootPath = Path.GetPathRoot(fullRoot);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return new DiskSpaceCheckResult(false, 0, 0, $"Не удалось определить диск для папки установки: {fullRoot}");
        }

        var drive = new DriveInfo(rootPath);
        var archiveSize = manifest?.Modpack.ArchiveSize ?? 0;
        var forgeSize = manifest?.Runtime.ForgeInstallerSize ?? 0;

        // Реальная оценка пикового использования диска при установке:
        //   zip модпака в кэше + распакованный модпак + forge-installer + базовый клиент/Java/ассеты + запас.
        var modpackBytes = archiveSize > 0 ? archiveSize : UnknownArchiveFallbackBytes;
        var extractedBytes = (long)(modpackBytes * ExtractedMultiplier);
        var requiredBytes = modpackBytes + extractedBytes + forgeSize + BaseGameFootprintBytes + WorkingHeadroomBytes;
        var availableBytes = drive.AvailableFreeSpace;

        if (availableBytes < requiredBytes)
        {
            return new DiskSpaceCheckResult(
                false,
                availableBytes,
                requiredBytes,
                $"Недостаточно места на диске {drive.Name}. Свободно {FormatBytes(availableBytes)}, нужно минимум {FormatBytes(requiredBytes)}.");
        }

        return new DiskSpaceCheckResult(true, availableBytes, requiredBytes, $"Свободно {FormatBytes(availableBytes)} на диске {drive.Name}.");
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
