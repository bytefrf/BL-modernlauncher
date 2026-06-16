using System.Text.Json;

namespace Launcher.App.Models;

public sealed class UserSettings
{
    public string Username { get; set; } = "Player";
    public string SupportEmail { get; set; } = string.Empty;
    public string ClientId { get; set; } = Guid.NewGuid().ToString();
    public bool TelemetryEnabled { get; set; } = true;
    public string ThemeId { get; set; } = "verdant";
    public int MemoryMb { get; set; } = 4096;
    public string InstallRoot { get; set; } = string.Empty;
    public string JavaExecutable { get; set; } = string.Empty;
    public string JvmArguments { get; set; } = string.Empty;
    public string GameArguments { get; set; } = string.Empty;
    public bool UseCustomResolution { get; set; } = true;
    public int ResolutionWidth { get; set; } = 1280;
    public int ResolutionHeight { get; set; } = 720;

    public string ResolveInstallRoot(string manifestRoot, string fallbackRoot)
    {
        var root = string.IsNullOrWhiteSpace(InstallRoot)
            ? (string.IsNullOrWhiteSpace(manifestRoot) ? fallbackRoot : manifestRoot)
            : InstallRoot;

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
    }

    public static UserSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new UserSettings();
        }

        return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), JsonOptions()) ?? new UserSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions()));
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
