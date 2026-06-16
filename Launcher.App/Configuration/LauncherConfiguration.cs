using System.Text.Json;

namespace Launcher.App.Configuration;

public sealed class LauncherConfiguration
{
    public string LauncherName { get; set; } = "TerraFirmaGreg Modern";
    public string ManifestUrl { get; set; } = string.Empty;
    public string ModpackManifestUrl { get; set; } = "https://bl-modern.ru/download/modpack-manifest.json";
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
            var configuration = JsonSerializer.Deserialize<LauncherConfiguration>(File.ReadAllText(configPath), JsonOptions()) ??
                                new LauncherConfiguration();
            configuration.ConfigPath = configPath;
            return configuration;
        }

        return new LauncherConfiguration
        {
            ConfigPath = "embedded"
        };
    }

    public string GetDistributionRoot()
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(DistributionRoot));
    }

    public string GetUserSettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ForgeLauncher",
            ".launcher",
            "user-settings.json");
    }

    public string GetCachedModpackManifestPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ForgeLauncher",
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
