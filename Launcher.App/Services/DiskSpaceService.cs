using Launcher.App.Models;

namespace Launcher.App.Services;

public static class DiskSpaceService
{
    private const long SafetyReserveBytes = 6L * 1024 * 1024 * 1024;

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
        var requiredBytes = Math.Max(SafetyReserveBytes, archiveSize + forgeSize + SafetyReserveBytes);
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
