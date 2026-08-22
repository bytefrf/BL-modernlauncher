using System.Windows;
using System.Windows.Input;
using Launcher.App.Theming;

namespace Launcher.App;

/// <summary>
/// Окно «объяснили и предложили исправить». Системный MessageBox выглядит чужеродно рядом с
/// остальным лаунчером, поэтому новые предупреждения показываются этим окном.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string headline, string message, string themeId,
        string acceptText = "Исправить", string declineText = "Оставить как есть")
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, themeId);

        Title = title;
        TitleTextBlock.Text = title;
        HeadlineTextBlock.Text = headline;
        MessageTextBlock.Text = message;
        AcceptButton.Content = acceptText;
        DeclineButton.Content = declineText;
    }

    /// <summary>true, если игрок согласился с предложенным исправлением.</summary>
    public bool Accepted { get; private set; }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void AcceptButton_Click(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void DeclineButton_Click(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }
}
