using System.Net.Http;
using System.Text.Json;

namespace Launcher.App.Services;

public sealed record SkinInfo(bool HasSkin, string Model, string? Url);

public sealed record SkinOperationResult(bool Success, string Message, string Model);

/// <summary>
/// Скины игроков через тот же API, что использует кабинет на сайте (`/api/skin.php`).
///
/// GET  ?name=&lt;ник&gt;        — PNG скина, публично, без авторизации.
/// POST action=info          — есть ли скин и какая модель, тоже без пароля.
/// POST action=upload|reset  — ТРЕБУЕТ пароль от аккаунта игрока (сервер сверяет его с БД).
///
/// Пароль используется только для одного запроса и НИКУДА не сохраняется: ни в настройки, ни в
/// логи, ни в телеметрию, ни в пакет саппорт-логов. Передаётся только по HTTPS.
/// </summary>
public sealed class SkinClient(HttpClient httpClient)
{
    public const string DefaultEndpoint = "https://bl-modern.ru/api/skin.php";

    /// <summary>Требования сервера к файлу скина (см. skin.php).</summary>
    public const int MaxSkinBytes = 250 * 1024;

    public static bool IsValidSkinSize(int width, int height) => width == 64 && height is 64 or 32;

    /// <summary>Скачивает PNG скина. null — скина нет (404) или сеть недоступна.</summary>
    public async Task<byte[]?> GetSkinAsync(string nickname, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return null;
        }

        var url = $"{DefaultEndpoint}?name={Uri.EscapeDataString(nickname.Trim())}&t={DateTime.UtcNow.Ticks}";
        using var response = await httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<SkinInfo?> GetInfoAsync(string nickname, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent("info"), "action" },
            { new StringContent(nickname), "username" }
        };

        using var response = await httpClient.PostAsync(DefaultEndpoint, form, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            return null;
        }

        return new SkinInfo(
            root.TryGetProperty("has_skin", out var has) && has.ValueKind == JsonValueKind.True,
            root.TryGetProperty("model", out var model) ? model.GetString() ?? "classic" : "classic",
            root.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null);
    }

    /// <summary>Загружает скин. Пароль нужен серверу для проверки и нигде не сохраняется.</summary>
    public Task<SkinOperationResult> UploadAsync(
        string nickname,
        string password,
        string model,
        byte[] pngBytes,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        var file = new ByteArrayContent(pngBytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

        var form = new MultipartFormDataContent
        {
            { new StringContent("upload"), "action" },
            { new StringContent(nickname), "username" },
            { new StringContent(password), "current_password" },
            { new StringContent(model == "slim" ? "slim" : "classic"), "model" },
            { file, "skin", string.IsNullOrWhiteSpace(fileName) ? "skin.png" : fileName }
        };

        return SendAsync(form, cancellationToken);
    }

    public Task<SkinOperationResult> ResetAsync(string nickname, string password, CancellationToken cancellationToken = default)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("reset"), "action" },
            { new StringContent(nickname), "username" },
            { new StringContent(password), "current_password" }
        };

        return SendAsync(form, cancellationToken);
    }

    private async Task<SkinOperationResult> SendAsync(MultipartFormDataContent form, CancellationToken cancellationToken)
    {
        using (form)
        {
            using var response = await httpClient.PostAsync(DefaultEndpoint, form, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                return new SkinOperationResult(
                    root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True,
                    root.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("model", out var model) ? model.GetString() ?? "classic" : "classic");
            }
            catch (JsonException)
            {
                return new SkinOperationResult(false, "Сервер вернул неожиданный ответ.", "classic");
            }
        }
    }
}
