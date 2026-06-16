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

public sealed class ManifestLauncherInfo
{
    public string Title { get; set; } = "Forge Launcher";
    public string NewsUrl { get; set; } = string.Empty;
    public string SupportUrl { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
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
    public string JavaRuntimeUrl { get; set; } = string.Empty;
    public List<string> JavaRuntimeFallbackUrls { get; set; } = [];
    public string MinecraftVersionManifestUrl { get; set; } = string.Empty;
    public string MinecraftVersionJsonUrl { get; set; } = string.Empty;
    public string MinecraftClientUrl { get; set; } = string.Empty;
    public string MinecraftAssetIndexUrl { get; set; } = string.Empty;
    public string MinecraftLibrariesBaseUrl { get; set; } = string.Empty;
    public string MinecraftAssetsBaseUrl { get; set; } = string.Empty;
    public string MainVersionId { get; set; } = "1.20.1-forge-47.3.29";
    public bool RequiresInstalledRuntime { get; set; } = true;
    public bool AutoInstallForge { get; set; }
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
