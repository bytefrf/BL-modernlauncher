using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.App.Configuration;
using Launcher.App.Models;
using Launcher.App.Platform;

namespace Launcher.App.Services;

public sealed class ArchiveInstallService(HttpClient httpClient)
{
    public async Task<ArchiveInstallSummary> InstallAsync(
        LauncherConfiguration configuration,
        ModpackManifest? manifest,
        UserSettings settings,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken,
        bool forceInstall = false)
    {
        var archiveUrl = manifest?.Modpack.ArchiveUrl ?? configuration.ModpackArchiveUrl;
        if (string.IsNullOrWhiteSpace(archiveUrl))
        {
            throw new InvalidOperationException("Direct modpack archive URL is not configured.");
        }

        var root = GetInstallRoot(configuration, manifest, settings);
        var launcherRoot = Path.Combine(root, ".launcher");
        var cacheRoot = Path.Combine(launcherRoot, "cache");
        var markerPath = Path.Combine(launcherRoot, "modpack.version");
        var version = GetVersion(configuration, manifest, archiveUrl);
        var forceReinstall = forceInstall || manifest?.Updates.ForceReinstall == true;
        // Маркер существует ⇒ это обновление/переустановка поверх прежней версии (до раннего выхода
        // ниже мы доходим только когда версия отличается или forceReinstall). Нужно для очистки пак-папок.
        var isUpdate = File.Exists(markerPath);

        Directory.CreateDirectory(cacheRoot);
        if (!forceReinstall && File.Exists(markerPath) && File.ReadAllText(markerPath).Trim().Equals(version, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new FileSyncProgress(100, "Модпак уже установлен", "Архив модпака уже установлен."));
            return new ArchiveInstallSummary(false, root, markerPath, "skipped");
        }

        // ЗАЩИТА ОТ ОТКАТА. Версия сборки живёт в трёх источниках: каталог (актуальный), статический
        // modpack-manifest.json на сайте и встроенный в exe манифест. Когда catalog.php таймаутит,
        // лаунчер уходит на фолбэк, а там версия может быть СТАРЕЕ установленной — и без этой
        // проверки он «обновлял» игрока назад, переустанавливая старый архив поверх новой сборки.
        // Ставим старую версию только если сервер явно разрешил (updates.allowDowngrade).
        if (isUpdate && !forceInstall && manifest?.Updates.AllowDowngrade != true)
        {
            var installedVersion = ExtractMarkerVersion(File.ReadAllText(markerPath));
            var candidateVersion = ExtractMarkerVersion(version);
            if (IsOlderVersion(candidateVersion, installedVersion))
            {
                progress?.Report(new FileSyncProgress(100, "Установлена более новая версия",
                    $"Сервер предлагает {candidateVersion}, а установлено {installedVersion}. Откат не выполняем."));
                return new ArchiveInstallSummary(false, root, markerPath, "downgrade-skipped");
            }
        }

        // Дельта-обновление через changeset: если это обновление уже установленной сборки (маркер есть),
        // задан changesetUrl и локальная версия совпадает с basedOn — качаем только изменённые файлы и
        // удаляем перечисленные. Иначе (отстал больше чем на шаг, нет changeset, ошибка) — полная
        // установка из ZIP (он всегда актуальный). Обновление обязано пройти в любом случае.
        if (isUpdate && manifest is not null && !string.IsNullOrWhiteSpace(manifest.Updates.ChangesetUrl)
            && manifest.Install.CleanBeforeInstall != true)
        {
            var localVersion = ExtractMarkerVersion(File.ReadAllText(markerPath));
            try
            {
                var changesetSummary = await ApplyChangesetAsync(root, manifest, version, localVersion, markerPath, launcherRoot, progress, cancellationToken);
                if (changesetSummary is not null)
                {
                    return changesetSummary;
                }
                // basedOn не совпал с локальной версией → игрок отстал → уходим на полный ZIP.
                progress?.Report(new FileSyncProgress(0, "Большое обновление, полная установка", "Версия отстала от changeset — ставим полный архив."));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception changesetException)
            {
                progress?.Report(new FileSyncProgress(0, "Дельта-обновление не удалось, полная установка", changesetException.Message));
            }
        }

        var archiveFileName = manifest?.Modpack.ArchiveFileName;
        if (string.IsNullOrWhiteSpace(archiveFileName))
        {
            archiveFileName = $"modpack-{SanitizeFileName(version)}.zip";
        }

        var archivePath = Path.Combine(cacheRoot, SanitizeFileName(archiveFileName));
        var archiveSources = ResolveArchiveSources(manifest, archiveUrl);
        if (archiveSources.Count == 0)
        {
            throw new InvalidOperationException(NoArchiveSourceMessage);
        }

        var expectedHash = manifest?.Modpack.ArchiveSha256 ?? configuration.ModpackArchiveSha256;
        var integrityRequired = manifest?.Integrity.Required ?? !string.IsNullOrWhiteSpace(expectedHash);
        if (integrityRequired && string.IsNullOrWhiteSpace(expectedHash))
        {
            // Целостность объявлена обязательной, но хэш не задан — fail-closed, иначе проверка обходится пустым хэшем.
            throw new InvalidOperationException("Integrity is required, but archiveSha256 is not configured. Refusing to install an unverified archive.");
        }

        // Хэш проверяется ВНУТРИ перебора источников: зеркало с посторонним файлом должно приводить
        // к переходу на следующий адрес, а не валить всю установку. Размер сверяется ещё раньше,
        // прямо при скачивании, поэтому сюда доходит только совпавший по размеру файл.
        var usedUrl = await DownloadArchiveFromSourcesAsync(archiveSources, archivePath, manifest?.Modpack.ArchiveSize ?? 0, expectedHash, progress, cancellationToken);
        await EnsureZipCanBeOpenedAsync(usedUrl, archivePath, manifest?.Modpack.ArchiveSize ?? 0, progress, cancellationToken);

        if (manifest?.Install.CleanBeforeInstall == true)
        {
            CleanInstallRoot(root, manifest.Install.PreservePaths);
        }
        else if (isUpdate || forceReinstall)
        {
            // Обновление существующей сборки: пересоздаём только пак-папки (mods/config/...), чтобы
            // не остались осиротевшие/дублирующиеся моды и старые конфиги. Миры/настройки не трогаем.
            CleanManagedModpackDirectories(root, manifest, progress);
        }

        progress?.Report(new FileSyncProgress(90, "Распаковка модпака", null));
        ExtractArchive(archivePath, root, manifest?.Modpack.StripPrefix ?? ".minecraft/");
        PinModpackServers(root, manifest);

        Directory.CreateDirectory(launcherRoot);
        await File.WriteAllTextAsync(markerPath, version, cancellationToken);
        progress?.Report(new FileSyncProgress(100, "Модпак установлен", $"Установлена версия модпака {version}."));
        return new ArchiveInstallSummary(true, root, markerPath, "archive");
    }

    public Task<ArchiveInstallSummary> InstallAsync(
        LauncherConfiguration configuration,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        return InstallAsync(configuration, null, new UserSettings(), progress, cancellationToken);
    }

    private const int ArchiveDownloadAttempts = 4;
    private static readonly TimeSpan ArchiveRetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SpeedLogInterval = TimeSpan.FromSeconds(2);

    internal const string NoArchiveSourceMessage =
        "Сервер сборки сейчас недоступен, а рабочего запасного адреса архива в манифесте нет. " +
        "Установленные файлы не пострадали — попробуй запустить лаунчер позже.";

    // Хосты из документации/шаблонов. Встроенный в exe резервный манифест заполнен именно ими, и без
    // этой проверки лаунчер в самом безнадёжном случае (нет ни каталога, ни статического манифеста,
    // ни кэша) честно шёл качать 800 МБ с example.com, а игрок получал «Ошибка сети, отключи VPN».
    private static readonly string[] PlaceholderHosts = ["example.com", "example.org", "example.net", "invalid"];

    // Порядок скачивания: основной адрес, затем зеркала из манифеста. Заглушки и дубли отбрасываем.
    private static List<string> ResolveArchiveSources(ModpackManifest? manifest, string archiveUrl)
    {
        var sources = new List<string>();
        AddArchiveSource(sources, manifest, archiveUrl);
        foreach (var mirror in manifest?.Modpack.ArchiveFallbackUrls ?? [])
        {
            AddArchiveSource(sources, manifest, mirror);
        }

        return sources;
    }

    private static void AddArchiveSource(List<string> sources, ModpackManifest? manifest, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsPlaceholderUrl(value))
        {
            return;
        }

        string resolved;
        try
        {
            resolved = ResolveArchiveUrl(manifest, value);
        }
        catch (InvalidOperationException)
        {
            // Относительный адрес без известного URL манифеста — использовать нечем.
            return;
        }

        if (!sources.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            sources.Add(resolved);
        }
    }

    private static bool IsPlaceholderUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return PlaceholderHosts.Any(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    // Пробует источники по очереди и возвращает тот, с которого архив реально скачался (он же пойдёт
    // на перекачку, если zip не откроется). Хэш проверяется одинаково для любого зеркала.
    private async Task<string> DownloadArchiveFromSourcesAsync(
        IReadOnlyList<string> sources,
        string archivePath,
        long expectedSize,
        string expectedHash,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var index = 0; index < sources.Count; index++)
        {
            try
            {
                await DownloadArchiveAsync(sources[index], archivePath, expectedSize, progress, cancellationToken);
                if (!string.IsNullOrWhiteSpace(expectedHash) && !HashesMatch(archivePath, expectedHash))
                {
                    // Файл не тот, что объявлен в манифесте. Удаляем, чтобы повторная попытка не
                    // «докачивала» испорченный остаток, и пробуем следующий источник.
                    File.Delete(archivePath);
                    throw new InvalidDataException($"Archive SHA-256 from {sources[index]} does not match the manifest.");
                }

                return sources[index];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
                if (index + 1 < sources.Count)
                {
                    progress?.Report(new FileSyncProgress(
                        0,
                        $"Источник недоступен, пробуем зеркало {index + 2} из {sources.Count}",
                        $"Archive source failed ({sources[index]}): {exception.Message}"));
                }
            }
        }

        throw lastError ?? new InvalidOperationException(NoArchiveSourceMessage);
    }

    // Обёртка с повторами: обрыв сети (SocketException/IOException/HttpRequestException) больше не валит установку —
    // докачка продолжается с места обрыва (через Range), до 4 попыток.
    private async Task DownloadArchiveAsync(string url, string archivePath, long expectedSize, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= ArchiveDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadArchiveCoreAsync(url, archivePath, expectedSize, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (IsTransientDownloadError(ex, cancellationToken))
            {
                lastError = ex;
                progress?.Report(new FileSyncProgress(0, $"Обрыв сети, повтор загрузки {attempt}/{ArchiveDownloadAttempts}", ex.Message));
                if (attempt < ArchiveDownloadAttempts)
                {
                    await Task.Delay(ArchiveRetryDelay, cancellationToken);
                }
            }
        }

        throw new IOException(
            "Не удалось скачать архив модпака из-за обрывов сети. Загрузка продолжится с места обрыва при следующем запуске.",
            lastError);
    }

    private static bool IsTransientDownloadError(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is HttpRequestException or IOException or System.Net.Sockets.SocketException)
        {
            return true;
        }

        return exception.InnerException is not null && IsTransientDownloadError(exception.InnerException, cancellationToken);
    }

    private async Task DownloadArchiveCoreAsync(string url, string archivePath, long expectedSize, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken)
    {
        var tempPath = archivePath + ".download";

        if (File.Exists(archivePath)
            && (expectedSize <= 0 || new FileInfo(archivePath).Length == expectedSize))
        {
            progress?.Report(new FileSyncProgress(85, $"Архив модпака уже загружен: {FormatBytes(new FileInfo(archivePath).Length)}", null));
            return;
        }

        if (File.Exists(tempPath) && expectedSize > 0 && new FileInfo(tempPath).Length > expectedSize)
        {
            File.Delete(tempPath);
        }

        var existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
            progress?.Report(new FileSyncProgress(
                Math.Clamp(existingBytes * 85d / Math.Max(expectedSize, existingBytes), 0, 85),
                $"Докачка модпака {FormatBytes(existingBytes)} из {FormatBytes(Math.Max(expectedSize, existingBytes))}",
                $"Докачка архива модпака с байта {existingBytes}."));
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var resumed = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (existingBytes > 0 && !resumed)
        {
            progress?.Report(new FileSyncProgress(0, "Сервер не поддерживает докачку, начинаю заново", "Сервер проигнорировал Range-запрос. Загрузка архива начата заново."));
            existingBytes = 0;
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        var serverContentBytes = response.Content.Headers.ContentLength ?? 0;
        var totalBytes = ResolveTotalDownloadBytes(response, expectedSize, existingBytes, serverContentBytes);
        if (expectedSize > 0 && totalBytes != expectedSize)
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw new InvalidDataException($"Archive size mismatch. Manifest: {expectedSize}, server: {totalBytes}.");
        }

        long downloadedBytes = existingBytes;
        var startedAt = Stopwatch.StartNew();
        // Первую строку скорости пишем сразу: в логе должно быть видно, что загрузка реально пошла.
        var lastSpeedLogAt = -SpeedLogInterval;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(tempPath, existingBytes > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloadedBytes += read;
                var percentage = Math.Clamp(downloadedBytes * 85d / Math.Max(totalBytes, 1), 0, 85);
                // Прогресс приходит на каждый буфер (80 КБ) — это сотни репортов в секунду. Полосу и
                // подпись обновляем каждый раз, а скорость пишем в лог не чаще SpeedLogInterval: раньше
                // каждая такая строка уходила в файл отдельным открытием, и в саппорт-бандлы попадали
                // логи на 50 000 строк, где 99,8% — «3,1 MB/с», а полезная диагностика в них тонула.
                var elapsed = startedAt.Elapsed;
                string? speedLogLine = null;
                if (elapsed - lastSpeedLogAt >= SpeedLogInterval)
                {
                    lastSpeedLogAt = elapsed;
                    speedLogLine = FormatBytesPerSecond(downloadedBytes - existingBytes, elapsed);
                }

                progress?.Report(new FileSyncProgress(
                    percentage,
                    $"Загрузка модпака {FormatBytes(downloadedBytes)} из {FormatBytes(totalBytes)}",
                    speedLogLine));
            }
        }

        if (expectedSize > 0 && new FileInfo(tempPath).Length != expectedSize)
        {
            var actualSize = new FileInfo(tempPath).Length;
            throw new InvalidDataException($"Downloaded archive size mismatch. Manifest: {expectedSize}, partial file: {actualSize}. Download can be resumed on next run.");
        }

        File.Move(tempPath, archivePath, true);

        if (expectedSize > 0 && new FileInfo(archivePath).Length != expectedSize)
        {
            var actualSize = new FileInfo(archivePath).Length;
            File.Delete(archivePath);
            throw new InvalidDataException($"Downloaded archive size mismatch. Manifest: {expectedSize}, file: {actualSize}.");
        }
    }

    private static long ResolveTotalDownloadBytes(HttpResponseMessage response, long expectedSize, long existingBytes, long contentBytes)
    {
        if (expectedSize > 0)
        {
            return expectedSize;
        }

        if (response.Content.Headers.ContentRange?.Length is long contentRangeLength)
        {
            return contentRangeLength;
        }

        return Math.Max(1, existingBytes + contentBytes);
    }

    private async Task EnsureZipCanBeOpenedAsync(string url, string archivePath, long expectedSize, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken)
    {
        if (CanOpenZip(archivePath))
        {
            return;
        }

        progress?.Report(new FileSyncProgress(86, "Проверка архива модпака", "Кэшированный архив поврежден, загружаю заново."));
        File.Delete(archivePath);
        await DownloadArchiveAsync(url, archivePath, expectedSize, progress, cancellationToken);

        if (!CanOpenZip(archivePath) && !CanOpenWith7Zip(archivePath))
        {
            throw new InvalidDataException("Downloaded modpack archive is corrupted or incomplete. The server returned a file that cannot be fully opened as a zip archive.");
        }
    }

    private static bool CanOpenZip(string archivePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            _ = archive.Entries.Count;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanListWithTar(string archivePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = TarExecutableName,
                ArgumentList = { "-tf", archivePath },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return false;
            }

            var firstLine = process.StandardOutput.ReadLine();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
            }

            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(firstLine);
        }
        catch
        {
            return false;
        }
    }

    private static bool CanOpenWith7Zip(string archivePath)
    {
        var sevenZipPath = ResolveSevenZipPath();
        if (sevenZipPath is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = sevenZipPath,
                ArgumentList = { "t", archivePath },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractArchive(string archivePath, string targetRoot, string stripPrefix)
    {
        if (CanOpenZip(archivePath))
        {
            ExtractZipSafely(archivePath, targetRoot, stripPrefix);
            return;
        }

        if (CanOpenWith7Zip(archivePath))
        {
            ExtractWith7Zip(archivePath, targetRoot, stripPrefix);
            return;
        }

        throw new InvalidDataException("Downloaded modpack archive is corrupted or incomplete. The server returned a file that cannot be fully opened as a zip archive.");
    }

    private static void ExtractZipSafely(string archivePath, string targetRoot, string stripPrefix)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var fullTargetRoot = Path.GetFullPath(targetRoot);

        foreach (var entry in archive.Entries)
        {
            var relativePath = NormalizeArchivePath(entry.FullName, stripPrefix);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(fullTargetRoot, relativePath));
            if (!IsWithinDirectory(destinationPath, fullTargetRoot))
            {
                throw new InvalidOperationException($"Archive entry escapes install directory: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, true);
        }
    }

    private static void ExtractWithTar(string archivePath, string targetRoot, string stripPrefix)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"modpack-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = TarExecutableName,
                ArgumentList = { "-xf", archivePath, "-C", stagingRoot },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException($"Could not start {TarExecutableName}.");

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"{TarExecutableName} could not extract archive: {stderr}");
            }

            CopyDirectoryFlatteningMinecraft(stagingRoot, targetRoot, stripPrefix);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, true);
            }
        }
    }

    private static void ExtractWith7Zip(string archivePath, string targetRoot, string stripPrefix)
    {
        var sevenZipPath = ResolveSevenZipPath() ?? throw new FileNotFoundException("7-Zip executable was not found.");
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"modpack-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = sevenZipPath,
                ArgumentList = { "x", archivePath, $"-o{stagingRoot}", "-y" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Could not start 7-Zip.");

            var output = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"7-Zip could not extract archive. Output: {output} {stderr}");
            }

            CopyDirectoryFlatteningMinecraft(stagingRoot, targetRoot, stripPrefix);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, true);
            }
        }
    }

    /// <summary>
    /// Имя tar: на Windows это встроенный в систему <c>tar.exe</c>, на Unix — обычный <c>tar</c>
    /// (в macOS это bsdtar, распаковку zip он тоже умеет).
    /// </summary>
    private static string TarExecutableName => HostPlatform.IsWindows ? "tar.exe" : "tar";

    private static string? ResolveSevenZipPath()
    {
        if (HostPlatform.IsWindows)
        {
            var candidates = new[]
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
                "7z.exe"
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        // На Unix 7-Zip зовётся по-разному: 7zz — официальная сборка, 7z и 7za — из пакетов
        // p7zip. Штатной установки нет, поэтому ищем и по абсолютным путям, и по PATH.
        var unixCandidates = new[]
        {
            "/usr/bin/7zz",
            "/usr/bin/7z",
            "/usr/bin/7za",
            "/usr/local/bin/7zz",
            "/usr/local/bin/7z",
            "/opt/homebrew/bin/7zz",
            "/opt/homebrew/bin/7z"
        };

        return unixCandidates.FirstOrDefault(File.Exists)
            ?? ResolveFromPath("7zz")
            ?? ResolveFromPath("7z")
            ?? ResolveFromPath("7za");
    }

    /// <summary>
    /// Ищет исполняемый файл в каталогах переменной PATH.
    /// </summary>
    private static string? ResolveFromPath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Битый элемент PATH не должен ломать поиск целиком.
            }
        }

        return null;
    }

    private static void CopyDirectoryFlatteningMinecraft(string stagingRoot, string targetRoot, string stripPrefix)
    {
        foreach (var file in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(stagingRoot, file);
            relativePath = NormalizeArchivePath(relativePath, stripPrefix);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
            var fullTargetRoot = Path.GetFullPath(targetRoot);
            if (!IsWithinDirectory(destinationPath, fullTargetRoot))
            {
                throw new InvalidOperationException($"Archive entry escapes install directory: {relativePath}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, true);
        }
    }

    private static string NormalizeArchivePath(string entryName, string stripPrefix)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');
        var normalizedPrefix = stripPrefix.Replace('\\', '/').TrimStart('/');
        if (!normalizedPrefix.EndsWith('/'))
        {
            normalizedPrefix += "/";
        }

        var prefixDirectory = normalizedPrefix.TrimEnd('/');
        if (normalized.Equals(prefixDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (normalized.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[normalizedPrefix.Length..];
        }

        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    // ---- Дельта-обновление через changeset ------------------------------------------------------

    private const int ChangesetDownloadAttempts = 4;

    // Извлекает версию сборки из содержимого маркера modpack.version (формат "id:version").
    /// <summary>
    /// Публичная обёртка для UI: версия из манифеста старее установленной (маркеры вида
    /// «tfgm:0.13.4» тоже принимаются). Нужна, чтобы кнопка не звала «Обновить» на откат.
    /// </summary>
    public static bool IsManifestVersionOlder(string manifestVersion, string installedMarker) =>
        IsOlderVersion(ExtractMarkerVersion(manifestVersion), ExtractMarkerVersion(installedMarker));

    /// <summary>
    /// Маркеры указывают на РАЗНЫЕ сборки: у обеих строк есть явный id (часть до двоеточия)
    /// и эти id не совпадают. Без id хотя бы у одной стороны сравнивать нечего — тогда false.
    /// Нужна кнопке: числа версий разных сборок несравнимы, и защита от отката на них врёт.
    /// </summary>
    public static bool IsDifferentModpack(string manifestVersion, string installedMarker)
    {
        var left = ExtractMarkerId(manifestVersion);
        var right = ExtractMarkerId(installedMarker);
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        return !left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Сравнение версий сборок вида 0.13.4 / 0.12.8-flat / v59 / 1.15.3. Числовые части сравниваем
    /// по значению, суффиксы игнорируем. Если хотя бы одну версию распарсить не удалось — считаем,
    /// что откат не доказан (возвращаем false): лучше поставить, чем застрять на старой версии.
    /// </summary>
    private static bool IsOlderVersion(string candidate, string installed)
    {
        var left = ParseVersionParts(candidate);
        var right = ParseVersionParts(installed);
        if (left.Count == 0 || right.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var a = i < left.Count ? left[i] : 0;
            var b = i < right.Count ? right[i] : 0;
            if (a != b)
            {
                return a < b;
            }
        }

        return false;
    }

    private static List<int> ParseVersionParts(string value)
    {
        var parts = new List<int>();
        foreach (Match match in Regex.Matches(value ?? string.Empty, @"\d+"))
        {
            if (int.TryParse(match.Value, out var number))
            {
                parts.Add(number);
            }

            if (parts.Count == 4)
            {
                break;
            }
        }

        return parts;
    }

    private static string ExtractMarkerId(string marker)
    {
        var value = (marker ?? string.Empty).Trim();
        var colon = value.IndexOf(':');
        if (colon <= 0)
        {
            return string.Empty;
        }

        // Отсекаем URL (в конфигурации версией может быть просто адрес архива): "https://…"
        // не должно читаться как id сборки «https».
        var id = value[..colon].Trim();
        return value.AsSpan(colon).StartsWith("://") || id.Contains('/') ? string.Empty : id;
    }

    private static string ExtractMarkerVersion(string marker)
    {
        var value = (marker ?? string.Empty).Trim();
        var colon = value.IndexOf(':');
        return colon >= 0 ? value[(colon + 1)..].Trim() : value;
    }

    // Применяет changeset текущей версии, ЕСЛИ его basedOn совпадает с локальной версией. Качает файлы
    // из update[] (с проверкой хэша; совпавшие пропускает — самолечение), удаляет файлы из delete[].
    // Возвращает null, если changeset не подходит (basedOn ≠ локальная версия) — тогда вызывающий код
    // ставит полный ZIP. Данные игрока (PreservePaths + saves/screenshots/backups) не трогаются.
    private async Task<ArchiveInstallSummary?> ApplyChangesetAsync(
        string root,
        ModpackManifest manifest,
        string version,
        string localVersion,
        string markerPath,
        string launcherRoot,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new FileSyncProgress(0, "Проверка обновления", "Загружаю список изменений сборки."));
        var changesetUri = manifest.ResolveUri(manifest.Updates.ChangesetUrl);
        var changeset = await LoadChangesetAsync(changesetUri, cancellationToken);

        // basedOn должен совпасть с тем, что стоит у игрока; иначе он отстал → полный ZIP.
        if (string.IsNullOrWhiteSpace(changeset.BasedOn) ||
            !changeset.BasedOn.Trim().Equals(localVersion, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fullRoot = Path.GetFullPath(root);

        // Скачиваем/обновляем файлы. Совпавший по хэшу локальный файл пропускаем.
        var toDownload = new List<(ChangesetFile File, string Target)>();
        foreach (var file in changeset.Update ?? [])
        {
            if (string.IsNullOrWhiteSpace(file.Path) || string.IsNullOrWhiteSpace(file.Url))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(fullRoot, NormalizeRelativePath(file.Path)));
            if (!IsWithinDirectory(target, fullRoot))
            {
                throw new InvalidOperationException($"Файл changeset выходит за папку установки: {file.Path}");
            }

            if (File.Exists(target) && !string.IsNullOrWhiteSpace(file.Sha256) && HashesMatch(target, file.Sha256))
            {
                continue;
            }

            toDownload.Add((file, target));
        }

        var totalBytes = Math.Max(1L, toDownload.Sum(item => Math.Max(item.File.Size, 1)));
        long downloadedBytes = 0;
        var startedAt = Stopwatch.StartNew();

        for (var index = 0; index < toDownload.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (file, target) = toDownload[index];
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var fileUri = new Uri(changesetUri, file.Url);

            var before = downloadedBytes;
            await DownloadChangesetFileAsync(fileUri, target, file, bytes =>
            {
                var current = before + bytes;
                downloadedBytes = current;
                progress?.Report(new FileSyncProgress(
                    Math.Clamp(current * 95d / totalBytes, 0, 95),
                    $"Загрузка обновления {FormatBytes(current)} из {FormatBytes(totalBytes)}",
                    $"{FormatBytesPerSecond(current, startedAt.Elapsed)} · {file.Path}"));
            }, cancellationToken);

            downloadedBytes = before + Math.Max(file.Size, 0);
            progress?.Report(new FileSyncProgress(
                Math.Clamp(downloadedBytes * 95d / totalBytes, 0, 95),
                $"Обновлено файлов {index + 1} из {toDownload.Count}",
                $"Обновлён {file.Path}"));
        }

        // Удаляем перечисленные в delete[] файлы (кроме данных игрока — защита от кривого changeset).
        var deleted = DeleteChangesetFiles(fullRoot, manifest, changeset.Delete);
        if (deleted > 0)
        {
            progress?.Report(new FileSyncProgress(97, "Удаление устаревших файлов", $"Удалено файлов: {deleted}"));
        }

        PinModpackServers(root, manifest);
        Directory.CreateDirectory(launcherRoot);
        await File.WriteAllTextAsync(markerPath, version, cancellationToken);

        var changedTotal = toDownload.Count + deleted;
        var summaryText = changedTotal == 0 ? "Сборка уже актуальна" : $"Обновлено файлов: {toDownload.Count}, удалено: {deleted}";
        progress?.Report(new FileSyncProgress(100, summaryText, $"Дельта-обновление до {version} завершено."));
        return new ArchiveInstallSummary(changedTotal > 0, root, markerPath, "delta", toDownload.Count);
    }

    private async Task<ModpackChangeset> LoadChangesetAsync(Uri changesetUri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(changesetUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<ModpackChangeset>(stream, ChangesetJsonOptions(), cancellationToken)
               ?? throw new InvalidOperationException("Сервер вернул пустой changeset сборки.");
    }

    // Качает один файл changeset с ретраями и докачкой по Range; проверяет размер (если известен) и SHA-256.
    private async Task DownloadChangesetFileAsync(Uri url, string targetPath, ChangesetFile file, Action<long> onBytes, CancellationToken cancellationToken)
    {
        var tempPath = targetPath + ".download";
        Exception? lastError = null;

        for (var attempt = 1; attempt <= ChangesetDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
                if (file.Size > 0 && existingBytes > file.Size)
                {
                    File.Delete(tempPath);
                    existingBytes = 0;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existingBytes > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existingBytes, null);
                }

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var resumed = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (existingBytes > 0 && !resumed)
                {
                    existingBytes = 0;
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }

                onBytes(existingBytes);
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(tempPath, existingBytes > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long written = existingBytes;
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        written += read;
                        onBytes(written);
                    }
                }

                if (file.Size > 0 && new FileInfo(tempPath).Length != file.Size)
                {
                    File.Delete(tempPath);
                    throw new InvalidDataException($"Размер файла не совпал: {file.Path}.");
                }

                if (!string.IsNullOrWhiteSpace(file.Sha256) && !HashesMatch(tempPath, file.Sha256))
                {
                    File.Delete(tempPath);
                    throw new InvalidOperationException($"SHA-256 не совпал после загрузки: {file.Path}.");
                }

                File.Move(tempPath, targetPath, true);
                return;
            }
            catch (Exception ex) when (IsTransientDownloadError(ex, cancellationToken))
            {
                lastError = ex;
                if (attempt < ChangesetDownloadAttempts)
                {
                    await Task.Delay(ArchiveRetryDelay, cancellationToken);
                }
            }
        }

        throw new IOException($"Не удалось скачать файл сборки {file.Path} из-за обрывов сети.", lastError);
    }

    // Удаляет файлы из delete[] changeset'а. Защита: только внутри папки установки и НИКОГДА не трогаем
    // данные игрока (PreservePaths + saves/screenshots/backups), даже если changeset их ошибочно перечислит.
    private static int DeleteChangesetFiles(string fullRoot, ModpackManifest manifest, IReadOnlyCollection<string> deletePaths)
    {
        if (deletePaths is null || deletePaths.Count == 0)
        {
            return 0;
        }

        var preserve = manifest.Install.PreservePaths
            .Concat(AlwaysPreservedPaths)
            .Select(path => Path.GetFullPath(Path.Combine(fullRoot, path.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();

        var deleted = 0;
        foreach (var relativePath in deletePaths)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(fullRoot, NormalizeRelativePath(relativePath)));
            if (!IsWithinDirectory(target, fullRoot) || target.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (preserve.Any(p => IsWithinDirectory(target, p)))
            {
                continue;
            }

            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                    deleted++;
                    RemoveEmptyParents(target, fullRoot);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Занятый файл — оставляем; следующее обновление доберёт.
            }
        }

        return deleted;
    }

    // Убирает опустевшие после удаления папки, не выходя за корень установки.
    private static void RemoveEmptyParents(string file, string fullRoot)
    {
        var dir = Path.GetDirectoryName(file);
        while (!string.IsNullOrEmpty(dir) && IsWithinDirectory(dir, fullRoot) && !dir.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
                else
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }
        }
    }

    private static JsonSerializerOptions ChangesetJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // Пиннит игровые сервера сборки в servers.dat. Каждая сборка задаёт свои сервера в манифесте
    // (поле servers). Если их нет — фолбэк на исторический tfgm-дефолт (обратная совместимость).
    private static void PinModpackServers(string root, ModpackManifest? manifest)
    {
        var servers = manifest?.Servers?
            .Where(server => !string.IsNullOrWhiteSpace(server.Host))
            .ToList();

        if (servers is { Count: > 0 })
        {
            // Свои сервера сборки — без подсева tfgm-шаблона, чтобы у сборки был только её сервер.
            foreach (var server in servers)
            {
                var name = string.IsNullOrWhiteSpace(server.Name) ? server.Host : server.Name;
                MultiplayerServerListService.EnsureServer(root, name, server.Host.Trim(), seedEmbeddedTemplate: false);
            }

            return;
        }

        // Старые манифесты без секции servers — прежнее поведение для tfgm.
        MultiplayerServerListService.EnsureServer(root, "BL-modern TFGM #1", "play.bl-modern.ru");
        MultiplayerServerListService.EnsureServer(root, "BL-modern TFGM #2", "tfgm2.bl-modern.ru");
    }

    private static string GetInstallRoot(LauncherConfiguration configuration, ModpackManifest? manifest, UserSettings settings)
    {
        var root = manifest?.Install.Root;
        // При мульти-сборочном каталоге изолируем сборки по id (см. UserSettings.ResolveInstallRoot).
        var catalogModpackId = configuration.IsMultiModpackCatalog ? manifest?.Modpack.Id : null;
        return settings.ResolveInstallRoot(root ?? string.Empty, configuration.GetDistributionRoot(), catalogModpackId);
    }

    private static string GetVersion(LauncherConfiguration configuration, ModpackManifest? manifest, string archiveUrl)
    {
        if (manifest is not null)
        {
            return $"{manifest.Modpack.Id}:{manifest.Modpack.Version}";
        }

        return string.IsNullOrWhiteSpace(configuration.ModpackVersion)
            ? archiveUrl
            : configuration.ModpackVersion;
    }

    private static string ResolveArchiveUrl(ModpackManifest? manifest, string archiveUrl)
    {
        return manifest?.ResolveUri(archiveUrl).ToString() ?? archiveUrl;
    }

    // Пользовательские данные, которые нельзя стирать при переустановке независимо от манифеста.
    private static readonly string[] AlwaysPreservedPaths = ["saves", "screenshots", "backups"];

    private static void CleanInstallRoot(string root, IReadOnlyCollection<string> preservePaths)
    {
        var fullRoot = Path.GetFullPath(root);
        // ЗАЩИТА ОТ ПОТЕРИ ДАННЫХ: cleanBeforeInstall удаляет всё в папке установки, кроме preserve-списка.
        // Если игрок указал корнем диска (D:\) или системную/личную папку — это снесло бы его файлы.
        EnsureSafeToClean(fullRoot);
        Directory.CreateDirectory(fullRoot);
        // Не стираем вложенные папки ДРУГИХ установленных сборок (у них свой маркер modpack.version):
        // при кастомном пути новая сборка ставится в подпапку базовой, и полная чистка одной сборки
        // не должна сносить соседнюю.
        var preserve = preservePaths
            .Concat(AlwaysPreservedPaths)
            .Select(path => Path.GetFullPath(Path.Combine(fullRoot, path.Replace('/', Path.DirectorySeparatorChar))))
            .Concat(GetNestedModpackInstallDirectories(fullRoot))
            .ToArray();
        var launcherFiles = GetProtectedLauncherFiles(fullRoot);

        foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
        {
            if (preserve.Any(path => IsWithinDirectory(file, path)))
            {
                continue;
            }

            if (launcherFiles.Contains(file))
            {
                continue;
            }

            File.Delete(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(fullRoot, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            if (preserve.Any(path => IsWithinDirectory(directory, path)))
            {
                continue;
            }

            if (launcherFiles.Any(file => file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    // При обновлении сносим только папки, которыми управляет сборка (mods/config/kubejs/...),
    // чтобы убрать осиротевшие/дублирующиеся моды. Дальше распаковка восстановит их из нового архива.
    // Защита: не выходим за пределы root, не трогаем сам root и пути из preserve-списка (данные игрока).
    private static void CleanManagedModpackDirectories(string root, ModpackManifest? manifest, IProgress<FileSyncProgress>? progress)
    {
        var resetPaths = manifest?.Install.UpdateResetPaths;
        if (resetPaths is null || resetPaths.Count == 0)
        {
            return;
        }

        var fullRoot = Path.GetFullPath(root);
        var preserve = (manifest?.Install.PreservePaths ?? new List<string>())
            .Concat(AlwaysPreservedPaths)
            .Select(path => Path.GetFullPath(Path.Combine(fullRoot, path.Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();

        var cleaned = new List<string>();
        foreach (var relativePath in resetPaths)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            // Не выходим за папку установки и не сносим её саму.
            if (!IsWithinDirectory(target, fullRoot) || target.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Никогда не удаляем то, что в preserve-списке (saves/options/...) — даже при кривом манифесте.
            if (preserve.Any(p => IsWithinDirectory(target, p) || IsWithinDirectory(p, target)))
            {
                continue;
            }

            if (!Directory.Exists(target))
            {
                continue;
            }

            // Если в этой папке лежит маркер другой сборки — это вложенная установка, не наша пак-папка.
            if (File.Exists(Path.Combine(target, ".launcher", "modpack.version")))
            {
                continue;
            }

            try
            {
                Directory.Delete(target, true);
                cleaned.Add(relativePath);
            }
            catch
            {
                // Занятый/недоступный файл (антивирус/процесс) — распаковка всё равно перезапишет содержимое.
            }
        }

        if (cleaned.Count > 0)
        {
            progress?.Report(new FileSyncProgress(88, "Обновление: очистка папок сборки", $"Очищены папки перед обновлением: {string.Join(", ", cleaned)}"));
        }
    }

    // Непосредственные подпапки root, в которых лежит маркер другой установленной сборки
    // (.launcher/modpack.version). Их нельзя удалять при полной чистке/удалении текущей сборки.
    private static IReadOnlyList<string> GetNestedModpackInstallDirectories(string fullRoot)
    {
        var result = new List<string>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(fullRoot))
            {
                if (File.Exists(Path.Combine(directory, ".launcher", "modpack.version")))
                {
                    result.Add(Path.GetFullPath(directory));
                }
            }
        }
        catch
        {
            // Нет доступа к перечислению — лучше ничего не удалять рискованно, но это лишь список preserve.
        }

        return result;
    }

    private static bool IsWithinDirectory(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    // Никогда не выполняем массовую очистку в корне диска или в системной/личной папке —
    // иначе при cleanBeforeInstall лаунчер удалил бы личные файлы пользователя.
    private static void EnsureSafeToClean(string fullRoot)
    {
        var normalized = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathRoot = Path.GetPathRoot(fullRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.IsNullOrEmpty(normalized)
            || normalized.Length <= 2
            || normalized.Equals(pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Папка установки указана как корень диска — установка отменена ради безопасности данных. " +
                "Укажи отдельную папку для сборки (например D:\\BL-modern) в настройках лаунчера.");
        }

        var protectedRoots = new[]
        {
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.CommonApplicationData
        };

        foreach (var folder in protectedRoots)
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)
                && normalized.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Папка установки совпадает с системной или личной папкой — установка отменена ради безопасности данных. " +
                    "Укажи отдельную папку для сборки в настройках лаунчера.");
            }
        }
    }

    private static HashSet<string> GetProtectedLauncherFiles(string installRoot)
    {
        var protectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var launcherBaseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var launcherExecutablePath = Path.GetFullPath(Environment.ProcessPath ?? Path.Combine(launcherBaseDirectory, "Launcher.App.exe"));

        if (!launcherExecutablePath.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
        {
            return protectedFiles;
        }

        protectedFiles.Add(launcherExecutablePath);

        var launcherPdbPath = Path.ChangeExtension(launcherExecutablePath, ".pdb");
        if (File.Exists(launcherPdbPath))
        {
            protectedFiles.Add(launcherPdbPath);
        }

        return protectedFiles;
    }

    private static bool HashesMatch(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var actual = Convert.ToHexString(sha256.ComputeHash(stream));
        return actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static string NormalizeRelativePath(string value)
    {
        return value.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.0} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
    }

    private static string FormatBytesPerSecond(long bytes, TimeSpan elapsed)
    {
        var seconds = Math.Max(1, elapsed.TotalSeconds);
        var perSecond = (long)(bytes / seconds);
        return $"{FormatBytes(perSecond)}/с";
    }
}

public sealed record ArchiveInstallSummary(
    bool Installed,
    string InstallPath,
    string VersionMarkerPath,
    string Mode = "archive",
    int ChangedFiles = 0);
