using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Launcher.App.Models;
using Launcher.App.Platform;

namespace Launcher.App.Services;

public sealed class JavaRuntimeInstallService(HttpClient httpClient)
{
    private const int DownloadAttemptsPerUrl = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    // Устанавливает управляемую Java в .launcher/runtime/java-{major}. Мажорная версия берётся из
    // манифеста сборки (17 — Forge 1.20.1, 21 — NeoForge 1.21.1). Forge-сборки используют major=17,
    // как и раньше; путь установки и кэш-архив версионные, поэтому сборки на разных Java не конфликтуют.
    public async Task<string> EnsureManagedJavaAsync(
        string installRoot,
        ManifestRuntimeInfo runtime,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var major = runtime.JavaVersion > 0 ? runtime.JavaVersion : 17;
        var javaPath = JavaValidationService.GetManagedConsoleJavaPath(installRoot, major);
        var runtimeRoot = JavaValidationService.GetManagedJavaHome(installRoot, major);

        if (IsJavaRuntimeComplete(runtimeRoot))
        {
            progress?.Report(new FileSyncProgress(100, $"Локальная Java {major} уже установлена", null));
            return javaPath;
        }

        // java.exe мог остаться от прерванной распаковки, но без lib\jvm.cfg JRE нерабочая
        // (инсталлер лоадера падает с "could not open jvm.cfg"). Сносим неполную и ставим заново.
        if (Directory.Exists(runtimeRoot))
        {
            progress?.Report(new FileSyncProgress(0, $"Обнаружена неполная Java {major} — переустанавливаю", null));
            try { Directory.Delete(runtimeRoot, true); } catch { /* перезапишется при копировании */ }
        }

        var cacheRoot = Path.Combine(installRoot, ".launcher", "cache");
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(runtimeRoot)!);

        var archivePath = Path.Combine(
            cacheRoot,
            $"temurin-jre-{major}-{AdoptiumOs}-{AdoptiumArchitecture}{(RuntimeArchiveIsZip ? ".zip" : ".tar.gz")}");
        await DownloadRuntimeAsync(archivePath, runtime, major, progress, cancellationToken);

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"launcher-java-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);

        try
        {
            progress?.Report(new FileSyncProgress(90, $"Распаковка локальной Java {major}", null));
            try
            {
                ExtractRuntimeArchive(archivePath, stagingRoot);
            }
            catch (InvalidDataException)
            {
                // Кэшированный архив битый/недокачан — удаляем, чтобы при следующем запуске скачать заново.
                try { File.Delete(archivePath); } catch { }
                throw new InvalidDataException($"Архив Java {major} повреждён. Он будет перекачан при следующем запуске лаунчера.");
            }

            var extractedJava = Directory
                .EnumerateFiles(stagingRoot, HostPlatform.JavaExecutableName, SearchOption.AllDirectories)
                .FirstOrDefault(IsJavaExecutableInBinDirectory)
                ?? throw new InvalidDataException(
                    $"В архиве Java {major} не найден bin{Path.DirectorySeparatorChar}{HostPlatform.JavaExecutableName}.");

            var extractedHome = Directory.GetParent(Path.GetDirectoryName(extractedJava)!)!.FullName;
            if (Directory.Exists(runtimeRoot))
            {
                Directory.Delete(runtimeRoot, true);
            }

            CopyDirectory(extractedHome, runtimeRoot);
            RestoreExecutableBits(runtimeRoot);
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
                $"Java {major} установлена не полностью (нет bin\\java.exe или lib\\jvm.cfg): {runtimeRoot}. " +
                "Архив будет перекачан при следующем запуске.");
        }

        progress?.Report(new FileSyncProgress(100, $"Локальная Java {major} установлена", $"Java {major} установлена: {javaPath}"));
        return javaPath;
    }

    private async Task DownloadRuntimeAsync(
        string archivePath,
        ManifestRuntimeInfo runtime,
        int major,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(archivePath) && new FileInfo(archivePath).Length > 0)
        {
            // Доверяем кэшу только если архив открывается целиком — иначе распаковка даст неполную Java.
            if (CanOpenRuntimeArchive(archivePath))
            {
                progress?.Report(new FileSyncProgress(85, $"Архив Java {major} уже загружен: {FormatBytes(new FileInfo(archivePath).Length)}", null));
                return;
            }

            progress?.Report(new FileSyncProgress(5, $"Кэш Java {major} повреждён — качаю заново", null));
            try { File.Delete(archivePath); } catch { }
        }

        var urls = BuildRuntimeUrls(runtime, major);
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
                        $"Загрузка локальной Java {major}",
                        attempt == 1 ? $"Источник: {url}" : $"Повторная попытка {attempt}/{DownloadAttemptsPerUrl}: {url}"));
                    await DownloadRuntimeFromUrlAsync(url, archivePath, tempPath, major, progress, cancellationToken);
                    return;
                }
                catch (Exception ex) when (IsTransientDownloadError(ex, cancellationToken))
                {
                    lastError = ex;
                    progress?.Report(new FileSyncProgress(5, $"Сбой загрузки Java {major}, повторяю попытку", ex.Message));
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

        throw new IOException($"Не удалось загрузить Java {major} после нескольких попыток. Если у игрока включен Zapret, VPN, прокси или HTTPS-фильтр антивируса, они могут рвать TLS-соединение.", lastError);
    }

    private async Task DownloadRuntimeFromUrlAsync(
        string url,
        string archivePath,
        string tempPath,
        int major,
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
                        ? $"Загрузка Java {major}: {FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes)}"
                        : $"Загрузка Java {major}: {FormatBytes(downloadedBytes)}",
                    null));
            }
        }

        File.Move(tempPath, archivePath, true);
    }

    /// <summary>
    /// Распаковывает архив Java: zip на Windows, tar.gz на Linux и macOS.
    /// </summary>
    private static void ExtractRuntimeArchive(string archivePath, string stagingRoot)
    {
        if (RuntimeArchiveIsZip)
        {
            ZipFile.ExtractToDirectory(archivePath, stagingRoot, true);
            return;
        }

        try
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, stagingRoot, overwriteFiles: true);
        }
        catch (Exception exception) when (exception is not InvalidDataException)
        {
            // Недокачанный tar.gz бросает не InvalidDataException, а IOException или EndOfStreamException.
            // Приводим к общему виду, чтобы вызывающий код одинаково сбросил кэш и перекачал архив.
            throw new InvalidDataException("Архив Java повреждён.", exception);
        }
    }

    /// <summary>
    /// Возвращает право на исполнение файлам JRE. Ни zip, ни копирование файлов прав не сохраняют,
    /// а без бита +x на <c>bin/java</c> запуск падает с «Permission denied».
    /// </summary>
    private static void RestoreExecutableBits(string runtimeRoot)
    {
        if (HostPlatform.IsWindows)
        {
            return;
        }

        foreach (var directory in new[] { "bin", Path.Combine("lib", "jspawnhelper") })
        {
            var path = Path.Combine(runtimeRoot, directory);
            if (File.Exists(path))
            {
                TrySetExecutable(path);
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    TrySetExecutable(file);
                }
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void TrySetExecutable(string path)
    {
        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Права могут не выставиться на нестандартной ФС — сообщим об этом уже при запуске.
        }
    }

    private static List<string> BuildRuntimeUrls(ManifestRuntimeInfo runtime, int major)
    {
        var urls = new List<string>();

        // Адреса, заданные для текущей ОС явно.
        if (runtime.JavaRuntimeUrlsByOs.TryGetValue(HostPlatform.MojangOsName, out var osUrls))
        {
            urls.AddRange(osUrls.Where(url => !string.IsNullOrWhiteSpace(url)));
        }

        // Старые поля манифеста содержат zip под Windows x64. На Linux и macOS они не подойдут:
        // распаковались бы в мусор вместо рабочей JRE, поэтому туда их не берём.
        if (HostPlatform.IsWindows)
        {
            if (!string.IsNullOrWhiteSpace(runtime.JavaRuntimeUrl))
            {
                urls.Add(runtime.JavaRuntimeUrl);
            }

            urls.AddRange(runtime.JavaRuntimeFallbackUrls.Where(url => !string.IsNullOrWhiteSpace(url)));
        }

        if (urls.Count == 0)
        {
            // Фолбэк под нужную мажорную версию (Forge → 17, NeoForge 1.21.1 → 21).
            urls.Add($"https://api.adoptium.net/v3/binary/latest/{major}/ga/{AdoptiumOs}/{AdoptiumArchitecture}/jre/hotspot/normal/eclipse?project=jdk");
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Имя ОС в терминах Adoptium API: <c>windows</c>, <c>linux</c>, <c>mac</c>.</summary>
    private static string AdoptiumOs => HostPlatform.IsWindows ? "windows" : HostPlatform.IsMacOS ? "mac" : "linux";

    /// <summary>Архитектура в терминах Adoptium API. Apple Silicon и ARM-ноутбуки — <c>aarch64</c>.</summary>
    private static string AdoptiumArchitecture => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "aarch64",
        Architecture.X86 => "x86",
        _ => "x64"
    };

    /// <summary>
    /// Adoptium отдаёт zip только для Windows, для Linux и macOS — tar.gz. От расширения зависит
    /// и имя файла в кэше, и способ распаковки.
    /// </summary>
    private static bool RuntimeArchiveIsZip => HostPlatform.IsWindows;

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
            CopyFileResilient(file, targetPath);
        }
    }

    // Файлы вроде ucrtbase.dll нередко заняты запущенной Java/игрой или антивирусом во время копирования.
    // Повторяем несколько раз; если цель уже на месте и совпадает по размеру — считаем её корректной и
    // пропускаем; иначе бросаем понятную ошибку вместо «Access denied».
    private static void CopyFileResilient(string source, string target)
    {
        const int attempts = 4;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                File.Copy(source, target, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length)
                {
                    return;
                }

                if (attempt == attempts)
                {
                    throw new IOException(
                        $"Не удалось скопировать файл Java «{Path.GetFileName(target)}»: он занят другой программой. " +
                        "Закрой запущенную Minecraft/Java и временно отключи антивирус, затем повтори запуск.",
                        ex);
                }

                Thread.Sleep(250);
            }
        }
    }

    // JRE считается полной только при наличии и java.exe, и lib\jvm.cfg (без него JVM не стартует).
    private static bool IsJavaRuntimeComplete(string javaHome)
    {
        if (string.IsNullOrWhiteSpace(javaHome))
        {
            return false;
        }

        var javaExe = Path.Combine(javaHome, "bin", HostPlatform.JavaExecutableName);
        var jvmCfg = Path.Combine(javaHome, "lib", "jvm.cfg");
        return File.Exists(javaExe) && File.Exists(jvmCfg);
    }

    /// <summary>
    /// Проверяет, что кэшированный архив Java читается целиком. Формат зависит от ОС, поэтому
    /// на Unix проверять zip-ом нельзя — иначе годный tar.gz каждый раз считался бы битым
    /// и Java перекачивалась бы при каждом запуске.
    /// </summary>
    private static bool CanOpenRuntimeArchive(string archivePath)
    {
        if (RuntimeArchiveIsZip)
        {
            return CanOpenZip(archivePath);
        }

        try
        {
            using var file = File.OpenRead(archivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            // Достаточно дочитать до конца: повреждение или обрыв вылезут исключением.
            while (reader.GetNextEntry() is not null)
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            return false;
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
