using Avalonia;
using Launcher.App.Platform;

namespace Launcher.Avalonia;

internal static class Program
{
    // Точка входа обязана быть до инициализации Avalonia: до вызова Start() нельзя трогать
    // ничего, что зависит от подсистемы отрисовки.
    [STAThread]
    public static void Main(string[] args)
    {
        // Профиль выбираем до всего остального: от него зависят пути к настройкам,
        // кэшу и папке установки.
        LauncherProfile.UseFromCommandLine(args);

        // --check-runtime: поставить Java по манифесту и показать её версию, без окна.
        // Нужен для проверки на настоящих Linux и macOS: там Java приезжает в tar.gz,
        // а после распаковки ей обязателен бит запуска. Через интерфейс это не проверить
        // автоматически, а ошибка здесь ломает запуск игры у каждого игрока.
        if (args.Any(argument => argument.Equals("--check-runtime", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.ExitCode = RuntimeCheck.RunAsync().GetAwaiter().GetResult();
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Используется и визуальным дизайнером — сигнатура и имя менять нельзя.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
