namespace Launcher.App.Services;

/// <summary>Что показать игроку, когда сайт недоступен.</summary>
/// <param name="IsOffline">true — работаем без связи с сайтом.</param>
/// <param name="CanPlay">true — играть всё равно можно.</param>
public sealed record OfflineState(bool IsOffline, bool CanPlay, string Message);

/// <summary>
/// Объясняет игроку офлайн-состояние вместо молчания.
/// </summary>
/// <remarks>
/// Лаунчер и раньше умел работать без сайта: манифест берётся из кэша, а если его нет — из вшитого
/// резерва. Но игроку об этом не говорилось ничего, и «Играть» на устаревших данных выглядело как
/// случайность. Здесь решается, что именно написать: без связи, но со сборкой на диске играть
/// можно, а вот ставить с нуля — нет.
/// </remarks>
public static class OfflineStateEvaluator
{
    public static OfflineState Evaluate(ModpackManifestSource source, bool modpackInstalled)
    {
        if (source == ModpackManifestSource.Remote)
        {
            return new OfflineState(false, true, string.Empty);
        }

        var origin = source == ModpackManifestSource.Cached
            ? "используем последние сохранённые данные"
            : "используем встроенный резервный список";

        if (modpackInstalled)
        {
            return new OfflineState(
                true,
                true,
                $"Нет связи с bl-modern.ru — {origin}. Играть можно, но новости, список серверов " +
                "и обновления сборки появятся, когда связь вернётся.");
        }

        return new OfflineState(
            true,
            false,
            $"Нет связи с bl-modern.ru — {origin}. Сборка ещё не установлена, а скачать её без " +
            "интернета нельзя. Проверь подключение, VPN и антивирус и нажми «Играть» ещё раз.");
    }
}
