using System.Net.Mail;
using System.Windows;
using Launcher.App.Models;
using Launcher.App.Services;
using Launcher.App.Theming;

namespace Launcher.App;

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
    private readonly string _themeId;
    private bool _isBusy;

    public SupportWindow(
        SupportChatClient client,
        string apiUrl,
        UserSettings settings,
        string settingsPath,
        string clientId,
        string username,
        string launcherVersion,
        string modpackVersion,
        string themeId)
    {
        InitializeComponent();
        _client = client;
        _apiUrl = apiUrl;
        _settings = settings;
        _settingsPath = settingsPath;
        _clientId = clientId;
        _username = username;
        _launcherVersion = launcherVersion;
        _modpackVersion = modpackVersion;
        _themeId = themeId;
        LauncherThemeCatalog.ApplyTheme(Resources, _themeId);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        EmailTextBox.Text = _settings.SupportEmail ?? string.Empty;
        await RefreshThreadAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshThreadAsync();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1)
        {
            return;
        }

        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        var message = MessageTextBox.Text.Trim();
        var email = EmailTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            StatusTextBlock.Text = "Напиши сообщение перед отправкой.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
        {
            StatusTextBlock.Text = "Укажи корректный email или оставь поле пустым.";
            return;
        }

        await RunBusyAsync(async () =>
        {
            SaveEmail(email);
            StatusTextBlock.Text = "Отправляю сообщение...";
            var thread = await _client.SendMessageAsync(
                _apiUrl,
                _clientId,
                _username,
                email,
                _launcherVersion,
                _modpackVersion,
                message);

            MessageTextBox.Clear();
            RenderThread(thread);
            StatusTextBlock.Text = string.IsNullOrWhiteSpace(email)
                ? "Сообщение отправлено. Ответ появится в этом окне."
                : "Сообщение отправлено. Ответ появится в этом окне и может быть продублирован на email.";
        });
    }

    private async Task RefreshThreadAsync()
    {
        await RunBusyAsync(async () =>
        {
            var email = EmailTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(email) && !IsValidEmail(email))
            {
                throw new InvalidOperationException("Укажи корректный email или оставь поле пустым.");
            }

            SaveEmail(email);
            StatusTextBlock.Text = "Загружаю сообщения...";
            var thread = await _client.GetThreadAsync(
                _apiUrl,
                _clientId,
                _username,
                email,
                _launcherVersion,
                _modpackVersion);

            RenderThread(thread);
            StatusTextBlock.Text = "Готово.";
        });
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _isBusy = true;
            await action();
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = $"Ошибка поддержки: {exception.Message}";
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void RenderThread(SupportThreadDto thread)
    {
        var ticket = thread.Ticket;
        TicketInfoTextBlock.Text = ticket is null
            ? "Диалог будет создан после первого сообщения."
            : $"Обращение {ticket.TicketKey} | статус: {TranslateStatus(ticket.Status)} | обновлено: {ticket.UpdatedAt}{BuildEmailStatus(ticket.Email)}";

        if (ticket is not null && !string.IsNullOrWhiteSpace(ticket.Email) && string.IsNullOrWhiteSpace(EmailTextBox.Text))
        {
            EmailTextBox.Text = ticket.Email;
        }

        MessagesItemsControl.ItemsSource = thread.Messages
            .Select(message => new SupportMessageViewModel(
                $"{TranslateAuthor(message.AuthorType, message.AuthorName)} | {message.CreatedAt}",
                message.Message))
            .ToList();

        MessagesScrollViewer.ScrollToEnd();
    }

    private static string TranslateStatus(string status)
    {
        return status switch
        {
            "closed" => "закрыто",
            "answered" => "админ ответил",
            _ => "открыто"
        };
    }

    private static string TranslateAuthor(string authorType, string authorName)
    {
        if (authorType.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(authorName) ? "Поддержка" : $"Поддержка: {authorName}";
        }

        return "Вы";
    }

    private void SaveEmail(string email)
    {
        _settings.SupportEmail = email;
        if (!string.IsNullOrWhiteSpace(_settingsPath))
        {
            _settings.Save(_settingsPath);
        }
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            _ = new MailAddress(email);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildEmailStatus(string email)
    {
        return string.IsNullOrWhiteSpace(email) ? string.Empty : $" | email: {email}";
    }

    private sealed record SupportMessageViewModel(string Header, string Message);
}
