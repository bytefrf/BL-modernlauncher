using System.Text.Json.Serialization;

namespace Launcher.App.Models;

/// <summary>
/// Каталог ДОПОЛНИТЕЛЬНЫХ модов, одобренных администрацией. Игрок может включить любой из них,
/// но добавить произвольный jar через лаунчер нельзя — ставится только то, что есть в этом списке
/// и проходит проверку SHA-256. Адрес каталога берётся из манифеста сборки (optionalModsUrl);
/// поле пустое ⇒ раздел «Дополнительные моды» просто не показывается.
/// </summary>
public sealed class OptionalModsCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<OptionalModEntry> Mods { get; set; } = [];

    [JsonIgnore]
    public Uri? SourceUri { get; set; }
}

public sealed class OptionalModEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    /// <summary>Имя файла в папке mods. Задаётся явно, чтобы не зависеть от URL.</summary>
    public string FileName { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }

    /// <summary>Версия игры/загрузчик — чтобы не предлагать мод не от той сборки.</summary>
    public string MinecraftVersion { get; set; } = string.Empty;
    public string Loader { get; set; } = string.Empty;

    /// <summary>Мод можно временно скрыть, не удаляя из каталога.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Включён по умолчанию у тех, кто ещё не делал выбор.</summary>
    public bool DefaultOn { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id) &&
        !string.IsNullOrWhiteSpace(Url) &&
        !string.IsNullOrWhiteSpace(Sha256) &&
        !string.IsNullOrWhiteSpace(FileName);
}

/// <summary>Что лаунчер сейчас положил в mods/ — чтобы потом убрать ровно свои файлы.</summary>
public sealed class InstalledOptionalMods
{
    public List<InstalledOptionalMod> Items { get; set; } = [];
}

public sealed class InstalledOptionalMod
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}
