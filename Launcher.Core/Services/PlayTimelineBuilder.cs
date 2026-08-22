using Launcher.App.Models;

namespace Launcher.App.Services;

/// <summary>Один столбик графика: день и сколько в него сыграно.</summary>
/// <param name="HeightFraction">Высота столбика от 0 до 1 относительно самого крупного дня.</param>
public sealed record PlayTimelineDay(DateTime Date, long Seconds, double HeightFraction)
{
    public bool HasPlay => Seconds > 0;

    /// <summary>Подсказка при наведении: «14 августа · 2 ч 35 мин».</summary>
    public string Tooltip => HasPlay
        ? $"{Date:dd MMMM} · {Format(Seconds)}"
        : $"{Date:dd MMMM} · не играл";

    public static string Format(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1)
        {
            return span.Minutes > 0 ? $"{(int)span.TotalHours} ч {span.Minutes} мин" : $"{(int)span.TotalHours} ч";
        }

        return $"{Math.Max(1, (int)span.TotalMinutes)} мин";
    }
}

/// <summary>Готовый график для профиля.</summary>
public sealed record PlayTimeline(
    IReadOnlyList<PlayTimelineDay> Days,
    long TotalSeconds,
    int ActiveDays,
    PlayTimelineDay? BestDay)
{
    public bool IsEmpty => TotalSeconds == 0;

    public string Caption => IsEmpty
        ? "За последний месяц игровых сессий не было."
        : $"За 30 дней: {PlayTimelineDay.Format(TotalSeconds)} за {ActiveDays} " +
          $"{DaysWord(ActiveDays)}, лучший день — {BestDay!.Date:dd MMMM} ({PlayTimelineDay.Format(BestDay.Seconds)}).";

    private static string DaysWord(int count)
    {
        var tens = count % 100;
        if (tens is >= 11 and <= 14)
        {
            return "дней";
        }

        return (count % 10) switch
        {
            1 => "день",
            2 or 3 or 4 => "дня",
            _ => "дней"
        };
    }
}

/// <summary>
/// Строит график «сколько играл за последний месяц» из накопленной в профиле статистики по дням.
/// </summary>
/// <remarks>
/// Данные для этого копились и раньше — не хватало только формы, в которой их можно показать.
/// Дни без игры остаются в списке пустыми столбиками: без них пропуски не видны и график врёт.
/// </remarks>
public static class PlayTimelineBuilder
{
    public const int DefaultDays = 30;

    public static PlayTimeline Build(PlayerProfile? profile, DateTime todayLocal, int days = DefaultDays)
    {
        var history = profile?.DailyPlaySeconds ?? new Dictionary<string, long>(StringComparer.Ordinal);
        var raw = new List<(DateTime Date, long Seconds)>();

        for (var offset = days - 1; offset >= 0; offset--)
        {
            var date = todayLocal.Date.AddDays(-offset);
            history.TryGetValue(date.ToString("yyyy-MM-dd"), out var seconds);
            raw.Add((date, Math.Max(0, seconds)));
        }

        var maximum = raw.Count == 0 ? 0 : raw.Max(entry => entry.Seconds);
        var result = raw
            .Select(entry => new PlayTimelineDay(
                entry.Date,
                entry.Seconds,
                maximum > 0 ? (double)entry.Seconds / maximum : 0))
            .ToList();

        var total = result.Sum(day => day.Seconds);
        var active = result.Count(day => day.HasPlay);
        var best = total > 0 ? result.OrderByDescending(day => day.Seconds).First() : null;

        return new PlayTimeline(result, total, active, best);
    }
}
