using System.IO;
using System.Text;

namespace Launcher.App;

/// <summary>
/// Тексты вокруг разобранной ошибки: файл лога рядом со сборкой и выжимка для буфера обмена.
/// </summary>
/// <remarks>
/// Вынесено из <c>MainWindow.xaml.cs</c> и <c>ErrorWindow.xaml.cs</c> (WPF), чтобы Avalonia-версия
/// писала ТОТ ЖЕ лог. Иначе игрок с Linux прислал бы в поддержку файл другого формата, и разбор
/// саппорт-бандлов пришлось бы учить двум вариантам.
/// </remarks>
public static class ErrorReport
{
    /// <summary>
    /// Пишет лог ошибки в <c>&lt;installRoot&gt;/.launcher/logs</c> и возвращает путь к нему.
    /// </summary>
    /// <param name="failure">
    /// Причина, по которой лог не удалось записать, или <c>null</c>. Само по себе это не повод
    /// ронять показ окна ошибки: игроку важнее увидеть разбор, чем узнать про недоступную папку.
    /// </param>
    public static string Write(
        string installRoot,
        ErrorInfo errorInfo,
        Exception exception,
        string launcherVersion,
        string modpackVersion,
        string manifestUrl,
        out string? failure)
    {
        try
        {
            var logRoot = Path.Combine(installRoot, ".launcher", "logs");
            Directory.CreateDirectory(logRoot);

            var logPath = Path.Combine(logRoot, $"launcher-error-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(logPath, BuildLogContent(
                installRoot, errorInfo, exception, launcherVersion, modpackVersion, manifestUrl));

            failure = null;
            return logPath;
        }
        catch (Exception logException)
        {
            failure = logException.Message;
            return string.Empty;
        }
    }

    /// <summary>Содержимое файла лога. Формат дословно тот же, что был в WPF-версии.</summary>
    public static string BuildLogContent(
        string installRoot,
        ErrorInfo errorInfo,
        Exception exception,
        string launcherVersion,
        string modpackVersion,
        string manifestUrl)
        => new StringBuilder()
            .AppendLine($"Время: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"Заголовок: {errorInfo.Title}")
            .AppendLine($"Описание: {errorInfo.Summary}")
            .AppendLine()
            .AppendLine("Что сделать:")
            .AppendLine(string.Join(Environment.NewLine, errorInfo.Actions.Select(action => "- " + action)))
            .AppendLine()
            .AppendLine($"Версия лаунчера: {launcherVersion}")
            .AppendLine($"Версия модпака: {modpackVersion}")
            .AppendLine($"Папка установки: {installRoot}")
            .AppendLine($"Манифест: {manifestUrl}")
            .AppendLine($"ОС: {Environment.OSVersion.VersionString}")
            .AppendLine()
            .AppendLine("Технические детали:")
            .AppendLine(exception.ToString())
            .ToString();

    /// <summary>Текст кнопки «Скопировать» — его игрок вставляет в чат поддержки.</summary>
    public static string BuildClipboardText(ErrorInfo errorInfo, string logPath)
        => new StringBuilder()
            .AppendLine(errorInfo.Title)
            .AppendLine()
            .AppendLine(errorInfo.Summary)
            .AppendLine()
            .AppendLine("Что сделать:")
            .AppendLine(string.Join(Environment.NewLine, errorInfo.Actions.Select(action => "- " + action)))
            .AppendLine()
            .AppendLine("Лог:")
            .AppendLine(logPath)
            .AppendLine()
            .AppendLine("Технические детали:")
            .AppendLine(errorInfo.TechnicalDetails)
            .ToString();
}

/// <summary>
/// Итог отправки лога в поддержку. Раньше запись жила в <c>ErrorWindow.xaml.cs</c> (WPF) —
/// Avalonia-версия не могла её увидеть, не таща за собой WPF.
/// </summary>
public sealed record SupportLogSendResult(bool Sent, string PackagePath, string Message);
