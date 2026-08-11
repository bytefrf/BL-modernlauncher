using System.Text.RegularExpressions;

namespace Launcher.App.Services;

/// <summary>
/// Убирает из текста телеметрии персональные/локальные данные: пути с именем пользователя и URL.
/// Используется и в краш-телеметрии, и при отправке хвоста логов на сервер.
/// </summary>
public static class TelemetrySanitizer
{
    public static string Sanitize(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = Regex.Replace(value, @"'[A-Za-z]:\\[^']*'", "'<path>'");
        sanitized = Regex.Replace(sanitized, @"[A-Za-z]:\\[^\s'""]+", "<path>");
        sanitized = Regex.Replace(sanitized, @"https?://\S+", "<url>");
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "...";
    }

    /// <summary>Многострочный хвост лога: чистит пути/URL, но сохраняет переносы строк.</summary>
    public static string SanitizeMultiline(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = Regex.Replace(value, @"[A-Za-z]:\\[^\s'""\r\n]+", "<path>");
        sanitized = Regex.Replace(sanitized, @"https?://\S+", "<url>");
        sanitized = sanitized.Trim();
        return sanitized.Length <= maxLength ? sanitized : sanitized[^maxLength..];
    }

    /// <summary>Верхний кадр стека из заданного пространства имён — для группировки крашей по месту.</summary>
    public static string ResolveSite(Exception? exception, string namespacePrefix)
    {
        var stack = exception?.StackTrace;
        if (string.IsNullOrEmpty(stack))
        {
            return "unknown";
        }

        foreach (var line in stack.Split('\n'))
        {
            var trimmed = line.Trim();
            var markerIndex = trimmed.IndexOf(namespacePrefix, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var start = markerIndex + namespacePrefix.Length;
            var end = trimmed.IndexOf('(', start);
            if (end > start)
            {
                return trimmed[start..end].Trim();
            }
        }

        return "unknown";
    }
}
