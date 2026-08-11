namespace Launcher.App.Platform;

/// <summary>
/// Изолированный профиль лаунчера для проверок: свои настройки, свой кэш и своя папка установки.
/// Включается ключом командной строки <c>--profile=&lt;имя&gt;</c>.
/// </summary>
/// <remarks>
/// Заведён, чтобы можно было гонять установку и запуск игры, не трогая рабочую сборку игрока.
/// Без ключа поведение остаётся ровно прежним — те же пути, что и всегда.
/// Изолируется и папка установки: обычный путь берётся из настроек игрока или из манифеста
/// (<c>%AppData%\ForgeLauncher</c>), и без подмены проверочный запуск ушёл бы в рабочую папку.
/// </remarks>
public static class LauncherProfile
{
    private const string DefaultFolderName = "ForgeLauncher";

    /// <summary>Имя профиля или <c>null</c>, если работаем в обычном режиме.</summary>
    public static string? Name { get; private set; }

    public static bool IsIsolated => !string.IsNullOrWhiteSpace(Name);

    /// <summary>
    /// Имя папки с данными лаунчера внутри каталога приложений пользователя.
    /// </summary>
    public static string DataFolderName => IsIsolated ? $"{DefaultFolderName}-{Name}" : DefaultFolderName;

    /// <summary>
    /// Куда ставить игру в изолированном профиле. Для обычного режима не используется.
    /// </summary>
    public static string IsolatedInstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        DataFolderName,
        "game");

    /// <summary>
    /// Разбирает <c>--profile=&lt;имя&gt;</c> из аргументов запуска. Вызывать до чтения
    /// конфигурации и настроек, иначе пути успеют вычислиться по обычным правилам.
    /// </summary>
    public static void UseFromCommandLine(IEnumerable<string> args)
    {
        foreach (var argument in args)
        {
            const string prefix = "--profile=";
            if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Use(argument[prefix.Length..]);
            return;
        }
    }

    public static void Use(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            Name = null;
            return;
        }

        // Имя попадает в путь, поэтому чистим его от разделителей и прочего мусора.
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        Name = trimmed.Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(Name))
        {
            Name = null;
        }
    }
}
