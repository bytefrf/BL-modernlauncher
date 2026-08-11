using System.Net.Http;
using System.Text.Json;
using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed class ManifestClient(HttpClient httpClient)
{
    public async Task<LauncherManifest> GetManifestAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        // Пустой URL = каталог не ответил и запасной манифест в конфиге не задан. Без этой проверки
        // игрок получал голое UriFormatException («Invalid URI: The URI is empty») по нажатию «Играть».
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            throw new InvalidOperationException(
                "Не удалось получить данные сборки: сервер не ответил, а запасной адрес манифеста не настроен. Проверь интернет и попробуй ещё раз.");
        }

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
