using System.Runtime.Versioning;

namespace Launcher.App.Platform;

/// <summary>
/// Единая точка ответов на вопрос «на чём мы запущены». Заведена ради порта на Linux и macOS:
/// раньше эти допущения были рассыпаны по коду в виде «java.exe», «;» и путей Program Files.
/// </summary>
/// <remarks>
/// Правило при доработках: поведение на Windows обязано остаться ровно прежним. Все ветки
/// для Linux и macOS — дополнительные, они не меняют то, что уже работает у игроков.
/// </remarks>
public static class HostPlatform
{
    // Атрибуты-«охранники» нужны, чтобы анализатор совместимости (CA1416) понимал: проверка
    // этих свойств равносильна вызову OperatingSystem.IsWindows() и т.д. Без них любой вызов
    // платформенного API под нашим if помечается предупреждением.
    [SupportedOSPlatformGuard("windows")]
    public static bool IsWindows => OperatingSystem.IsWindows();

    [SupportedOSPlatformGuard("linux")]
    public static bool IsLinux => OperatingSystem.IsLinux();

    [SupportedOSPlatformGuard("macos")]
    public static bool IsMacOS => OperatingSystem.IsMacOS();

    /// <summary>
    /// Java для диагностики и установщиков: пишет в stdout, консольное окно допустимо.
    /// </summary>
    public static string JavaExecutableName => IsWindows ? "java.exe" : "java";

    /// <summary>
    /// Java для запуска самой игры. Отдельный бесконсольный бинарник есть только у Windows,
    /// на Unix эту роль играет обычный <c>java</c>.
    /// </summary>
    public static string JavawExecutableName => IsWindows ? "javaw.exe" : "java";

    /// <summary>
    /// Разделитель элементов classpath в командной строке JVM: <c>;</c> на Windows, <c>:</c> на Unix.
    /// </summary>
    public static char ClassPathSeparator => IsWindows ? ';' : ':';

    /// <summary>
    /// Короткое имя ОС в том виде, в каком его ждут манифесты Mojang (<c>windows</c>, <c>linux</c>, <c>osx</c>).
    /// </summary>
    public static string MojangOsName => IsWindows ? "windows" : IsMacOS ? "osx" : "linux";
}
