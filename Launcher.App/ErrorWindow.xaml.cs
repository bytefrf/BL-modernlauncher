using System.Diagnostics;
using System.Text;
using System.Windows;
using Launcher.App.Theming;

namespace Launcher.App;

public partial class ErrorWindow : Window
{
    private readonly ErrorInfo _errorInfo;
    private readonly string _logPath;
    private readonly Func<Task<SupportLogSendResult>>? _sendLogAsync;
    private readonly string _themeId;

    public ErrorWindow(ErrorInfo errorInfo, string logPath, string themeId, Func<Task<SupportLogSendResult>>? sendLogAsync = null)
    {
        InitializeComponent();
        _errorInfo = errorInfo;
        _logPath = logPath;
        _themeId = themeId;
        _sendLogAsync = sendLogAsync;
        LauncherThemeCatalog.ApplyTheme(Resources, _themeId);

        TitleTextBlock.Text = errorInfo.Title;
        SummaryTextBlock.Text = errorInfo.Summary;
        ActionsItemsControl.ItemsSource = errorInfo.Actions;
        TechnicalTextBox.Text = errorInfo.TechnicalDetails;
        LogPathTextBlock.Text = string.IsNullOrWhiteSpace(logPath)
            ? string.Empty
            : $"Лог сохранен: {logPath}";
        OpenLogsButton.IsEnabled = !string.IsNullOrWhiteSpace(logPath) && Directory.Exists(Path.GetDirectoryName(logPath));
        SendLogButton.IsEnabled = _sendLogAsync is not null;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder()
            .AppendLine(_errorInfo.Title)
            .AppendLine()
            .AppendLine(_errorInfo.Summary)
            .AppendLine()
            .AppendLine("Что сделать:")
            .AppendLine(string.Join(Environment.NewLine, _errorInfo.Actions.Select(action => "- " + action)))
            .AppendLine()
            .AppendLine("Лог:")
            .AppendLine(_logPath)
            .AppendLine()
            .AppendLine("Технические детали:")
            .AppendLine(_errorInfo.TechnicalDetails)
            .ToString();

        System.Windows.Clipboard.SetText(text);
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_logPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private async void SendLogButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sendLogAsync is null)
        {
            return;
        }

        SendLogButton.IsEnabled = false;
        SupportStatusTextBlock.Text = "Собираю и отправляю лог...";

        try
        {
            var result = await _sendLogAsync();
            SupportStatusTextBlock.Text = result.Message;
            if (!string.IsNullOrWhiteSpace(result.PackagePath))
            {
                LogPathTextBlock.Text = $"{LogPathTextBlock.Text}{Environment.NewLine}Архив поддержки: {result.PackagePath}";
            }
        }
        catch (Exception exception)
        {
            SupportStatusTextBlock.Text = $"Не удалось собрать лог: {exception.Message}";
        }
        finally
        {
            SendLogButton.IsEnabled = true;
        }
    }
}

public sealed record SupportLogSendResult(bool Sent, string PackagePath, string Message);
