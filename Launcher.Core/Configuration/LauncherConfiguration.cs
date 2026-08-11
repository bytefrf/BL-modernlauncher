using System.Text.Json;
using System.Text.Json.Serialization;
using Launcher.App.Platform;

namespace Launcher.App.Configuration;

public sealed class LauncherConfiguration
{
    // Рантайм-флаг (не из файла): true только когда каталог реально отдал >1 сборки. Включает
    // per-pack изоляцию папки установки при кастомном пути. Одиночный режим (в т.ч. фолбэк при
    // недоступном catalog.php) → false, поэтому путь старых пользователей НЕ меняется.
    [JsonIgnore]
    public bool IsMultiModpackCatalog { get; set; }

    public string LauncherName { get; set; } = "TerraFirmaGreg Modern";
    public string ManifestUrl { get; set; } = string.Empty;
    public string ModpackManifestUrl { get; set; } = "https://bl-modern.ru/download/modpack-manifest.json";
    // Режим каталога (несколько сборок). Если catalog.php недоступен — InitializeCatalogAsync
    // безопасно откатывается на одиночный режим (ModpackManifestUrl). ModpackManifestUrl оставляем
    // заполненным: его использует бутстраппер для проверки обновления App и как фолбэк App.
    public string CatalogUrl { get; set; } = "https://bl-modern.ru/api/catalog.php";

    public bool UsesCatalog() => !string.IsNullOrWhiteSpace(CatalogUrl);
    public string ModpackArchiveUrl { get; set; } = string.Empty;
    public string ModpackVersion { get; set; } = string.Empty;
    public string ModpackArchiveSha256 { get; set; } = string.Empty;
    public string DistributionRoot { get; set; } = "%AppData%\\ForgeLauncher";
    public string LauncherExecutable { get; set; } = "Launcher.App.exe";
    public string LauncherVersionFile { get; set; } = "launcher.version";

    public string ConfigPath { get; private set; } = string.Empty;

    public static LauncherConfiguration Load(string baseDirectory)
    {
        var configPath = Path.Combine(baseDirectory, "launcher.config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var configuration = JsonSerializer.Deserialize<LauncherConfiguration>(File.ReadAllText(configPath), JsonOptions()) ??
                                    new LauncherConfiguration();
                configuration.ConfigPath = configPath;
                return configuration;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                // Битый config не должен ронять запуск — падаем на встроенные значения по умолчанию.
                return new LauncherConfiguration
                {
                    ConfigPath = "embedded (config parse failed)"
                };
            }
        }

        return new LauncherConfiguration
        {
            ConfigPath = "embedded"
        };
    }

    public string GetDistributionRoot()
    {
        return LauncherPaths.ExpandFull(DistributionRoot);
    }

    public string GetUserSettingsPath()
    {
        return Path.Combine(
            LauncherPaths.GetApplicationDataRoot(),
            LauncherProfile.DataFolderName,
            ".launcher",
            "user-settings.json");
    }

    /// <summary>
    /// Профиль игрока (часы/ачивки) — отдельный файл рядом с настройками, чтобы статистику
    /// можно было позже синхронизировать с сайтом, не смешивая её с настройками лаунчера.
    /// </summary>
    public string GetPlayerProfilePath()
    {
        return Path.Combine(
            LauncherPaths.GetApplicationDataRoot(),
            LauncherProfile.DataFolderName,
            ".launcher",
            "player-stats.json");
    }

    public string GetCachedModpackManifestPath()
    {
        return Path.Combine(
            LauncherPaths.GetApplicationDataRoot(),
            LauncherProfile.DataFolderName,
            ".launcher",
            "modpack-manifest.cached.json");
    }

    public bool UsesDirectModpackArchive()
    {
        return !string.IsNullOrWhiteSpace(ModpackManifestUrl) || !string.IsNullOrWhiteSpace(ModpackArchiveUrl);
    }

    public bool UsesModpackManifest()
    {
        return !string.IsNullOrWhiteSpace(ModpackManifestUrl);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
    }
}
