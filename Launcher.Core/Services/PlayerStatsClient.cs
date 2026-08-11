using System.Net.Http;
using System.Text.Json;
using Launcher.App.Models;

namespace Launcher.App.Services;

/// <summary>
/// Игровая статистика игрока с сайта (`/api/player.php?nick=...`) — источник правды для
/// достижений и уровня в кабинете. Эндпоинт публичный, авторизация не нужна: сайт по нему же
/// рисует открытые профили.
/// </summary>
public sealed class PlayerStatsClient(HttpClient httpClient)
{
    public const string DefaultEndpoint = "https://bl-modern.ru/api/player.php";

    public async Task<SitePlayerResponse?> GetAsync(string nickname, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return null;
        }

        var url = $"{DefaultEndpoint}?nick={Uri.EscapeDataString(nickname.Trim())}";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<SitePlayerResponse>(stream, JsonOptions(), cancellationToken);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
}
