using Launcher.App.Models;
using Launcher.App.Platform;

namespace Launcher.App.Services;

/// <summary>Как именно лаунчер оказался на компьютере игрока.</summary>
public enum LauncherInstallationKind
{
    /// <summary>Обычная папка: файлы можно перезаписать поверх (Windows и портативная распаковка).</summary>
    Portable,

    /// <summary>Один файл <c>.AppImage</c>: обновление — это замена самого файла.</summary>
    AppImage,

    /// <summary>Бандл <c>.app</c> на macOS: заменять надо каталог целиком.</summary>
    MacBundle,

    /// <summary>
    /// Установлен пакетным менеджером (<c>/opt</c>, <c>/usr</c>). Обновлять себя НЕЛЬЗЯ:
    /// прав на запись нет, а подмена файлов рассинхронизирует базу пакетов.
    /// </summary>
    SystemPackage
}

/// <summary>Что лаунчер может сделать с обновлением при текущем способе установки.</summary>
public sealed record LauncherUpdatePlan(
    LauncherInstallationKind Kind,
    bool CanSelfUpdate,
    string TargetPath,
    string Instruction);

/// <summary>
/// Определяет способ установки и решает, обновляться самому или отправить игрока за пакетом.
/// </summary>
/// <remarks>
/// На Windows лаунчер всегда обновляет себя сам, а на Linux и macOS так можно не всегда:
/// приложение из <c>.deb</c> лежит в системном каталоге под root, и попытка переписать его
/// закончится отказом в доступе, а если прав хватит — сломает учёт файлов у apt.
/// </remarks>
public static class LauncherInstallation
{
    /// <summary>
    /// Определяет способ установки по пути к исполняемому файлу и переменным окружения.
    /// </summary>
    /// <param name="executablePath">Путь к текущему исполняемому файлу.</param>
    /// <param name="appImagePath">
    /// Значение переменной <c>APPIMAGE</c> — её выставляет сам AppImage при запуске.
    /// </param>
    public static LauncherInstallationKind Detect(string? executablePath, string? appImagePath = null)
    {
        appImagePath ??= Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImagePath))
        {
            return LauncherInstallationKind.AppImage;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return LauncherInstallationKind.Portable;
        }

        var normalized = executablePath.Replace('\\', '/');

        // Решение принимается ТОЛЬКО по виду пути, без оглядки на текущую ОС: windows-путь
        // никогда не начинается с «/opt/» и не содержит «.app/Contents/», поэтому лишняя
        // проверка платформы ничего не давала, зато делала логику непроверяемой тестом.
        if (normalized.Contains(".app/Contents/", StringComparison.OrdinalIgnoreCase))
        {
            return LauncherInstallationKind.MacBundle;
        }

        if (normalized.StartsWith("/opt/", StringComparison.Ordinal) ||
            normalized.StartsWith("/usr/", StringComparison.Ordinal))
        {
            return LauncherInstallationKind.SystemPackage;
        }

        return LauncherInstallationKind.Portable;
    }

    /// <summary>Строит план обновления с текстом, который увидит игрок.</summary>
    public static LauncherUpdatePlan BuildPlan(string? executablePath, string? appImagePath = null)
    {
        appImagePath ??= Environment.GetEnvironmentVariable("APPIMAGE");
        var kind = Detect(executablePath, appImagePath);

        return kind switch
        {
            LauncherInstallationKind.AppImage => new LauncherUpdatePlan(
                kind,
                CanSelfUpdate: true,
                TargetPath: appImagePath ?? executablePath ?? string.Empty,
                Instruction: "Лаунчер обновит сам себя и перезапустится."),

            LauncherInstallationKind.MacBundle => new LauncherUpdatePlan(
                kind,
                CanSelfUpdate: true,
                TargetPath: FindBundleRoot(executablePath!),
                Instruction: "Лаунчер обновит сам себя и перезапустится."),

            LauncherInstallationKind.SystemPackage => new LauncherUpdatePlan(
                kind,
                CanSelfUpdate: false,
                TargetPath: executablePath ?? string.Empty,
                Instruction: "Лаунчер установлен системным пакетом, поэтому обновляется через него: " +
                             "скачай новый пакет с сайта и установи его поверх — настройки и сборки останутся на месте."),

            _ => new LauncherUpdatePlan(
                kind,
                CanSelfUpdate: true,
                TargetPath: executablePath ?? string.Empty,
                Instruction: "Лаунчер обновит сам себя и перезапустится.")
        };
    }

    /// <summary>
    /// Выбирает пакет обновления под текущий способ установки. Если в манифесте нет раздела
    /// по способам, возвращается старый общий пакет — поведение Windows не меняется.
    /// </summary>
    public static ManifestLauncherPackage? ResolvePackage(ManifestLauncherInfo launcher, LauncherInstallationKind kind)
    {
        var key = kind switch
        {
            LauncherInstallationKind.AppImage => "appimage",
            LauncherInstallationKind.MacBundle => "macbundle",
            LauncherInstallationKind.SystemPackage => "package",
            _ => HostPlatform.IsWindows ? "windows" : "portable"
        };

        if (launcher.PackagesByInstall.TryGetValue(key, out var package) &&
            (!string.IsNullOrWhiteSpace(package.Url) || !string.IsNullOrWhiteSpace(package.DownloadPageUrl)))
        {
            return package;
        }

        // Системному пакету общий zip не подходит: его нельзя ни распаковать поверх, ни поставить.
        if (kind == LauncherInstallationKind.SystemPackage)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(launcher.PackageUrl)
            ? null
            : new ManifestLauncherPackage { Url = launcher.PackageUrl, Sha256 = launcher.Sha256 };
    }

    /// <summary>Путь к каталогу <c>.app</c> по пути внутри него.</summary>
    private static string FindBundleRoot(string executablePath)
    {
        var current = Path.GetDirectoryName(executablePath.Replace('\\', '/'));
        while (!string.IsNullOrEmpty(current))
        {
            if (current.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return executablePath;
    }
}
