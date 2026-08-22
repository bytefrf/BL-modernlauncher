namespace Launcher.App.Services;

/// <summary>Насколько разумно выбранное количество памяти для игры.</summary>
public enum MemoryVerdictLevel
{
    /// <summary>Значение нормальное, беспокоить игрока не о чем.</summary>
    Ok,

    /// <summary>Памяти мало — игра будет вылетать. Показываем окно.</summary>
    TooLow,

    /// <summary>Памяти выделено слишком много — станет только хуже. Показываем окно.</summary>
    TooHigh
}

public sealed record MemoryVerdict(
    MemoryVerdictLevel Level,
    string Title,
    string Message,
    int RecommendedMb)
{
    public bool NeedsAttention => Level != MemoryVerdictLevel.Ok;
}

/// <summary>
/// Проверяет выбранный объём памяти для Minecraft и объясняет игроку, что не так.
/// </summary>
/// <remarks>
/// Объём ОЗУ лаунчер знал и раньше, но использовал только в телеметрии: настройка памяти о железе
/// не знала вообще ничего. Мало памяти — игра вылетает на загрузке мира, слишком много — операционной
/// системе ничего не остаётся, начинается своп и паузы сборщика мусора. Обе крайности выглядят для
/// игрока одинаково («лаунчер сломался»), поэтому объясняем словами, а не цифрой в поле.
/// </remarks>
public static class MemoryAdvisor
{
    /// <summary>Ниже этого сборки уровня TFGM/GTO не доживают до игры.</summary>
    public const int MinimumComfortableMb = 4096;

    /// <summary>Выше этого выигрыша нет: паузы сборщика мусора растут быстрее пользы.</summary>
    public const int PracticalMaximumMb = 10240;

    /// <summary>Сколько памяти обязательно оставить системе и драйверам.</summary>
    private const int ReservedForSystemMb = 2048;

    public static MemoryVerdict Evaluate(int memoryMb, long? totalRamMb)
    {
        var recommended = Recommend(totalRamMb);

        if (memoryMb < MinimumComfortableMb)
        {
            return new MemoryVerdict(
                MemoryVerdictLevel.TooLow,
                "Игре выделено слишком мало памяти",
                $"Сейчас выделено {FormatGb(memoryMb)}, а сборке нужно минимум {FormatGb(MinimumComfortableMb)}.\n\n" +
                "С таким запасом игра почти наверняка вылетит — обычно на загрузке мира или при заходе на сервер, " +
                "и выглядеть это будет как поломка лаунчера.\n\n" +
                $"Поставь {FormatGb(recommended)} — это подходящее значение для твоего компьютера.\n\n" +
                "Больше — не значит лучше: если отдать игре почти всю память, системе ничего не останется, " +
                "начнутся подтормаживания и вылеты уже по другой причине.",
                recommended);
        }

        // Верхняя граница считается от железа: на 8 ГБ ОЗУ вредны уже 6 ГБ, на 32 ГБ — нет.
        var safeMaximum = SafeMaximum(totalRamMb);
        if (memoryMb > safeMaximum)
        {
            var reason = totalRamMb is > 0
                ? $"На этом компьютере {FormatGb((int)totalRamMb.Value)} оперативной памяти, и системе нужно оставить хотя бы {FormatGb(ReservedForSystemMb)}."
                : "Больше 10 ГБ игре не помогают: паузы сборщика мусора растут быстрее, чем польза от запаса.";

            return new MemoryVerdict(
                MemoryVerdictLevel.TooHigh,
                "Игре выделено слишком много памяти",
                $"Сейчас выделено {FormatGb(memoryMb)}. {reason}\n\n" +
                "Когда игре отдают почти всю память, Windows начинает выгружать данные на диск: " +
                "появляются фризы, а игра может вылететь так же, как при нехватке памяти.\n\n" +
                $"Поставь {FormatGb(recommended)} — этого достаточно даже с шейдерами.",
                recommended);
        }

        return new MemoryVerdict(MemoryVerdictLevel.Ok, string.Empty, string.Empty, recommended);
    }

    /// <summary>Рекомендуемое значение: половина ОЗУ, но в разумных границах.</summary>
    public static int Recommend(long? totalRamMb)
    {
        if (totalRamMb is not > 0)
        {
            return 6144;
        }

        var half = (int)(totalRamMb.Value / 2);
        var safe = SafeMaximum(totalRamMb);
        var recommended = Math.Min(half, safe);

        // На слабой машине честнее предложить минимум, чем красивое, но недостижимое число.
        return Math.Clamp(RoundToGb(recommended), MinimumComfortableMb, PracticalMaximumMb);
    }

    private static int SafeMaximum(long? totalRamMb)
    {
        if (totalRamMb is not > 0)
        {
            return PracticalMaximumMb;
        }

        var byHardware = (int)Math.Max(MinimumComfortableMb, totalRamMb.Value - ReservedForSystemMb);
        return Math.Min(byHardware, PracticalMaximumMb);
    }

    private static int RoundToGb(int megabytes) => Math.Max(1, megabytes / 1024) * 1024;

    private static string FormatGb(int megabytes)
    {
        var gigabytes = megabytes / 1024.0;
        return Math.Abs(gigabytes - Math.Round(gigabytes)) < 0.05
            ? $"{Math.Round(gigabytes):0} ГБ"
            : $"{gigabytes:0.#} ГБ";
    }
}
