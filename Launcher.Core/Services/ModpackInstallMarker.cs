using Launcher.App.Platform;

namespace Launcher.App.Services;

/// <summary>
/// Маркер установленной сборки: файл <c>.launcher/modpack.version</c> с содержимым
/// <c>&lt;id&gt;:&lt;версия&gt;</c>. По нему лаунчер отличает «эта сборка уже стоит здесь»
/// от «в папке лежит чужая сборка».
/// </summary>
/// <remarks>
/// Раньше эта проверка существовала в двух копиях — в <c>UserSettings</c> и в <c>MainWindow</c>.
/// Копия из окна раскрывала путь через <see cref="Environment.ExpandEnvironmentVariables"/>
/// напрямую, из-за чего на Linux и macOS значение вида <c>%AppData%\ForgeLauncher</c>
/// не раскрывалось и сборка всегда считалась неустановленной.
/// </remarks>
public static class ModpackInstallMarker
{
    public const string RelativePath = ".launcher/modpack.version";

    /// <summary>Путь к файлу маркера внутри папки установки.</summary>
    public static string GetPath(string installRoot)
        => Path.Combine(LauncherPaths.Expand(installRoot), ".launcher", "modpack.version");

    /// <summary>Установлена ли сборка <paramref name="modpackId"/> именно в этой папке.</summary>
    public static bool IsInstalledAt(string? installRoot, string modpackId)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || string.IsNullOrWhiteSpace(modpackId))
        {
            return false;
        }

        try
        {
            var marker = GetPath(installRoot);
            return File.Exists(marker)
                && File.ReadAllText(marker).Trim().StartsWith(modpackId + ":", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Нет доступа к папке или битый файл — считаем, что сборки здесь нет.
            return false;
        }
    }

    /// <summary>Версия сборки из маркера или <c>null</c>, если маркера нет.</summary>
    public static string? ReadVersion(string? installRoot, string modpackId)
    {
        if (!IsInstalledAt(installRoot, modpackId))
        {
            return null;
        }

        try
        {
            var value = File.ReadAllText(GetPath(installRoot!)).Trim();
            var separator = value.IndexOf(':');
            return separator >= 0 && separator < value.Length - 1 ? value[(separator + 1)..] : null;
        }
        catch
        {
            return null;
        }
    }
}
