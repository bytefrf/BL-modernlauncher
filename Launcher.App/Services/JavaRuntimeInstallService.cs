using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed class JavaRuntimeInstallService(HttpClient httpClient)
{
    private const string TemurinJava17JreUrl = "https://api.adoptium.net/v3/binary/latest/17/ga/windows/x64/jre/hotspot/normal/eclipse?project=jdk";
    private const int DownloadAttemptsPerUrl = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public async Task<string> EnsureJava17Async(
        string installRoot,
        ManifestRuntimeInfo runtime,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var javaPath = JavaValidationService.GetManagedConsoleJavaPath(installRoot);
        var runtimeRoot = JavaValidationService.GetManagedJavaHome(installRoot);

        if (IsJavaRuntimeComplete(runtimeRoot))
        {
            progress?.Report(new FileSyncProgress(100, "Локальная Java 17 уже установлена", null));
            return javaPath;
        }

        // java.exe мог остаться от прерванной распаковки, но без lib\jvm.cfg JRE нерабочая
        // (Forge installer падает с "could not open jvm.cfg"). Сносим неполную и ставим заново.
        if (Directory.Exists(runtimeRoot))
        {
            progress?.Report(new FileSyncProgress(0, "Обнаружена неполная Java 17 — переустанавливаю", null));
            try { Directory.Delete(runtimeRoot, true); } catch { /* перезапишется при копировании */ }
        }

        var cacheRoot = Path.Combine(installRoot, ".launcher", "cache");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeRoot)!);

        var archivePath = Path.Combine(cacheRoot, "temurin-jre-17-windows-x64.zip");
        await DownloadRuntimeAsync(archivePath, runtime, progress, cancellationToken);

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"launcher-java-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        try
        {
            progress?.Report(new FileSyncProgress(90, "Распаковка локальной Java 17", null));
            try
            {
                ZipFile.ExtractToDirectory(archivePath, stagingRoot, true);
            }
            catch (InvalidDataException)
            {
                // Кэшированный архив битый/недокачан — удаляем, чтобы при следующем запуске скачать заново.
                try { File.Delete(archivePath); } catch { }
                throw new InvalidDataException("Архив Java 17 повреждён. Он будет перекачан при следующем запуске лаунчера.");
            }

            var extractedJava = Directory
                .EnumerateFiles(stagingRoot, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(IsJavaExecutableInBinDirectory)
                ?? throw new InvalidDataException("В архиве Java 17 не найден bin\\java.exe.");

            var extractedHome = Directory.GetParent(Path.GetDirectoryName(extractedJava)!)!.FullName;
            if (Directory.Exists(runtimeRoot))
            {
                Directory.Delete(runtimeRoot, true);
            }

            CopyDirectory(extractedHome, runtimeRoot);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, true);
            }
        }

        if (!IsJavaRuntimeComplete(runtimeRoot))
        {
            // Архив оказался неполным (нет jvm.cfg и т.п.). Чистим кэш-архив, чтобы следующая попытка скачала заново.
            try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { }
            throw new FileNotFoundException(
                $"Java 17 установлена не полностью (нет bin\\java.exe или lib\\jvm.cfg): {runtimeRoot}. " +
                "Архив будет перекачан при следующем запуске.");
        }

        progress?.Report(new FileSyncProgress(100, "Локальная Java 17 установлена", $"Java 17 установлена: {javaPath}"));
        return javaPath;
    }

    private async Task DownloadRuntimeAsync(
        string archivePath,
        ManifestRuntimeInfo runtime,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(archivePath) && new FileInfo(archivePath).Length > 0)
        {
            // Доверяем кэшу только если это валидный zip — иначе распаковка даст неполную Java.
            if (CanOpenZip(archivePath))
            {
                progress?.Report(new FileSyncProgress(85, $"Архив Java 17 уже загружен: {FormatBytes(new FileInfo(archivePath).Length)}", null));
                return;
            }

            progress?.Report(new FileSyncProgress(5, "Кэш Java 17 повреждён — качаю заново", null));
            try { File.Delete(archivePath); } catch { }
        }

        var urls = BuildRuntimeUrls(runtime);
        var tempPath = archivePath + ".download";
        Exception? lastError = null;

        foreach (var url in urls)
        {
            for (var attempt = 1; attempt <= DownloadAttemptsPerUrl; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    progress?.Report(new FileSyncProgress(
                        5,
                        "Загрузка локальной Java 17",
                        attempt == 1 ? $"Источник: {url}" : $"Повторная попытка {attempt}/{DownloadAttemptsPerUrl}: {url}"));
                    await DownloadRuntimeFromUrlAsync(url, archivePath, tempPath, progress, cancellationToken);
                    return;
                }
                catch (Exception ex) when (IsTransientDownloadError(ex, cancellationToken))
                {
                    lastError = ex;
                    progress?.Report(new FileSyncProgress(5, "Сбой загрузки Java 17, повторяю попытку", ex.Message));
                    if (attempt < DownloadAttemptsPerUrl)
                    {
                        await Task.Delay(RetryDelay, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    break;
                }
            }
        }

        throw new IOException("Не удалось загрузить Java 17 после нескольких попыток. Если у игрока включен Zapret, VPN, прокси или HTTPS-фильтр антивируса, они могут рвать TLS-соединение.", lastError);
    }

    private async Task DownloadRuntimeFromUrlAsync(
        string url,
        string archivePath,
        string tempPath,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
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
            File.Delete(tempPath);
        }

        var totalBytes = response.Content.Headers.ContentRange?.Length
            ?? (response.Content.Headers.ContentLength is long contentLength ? existingBytes + contentLength : 0);

        long downloadedBytes = existingBytes;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(tempPath, existingBytes > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloadedBytes += read;
                var progressPercent = totalBytes > 0
                    ? Math.Clamp(downloadedBytes * 85d / Math.Max(totalBytes, 1), 5, 85)
                    : 50;
                progress?.Report(new FileSyncProgress(
                    progressPercent,
                    totalBytes > 0
                        ? $"Загрузка Java 17: {FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes)}"
                        : $"Загрузка Java 17: {FormatBytes(downloadedBytes)}",
                    null));
            }
        }

        File.Move(tempPath, archivePath, true);
    }

    private static List<string> BuildRuntimeUrls(ManifestRuntimeInfo runtime)
    {
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(runtime.JavaRuntimeUrl))
        {
            urls.Add(runtime.JavaRuntimeUrl);
        }

        urls.AddRange(runtime.JavaRuntimeFallbackUrls.Where(url => !string.IsNullOrWhiteSpace(url)));

        if (urls.Count == 0)
        {
            urls.Add(TemurinJava17JreUrl);
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsTransientDownloadError(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        if (exception is HttpRequestException or IOException)
        {
            return true;
        }

        return exception.InnerException is not null && IsTransientDownloadError(exception.InnerException, cancellationToken);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var targetPath = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, true);
        }
    }

    // JRE считается полной только при наличии и java.exe, и lib\jvm.cfg (без него JVM не стартует).
    private static bool IsJavaRuntimeComplete(string javaHome)
    {
        if (string.IsNullOrWhiteSpace(javaHome))
        {
            return false;
        }

        var javaExe = Path.Combine(javaHome, "bin", "java.exe");
        var jvmCfg = Path.Combine(javaHome, "lib", "jvm.cfg");
        return File.Exists(javaExe) && File.Exists(jvmCfg);
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

    private static bool IsJavaExecutableInBinDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory)
            && Path.GetFileName(directory).Equals("bin", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d:0.0} MB";
        }

        return bytes >= 1024 ? $"{bytes / 1024d:0.0} KB" : $"{bytes} B";
    }
}
