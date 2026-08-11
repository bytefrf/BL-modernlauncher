using System.Text.Json.Serialization;

namespace Launcher.App.Models;

/// <summary>
/// Ответ `GET /api/player.php?nick=...` — игровая статистика игрока с серверов.
/// Именно из неё сайт считает достижения, XP и уровень в кабинете.
/// </summary>
public sealed class SitePlayerResponse
{
    public bool Found { get; set; }

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>Сколько всего игроков в рейтинге (для процентилей).</summary>
    public int Total { get; set; }

    public SitePlayerStats Player { get; set; } = new();

    /// <summary>Места игрока в топах: hours/kills/kd/... → номер места.</summary>
    public Dictionary<string, double> Ranks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SitePlayerStats
{
    public string Name { get; set; } = string.Empty;
    public double Hours { get; set; }
    public double Kills { get; set; }
    public double Deaths { get; set; }
    public double Kd { get; set; }
    public double Walk { get; set; }
    public double Ride { get; set; }
    public double Jump { get; set; }
    public double Dealt { get; set; }
    public double Taken { get; set; }
    public double Mined { get; set; }
    public double Crafted { get; set; }
    public double Chests { get; set; }
    public List<string> Servers { get; set; } = [];

    [JsonPropertyName("per_server")]
    public List<SitePerServerHours> PerServer { get; set; } = [];

    /// <summary>Достаёт метрику по тому же ключу, что использует движок ачивок на сайте.</summary>
    public double GetMetric(string key) => key switch
    {
        "hours" => Hours,
        "kills" => Kills,
        "deaths" => Deaths,
        "kd" => Kd,
        "walk" => Walk,
        "ride" => Ride,
        "jump" => Jump,
        "dealt" => Dealt,
        "taken" => Taken,
        "mined" => Mined,
        "crafted" => Crafted,
        "chests" => Chests,
        _ => 0
    };
}

public sealed class SitePerServerHours
{
    public string Label { get; set; } = string.Empty;
    public double Hours { get; set; }
}
