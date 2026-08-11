using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Launcher.App.Services;

public sealed class TelemetryClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task SendEventAsync(
        string telemetryUrl,
        string clientId,
        string launcherVersion,
        string modpackVersion,
        string osVersion,
        string eventName,
        Dictionary<string, object?>? properties = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(telemetryUrl) ||
            string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(launcherVersion) ||
            string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        var payload = new TelemetryEnvelope(
            clientId,
            launcherVersion,
            modpackVersion,
            osVersion,
            [
                new TelemetryEvent(
                    eventName,
                    DateTime.UtcNow,
                    properties)
            ]);

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(telemetryUrl, content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private sealed record TelemetryEnvelope(
        string ClientId,
        string LauncherVersion,
        string ModpackVersion,
        string OsVersion,
        IReadOnlyList<TelemetryEvent> Events);

    private sealed record TelemetryEvent(
        string Name,
        DateTime Timestamp,
        Dictionary<string, object?>? Properties);
}
