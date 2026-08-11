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

        expanded = ReplaceToken(expanded, "%AppData%", GetApplicationDataRoot());
        expanded = ReplaceToken(expanded, "%LocalAppData%", GetLocalApplicationDataRoot());
        expanded = ReplaceToken(expanded, "%UserProfile%", GetHomeRoot());
        expanded = ReplaceToken(expanded, "%Home%", GetHomeRoot());

        // Тильда в начале — привычная для Unix запись домашнего каталога.
        if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded == "~")
        {
            var home = GetHomeRoot();
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

    /// <summary>
    /// Каталог данных приложений (аналог <c>%AppData%</c>). Всегда возвращает непустой путь.
    /// </summary>
    /// <remarks>
    /// НЕ заменять на голый <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>:
    /// на живой Ubuntu он вернул ПУСТУЮ строку (пустой <c>XDG_CONFIG_HOME</c>), после чего
    /// <c>%AppData%\ForgeLauncher</c> превращался в папку с буквальным именем «%AppData%»
    /// рядом с исполняемым файлом. Найдено первым запуском на настоящем Linux.
    /// </remarks>
    public static string GetApplicationDataRoot()
        => ResolveFolder(Environment.SpecialFolder.ApplicationData, "XDG_CONFIG_HOME", ".config");

    /// <summary>Каталог локальных данных (аналог <c>%LocalAppData%</c>). Всегда непустой.</summary>
    public static string GetLocalApplicationDataRoot()
        => ResolveFolder(Environment.SpecialFolder.LocalApplicationData, "XDG_DATA_HOME", ".local/share");

    /// <summary>Домашний каталог пользователя. Всегда непустой.</summary>
    public static string GetHomeRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        home = Environment.GetEnvironmentVariable("HOME");
        return string.IsNullOrWhiteSpace(home) ? Path.GetTempPath() : home;
    }

    private static string ResolveFolder(Environment.SpecialFolder folder, string xdgVariable, string fallbackSubdirectory)
    {
        var value = Environment.GetFolderPath(folder);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        if (HostPlatform.IsWindows)
        {
            // На Windows это означало бы сломанный профиль; лучше вернуть хоть что-то рабочее,
            // чем относительный путь рядом с exe.
            return Path.Combine(GetHomeRoot(), "AppData", "Roaming");
        }

        var xdg = Environment.GetEnvironmentVariable(xdgVariable);
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return xdg;
        }

        return Path.Combine(GetHomeRoot(), fallbackSubdirectory.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ReplaceToken(string value, string token, string replacement)
    {
        if (!value.Contains(token, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(replacement))
        {
            return value;
        }

        return value.Replace(token, replacement, StringComparison.OrdinalIgnoreCase);
    }
}
