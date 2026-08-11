namespace Launcher.App.Platform;

/// <summary>
/// Раскрывает пути из конфигурации и манифестов. Нужен из-за того, что значения вроде
/// <c>%AppData%\ForgeLauncher</c> написаны в расчёте на Windows: на Linux и macOS
/// <see cref="Environment.ExpandEnvironmentVariables"/> их не трогает, и путь превратился бы
/// в относительный каталог с буквальным именем «%AppData%\ForgeLauncher».
/// </summary>
public static class LauncherPaths
{
    /// <summary>
    /// На Windows поведение прежнее — ровно <see cref="Environment.ExpandEnvironmentVariables"/>.
    /// На Unix дополнительно подставляются windows-переменные и правится разделитель каталогов.
    /// </summary>
    public static string Expand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value ?? string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (HostPlatform.IsWindows)
        {
            return expanded;
        }

        expanded = ReplaceToken(expanded, "%AppData%", Environment.SpecialFolder.ApplicationData);
        expanded = ReplaceToken(expanded, "%LocalAppData%", Environment.SpecialFolder.LocalApplicationData);
        expanded = ReplaceToken(expanded, "%UserProfile%", Environment.SpecialFolder.UserProfile);
        expanded = ReplaceToken(expanded, "%Home%", Environment.SpecialFolder.UserProfile);

        // Тильда в начале — привычная для Unix запись домашнего каталога.
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded == "~")
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded.Length == 1 ? home : Path.Combine(home, expanded[2..]);
        }

        // Значения писались с windows-разделителем. На Unix обратный слэш — обычный символ имени
        // файла, поэтому такой путь иначе стал бы одним каталогом со слэшами внутри имени.
        return expanded.Replace('\\', '/');
    }

    /// <summary>
    /// Раскрывает путь и приводит его к абсолютному виду.
    /// </summary>
    public static string ExpandFull(string? value) => Path.GetFullPath(Expand(value));

    private static string ReplaceToken(string value, string token, Environment.SpecialFolder folder)
    {
        if (!value.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var replacement = Environment.GetFolderPath(folder);
        return string.IsNullOrEmpty(replacement)
            ? value
            : value.Replace(token, replacement, StringComparison.OrdinalIgnoreCase);
    }
}
