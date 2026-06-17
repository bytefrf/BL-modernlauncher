using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography;
using Launcher.App.Configuration;
using Launcher.App.Models;

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

        Directory.CreateDirectory(cacheRoot);
        if (!forceReinstall && File.Exists(markerPath) && File.ReadAllText(markerPath).Trim().Equals(version, StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new FileSyncProgress(100, "Модпак уже установлен", "Архив модпака уже установлен."));
            return new ArchiveInstallSummary(false, root, markerPath);
        }

        var archiveFileName = manifest?.Modpack.ArchiveFileName;
        if (string.IsNullOrWhiteSpace(archiveFileName))
        {
            archiveFileName = $"modpack-{SanitizeFileName(version)}.zip";
        }

        var archivePath = Path.Combine(cacheRoot, SanitizeFileName(archiveFileName));
        await DownloadArchiveAsync(ResolveArchiveUrl(manifest, archiveUrl), archivePath, manifest?.Modpack.ArchiveSize ?? 0, progress, cancellationToken);
        await EnsureZipCanBeOpenedAsync(ResolveArchiveUrl(manifest, archiveUrl), archivePath, manifest?.Modpack.ArchiveSize ?? 0, progress, cancellationToken);

        var expectedHash = manifest?.Modpack.ArchiveSha256 ?? configuration.ModpackArchiveSha256;
        var integrityRequired = manifest?.Integrity.Required ?? !string.IsNullOrWhiteSpace(expectedHash);
        if (!string.IsNullOrWhiteSpace(expectedHash) && !HashesMatch(archivePath, expectedHash))
        {
            File.Delete(archivePath);
            throw new InvalidOperationException("Downloaded archive SHA-256 does not match configuration.");
        }
        else if (integrityRequired && string.IsNullOrWhiteSpace(expectedHash))
        {
            // Целостность объявлена обязательной, но хэш не задан — fail-closed, иначе проверка обходится пустым хэшем.
            throw new InvalidOperationException("Integrity is required, but archiveSha256 is not configured. Refusing to install an unverified archive.");
        }

        if (manifest?.Install.CleanBeforeInstall == true)
        {
            CleanInstallRoot(root, manifest.Install.PreservePaths);
        }

        progress?.Report(new FileSyncProgress(90, "Распаковка модпака", null));
        ExtractArchive(archivePath, root, manifest?.Modpack.StripPrefix ?? ".minecraft/");
        MultiplayerServerListService.EnsureServer(root, "BL-modern TFGM #1", "play.bl-modern.ru");
        MultiplayerServerListService.EnsureServer(root, "BL-modern TFGM #2", "tfgm2.bl-modern.ru");

        Directory.CreateDirectory(launcherRoot);
        await File.WriteAllTextAsync(markerPath, version, cancellationToken);
        progress?.Report(new FileSyncProgress(100, "Модпак установлен", $"Установлена версия модпака {version}."));
        return new ArchiveInstallSummary(true, root, markerPath);
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
                progress?.Report(new FileSyncProgress(
                    percentage,
                    $"Загрузка модпака {FormatBytes(downloadedBytes)} из {FormatBytes(totalBytes)}",
                    $"{FormatBytesPerSecond(downloadedBytes - existingBytes, startedAt.Elapsed)}"));
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
                FileName = "tar.exe",
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
                FileName = "tar.exe",
                ArgumentList = { "-xf", archivePath, "-C", stagingRoot },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Could not start tar.exe.");

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"tar.exe could not extract archive: {stderr}");
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

    private static string? ResolveSevenZipPath()
    {
        var candidates = new[]
        {
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            "7z.exe"
        };

        return candidates.FirstOrDefault(File.Exists);
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

    private static string GetInstallRoot(LauncherConfiguration configuration, ModpackManifest? manifest, UserSettings settings)
    {
        var root = manifest?.Install.Root;
        return settings.ResolveInstallRoot(root ?? string.Empty, configuration.GetDistributionRoot());
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
        Directory.CreateDirectory(root);
        var fullRoot = Path.GetFullPath(root);
        var preserve = preservePaths
            .Concat(AlwaysPreservedPaths)
            .Select(path => Path.GetFullPath(Path.Combine(fullRoot, path.Replace('/', Path.DirectorySeparatorChar))))
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

    private static bool IsWithinDirectory(string candidate, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

public sealed record ArchiveInstallSummary(bool Installed, string InstallPath, string VersionMarkerPath);
