using System.Net.Http;
using System.Text.Json;
using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed class ManifestClient(HttpClient httpClient)
{
    public async Task<LauncherManifest> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        var manifestUri = new Uri(manifestUrl, UriKind.Absolute);
        using var response = await httpClient.GetAsync(manifestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<LauncherManifest>(stream, JsonOptions(), cancellationToken) ??
                       throw new InvalidOperationException("Сервер вернул пустой манифест.");

        manifest.SourceUri = manifestUri;
        return manifest;
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }
}
