using System.Diagnostics;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Семейство ОС одним словом: <c>Windows</c>, <c>Linux</c>, <c>macOS</c>. Нужно там, где
    /// важно не «какая сборка ядра», а «сколько игроков на какой системе».
    /// </summary>
    public static string OsFamilyName => IsWindows ? "Windows" : IsMacOS ? "macOS" : "Linux";

    private static readonly Lazy<string> LazyOsDescription = new(BuildOsDescription);

    /// <summary>
    /// Читаемое описание системы для телеметрии и логов, например
    /// <c>Microsoft Windows NT 10.0.26200.0</c>, <c>Linux 6.8.0 · Ubuntu 24.04 LTS</c>, <c>macOS 14.5</c>.
    /// </summary>
    /// <remarks>
    /// На Windows возвращается ровно то же значение, что отправлялось раньше
    /// (<see cref="Environment.OSVersion"/>) — иначе в статистике сайта разошлись бы группы
    /// по версиям. Ветки Unix заведены потому, что там <c>OSVersion.VersionString</c> у Linux
    /// и macOS одинаково начинается со слова «Unix» — различить их в отчёте было невозможно.
    /// Строка укорочена до 64 символов: столько принимает колонка <c>os_version</c> на сайте.
    /// </remarks>
    public static string OsDescription => LazyOsDescription.Value;

    private static string BuildOsDescription()
    {
        if (IsWindows)
        {
            return Environment.OSVersion.VersionString;
        }

        var description = IsMacOS ? BuildMacOsDescription() : BuildLinuxDescription();
        return description.Length > 64 ? description[..64].TrimEnd(' ', '·') : description;
    }

    private static string BuildMacOsDescription()
    {
        var product = RunTool("/usr/bin/sw_vers", "-productVersion");
        return string.IsNullOrWhiteSpace(product)
            // Без sw_vers остаётся версия ядра Darwin — она не равна версии macOS,
            // поэтому и подписана честно, а не выдаётся за неё.
            ? "macOS (Darwin " + Environment.OSVersion.Version.ToString(3) + ")"
            : "macOS " + product.Trim();
    }

    private static string BuildLinuxDescription()
    {
        // OSDescription на Linux — это вывод uname: «Linux 6.8.0-45-generic #45~22.04.1-Ubuntu SMP …».
        // Берём только номер ядра: остальное у каждого дистрибутива своё и в отчёт не помещается.
        var kernel = RuntimeInformation.OSDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var version = kernel.Length > 1 ? kernel[1].Split('-')[0] : Environment.OSVersion.Version.ToString(3);
        var distro = ReadOsReleasePrettyName();
        return string.IsNullOrWhiteSpace(distro) ? "Linux " + version : $"Linux {version} · {distro}";
    }

    /// <summary>Название дистрибутива из <c>/etc/os-release</c>; пусто, если файла нет.</summary>
    private static string ReadOsReleasePrettyName()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                if (!line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                {
                    continue;
                }

                return line["PRETTY_NAME=".Length..].Trim().Trim('"');
            }
        }
        catch
        {
            // Файла может не быть вовсе (нестандартный дистрибутив, урезанный контейнер) —
            // это не повод остаться без описания системы.
        }

        return string.Empty;
    }

    private static string RunTool(string fileName, params string[] arguments)
    {
        try
        {
            if (!File.Exists(fileName))
            {
                return string.Empty;
            }

            var startInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }
}
