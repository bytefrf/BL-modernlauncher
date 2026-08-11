using System.Net.Http;
using System.Text.Json;
using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed class CatalogClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<CatalogManifest> GetCatalogAsync(string catalogUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogUrl))
        {
            throw new InvalidOperationException("Catalog URL is not configured.");
        }

        var uri = new Uri(catalogUrl, UriKind.Absolute);
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var catalog = await JsonSerializer.DeserializeAsync<CatalogManifest>(stream, JsonOptions, cancellationToken)
                      ?? throw new InvalidOperationException("Catalog response was empty.");

        catalog.SourceUri = uri;
        catalog.Modpacks = catalog.Modpacks
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.ManifestUrl))
            .OrderBy(entry => entry.Order)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return catalog;
    }
}
