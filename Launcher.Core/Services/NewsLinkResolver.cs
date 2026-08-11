namespace Launcher.App.Services;

/// <summary>
/// Приводит ссылки из новостей к абсолютному виду.
/// </summary>
/// <remarks>
/// В ленте адреса бывают относительными («/news/42», «images/a.png»), а открывать браузером
/// и качать картинку можно только абсолютный адрес. Правило одно на WPF и Avalonia, иначе
/// на одной из систем часть новостей осталась бы без картинки и без кнопки «Читать».
/// </remarks>
public static class NewsLinkResolver
{
    public static string Resolve(string? value, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return new Uri(baseUri, value).ToString();
        }

        return value;
    }
}
