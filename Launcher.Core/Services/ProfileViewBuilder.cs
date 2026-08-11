using Launcher.App.Models;

namespace Launcher.App.Services;

/// <param name="Value">Крупное число на плитке.</param>
/// <param name="Caption">Подпись под ним.</param>
public sealed record ProfileStatTile(string Value, string Caption);

/// <param name="Percent">Доля от самой наигранной сборки, 0–100.</param>
public sealed record ProfileModpackRow(string Name, string TimeText, double Percent);

/// <summary>
/// Готовит содержимое личного кабинета. Вынесено из <c>MainWindow.xaml.cs</c>: набор плиток,
/// их порядок и форматирование времени должны совпадать в обоих интерфейсах.
/// </summary>
public static class ProfileViewBuilder
{
    public static IReadOnlyList<ProfileStatTile> BuildStatTiles(
        PlayerProfile profile,
        SiteLevel? siteLevel,
        IReadOnlyList<SiteAchievement> siteAchievements)
    {
        var tiles = new List<ProfileStatTile>();

        // Первыми — данные с сайта: уровень и достижения там же, что в кабинете на сайте.
        if (siteLevel is not null)
        {
            tiles.Add(new($"ур. {siteLevel.Level}", siteLevel.Title));
            tiles.Add(new($"{siteAchievements.Count(a => a.Earned)} / {siteAchievements.Count}", "достижений"));
            tiles.Add(new(siteLevel.TotalXp.ToString("N0"), "XP всего"));
        }

        tiles.Add(new(FormatPlaytime(profile.TotalPlaySeconds), "в игре через лаунчер"));
        tiles.Add(new(profile.TotalLaunches.ToString(), "запусков игры"));
        tiles.Add(new(profile.GetLiveStreakDays().ToString(), "дней подряд"));
        tiles.Add(new(profile.DistinctModpackCount.ToString(), "сборок опробовано"));

        return tiles;
    }

    /// <summary>
    /// Время по сборкам, от самой наигранной к остальным. Пустой список означает,
    /// что заголовок «Время по сборкам» показывать не нужно.
    /// </summary>
    public static IReadOnlyList<ProfileModpackRow> BuildModpackRows(PlayerProfile profile)
    {
        var played = profile.Modpacks
            .Where(pair => pair.Value.PlaySeconds > 0)
            .OrderByDescending(pair => pair.Value.PlaySeconds)
            .ToList();

        if (played.Count == 0)
        {
            return [];
        }

        var max = played[0].Value.PlaySeconds;
        return played
            .Select(pair => new ProfileModpackRow(
                string.IsNullOrWhiteSpace(pair.Value.Name) ? pair.Key : pair.Value.Name,
                FormatPlaytime(pair.Value.PlaySeconds),
                max <= 0 ? 0 : pair.Value.PlaySeconds * 100.0 / max))
            .ToList();
    }

    /// <summary>Время в игре: минуты до часа, дальше часы с минутами.</summary>
    public static string FormatPlaytime(long seconds)
    {
        if (seconds < 60)
        {
            return "0 мин";
        }

        if (seconds < 3600)
        {
            return $"{seconds / 60} мин";
        }

        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        return minutes == 0 ? $"{hours} ч" : $"{hours} ч {minutes} мин";
    }

    /// <summary>Подпись прогресса достижения: «Получено · +25 XP» или «12 / 50 часов».</summary>
    public static string BuildAchievementProgressText(SiteAchievement achievement)
    {
        if (achievement.Earned)
        {
            return $"Получено · +{achievement.Xp} XP";
        }

        // Разовые достижения не имеют шкалы — у них показываем только награду.
        if (achievement.Group == "special")
        {
            return $"Не получено · +{achievement.Xp} XP";
        }

        var unit = string.IsNullOrEmpty(achievement.Unit) ? string.Empty : " " + achievement.Unit;
        return $"{FormatMetric(achievement.Current)} / {FormatMetric(achievement.Target)}{unit}";
    }

    /// <summary>Целые значения без дробей, дробные — с одним знаком.</summary>
    public static string FormatMetric(double value) =>
        Math.Abs(value % 1) < 0.05 ? ((long)Math.Round(value)).ToString("N0") : value.ToString("0.#");

    /// <summary>
    /// Заполнение шкалы достижения в процентах. ОБЯЗАТЕЛЬНО ограничиваем сотней:
    /// у выполненных достижений <c>Progress</c> бывает больше единицы (сделано больше цели),
    /// и полоса вылезала за карточку.
    /// </summary>
    public static double BuildAchievementPercent(SiteAchievement achievement)
        => Math.Clamp(achievement.Progress * 100, 0, 100);

    /// <summary>
    /// Порядок показа: полученные вперёд, дальше — по близости к цели, чтобы было видно,
    /// что вот-вот откроется.
    /// </summary>
    public static IReadOnlyList<SiteAchievement> SortForDisplay(IReadOnlyList<SiteAchievement> achievements)
        => achievements
            .Select((achievement, index) => (achievement, index))
            .OrderByDescending(pair => pair.achievement.Earned)
            .ThenByDescending(pair => pair.achievement.Earned ? 0 : BuildAchievementPercent(pair.achievement))
            .ThenBy(pair => pair.index)
            .Select(pair => pair.achievement)
            .ToList();
}
