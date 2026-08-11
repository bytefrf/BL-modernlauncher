using System.Text.Json.Serialization;

namespace Launcher.App.Models;

public sealed class ModpackManifest
{
    public int SchemaVersion { get; set; } = 1;
    public ManifestLauncherInfo Launcher { get; set; } = new();
    public ManifestModpackInfo Modpack { get; set; } = new();
    public ManifestInstallInfo Install { get; set; } = new();
    public ManifestRuntimeInfo Runtime { get; set; } = new();
    public ManifestUpdateInfo Updates { get; set; } = new();
    public ManifestIntegrityInfo Integrity { get; set; } = new();
    public ManifestUiInfo Ui { get; set; } = new();
    // Игровые сервера сборки — пиннятся в servers.dat при установке. У каждой сборки свой сервер.
    public List<ManifestServerEntry> Servers { get; set; } = [];

    /// <summary>
    /// Каталог дополнительных модов, одобренных администрацией (JSON, формат OptionalModsCatalog).
    /// Пусто ⇒ раздел «Дополнительные моды» в лаунчере не показывается — безопасный дефолт.
    /// </summary>
    public string OptionalModsUrl { get; set; } = string.Empty;

    [JsonIgnore]
    public Uri? SourceUri { get; set; }

    public Uri ResolveUri(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        if (SourceUri is null)
        {
            throw new InvalidOperationException("Cannot build relative URL without modpack manifest URL.");
        }

        return new Uri(SourceUri, value);
    }
}

public sealed class ManifestServerEntry
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
}

// Changeset одной версии: что скачать/обновить и что удалить при переходе basedOn → version.
// Файл лежит по URL из manifest.updates.changesetUrl. Применяется, только если у игрока стоит
// именно версия basedOn; иначе (отстал больше чем на шаг / ошибка) — полная установка из ZIP.
public sealed class ModpackChangeset
{
    public int SchemaVersion { get; set; } = 1;
    public string Version { get; set; } = string.Empty;   // целевая версия (должна совпасть с modpack.version)
    public string BasedOn { get; set; } = string.Empty;   // версия, ОТ которой считался changeset
    public List<ChangesetFile> Update { get; set; } = []; // файлы скачать/перезаписать
    public List<string> Delete { get; set; } = [];        // файлы удалить (пути относительно корня установки)
}

// Один файл для скачивания: путь в установке (без strip-префикса .minecraft/), URL, хэш, размер.
public sealed class ChangesetFile
{
    public string Path { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}

public sealed class ManifestLauncherInfo
{
    public string Title { get; set; } = "Forge Launcher";
    public string NewsUrl { get; set; } = string.Empty;
    public string SupportUrl { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Пакеты обновления по способу установки. Ключи: <c>windows</c>, <c>portable</c>,
    /// <c>appimage</c>, <c>macbundle</c>, <c>package</c>.
    /// </summary>
    /// <remarks>
    /// Одного <see cref="PackageUrl"/> мало: на Linux и macOS формат обновления зависит от того,
    /// как лаунчер установлен. AppImage обновляется одним файлом, бандл .app — целым каталогом,
    /// а системный пакет вообще обновляется своим менеджером. Поле необязательное: если его нет,
    /// используется прежний <see cref="PackageUrl"/> и поведение не меняется.
    /// </remarks>
    public Dictionary<string, ManifestLauncherPackage> PackagesByInstall { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Один пакет обновления лаунчера.</summary>
public sealed class ManifestLauncherPackage
{
    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Куда отправить игрока, если обновиться самому нельзя (системный пакет).</summary>
    public string DownloadPageUrl { get; set; } = string.Empty;
}

public sealed class ManifestModpackInfo
{
    public string Id { get; set; } = "modpack";
    public string Name { get; set; } = "Modpack";
    public string Version { get; set; } = "1.0.0";
    public string MinecraftVersion { get; set; } = "1.20.1";
    public string Loader { get; set; } = "forge";
    public string LoaderVersion { get; set; } = "47.3.29";
    public string Description { get; set; } = string.Empty;
    public string ArchiveUrl { get; set; } = string.Empty;
    /// <summary>
    /// Зеркала архива сборки: пробуются по очереди, если основной адрес не ответил. Файл везде обязан
    /// быть тем же — SHA-256 проверяется одинаково, откуда бы он ни скачался.
    /// </summary>
    public List<string> ArchiveFallbackUrls { get; set; } = [];
    public string ArchiveFileName { get; set; } = "modpack.zip";
    public string ArchiveSha256 { get; set; } = string.Empty;
    public long ArchiveSize { get; set; }
    public string StripPrefix { get; set; } = ".minecraft/";
    public bool ClientRequired { get; set; } = true;
    public bool ServerPack { get; set; }
}

public sealed class ManifestInstallInfo
{
    public string Root { get; set; } = "%AppData%\\ForgeLauncher";
    public bool CleanBeforeInstall { get; set; }
    public bool StripPrefixRequired { get; set; } = true;
    // Папки, управляемые сборкой: при ОБНОВЛЕНИИ (смене версии) лаунчер их пересоздаёт из нового
    // архива, чтобы не оставались осиротевшие/дублирующиеся моды и старые конфиги. Данные игрока
    // (saves/options/... из PreservePaths) при этом не трогаются. Не применяется при первой установке.
    public List<string> UpdateResetPaths { get; set; } =
    [
        "mods",
        "config",
        "kubejs",
        "defaultconfigs",
        "scripts"
    ];
    public List<string> PreservePaths { get; set; } =
    [
        "saves",
        "screenshots",
        "options.txt",
        "servers.dat",
        "resourcepacks",
        "shaderpacks",
        "assets",
        "libraries",
        "versions",
        ".launcher"
    ];
}

public sealed class ManifestRuntimeInfo
{
    public int JavaVersion { get; set; } = 17;
    public string JavaExecutable { get; set; } = "javaw.exe";
    /// <summary>
    /// Адрес архива Java. Исторически это всегда zip под Windows x64, поэтому на Linux и macOS
    /// он не используется — там работает <see cref="JavaRuntimeUrlsByOs"/> или запасной Adoptium.
    /// </summary>
    public string JavaRuntimeUrl { get; set; } = string.Empty;
    public List<string> JavaRuntimeFallbackUrls { get; set; } = [];
    /// <summary>
    /// Адреса архива Java по операционным системам. Ключ — <c>windows</c>, <c>linux</c> или <c>osx</c>
    /// (как в <c>HostPlatform.MojangOsName</c>). Поле необязательное: если для текущей ОС записи нет,
    /// лаунчер сам соберёт адрес Adoptium под нужные ОС и архитектуру.
    /// </summary>
    public Dictionary<string, List<string>> JavaRuntimeUrlsByOs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string MinecraftVersionManifestUrl { get; set; } = string.Empty;
    public string MinecraftVersionJsonUrl { get; set; } = string.Empty;
    public string MinecraftClientUrl { get; set; } = string.Empty;
    public string MinecraftAssetIndexUrl { get; set; } = string.Empty;
    public string MinecraftLibrariesBaseUrl { get; set; } = string.Empty;
    public string MinecraftAssetsBaseUrl { get; set; } = string.Empty;
    /// <summary>
    /// Зеркала ванильных файлов Minecraft. Каждый адрес — корень, повторяющий раскладку папки игры:
    /// <c>{mirror}/versions/{id}/{id}.json|.jar</c>, <c>{mirror}/libraries/{path}</c>,
    /// <c>{mirror}/assets/indexes/{id}.json</c>, <c>{mirror}/assets/objects/{ab}/{hash}</c>.
    /// Такой слепок делается простой заливкой рабочей установки в бакет.
    ///
    /// Зеркала пробуются ПЕРЕД серверами Mojang: у части игроков они заблокированы провайдером, и
    /// именно на этом ломалась установка (недокачанный client.jar → «Could not find or load main class»).
    /// Оригинальный адрес Mojang при этом остаётся последним запасным — падение зеркала не ломает установку.
    /// Целостность всё равно проверяется по sha1/размеру из манифеста версии, поэтому зеркалу
    /// не нужно доверять на слово.
    /// </summary>
    public List<string> MojangMirrorBaseUrls { get; set; } = [];
    public string MainVersionId { get; set; } = "1.20.1-forge-47.3.29";
    public bool RequiresInstalledRuntime { get; set; } = true;
    public bool AutoInstallForge { get; set; }
    // Готовый Forge-рантайм (zip с versions/ + libraries/). Если задан и Forge ещё не установлен —
    // лаунчер скачивает и распаковывает его вместо запуска Forge-инсталлера (полная автономия от Forge maven).
    public string PrebuiltRuntimeUrl { get; set; } = string.Empty;
    public string PrebuiltRuntimeSha256 { get; set; } = string.Empty;
    public long PrebuiltRuntimeSize { get; set; }
    public string ForgeInstallerUrl { get; set; } = string.Empty;
    public string ForgeInstallerSha256 { get; set; } = string.Empty;
    public long ForgeInstallerSize { get; set; }
    public int MemoryMbDefault { get; set; } = 4096;
    public int MemoryMbMin { get; set; } = 2048;
    public int MemoryMbMax { get; set; } = 12288;
    public List<string> JvmArgs { get; set; } = ["-XX:+UseG1GC"];
    public List<string> GameArgs { get; set; } = [];
}

public sealed class ManifestUpdateInfo
{
    public string Mode { get; set; } = "archive";
    public bool AllowDowngrade { get; set; }
    public bool ForceReinstall { get; set; }
    // URL changeset'а текущей версии (JSON: version/basedOn/update[]/delete[]). Пусто → обновление
    // всегда через полный архив (как раньше). Обновляется вместе с archiveUrl/modpackVersion на релизе.
    public string ChangesetUrl { get; set; } = string.Empty;
}

public sealed class ManifestIntegrityInfo
{
    public bool Required { get; set; } = true;
    public string HashAlgorithm { get; set; } = "sha256";
}

public sealed class ManifestUiInfo
{
    public string BackgroundImageUrl { get; set; } = string.Empty;
    public string LogoUrl { get; set; } = string.Empty;
    public string AccentColor { get; set; } = "#2A7A4B";
}
