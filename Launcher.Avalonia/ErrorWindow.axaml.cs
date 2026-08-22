using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Launcher.App;
using Launcher.App.Platform;
using Launcher.Avalonia.Theming;
using Path = System.IO.Path;

namespace Launcher.Avalonia;

/// <summary>
/// Человеческий разбор ошибки: заголовок, что случилось, что сделать, и технические детали
/// под спойлером. Разбор делает общий с WPF <see cref="ErrorClassifier"/>.
/// </summary>
public partial class ErrorWindow : Window
{
    private readonly ErrorInfo _errorInfo;
    private readonly string _logPath;
    private readonly Func<Task<SupportLogSendResult>>? _sendLogAsync;
    private Func<string>? _repairAction;

    public ErrorWindow() : this(
        new ErrorInfo("Ошибка", string.Empty, [], string.Empty), string.Empty, null, null)
    {
    }

    public ErrorWindow(
        ErrorInfo errorInfo,
        string logPath,
        string? themeId,
        Func<Task<SupportLogSendResult>>? sendLogAsync = null)
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, themeId);

        _errorInfo = errorInfo;
        _logPath = logPath;
        _sendLogAsync = sendLogAsync;

        ConfigureWindowChrome();

        this.FindControl<TextBlock>("TitleTextBlock")!.Text = errorInfo.Title;
        this.FindControl<TextBlock>("SummaryTextBlock")!.Text = errorInfo.Summary;
        this.FindControl<ItemsControl>("ActionsItemsControl")!.ItemsSource = errorInfo.Actions;
        this.FindControl<TextBox>("TechnicalTextBox")!.Text = errorInfo.TechnicalDetails;
        this.FindControl<TextBlock>("LogPathTextBlock")!.Text = string.IsNullOrWhiteSpace(logPath)
            ? string.Empty
            : $"Лог сохранен: {logPath}";

        this.FindControl<Button>("OpenLogsButton")!.IsEnabled =
            !string.IsNullOrWhiteSpace(logPath) && Directory.Exists(Path.GetDirectoryName(logPath));
        this.FindControl<Button>("SendLogButton")!.IsEnabled = _sendLogAsync is not null;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Своя рамка на Windows, системная на Linux и macOS: окно без нативных кнопок ломает
    /// жесты оконного менеджера и лишает macOS «светофора».
    /// </summary>
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

    /// <summary>
    /// Включает кнопку «Починить». Результат показывается прямо в окне: игроку не нужен ещё один
    /// диалог поверх этого. Паритет с WPF-версией.
    /// </summary>
    public void EnableRepair(string buttonText, Func<string> repair)
    {
        _repairAction = repair;
        var button = this.FindControl<Button>("RepairButton")!;
        button.Content = buttonText;
        button.IsVisible = true;
    }

    private void RepairButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_repairAction is null)
        {
            return;
        }

        var button = this.FindControl<Button>("RepairButton")!;
        button.IsEnabled = false;
        this.FindControl<TextBlock>("SupportStatusTextBlock")!.Text = _repairAction();
        _repairAction = null;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private async void CopyButton_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            await clipboard.SetTextAsync(ErrorReport.BuildClipboardText(_errorInfo, _logPath));
            SetSupportStatus("Текст ошибки скопирован.");
        }
        catch (Exception exception)
        {
            // На Linux буфер обмена держит оконный менеджер: без него копирование недоступно,
            // но окно ошибки должно продолжать работать.
            SetSupportStatus($"Не удалось скопировать: {exception.Message}");
        }
    }

    private void OpenLogsButton_Click(object? sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_logPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            // UseShellExecute открывает папку системным файловым менеджером на всех трёх ОС.
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetSupportStatus($"Не удалось открыть папку логов: {exception.Message}");
        }
    }

    private async void SendLogButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_sendLogAsync is null)
        {
            return;
        }

        var button = this.FindControl<Button>("SendLogButton")!;
        button.IsEnabled = false;
        SetSupportStatus("Собираю и отправляю лог...");

        try
        {
            var result = await _sendLogAsync();
            SetSupportStatus(result.Message);

            if (!string.IsNullOrWhiteSpace(result.PackagePath))
            {
                var logPathText = this.FindControl<TextBlock>("LogPathTextBlock")!;
                logPathText.Text = $"{logPathText.Text}{Environment.NewLine}Архив поддержки: {result.PackagePath}";
            }
        }
        catch (Exception exception)
        {
            SetSupportStatus($"Не удалось собрать лог: {exception.Message}");
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void SetSupportStatus(string text)
        => this.FindControl<TextBlock>("SupportStatusTextBlock")!.Text = text;
}
