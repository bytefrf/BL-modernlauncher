using System.Text.Json.Serialization;

namespace Launcher.App.Models;

/// <summary>
/// Каталог сборок (читается с catalog.php). Список доступных модпаков + параметры
/// самообновления лаунчера. Если catalogUrl не задан — лаунчер работает в одиночном режиме.
/// </summary>
public sealed class CatalogManifest
{
    public int SchemaVersion { get; set; } = 1;
    public LauncherUpdateInfo Launcher { get; set; } = new();
    public List<CatalogModpackEntry> Modpacks { get; set; } = [];

    [JsonIgnore]
    public Uri? SourceUri { get; set; }
}

public sealed class CatalogModpackEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string IconUrl { get; set; } = string.Empty;
    public string BannerUrl { get; set; } = string.Empty;
    public int Order { get; set; }

    /// <summary>Абсолютный URL манифеста этой сборки (manifest.php?id=...).</summary>
    public string ManifestUrl { get; set; } = string.Empty;
}
