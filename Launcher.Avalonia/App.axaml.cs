using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Launcher.Avalonia;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Лаунчер сворачивается в трей при запуске игры, поэтому закрытие последнего
            // окна не должно завершать приложение — иначе оно умрёт вместе со сворачиванием.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Показывает главное окно из трея.</summary>
    private void RestoreMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return;
        }

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
        SetTrayVisible(false);
    }

    public void SetTrayVisible(bool visible)
    {
        var icons = TrayIcon.GetIcons(this);
        if (icons is { Count: > 0 })
        {
            icons[0].IsVisible = visible;
        }
    }

    private void TrayIcon_Clicked(object? sender, EventArgs e) => RestoreMainWindow();

    private void TrayOpen_Click(object? sender, EventArgs e) => RestoreMainWindow();

    private void TrayExit_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SetTrayVisible(false);
            desktop.Shutdown();
        }
    }
}
