using Launcher.App.Configuration;
using Launcher.App.Models;

namespace Launcher.App.Services;

/// <summary>
/// Решает, что предлагает главная кнопка лаунчера: обновить лаунчер, установить сборку,
/// обновить её или играть.
/// </summary>
/// <remarks>
/// Вынесено из <c>MainWindow.xaml.cs</c>. Логика тонкая и уже стоила игрокам перекачек:
/// именно здесь решается, считать ли установленную сборку устаревшей. Отдельными тестами
/// закрыт случай «установлено НОВЕЕ, чем в манифесте» — он возникает, когда каталог не ответил
/// и лаунчер читает статический или встроенный манифест со старой версией.
/// </remarks>
public static class PrimaryActionResolver
{
    /// <summary>
    /// Ожидаемая версия сборки в том виде, в каком она пишется в маркер: <c>&lt;id&gt;:&lt;версия&gt;</c>.
    /// </summary>
    public static string GetExpectedArchiveVersion(ModpackManifest? modpackManifest, LauncherConfiguration? configuration)
    {
        if (modpackManifest is not null)
        {
            return $"{modpackManifest.Modpack.Id}:{modpackManifest.Modpack.Version}";
        }

        if (configuration is null)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(configuration.ModpackVersion)
            ? configuration.ModpackArchiveUrl
            : configuration.ModpackVersion;
    }

    /// <param name="installedVersion">
    /// Содержимое маркера установленной сборки или <c>null</c>, если маркера нет.
    /// </param>
    public static PrimaryActionState Resolve(
        ModpackManifest? modpackManifest,
        LauncherConfiguration? configuration,
        string? installedVersion,
        bool launcherUpdateAvailable)
    {
        if (launcherUpdateAvailable)
        {
            return PrimaryActionState.LauncherUpdate;
        }

        if (configuration is null || !configuration.UsesDirectModpackArchive())
        {
            return PrimaryActionState.Play;
        }

        var expectedVersion = GetExpectedArchiveVersion(modpackManifest, configuration);
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return PrimaryActionState.Play;
        }

        if (modpackManifest?.Updates.ForceReinstall == true)
        {
            return PrimaryActionState.Update;
        }

        if (installedVersion is null)
        {
            return PrimaryActionState.Install;
        }

        var installed = installedVersion.Trim();
        if (installed.Equals(expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return PrimaryActionState.Play;
        }

        // В папке лежит ДРУГАЯ сборка. Номера версий разных сборок несравнимы (чужая 1.0.0
        // «новее» нашей 0.13.4), поэтому защиту от отката тут применять нельзя — иначе кнопка
        // звала бы «Играть» на чужой сборке. Ставим с нуля, установщик всё равно сверит id.
        if (ArchiveInstallService.IsDifferentModpack(expectedVersion, installed))
        {
            return PrimaryActionState.Install;
        }

        // Если установлено НОВЕЕ, чем предлагает манифест (каталог не ответил и мы читаем
        // статический/встроенный манифест со старой версией), то обновлять нечего: установка всё
        // равно откажется откатывать. Иначе кнопка бесконечно звала бы «Обновить» вхолостую.
        if (modpackManifest?.Updates.AllowDowngrade != true &&
            ArchiveInstallService.IsManifestVersionOlder(expectedVersion, installed))
        {
            return PrimaryActionState.Play;
        }

        return PrimaryActionState.Update;
    }

    /// <summary>Подпись главной кнопки для состояния.</summary>
    public static string GetButtonText(PrimaryActionState state) => state switch
    {
        PrimaryActionState.LauncherUpdate => "Обновить лаунчер",
        PrimaryActionState.Install => "Установить",
        PrimaryActionState.Update => "Обновить",
        _ => "Играть"
    };

    /// <summary>
    /// Есть ли обновление самого лаунчера. Без адреса пакета обновляться некуда,
    /// поэтому такой манифест считается «обновления нет».
    /// </summary>
    public static bool IsLauncherUpdateAvailable(
        ModpackManifest? modpackManifest,
        string localVersion,
        out string remoteVersion)
    {
        remoteVersion = modpackManifest?.Launcher.Version?.Trim() ?? string.Empty;
        if (modpackManifest is null ||
            string.IsNullOrWhiteSpace(remoteVersion) ||
            string.IsNullOrWhiteSpace(modpackManifest.Launcher.PackageUrl))
        {
            return false;
        }

        return IsRemoteVersionNewer(localVersion, remoteVersion);
    }

    public static bool IsRemoteVersionNewer(string localVersion, string remoteVersion)
    {
        var normalizedLocal = NormalizeVersion(localVersion);
        var normalizedRemote = NormalizeVersion(remoteVersion);
        if (Version.TryParse(normalizedLocal, out var localParsed) &&
            Version.TryParse(normalizedRemote, out var remoteParsed))
        {
            return remoteParsed > localParsed;
        }

        return !string.Equals(localVersion, remoteVersion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Приводит версию к виду, который понимает <see cref="Version"/>: отбрасывает
    /// суффиксы вида <c>+git</c> и <c>-beta</c>.
    /// </summary>
    public static string NormalizeVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var cleaned = value.Trim();
        var plusIndex = cleaned.IndexOf('+');
        if (plusIndex >= 0)
        {
            cleaned = cleaned[..plusIndex];
        }

        var dashIndex = cleaned.IndexOf('-');
        if (dashIndex >= 0)
        {
            cleaned = cleaned[..dashIndex];
        }

        // Возвращаем как есть, даже если строка опустела (например версия «-beta»):
        // тогда Version.TryParse не сработает и сравнение уйдёт на побайтное — так было
        // в WPF-версии, и менять это поведение при переносе нельзя.
        return cleaned;
    }
}
