using Launcher.App.Configuration;
using Launcher.App.Models;

namespace Launcher.App.Services;

/// <summary>
/// Что предлагает главная кнопка лаунчера. Вынесено из окна: состояние вычисляется из данных,
/// а не из интерфейса, и одинаково нужно обоим UI.
/// </summary>
public enum PrimaryActionState
{
    LauncherUpdate,
    Install,
    Update,
    Play
}

/// <summary>Как показывать сборку в списке каталога.</summary>
public enum ModpackInstallState
{
    /// <summary>Про эту сборку ничего не известно — метку не показываем.</summary>
    Unknown,
    NotInstalled,
    UpdateAvailable,
    Installed
}

/// <summary>Решения по каталогу сборок, не зависящие от интерфейса.</summary>
public static class ModpackCatalogLogic
{
    /// <summary>
    /// Состояние сборки для метки в списке. Для выбранной сборки берём его из состояния
    /// главной кнопки (там уже учтены версия и наличие обновления), для остальных —
    /// смотрим маркер в папке, которую игрок задал этой сборке.
    /// </summary>
    public static ModpackInstallState GetInstallState(
        string modpackId,
        string? selectedModpackId,
        PrimaryActionState primaryAction,
        UserSettings settings)
    {
        if (modpackId.Equals(selectedModpackId, StringComparison.OrdinalIgnoreCase))
        {
            return primaryAction switch
            {
                PrimaryActionState.Install => ModpackInstallState.NotInstalled,
                PrimaryActionState.Update => ModpackInstallState.UpdateAvailable,
                _ => ModpackInstallState.Installed
            };
        }

        if (settings.ModpackInstallRoots.TryGetValue(modpackId, out var root) && !string.IsNullOrWhiteSpace(root))
        {
            return ModpackInstallMarker.IsInstalledAt(root, modpackId)
                ? ModpackInstallState.Installed
                : ModpackInstallState.NotInstalled;
        }

        return ModpackInstallState.Unknown;
    }

    /// <summary>Подпись метки состояния. Пустая строка означает «метку не показывать».</summary>
    public static string GetInstallStateText(ModpackInstallState state) => state switch
    {
        ModpackInstallState.NotInstalled => "Не установлена",
        ModpackInstallState.UpdateAvailable => "Есть обновление",
        ModpackInstallState.Installed => "Установлена",
        _ => string.Empty
    };

    /// <summary>
    /// Какую сборку открыть при запуске: ранее выбранную игроком, иначе первую из каталога.
    /// </summary>
    public static CatalogModpackEntry? ChoosePreferred(
        IReadOnlyList<CatalogModpackEntry> modpacks,
        string? preferredId)
    {
        if (modpacks.Count == 0)
        {
            return null;
        }

        return modpacks.FirstOrDefault(entry => entry.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase))
            ?? modpacks[0];
    }

    /// <summary>
    /// Изолировать ли папки установки по id сборки. Только при реально мульти-сборочном каталоге:
    /// иначе одиночные пользователи (в том числе с кастомным путём) заметили бы смену папки
    /// установки и перекачали бы сборку заново.
    /// </summary>
    public static bool ShouldIsolateInstallRoots(IReadOnlyList<CatalogModpackEntry> modpacks)
        => modpacks.Count > 1;

    /// <summary>
    /// Куда реально ставится выбранная сборка с учётом настроек игрока и режима каталога.
    /// </summary>
    /// <remarks>
    /// Id сборки подставляется, только когда каталог мульти-сборочный: иначе одиночному
    /// пользователю папка сменилась бы на подпапку и сборка перекачалась бы заново.
    /// </remarks>
    public static string ResolveEffectiveInstallRoot(
        LauncherConfiguration? configuration,
        ModpackManifest? modpackManifest,
        UserSettings settings)
    {
        if (configuration is null)
        {
            return "-";
        }

        // Изолированный профиль перехватывается внутри ResolveInstallRoot — там же, где его
        // видят установщики. Дублировать проверку здесь нельзя: две реализации разъедутся.
        var catalogModpackId = configuration.IsMultiModpackCatalog ? modpackManifest?.Modpack.Id : null;
        return settings.ResolveInstallRoot(
            modpackManifest?.Install.Root ?? string.Empty,
            configuration.GetDistributionRoot(),
            catalogModpackId);
    }
}
