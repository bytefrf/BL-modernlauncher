using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed record SiteAchievement(
    string Id,
    string Group,
    string Icon,
    string Name,
    string Description,
    int Xp,
    bool Earned,
    double Progress,
    double Current,
    double Target,
    string Unit);

public sealed record SiteLevel(int Level, string Title, int TotalXp, int Into, int Need, double Progress);

/// <summary>
/// Точная копия движка достижений с сайта (`cabinet-core.js`: ACH_GROUPS / ACH_SPECIAL / TIER_XP /
/// levelFromXP / LEVEL_TITLES). Достижения там НЕ хранятся в базе, а выводятся из игровой
/// статистики, поэтому лаунчеру достаточно тех же формул — цифры совпадут с кабинетом.
///
/// ⚠️ Если правишь пороги/XP на сайте — поправь и здесь, иначе лаунчер и кабинет разойдутся.
/// </summary>
public static class SiteAchievementEngine
{
    private static readonly string[] Roman = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI"];
    private static readonly int[] TierXp = [10, 20, 35, 55, 80, 120, 170, 230, 300, 400, 520];

    private sealed record AchGroup(string Key, string Metric, string Icon, string Unit, string Base, double[] Thresholds);

    private static readonly AchGroup[] Groups =
    [
        new("hours",   "hours",   "⏱", "ч",  "Время в игре",            [1, 5, 10, 25, 50, 100, 200, 350, 500, 750, 1000]),
        new("kills",   "kills",   "⚔",  "",   "Убийства",                [1, 10, 50, 100, 250, 500, 1000, 2500, 5000]),
        new("mined",   "mined",   "💎", "",   "Добыто блоков",           [100, 1000, 5000, 10000, 25000, 50000, 100000]),
        new("walk",    "walk",    "🚶", "км", "Пройдено пешком",         [1, 5, 10, 25, 50, 100, 250]),
        new("ride",    "ride",    "🐴", "км", "Наезжено верхом",         [1, 5, 10, 25, 50, 100]),
        new("jump",    "jump",    "⬆",  "",   "Прыжки",                  [100, 1000, 5000, 10000, 25000, 50000]),
        new("dealt",   "dealt",   "💥", "HP", "Урон нанесён",            [100, 1000, 5000, 10000, 25000, 50000]),
        new("taken",   "taken",   "🛡", "HP", "Урон получен",            [100, 1000, 5000, 10000, 25000, 50000]),
        new("crafted", "crafted", "🔨", "",   "Скрафчено",               [10, 100, 500, 1000, 2500, 5000]),
        new("chests",  "chests",  "📦", "",   "Открыто сундуков",        [10, 50, 100, 250, 500]),
        new("kd",      "kd",      "🎯", "",   "Уровень K/D",             [1, 2, 3, 5, 8, 10]),
        new("deaths",  "deaths",  "💀", "",   "Боевой опыт (смерти)",    [10, 50, 100, 250, 500, 1000, 2000])
    ];

    private sealed record AchSpecial(
        string Id,
        string Icon,
        string Name,
        string Description,
        int Xp,
        Func<SitePlayerStats, Dictionary<string, double>, bool> Ok);

    private static readonly AchSpecial[] Specials =
    [
        new("sp_welcome",   "🚪", "Добро пожаловать",  "Зайти на сервер хотя бы раз",                      40,  (p, _) => p.Hours > 0),
        new("sp_day",       "📅", "Сутки в TFGM",      "Провести в игре 24 часа суммарно",                 90,  (p, _) => p.Hours >= 24),
        new("sp_immortal",  "🛡", "Ни одной смерти",   "Сыграть 2+ часа без единой смерти",                150, (p, _) => p.Deaths == 0 && p.Hours >= 2),
        new("sp_killer",    "🎯", "Машина убийств",    "K/D 5+ при 5+ часах игры",                         160, (p, _) => p.Kd >= 5 && p.Hours >= 5),
        new("sp_predator",  "☠",  "Хищник",            "K/D 10+ и 500+ убийств",                           220, (p, _) => p.Kd >= 10 && p.Kills >= 500),
        new("sp_both",      "🖥", "На двух фронтах",   "Играть и на TFGM-1, и на TFGM-2",                  120, (p, _) => p.Servers.Count >= 2),
        new("sp_warrior",   "🐴", "Закалённый воин",   "100+ часов и 1000+ убийств",                       200, (p, _) => p.Hours >= 100 && p.Kills >= 1000),
        new("sp_industry",  "🏭", "Промышленник",      "10 000+ блоков и 5 000+ крафтов",                  200, (p, _) => p.Mined >= 10000 && p.Crafted >= 5000),
        new("sp_nomad",     "🗺", "Великий странник",  "100 км пешком и 50 км верхом",                     180, (p, _) => p.Walk >= 100 && p.Ride >= 50),
        new("sp_marathon",  "🏃", "Марафонец",         "Пройти пешком 250 км",                             160, (p, _) => p.Walk >= 250),
        new("sp_minerk",    "⛰",  "Король шахт",       "Добыть 100 000 блоков",                            240, (p, _) => p.Mined >= 100000),
        new("sp_tank",      "❤",  "Несокрушимый",      "Получить 25 000 HP урона и выжить",                150, (p, _) => p.Taken >= 25000),
        new("sp_tactician", "♟",  "Тактик",            "Нанести вдвое больше урона, чем получить (от 5k)", 150, (p, _) => p.Dealt >= 5000 && p.Dealt >= p.Taken * 2),
        new("sp_looter",    "💎", "Расхититель",       "500 сундуков и 50 000 блоков",                     180, (p, _) => p.Chests >= 500 && p.Mined >= 50000),
        new("sp_pacifist",  "🕊", "Пацифист",          "10+ часов вообще без убийств",                     120, (p, _) => p.Kills == 0 && p.Hours >= 10),
        new("sp_champion",  "👑", "Чемпион сервера",   "Стать #1 хотя бы в одной категории",               300, (_, r) => r.Values.Any(value => Math.Abs(value - 1) < 0.0001)),
        new("sp_top10t",    "⭐", "В десятке по времени", "Войти в топ-10 по времени игры",                140, (_, r) => r.TryGetValue("hours", out var h) && h > 0 && h <= 10),
        new("sp_top10k",    "🏆", "В десятке по фрагам",  "Войти в топ-10 по убийствам",                   140, (_, r) => r.TryGetValue("kills", out var k) && k > 0 && k <= 10)
    ];

    public static IReadOnlyList<SiteAchievement> Build(SitePlayerResponse response)
    {
        var player = response.Player;
        var ranks = response.Ranks ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var list = new List<SiteAchievement>();

        foreach (var group in Groups)
        {
            var value = player.GetMetric(group.Metric);
            for (var i = 0; i < group.Thresholds.Length; i++)
            {
                var target = group.Thresholds[i];
                var unitSuffix = string.IsNullOrEmpty(group.Unit) ? string.Empty : " " + group.Unit;
                list.Add(new SiteAchievement(
                    $"{group.Key}_{i}",
                    group.Key,
                    group.Icon,
                    $"{group.Base} {Roman[Math.Min(i, Roman.Length - 1)]}",
                    $"Достичь {FormatNumber(target)}{unitSuffix}",
                    i < TierXp.Length ? TierXp[i] : 520,
                    value >= target,
                    target > 0 ? Math.Min(1, value / target) : 0,
                    value,
                    target,
                    group.Unit));
            }
        }

        foreach (var special in Specials)
        {
            bool earned;
            try
            {
                earned = special.Ok(player, ranks);
            }
            catch
            {
                earned = false;
            }

            list.Add(new SiteAchievement(
                special.Id,
                "special",
                special.Icon,
                special.Name,
                special.Description,
                special.Xp,
                earned,
                earned ? 1 : 0,
                earned ? 1 : 0,
                1,
                string.Empty));
        }

        return list;
    }

    public static SiteLevel BuildLevel(IEnumerable<SiteAchievement> achievements)
    {
        var totalXp = achievements.Where(achievement => achievement.Earned).Sum(achievement => achievement.Xp);

        // Тот же расчёт, что levelFromXP на сайте: порог уровня N равен 100*N.
        var level = 1;
        var accumulated = 0;
        var need = 100;
        while (totalXp >= accumulated + need)
        {
            accumulated += need;
            level++;
            need = 100 * level;
        }

        var into = totalXp - accumulated;
        return new SiteLevel(level, LevelTitle(level), totalXp, into, need, need > 0 ? into / (double)need : 0);
    }

    private static string LevelTitle(int level)
    {
        var title = "Новичок";
        foreach (var (min, name) in new[]
                 {
                     (1, "Новичок"), (5, "Искатель"), (10, "Бывалый"),
                     (18, "Ветеран"), (28, "Мастер"), (40, "Легенда")
                 })
        {
            if (level >= min)
            {
                title = name;
            }
        }

        return title;
    }

    private static string FormatNumber(double value) =>
        Math.Abs(value % 1) < 0.0001
            ? ((long)value).ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
            : value.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
}
