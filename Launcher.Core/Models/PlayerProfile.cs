using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.App.Models;

/// <summary>
/// Локальная статистика игрока и разблокированные ачивки. Лежит рядом с user-settings.json
/// в отдельном файле (player-stats.json), чтобы профиль можно было позже синхронизировать
/// с сайтом, не трогая настройки лаунчера.
/// </summary>
public sealed class PlayerProfile
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Тот же clientId, что и в настройках — ключ для будущей синхронизации с сайтом.</summary>
    public string ClientId { get; set; } = string.Empty;

    public long TotalPlaySeconds { get; set; }
    public int TotalLaunches { get; set; }
    public int CrashCount { get; set; }
    public long LongestSessionSeconds { get; set; }

    public DateTime? FirstLaunchUtc { get; set; }
    public DateTime? LastLaunchUtc { get; set; }

    /// <summary>Серия дней подряд с запуском игры (по локальной дате).</summary>
    public int CurrentStreakDays { get; set; }
    public int BestStreakDays { get; set; }

    /// <summary>Локальная дата последней сессии (yyyy-MM-dd) — база для подсчёта серии.</summary>
    public string LastPlayDate { get; set; } = string.Empty;

    /// <summary>Был ли краш в предыдущей сессии — нужно для ачивки «вернулся после краша».</summary>
    public bool LastSessionCrashed { get; set; }

    /// <summary>Статистика по каждой сборке: id → показатели.</summary>
    public Dictionary<string, ModpackPlayStats> Modpacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Разблокированные ачивки: id → когда разблокирована (UTC).</summary>
    public Dictionary<string, DateTime> Achievements { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Снимок достижений с сайта (id → когда лаунчер увидел их полученными). Нужен, чтобы
    /// показывать тост только на НОВЫХ достижениях, а не на всех при первой синхронизации.
    /// Сами достижения считает сайт по игровой статистике — здесь только «что уже видели».
    /// </summary>
    public Dictionary<string, DateTime> SiteAchievements { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Была ли уже хоть одна успешная синхронизация с сайтом (первая идёт без тостов).</summary>
    public bool SiteSynced { get; set; }

    [JsonIgnore]
    public int DistinctModpackCount => Modpacks.Count(pair => pair.Value.PlaySeconds > 0 || pair.Value.Launches > 0);

    [JsonIgnore]
    public string FavoriteModpackName
    {
        get
        {
            var best = Modpacks.Values
                .OrderByDescending(stats => stats.PlaySeconds)
                .ThenByDescending(stats => stats.Launches)
                .FirstOrDefault();
            return best is null || string.IsNullOrWhiteSpace(best.Name) ? string.Empty : best.Name;
        }
    }

    /// <summary>
    /// Записывает завершённую игровую сессию. Возвращает обновлённый профиль (мутирует себя).
    /// Слишком короткие сессии тоже считаются запуском, но время добавляется как есть.
    /// </summary>
    public void RecordSession(string modpackId, string modpackName, TimeSpan duration, bool crashed, DateTime startedAtLocal)
    {
        var seconds = (long)Math.Max(0, duration.TotalSeconds);
        TotalPlaySeconds += seconds;
        TotalLaunches++;
        LongestSessionSeconds = Math.Max(LongestSessionSeconds, seconds);
        LastLaunchUtc = DateTime.UtcNow;
        FirstLaunchUtc ??= startedAtLocal.ToUniversalTime();
        LastSessionCrashed = crashed;
        if (crashed)
        {
            CrashCount++;
        }

        var key = string.IsNullOrWhiteSpace(modpackId) ? "default" : modpackId;
        if (!Modpacks.TryGetValue(key, out var stats))
        {
            stats = new ModpackPlayStats();
            Modpacks[key] = stats;
        }

        if (!string.IsNullOrWhiteSpace(modpackName))
        {
            stats.Name = modpackName;
        }

        stats.PlaySeconds += seconds;
        stats.Launches++;
        stats.LastPlayedUtc = DateTime.UtcNow;

        UpdateStreak(startedAtLocal);
    }

    private void UpdateStreak(DateTime startedAtLocal)
    {
        var today = startedAtLocal.Date;
        var todayText = today.ToString("yyyy-MM-dd");

        if (LastPlayDate.Equals(todayText, StringComparison.Ordinal))
        {
            // Вторая сессия в тот же день серию не наращивает.
            return;
        }

        if (DateTime.TryParse(LastPlayDate, out var previous) && previous.Date == today.AddDays(-1))
        {
            CurrentStreakDays++;
        }
        else
        {
            CurrentStreakDays = 1;
        }

        BestStreakDays = Math.Max(BestStreakDays, CurrentStreakDays);
        LastPlayDate = todayText;
    }

    /// <summary>Серия обрывается, если последняя игра была не сегодня и не вчера.</summary>
    public int GetLiveStreakDays()
    {
        if (!DateTime.TryParse(LastPlayDate, out var previous))
        {
            return 0;
        }

        var daysAgo = (DateTime.Now.Date - previous.Date).Days;
        return daysAgo is 0 or 1 ? CurrentStreakDays : 0;
    }

    private static readonly object SaveLock = new();

    public static PlayerProfile Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PlayerProfile();
        }

        try
        {
            return JsonSerializer.Deserialize<PlayerProfile>(File.ReadAllText(path), JsonOptions()) ?? new PlayerProfile();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Профиль — не критичные данные: битый файл не должен мешать запуску игры.
            TryBackupCorruptFile(path);
            return new PlayerProfile();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, JsonOptions());

        // Атомарная запись — как в UserSettings: обрыв записи не должен оставить усечённый JSON.
        lock (SaveLock)
        {
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
    }

    private static void TryBackupCorruptFile(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", true);
        }
        catch
        {
            // Не критично — перезапишем при следующем Save.
        }
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
    }
}

public sealed class ModpackPlayStats
{
    public string Name { get; set; } = string.Empty;
    public long PlaySeconds { get; set; }
    public int Launches { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
}
