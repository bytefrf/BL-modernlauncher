using System.Net.Http;
using System.Text;
using System.Text.Json;
using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed class SupportChatClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<SupportThreadDto> GetThreadAsync(
        string apiUrl,
        string clientId,
        string username,
        string email,
        string launcherVersion,
        string modpackVersion,
        CancellationToken cancellationToken = default)
    {
        var response = await PostAsync(apiUrl, new
        {
            action = "player_get",
            clientId,
            username,
            email,
            launcherVersion,
            modpackVersion
        }, cancellationToken);

        return ReadThread(response);
    }

    public async Task<SupportThreadDto> SendMessageAsync(
        string apiUrl,
        string clientId,
        string username,
        string email,
        string launcherVersion,
        string modpackVersion,
        string message,
        CancellationToken cancellationToken = default)
    {
        var response = await PostAsync(apiUrl, new
        {
            action = "player_send",
            clientId,
            username,
            email,
            launcherVersion,
            modpackVersion,
            message
        }, cancellationToken);

        return ReadThread(response);
    }

    private async Task<JsonDocument> PostAsync(string apiUrl, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(apiUrl, content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        var document = JsonDocument.Parse(responseText);
        if (!response.IsSuccessStatusCode)
        {
            var message = document.RootElement.TryGetProperty("message", out var errorMessage)
                ? errorMessage.GetString()
                : $"Сервер поддержки вернул HTTP {(int)response.StatusCode}.";
            throw new InvalidOperationException(message);
        }

        if (document.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean())
        {
            var message = document.RootElement.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : "Сервер поддержки вернул ошибку.";
            throw new InvalidOperationException(message);
        }

        return document;
    }

    private static SupportThreadDto ReadThread(JsonDocument document)
    {
        using (document)
        {
            if (!document.RootElement.TryGetProperty("thread", out var threadElement))
            {
                return new SupportThreadDto();
            }

            return threadElement.Deserialize<SupportThreadDto>(JsonOptions) ?? new SupportThreadDto();
        }
    }
}
