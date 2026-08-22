using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Launcher.App.Platform;
using Launcher.Avalonia.Theming;

namespace Launcher.Avalonia;

/// <summary>
/// Простое окно «объяснили и предложили исправить». В WPF эту роль играет системный MessageBox,
/// в Avalonia его нет вообще, поэтому окно своё — иначе Linux и macOS остались бы без предупреждений.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() : this("Подтверждение", string.Empty, string.Empty, null)
    {
    }

    public ConfirmWindow(string title, string headline, string message, string? themeId, string acceptText = "Исправить", string declineText = "Оставить как есть")
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, themeId);
        ConfigureWindowChrome();

        Title = title;
        this.FindControl<TextBlock>("TitleTextBlock")!.Text = title;
        this.FindControl<TextBlock>("HeadlineTextBlock")!.Text = headline;
        this.FindControl<TextBlock>("MessageTextBlock")!.Text = message;
        this.FindControl<Button>("AcceptButton")!.Content = acceptText;
        this.FindControl<Button>("DeclineButton")!.Content = declineText;
    }

    /// <summary>true, если игрок согласился с предложенным исправлением.</summary>
    public bool Accepted { get; private set; }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Своя рамка на Windows, системная на Linux и macOS — как и у остальных окон.</summary>
    private void ConfigureWindowChrome()
    {
        var titleBar = this.FindControl<Border>("CustomTitleBar")!;
        if (HostPlatform.IsWindows)
        {
            SystemDecorations = SystemDecorations.BorderOnly;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            titleBar.IsVisible = true;
            return;
        }

        SystemDecorations = SystemDecorations.Full;
        ExtendClientAreaToDecorationsHint = false;
        titleBar.IsVisible = false;
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void AcceptButton_Click(object? sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void DeclineButton_Click(object? sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }
}
