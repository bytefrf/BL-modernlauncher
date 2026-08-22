using Launcher.App;

namespace Launcher.App.Services;

/// <summary>
/// Превращает разбор краша в то же <see cref="ErrorInfo"/>, которым лаунчер показывает свои
/// собственные ошибки.
/// </summary>
/// <remarks>
/// Раньше WPF-версия показывала разбор системным MessageBox: голый текст в окне, не имеющем
/// ничего общего с оформлением лаунчера, без кнопок «открыть логи» и «отправить в поддержку».
/// Avalonia уже использовала нормальное окно, но собирала ErrorInfo у себя — код разъезжался.
/// Теперь сборка одна на оба интерфейса.
/// </remarks>
public static class CrashErrorInfoBuilder
{
    public static ErrorInfo Build(CrashAnalysisResult analysis)
    {
        // Details — готовый текст анализатора: раскладываем его на строки-шаги, чтобы окно
        // выглядело как обычный разбор ошибки, а не как простыня.
        var actions = analysis.Details
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Equals(analysis.Summary, StringComparison.Ordinal))
            .ToArray();

        return new ErrorInfo(
            "Игра вылетела",
            analysis.Summary,
            actions.Length > 0 ? actions : ["Отправь лог в поддержку — разберёмся по нему."],
            $"Категория: {analysis.Category}{Environment.NewLine}" +
            $"Код выхода: {analysis.ExitCodeDescription}{Environment.NewLine}" +
            $"Найдено: {analysis.Evidence}{Environment.NewLine}{Environment.NewLine}" +
            analysis.LogTail);
    }

    /// <summary>Путь, который стоит показать игроку: крэш-репорт информативнее общего лога.</summary>
    public static string ResolveLogPath(CrashAnalysisResult analysis)
        => analysis.CrashReportPath ?? analysis.LatestLogPath;
}
