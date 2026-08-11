namespace Launcher.App.Services;

/// <summary>
/// Правила выбора ника. Вынесены из <c>MainWindow.xaml.cs</c>: проверка одинакова
/// для обоих интерфейсов, а разойтись они не должны — под этим ником игрока видят на сервере.
/// </summary>
public static class UsernameRules
{
    /// <summary>Ник по умолчанию. С ним запускать игру нельзя — иначе все заходят как «Player».</summary>
    public const string DefaultUsername = "Player";

    public const int MinLength = 3;

    /// <summary>Верхняя граница — ограничение самого Minecraft на длину имени профиля.</summary>
    public const int MaxLength = 16;

    public static bool IsValid(string? username)
    {
        var value = (username ?? string.Empty).Trim();
        if (value.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        if (value.Equals(DefaultUsername, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.All(symbol =>
            symbol is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.');
    }

    /// <summary>
    /// Человеческое объяснение, почему ник не подошёл. <c>null</c>, если всё в порядке.
    /// </summary>
    public static string? Describe(string? username)
    {
        var value = (username ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "Ник не может быть пустым.";
        }

        if (value.Length < MinLength)
        {
            return $"Слишком короткий ник — нужно минимум {MinLength} символа.";
        }

        if (value.Length > MaxLength)
        {
            return $"Слишком длинный ник — максимум {MaxLength} символов.";
        }

        if (value.Equals(DefaultUsername, StringComparison.OrdinalIgnoreCase))
        {
            return "«Player» — ник по умолчанию, выбери свой.";
        }

        return IsValid(value)
            ? null
            : "Разрешены латинские буквы, цифры и символы _ - . Кириллица и пробелы не подходят.";
    }
}
