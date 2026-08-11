using System.Diagnostics;
using System.Net.Http;
using Launcher.App.Configuration;
using Launcher.App.Models;

namespace Launcher.App.Services;

/// <summary>Чем закончилась установка или обновление сборки.</summary>
/// <param name="Mode">Что именно произошло: <c>archive</c>, <c>delta</c>, <c>sync</c> или <c>none</c>.</param>
/// <param name="Installed">Были ли записаны файлы (false — всё уже стояло).</param>
/// <param name="DurationMs">Сколько заняла операция целиком.</param>
/// <param name="ChangedFiles">Сколько файлов изменилось.</param>
/// <param name="StatusText">Готовый текст для строки состояния и подвала.</param>
/// <param name="SkippedFiles">Сколько файлов пропущено как уже актуальные (только режим синхронизации).</param>
public sealed record GameInstallOutcome(
    string Mode,
    bool Installed,
    long DurationMs,
    int ChangedFiles,
    string StatusText,
    int SkippedFiles = 0);

/// <summary>
/// Обратные вызовы, через которые установка сообщает о себе интерфейсу. Иначе ядру пришлось бы
/// знать про окно, а этого допускать нельзя — код общий для WPF и Avalonia.
/// </summary>
/// <param name="Log">Строка в журнал лаунчера.</param>
/// <param name="ReportRuntimeProgress">Прогресс подготовки рантайма (доля 0..45 общей шкалы).</param>
/// <param name="ReportArchiveProgress">Прогресс установки архива (доля 45..100 общей шкалы).</param>
/// <param name="ReportSyncProgress">Прогресс режима синхронизации файлов.</param>
/// <param name="SyncOptionalModsAsync">Возврат выбранных игроком доп. модов после обновления.</param>
public sealed record GameInstallCallbacks(
    Action<string> Log,
    IProgress<FileSyncProgress> ReportRuntimeProgress,
    IProgress<FileSyncProgress> ReportArchiveProgress,
    IProgress<FileSyncProgress> ReportSyncProgress,
    Func<Task> SyncOptionalModsAsync);

/// <summary>
/// Ставит и обновляет файлы игры. Вынесено из <c>MainWindow.xaml.cs</c>: сам порядок действий
/// (проверка места → рантайм → архив → доп. моды) от интерфейса не зависит.
/// </summary>
public sealed class GameInstallOrchestrator(HttpClient httpClient)
{
    /// <summary>
    /// Тяжёлые шаги (SHA-256 по архиву в сотни МБ, распаковка) синхронные и грузят диск и процессор.
    /// Они уводятся на фоновый поток: иначе окно помечается системой как «не отвечает».
    /// Прогресс идёт через <see cref="IProgress{T}"/> и сам возвращается в поток интерфейса.
    /// </summary>
    public async Task<GameInstallOutcome> EnsureGameFilesAsync(
        LauncherConfiguration configuration,
        ModpackManifest? modpackManifest,
        LauncherManifest? launcherManifest,
        UserSettings settings,
        GameInstallCallbacks callbacks,
        bool forceArchiveInstall = false,
        LauncherConfiguration? syncConfiguration = null,
        CancellationToken cancellationToken = default)
    {
        var overallStopwatch = Stopwatch.StartNew();

        // В режиме каталога ModpackManifestUrl пуст, но манифест выбранной сборки загружен —
        // это тоже archive-режим.
        var useArchiveMode = modpackManifest is not null || configuration.UsesDirectModpackArchive();

        if (useArchiveMode)
        {
            var runtimeInstaller = new RuntimeInstallService(httpClient);
            await Task.Run(
                () => runtimeInstaller.EnsureRuntimeAsync(
                    configuration, modpackManifest, settings, callbacks.ReportRuntimeProgress, cancellationToken),
                cancellationToken);

            var installer = new ArchiveInstallService(httpClient);
            var archiveSummary = await Task.Run(
                () => installer.InstallAsync(
                    configuration, modpackManifest, settings, callbacks.ReportArchiveProgress,
                    cancellationToken, forceArchiveInstall),
                cancellationToken);

            var isDelta = archiveSummary.Mode == "delta";
            var changedFiles = isDelta ? archiveSummary.ChangedFiles : archiveSummary.Installed ? 1 : 0;

            callbacks.Log(archiveSummary.Mode switch
            {
                "delta" => $"Delta update applied to {archiveSummary.InstallPath} ({archiveSummary.ChangedFiles} files)",
                "archive" => $"Archive installed to {archiveSummary.InstallPath}",
                _ => $"Archive already installed at {archiveSummary.InstallPath}"
            });

            // Обновление чистит mods/ — возвращаем туда выбранные игроком одобренные моды.
            await callbacks.SyncOptionalModsAsync();

            var doneStatus = forceArchiveInstall
                ? "Проверка модпака завершена"
                : isDelta && archiveSummary.Installed
                    ? $"Сборка обновлена (файлов: {archiveSummary.ChangedFiles})"
                    : "Модпак установлен";

            return new GameInstallOutcome(
                archiveSummary.Mode,
                archiveSummary.Installed,
                overallStopwatch.ElapsedMilliseconds,
                changedFiles,
                doneStatus);
        }

        if (launcherManifest is null)
        {
            throw new InvalidOperationException("Manifest is not loaded.");
        }

        // Режим синхронизации работает по «эффективной» конфигурации: в ней подставлена
        // выбранная игроком папка установки. Для archive-режима она не нужна — там путь
        // вычисляют сами установщики.
        var sync = new FileSyncService(httpClient);
        var summary = await Task.Run(
            () => sync.SyncAsync(syncConfiguration ?? configuration, launcherManifest, callbacks.ReportSyncProgress, cancellationToken),
            cancellationToken);

        callbacks.Log($"Sync finished. Downloaded: {summary.DownloadedFiles}, skipped: {summary.SkippedFiles}.");

        return new GameInstallOutcome(
            "sync",
            summary.DownloadedFiles > 0,
            overallStopwatch.ElapsedMilliseconds,
            summary.DownloadedFiles,
            $"Сборка обновлена, загружено файлов: {summary.DownloadedFiles}",
            summary.SkippedFiles);
    }
}
