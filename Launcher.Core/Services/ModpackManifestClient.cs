using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed class ModpackManifestClient(HttpClient httpClient)
{
    public async Task<ModpackManifest> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            throw new InvalidOperationException("Modpack manifest URL is not configured.");
        }

        if (File.Exists(Environment.ExpandEnvironmentVariables(manifestUrl)))
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(manifestUrl));
            await using var file = File.OpenRead(path);
            var localManifest = await JsonSerializer.DeserializeAsync<ModpackManifest>(file, JsonOptions(), cancellationToken) ??
                                throw new InvalidOperationException("Local modpack manifest is empty.");
            localManifest.SourceUri = new Uri(path);
            return localManifest;
        }

        var uri = new Uri(manifestUrl, UriKind.Absolute);
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<ModpackManifest>(stream, JsonOptions(), cancellationToken) ??
                       throw new InvalidOperationException("Remote modpack manifest is empty.");
        manifest.SourceUri = uri;
        return manifest;
    }

    public async Task<ModpackManifest> GetEmbeddedDefaultManifestAsync(CancellationToken cancellationToken = default)
    {
        const string resourceName = "Launcher.App.Defaults.modpack-manifest.json";
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName) ??
                                 throw new InvalidOperationException("Embedded modpack manifest was not found.");
        var manifest = await JsonSerializer.DeserializeAsync<ModpackManifest>(stream, JsonOptions(), cancellationToken) ??
                       throw new InvalidOperationException("Embedded modpack manifest is empty.");
        manifest.SourceUri = new Uri("embedded://modpack-manifest.json");
        return manifest;
    }

    public async Task<ModpackManifest> GetCachedManifestAsync(string cachePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
        {
            throw new FileNotFoundException("Cached modpack manifest was not found.", cachePath);
        }

        await using var file = File.OpenRead(cachePath);
        var manifest = await JsonSerializer.DeserializeAsync<ModpackManifest>(file, JsonOptions(), cancellationToken) ??
                       throw new InvalidOperationException("Cached modpack manifest is empty.");
        manifest.SourceUri = new Uri(Path.GetFullPath(cachePath));
        return manifest;
    }

    public async Task SaveManifestCacheAsync(ModpackManifest manifest, string cachePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cachePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        await using var file = File.Create(cachePath);
        await JsonSerializer.SerializeAsync(file, manifest, JsonOptions(), cancellationToken);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }
}
