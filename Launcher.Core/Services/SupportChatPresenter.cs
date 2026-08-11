using Launcher.App.Models;

namespace Launcher.App.Services;

/// <param name="Header">Кто написал и когда.</param>
public sealed record SupportMessageLine(string Header, string Message);

/// <summary>
/// Готовит переписку с поддержкой к показу. Вынесено из окна: подписи и переводы статусов
/// должны быть одинаковыми в обоих интерфейсах.
/// </summary>
public static class SupportChatPresenter
{
    public static string TranslateStatus(string status) => status switch
    {
        "closed" => "закрыто",
        "answered" => "админ ответил",
        _ => "открыто"
    };

    public static string TranslateAuthor(string authorType, string authorName)
    {
        if (authorType.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(authorName) ? "Поддержка" : $"Поддержка: {authorName}";
        }

        return "Вы";
    }

    /// <summary>
    /// Шапка обращения. Почту сюда НЕ выводим и с сервера не подставляем: адрес — личные данные,
    /// он живёт только в настройках на компьютере игрока и в поле ввода, которое игрок видит сам.
    /// </summary>
    public static string BuildTicketInfo(SupportTicketDto? ticket)
        => ticket is null
            ? "Диалог будет создан после первого сообщения."
            : $"Обращение {ticket.TicketKey} | статус: {TranslateStatus(ticket.Status)} | обновлено: {ticket.UpdatedAt}";

    public static IReadOnlyList<SupportMessageLine> BuildMessages(SupportThreadDto thread)
        => thread.Messages
            .Select(message => new SupportMessageLine(
                $"{TranslateAuthor(message.AuthorType, message.AuthorName)} | {message.CreatedAt}",
                message.Message))
            .ToList();
}
