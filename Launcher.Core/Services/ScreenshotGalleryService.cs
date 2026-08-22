using Launcher.App.Platform;

namespace Launcher.App.Services;

/// <summary>Один снимок из папки игры.</summary>
public sealed record GameScreenshot(string Path, string FileName, DateTime TakenAtUtc, long SizeBytes)
{
    /// <summary>Подпись под превью: дата съёмки в местном времени.</summary>
    public string Caption => TakenAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
}

/// <summary>
/// Список скриншотов игры для галереи в лаунчере.
/// </summary>
/// <remarks>
/// Minecraft складывает снимки в <c>screenshots</c> внутри папки сборки, а папка эта у каждого своя
/// и часто спрятана в AppData. Игроки делают скриншоты и потом не могут их найти — лаунчер знает
/// путь и может показать их сам.
/// </remarks>
public static class ScreenshotGalleryService
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg"];

    /// <summary>Сколько снимков показываем: дальше галерея превращается в файловый менеджер.</summary>
    public const int MaxItems = 60;

    public static string ResolveFolder(string installRoot)
    {
        return string.IsNullOrWhiteSpace(installRoot)
            ? string.Empty
            : Path.Combine(LauncherPaths.ExpandFull(installRoot), "screenshots");
    }

    /// <summary>Снимки от новых к старым. Пустой список, если папки ещё нет.</summary>
    public static IReadOnlyList<GameScreenshot> List(string installRoot, int maxItems = MaxItems)
    {
        var folder = ResolveFolder(installRoot);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                // Ноль байт остаётся, если игра упала прямо во время сохранения снимка: показывать
                // такой файл нечем, а превью на нём падает.
                .Where(info => info.Length > 0)
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(maxItems)
                .Select(info => new GameScreenshot(info.FullName, info.Name, info.LastWriteTimeUtc, info.Length))
                .ToList();
        }
        catch
        {
            // Папку могли удалить прямо во время чтения — для галереи это просто «снимков нет».
            return [];
        }
    }

    /// <summary>Удаляет снимок. Возвращает false, если файл занят или уже удалён.</summary>
    public static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
