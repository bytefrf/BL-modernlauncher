using System.Net.Http;
using System.Text.Json;
using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed class ServerStatsClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<ServerStats?> GetStatsAsync(string statsUrl, string serverKey = "", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(statsUrl))
        {
            return null;
        }

        var requestUrl = BuildRequestUrl(statsUrl, serverKey);
        using var response = await httpClient.GetAsync(new Uri(requestUrl, UriKind.Absolute), cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<ServerStats>(stream, JsonOptions, cancellationToken);
    }

    private static string BuildRequestUrl(string statsUrl, string serverKey)
    {
        if (string.IsNullOrWhiteSpace(serverKey))
        {
            return statsUrl;
        }

        var separator = statsUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{statsUrl}{separator}server={Uri.EscapeDataString(serverKey)}";
    }
}
