namespace Launcher.App.Models;

public sealed class SupportTicketDto
{
    public string TicketKey { get; set; } = string.Empty;
    public string Status { get; set; } = "open";
    public string Subject { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    // Поля Email здесь намеренно нет: сервер не возвращает адрес игрока обратно в лаунчер, чтобы
    // он не подставлялся в поле у того, кто открыл поддержку. Адрес хранится только локально.
    public string LauncherVersion { get; set; } = string.Empty;
    public string ModpackVersion { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public int UnreadAdminMessages { get; set; }
    public int UnreadPlayerMessages { get; set; }
}

public sealed class SupportMessageDto
{
    public string AuthorType { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

public sealed class SupportThreadDto
{
    public SupportTicketDto? Ticket { get; set; }
    public List<SupportMessageDto> Messages { get; set; } = [];
}
