using Launcher.App.Models;

namespace Launcher.App.Services;

/// <param name="DisplayName">Как показывать сервер в списке.</param>
/// <param name="Host">Адрес для подключения и пинга.</param>
public sealed record GameServerDefinition(string DisplayName, string Host);

/// <summary>Состояние одного сервера для показа в списке.</summary>
/// <param name="Detail">Подпись под названием: адрес и число игроков.</param>
/// <param name="StatusText">«Онлайн» или «Недоступен».</param>
/// <param name="PlayersText">Число игроков крупно; «—», если сервер не ответил.</param>
/// <param name="PlayersCaption">Слово «игрок/игрока/игроков» под числом.</param>
public sealed record GameServerStatus(
    string Name,
    string Detail,
    string StatusText,
    string PlayersText,
    string PlayersCaption,
    bool IsOnline,
    int OnlinePlayers,
    string Host);

/// <summary>
/// Список серверов сборки и их состояние. Вынесено из <c>MainWindow.xaml.cs</c>: тексты
/// и склонения должны быть одинаковыми в обоих интерфейсах.
/// </summary>
public static class GameServerStatusService
{
    /// <summary>Запасной список, если в манифесте сборки серверов нет.</summary>
    public static readonly IReadOnlyList<GameServerDefinition> DefaultServers =
    [
        new("BL-MODERN-TFGM-1", "play.bl-modern.ru"),
        new("BL-MODERN-TFGM-2", "tfgm2.bl-modern.ru")
    ];

    /// <summary>
    /// Сервера берутся из ВЫБРАННОЙ сборки: при переключении сборки список меняется.
    /// </summary>
    public static IReadOnlyList<GameServerDefinition> GetServers(ModpackManifest? manifest)
    {
        var servers = manifest?.Servers;
        if (servers is { Count: > 0 })
        {
            var mapped = servers
                .Where(server => !string.IsNullOrWhiteSpace(server.Host))
                .Select(server => new GameServerDefinition(
                    string.IsNullOrWhiteSpace(server.Name) ? server.Host : server.Name,
                    server.Host.Trim()))
                .ToList();

            if (mapped.Count > 0)
            {
                return mapped;
            }
        }

        return DefaultServers;
    }

    /// <summary>
    /// Собирает состояние по результату пинга. <c>null</c> означает, что сервер не ответил.
    /// </summary>
    public static GameServerStatus BuildStatus(GameServerDefinition server, MinecraftPingResult? ping)
    {
        // Сервер ответил на пинг = онлайн. Не ответил = недоступен.
        if (ping is null)
        {
            return new GameServerStatus(server.DisplayName, "Не отвечает", "Недоступен", "—", string.Empty, false, 0, server.Host);
        }

        var detail = ping.PlayersMax > 0 ? $"{server.Host} · {ping.PlayersOnline}/{ping.PlayersMax}" : server.Host;
        return new GameServerStatus(
            server.DisplayName,
            detail,
            "Онлайн",
            ping.PlayersOnline.ToString(),
            PlayersCaption(ping.PlayersOnline),
            true,
            ping.PlayersOnline,
            server.Host);
    }

    /// <summary>Строка над списком: сколько всего игроков и сколько серверов онлайн.</summary>
    public static string BuildSummary(IReadOnlyCollection<GameServerStatus> items)
    {
        if (items.Count == 0)
        {
            return "Статус серверов недоступен";
        }

        if (items.All(item => !item.IsOnline) && items.All(item => item.Detail == "Сервер недоступен"))
        {
            return "Статус серверов недоступен";
        }

        var totalPlayers = items.Where(item => item.IsOnline).Sum(item => item.OnlinePlayers);
        var onlineServers = items.Count(item => item.IsOnline);
        return onlineServers > 0
            ? $"Сейчас в игре {totalPlayers} {PlayersCaption(totalPlayers)} · серверов онлайн: {onlineServers}/{items.Count}"
            : "Серверы сейчас оффлайн";
    }

    /// <summary>Склонение слова «игрок» по числу — правила русского языка, не просто «s».</summary>
    public static string PlayersCaption(int players)
    {
        var lastTwo = players % 100;
        if (lastTwo is >= 11 and <= 14)
        {
            return "игроков";
        }

        return (players % 10) switch
        {
            1 => "игрок",
            2 or 3 or 4 => "игрока",
            _ => "игроков"
        };
    }
}
