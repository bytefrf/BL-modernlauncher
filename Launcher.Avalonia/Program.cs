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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Используется и визуальным дизайнером — сигнатура и имя менять нельзя.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
