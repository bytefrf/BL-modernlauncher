using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Launcher.App.Services;

/// <summary>
/// Минимальный синхронный отправитель краш-телеметрии для контекстов, где обычный путь недоступен:
/// глобальные обработчики необработанных исключений App (процесс вот-вот завершится, fire-and-forget
/// не успеет уйти). clientId и согласие на телеметрию читаются из user-settings.json. Тот же endpoint
/// и тот же формат конверта, что у <see cref="TelemetryClient"/>.
/// </summary>
public static class CrashTelemetryService
{
    private const string TelemetryEndpoint = "https://bl-modern.ru/api/telemetry.php";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void TrySendBlocking(
        string eventName,
        Dictionary<string, object?> properties,
        string launcherVersion,
        string modpackVersion = "unknown")
    {
        try
        {
            var (clientId, telemetryEnabled) = ReadIdentity();
            if (!telemetryEnabled || string.IsNullOrWhiteSpace(clientId))
            {
                return;
            }

            var envelope = new
            {
                clientId,
                launcherVersion = string.IsNullOrWhiteSpace(launcherVersion) ? "unknown" : launcherVersion,
                modpackVersion = string.IsNullOrWhiteSpace(modpackVersion) ? "unknown" : modpackVersion,
                osVersion = Environment.OSVersion.VersionString,
                events = new[]
                {
                    new { name = eventName, timestamp = DateTime.UtcNow, properties }
                }
            };

            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            client.PostAsync(TelemetryEndpoint, content).GetAwaiter().GetResult();
        }
        catch
        {
            // Телеметрия краша не должна мешать обработке самого краша.
        }
    }

    private static (string ClientId, bool TelemetryEnabled) ReadIdentity()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ForgeLauncher", ".launcher", "user-settings.json");
            if (File.Exists(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                var root = document.RootElement;
                var enabled = !root.TryGetProperty("telemetryEnabled", out var enabledElement) || enabledElement.GetBoolean();
                var clientId = root.TryGetProperty("clientId", out var idElement) ? idElement.GetString() : null;
                return (clientId ?? string.Empty, enabled);
            }
        }
        catch
        {
        }

        return (string.Empty, false);
    }
}
