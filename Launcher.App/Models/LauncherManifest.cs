using System.Text.Json.Serialization;

namespace Launcher.App.Models;

public sealed class LauncherManifest
{
    public LauncherUpdateInfo Launcher { get; set; } = new();
    public GameDistributionInfo Game { get; set; } = new();

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
            throw new InvalidOperationException("Невозможно построить относительный URL без адреса манифеста.");
        }

        return new Uri(SourceUri, value);
    }
}

public sealed class LauncherUpdateInfo
{
    public string Version { get; set; } = "1.0.0";
    public string PackageUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class GameDistributionInfo
{
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string MainVersionId { get; set; } = "forge-1.20.1";
    public string JavaExecutable { get; set; } = "javaw.exe";
    public bool DeleteOrphans { get; set; }
    public List<string> PreservePaths { get; set; } = ["saves", "screenshots", "logs", "resourcepacks", "shaderpacks"];
    public List<string> JavaArguments { get; set; } = [];
    public List<string> GameArguments { get; set; } = [];
    public List<DistributionFile> Files { get; set; } = [];
}

public sealed class DistributionFile
{
    public string Path { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long Size { get; set; }
}
