using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Launcher.App.Models;
using Launcher.App.Platform;
using Launcher.App.Services;
using Launcher.Avalonia.Theming;

namespace Launcher.Avalonia;

/// <summary>
/// Переписка с поддержкой прямо из лаунчера.
/// </summary>
public partial class SupportWindow : Window
{
    private readonly SupportChatClient _client;
    private readonly string _apiUrl;
    private readonly UserSettings _settings;
    private readonly string _settingsPath;
    private readonly string _clientId;
    private readonly string _username;
    private readonly string _launcherVersion;
    private readonly string _modpackVersion;
    private bool _busy;

    public SupportWindow() : this(
        new SupportChatClient(new System.Net.Http.HttpClient()),
        string.Empty, new UserSettings(), string.Empty, string.Empty, "Player", "0.0.0", "0.0.0", null)
    {
    }

    public SupportWindow(
        SupportChatClient client,
        string apiUrl,
        UserSettings settings,
        string settingsPath,
        string clientId,
        string username,
        string launcherVersion,
        string modpackVersion,
        string? themeId)
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, themeId);

        _client = client;
        _apiUrl = apiUrl;
        _settings = settings;
        _settingsPath = settingsPath;
        _clientId = clientId;
        _username = username;
        _launcherVersion = launcherVersion;
        _modpackVersion = modpackVersion;

        ConfigureWindowChrome();
        this.FindControl<TextBox>("EmailTextBox")!.Text = settings.SupportEmail;

        Opened += async (_, _) => await LoadThreadAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

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

    private async Task LoadThreadAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        var status = this.FindControl<TextBlock>("StatusTextBlock")!;
        status.Text = "Загружаем переписку…";

        try
        {
            var thread = await _client.GetThreadAsync(
                _apiUrl, _clientId, _username, GetEmail(), _launcherVersion, _modpackVersion);
            RenderThread(thread);
            status.Text = string.Empty;
        }
        catch (Exception exception)
        {
            status.Text = $"Не удалось загрузить переписку: {exception.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private void RenderThread(SupportThreadDto thread)
    {
        this.FindControl<TextBlock>("TicketInfoTextBlock")!.Text =
            SupportChatPresenter.BuildTicketInfo(thread.Ticket);
        this.FindControl<ItemsControl>("MessagesItemsControl")!.ItemsSource =
            SupportChatPresenter.BuildMessages(thread);

        // Прокручиваем к последнему сообщению: интересен свежий ответ, а не начало переписки.
        this.FindControl<ScrollViewer>("MessagesScrollViewer")!.ScrollToEnd();
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e) => await LoadThreadAsync();

    private async void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var messageBox = this.FindControl<TextBox>("MessageTextBox")!;
        var status = this.FindControl<TextBlock>("StatusTextBlock")!;
        var send = this.FindControl<Button>("SendButton")!;

        var message = (messageBox.Text ?? string.Empty).Trim();
        if (message.Length == 0)
        {
            status.Text = "Напиши сообщение перед отправкой.";
            return;
        }

        _busy = true;
        send.IsEnabled = false;
        status.Text = "Отправляем…";

        try
        {
            var thread = await _client.SendMessageAsync(
                _apiUrl, _clientId, _username, GetEmail(), _launcherVersion, _modpackVersion, message);

            // Почту сохраняем локально, чтобы не вводить её каждый раз.
            _settings.SupportEmail = GetEmail();
            if (!string.IsNullOrWhiteSpace(_settingsPath))
            {
                _settings.Save(_settingsPath);
            }

            messageBox.Text = string.Empty;
            RenderThread(thread);
            status.Text = "Сообщение отправлено.";
        }
        catch (Exception exception)
        {
            status.Text = $"Не удалось отправить: {exception.Message}";
        }
        finally
        {
            _busy = false;
            send.IsEnabled = true;
        }
    }

    private string GetEmail() => (this.FindControl<TextBox>("EmailTextBox")!.Text ?? string.Empty).Trim();

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
