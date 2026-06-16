using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.App.Services;

public static class CrashAnalyzerService
{
    private static readonly (string Category, string[] Needles, string Summary)[] KnownPatterns =
    [
        ("out_of_memory",
        [
            "OutOfMemoryError",
            "Java heap space",
            "GC overhead limit exceeded"
        ],
        "Не хватает оперативной памяти. Увеличь память в настройках лаунчера."),

        ("java_version",
        [
            "UnsupportedClassVersionError",
            "has been compiled by a more recent version"
        ],
        "Неверная версия Java. Для этой сборки нужна Java 17."),

        ("mod_dependency",
        [
            "Missing or unsupported mandatory dependencies",
            "Missing mandatory dependencies",
            "ModLoadingException"
        ],
        "Конфликт модов или зависимостей. Обычно помогает проверка файлов сборки."),

        ("language_provider",
        [
            "needs language provider"
        ],
        "Не хватает загрузчика или зависимости для одного из модов."),

        ("missing_file",
        [
            "FileNotFoundException",
            "NoSuchFileException",
            "Failed to download file"
        ],
        "Не найден или не скачался нужный файл. Нужна проверка файлов сборки."),

        ("forge_version_mismatch",
        [
            "Actual version:",
            "Expected range:",
            "forge"
        ],
        "Версия Forge не подходит одному из модов."),

        ("graphics_driver",
        [
            "Pixel format not accelerated",
            "OpenGL"
        ],
        "Проблема с видеодрайвером или OpenGL. Обнови драйвер видеокарты."),

        ("init_crash",
        [
            "The game crashed whilst initializing game",
            "Rendering overlay"
        ],
        "Краш на инициализации клиента. Чаще всего это конфликт модов, ресурсов или клиентских настроек."),

        ("module_resolution",
        [
            "java.lang.module.ResolutionException",
            "export package"
        ],
        "Конфликт модулей Java между модами. Обычно это дубли или несовместимые jar-файлы.")
    ];

    public static CrashAnalysisResult Analyze(string installRoot, int exitCode)
    {
        var latestLogPath = Path.Combine(installRoot, "logs", "latest.log");
        var crashReportPath = FindNewestCrashReport(installRoot);
        var logText = ReadTail(latestLogPath);
        var crashText = ReadTail(crashReportPath);
        var combined = $"{logText}{Environment.NewLine}{crashText}";

        var finding = DetectFinding(combined, exitCode);
        var details = BuildDetails(exitCode, finding.Summary, latestLogPath, crashReportPath);

        return new CrashAnalysisResult(
            finding.Summary,
            details,
            latestLogPath,
            crashReportPath,
            finding.Category,
            finding.Signature,
            finding.Evidence,
            !string.IsNullOrWhiteSpace(crashReportPath));
    }

    private static CrashFinding DetectFinding(string text, int exitCode)
    {
        foreach (var pattern in KnownPatterns)
        {
            if (pattern.Needles.All(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                var evidence = ExtractEvidence(text, pattern.Needles);
                return new CrashFinding(
                    pattern.Category,
                    pattern.Summary,
                    BuildSignature(pattern.Category, evidence, pattern.Summary),
                    evidence);
            }
        }

        var fallbackEvidence = ExtractEvidence(text, ["Exception", "Error", "Caused by:", "ResolutionException"]);
        return new CrashFinding(
            "unknown",
            $"Minecraft завершился с кодом {exitCode}. Точная причина не распознана автоматически.",
            BuildSignature("unknown", fallbackEvidence, $"exit_code_{exitCode}"),
            fallbackEvidence);
    }

    private static string BuildDetails(int exitCode, string summary, string latestLogPath, string? crashReportPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Minecraft быстро завершился после запуска.");
        builder.AppendLine();
        builder.AppendLine("Что найдено:");
        builder.AppendLine($"- {summary}");
        builder.AppendLine();
        builder.AppendLine($"Код завершения: {exitCode}");
        builder.AppendLine();
        builder.AppendLine("Файлы для диагностики:");
        builder.AppendLine(File.Exists(latestLogPath) ? latestLogPath : $"{latestLogPath} (не найден)");
        if (!string.IsNullOrWhiteSpace(crashReportPath))
        {
            builder.AppendLine(crashReportPath);
        }

        return builder.ToString();
    }

    private static string ExtractEvidence(string text, IReadOnlyList<string> needles)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (needles.Any(needle => trimmed.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                return SanitizeEvidence(trimmed);
            }
        }

        return string.Empty;
    }

    private static string SanitizeEvidence(string value)
    {
        var sanitized = Regex.Replace(value, @"[A-Za-z]:\\[^ \r\n\t]+", "<path>");
        sanitized = Regex.Replace(sanitized, @"https?://\S+", "<url>");
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        const int maxLength = 220;
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "...";
    }

    private static string BuildSignature(string category, string evidence, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(evidence) ? fallback : evidence;
        source = Regex.Replace(source.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        if (source.Length > 80)
        {
            source = source[..80].Trim('_');
        }

        return string.IsNullOrWhiteSpace(source) ? category : $"{category}:{source}";
    }

    private static string? FindNewestCrashReport(string installRoot)
    {
        var crashRoot = Path.Combine(installRoot, "crash-reports");
        if (!Directory.Exists(crashRoot))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(crashRoot, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static string ReadTail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        var text = File.ReadAllText(path);
        const int maxLength = 160_000;
        return text.Length <= maxLength ? text : text[^maxLength..];
    }

    private sealed record CrashFinding(string Category, string Summary, string Signature, string Evidence);
}

public sealed record CrashAnalysisResult(
    string Summary,
    string Details,
    string LatestLogPath,
    string? CrashReportPath,
    string Category,
    string Signature,
    string Evidence,
    bool HasCrashReport);
