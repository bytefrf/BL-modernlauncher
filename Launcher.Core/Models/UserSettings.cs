using System.Text.Json;
using Launcher.App.Platform;

namespace Launcher.App.Models;

public sealed class UserSettings
{
    public string Username { get; set; } = "Player";
    public string SupportEmail { get; set; } = string.Empty;

    /// <summary>Последние использованные ники — для быстрого переключения между аккаунтами.</summary>
    public List<string> RecentUsernames { get; set; } = [];
    public string ClientId { get; set; } = Guid.NewGuid().ToString();
    public bool TelemetryEnabled { get; set; } = true;
    public string ThemeId { get; set; } = "verdant";
    public string SelectedModpackId { get; set; } = string.Empty;
    public int MemoryMb { get; set; } = 4096;
    // Глобальный путь установки (legacy/одиночный режим и база для подпапок новых сборок).
    public string InstallRoot { get; set; } = string.Empty;
    // Путь установки на КОНКРЕТНУЮ сборку (id → папка). Позволяет игроку держать сборки в разных
    // папках и указать, где уже лежит существующая сборка (чтобы не качать заново).
    public Dictionary<string, string> ModpackInstallRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string JavaExecutable { get; set; } = string.Empty;
    public string JvmArguments { get; set; } = string.Empty;
    public string GameArguments { get; set; } = string.Empty;
    public bool UseCustomResolution { get; set; } = true;
    public int ResolutionWidth { get; set; } = 1280;
    public int ResolutionHeight { get; set; } = 720;
    // Закрывать лаунчер при запуске игры вместо сворачивания в трей.
    public bool CloseOnGameStart { get; set; } = false;
    // Звуки интерфейса (ачивка разблокирована, сборка готова к запуску).
    public bool SoundEnabled { get; set; } = true;
    // Выбранные дополнительные моды: id сборки → список id модов из каталога одобренных.
    public Dictionary<string, List<string>> OptionalMods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Сборки, где игрок уже открывал окно модов: чтобы применить defaultOn только один раз.
    public List<string> OptionalModsInitialized { get; set; } = [];

    public string ResolveInstallRoot(string manifestRoot, string fallbackRoot, string? catalogModpackId = null, bool ignoreModpackOverride = false)
    {
        // ИЗОЛИРОВАННЫЙ ПРОФИЛЬ (--profile=имя) обязан перехватывать путь именно здесь: через этот
        // метод идут ВСЕ, включая ArchiveInstallService и RuntimeInstallService, которые считают
        // папку установки сами. Проверка выше по стеку их не покрывает, и установка уходила
        // в рабочую папку игрока.
        if (Platform.LauncherProfile.IsIsolated)
        {
            var isolated = Platform.LauncherProfile.IsolatedInstallRoot;
            return string.IsNullOrWhiteSpace(catalogModpackId)
                ? Path.GetFullPath(isolated)
                : Path.GetFullPath(Path.Combine(isolated, SanitizeSegment(catalogModpackId)));
        }

        // 1. Явная папка этой сборки, выбранная игроком (в т.ч. «где уже лежит tfgm») — высший приоритет.
        if (!ignoreModpackOverride &&
            !string.IsNullOrWhiteSpace(catalogModpackId) &&
            ModpackInstallRoots is not null &&
            ModpackInstallRoots.TryGetValue(catalogModpackId, out var perPack) &&
            !string.IsNullOrWhiteSpace(perPack))
        {
            return ToFullPath(perPack);
        }

        // 2. Глобальный кастомный путь игрока.
        if (!string.IsNullOrWhiteSpace(InstallRoot))
        {
            // В мульти-сборочном режиме (catalogModpackId задан) НОВАЯ сборка идёт в подпапку <id>,
            // чтобы сборки не перемешивались. НО если сборка уже установлена прямо в InstallRoot
            // (старый одиночный игрок) — не двигаем её, иначе у него всё перекачается заново.
            if (!string.IsNullOrWhiteSpace(catalogModpackId) && !IsModpackInstalledAt(InstallRoot, catalogModpackId))
            {
                return ToFullPath(Path.Combine(InstallRoot, SanitizeSegment(catalogModpackId)));
            }

            return ToFullPath(InstallRoot);
        }

        // 3. Без кастомного пути: per-pack manifestRoot (в каталоге у каждой сборки свой) или фолбэк.
        return ToFullPath(string.IsNullOrWhiteSpace(manifestRoot) ? fallbackRoot : manifestRoot);
    }

    private static string ToFullPath(string root) => LauncherPaths.ExpandFull(root);

    // Установлена ли сборка <id> прямо в root — по маркеру .launcher/modpack.version ("<id>:версия").
    // Реализация одна на весь лаунчер, чтобы проверка не расходилась между окном и настройками.
    private static bool IsModpackInstalledAt(string root, string modpackId)
        => Services.ModpackInstallMarker.IsInstalledAt(root, modpackId);

    private static string SanitizeSegment(string value)
    {
        // Оба разделителя режем ЯВНО, независимо от ОС: на Unix Path.GetInvalidFileNameChars()
        // не считает '\' запрещённым символом, а LauncherPaths.Expand позже превращает его в '/'.
        // Из-за этого id сборки вида "..\..\evil" уводил папку установки ВЫШЕ базовой.
        value = value.Replace('\\', '_').Replace('/', '_');

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        value = value.Trim();

        // Сегмент из одних точек ("." или "..") — это переход по дереву, а не имя папки.
        // Ни одна настоящая сборка так не называется.
        return value.Trim('.').Length == 0 ? "_" : value;
    }

    // Сериализация настроек идёт из разных async-цепочек; защищаемся от гонки на запись.
    private static readonly object SaveLock = new();

    public static UserSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new UserSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), JsonOptions()) ?? new UserSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Повреждённый файл настроек не должен мешать запуску: делаем бэкап и стартуем с дефолтами.
            TryBackupCorruptFile(path);
            return new UserSettings();
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

        // Атомарная запись: пишем во временный файл и заменяем целевой, чтобы обрыв
        // питания/краш в момент записи не оставил усечённый JSON.
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
            // Не критично — просто перезапишем при следующем Save.
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
