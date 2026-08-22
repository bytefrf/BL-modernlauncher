namespace Launcher.App.Services;

/// <summary>
/// Список последних ников для быстрого переключения.
/// </summary>
/// <remarks>
/// Поле ника было одно на весь лаунчер, и семьи с двумя-тремя игроками на одном компьютере
/// перепечатывали его руками каждый раз. Храним небольшую историю: последний использованный —
/// первым, дубли схлопываем без учёта регистра.
/// </remarks>
public static class UsernameHistory
{
    /// <summary>Больше пяти в меню уже не список, а свалка.</summary>
    public const int MaxEntries = 5;

    /// <summary>
    /// Возвращает новую историю с <paramref name="username"/> во главе.
    /// Пустые и служебные значения не запоминаются.
    /// </summary>
    public static List<string> Remember(IEnumerable<string>? history, string? username)
    {
        var existing = Normalize(history);
        var name = (username ?? string.Empty).Trim();

        // «Player» — заглушка по умолчанию, а не выбор игрока: в историю ей не место.
        if (name.Length == 0 || name.Equals("Player", StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        var result = new List<string> { name };
        foreach (var entry in existing)
        {
            if (!entry.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(entry);
            }
        }

        return result.Count > MaxEntries ? result[..MaxEntries] : result;
    }

    /// <summary>Ники для меню: без текущего, без пустот и дублей.</summary>
    public static List<string> ForMenu(IEnumerable<string>? history, string? currentUsername)
    {
        var current = (currentUsername ?? string.Empty).Trim();
        return Normalize(history)
            .Where(entry => !entry.Equals(current, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<string> Normalize(IEnumerable<string>? history)
    {
        if (history is null)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var raw in history)
        {
            var entry = (raw ?? string.Empty).Trim();
            if (entry.Length == 0 || result.Any(existing => existing.Equals(entry, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(entry);
            if (result.Count == MaxEntries)
            {
                break;
            }
        }

        return result;
    }
}
