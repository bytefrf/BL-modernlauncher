using System.Net.Http;
using System.Security.Cryptography;
using Launcher.App.Configuration;
using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed class FileSyncService(HttpClient httpClient)
{
    public async Task<FileSyncSummary> SyncAsync(
        LauncherConfiguration configuration,
        LauncherManifest manifest,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = configuration.GetDistributionRoot();
        Directory.CreateDirectory(root);

        var managedFiles = manifest.Game.Files
            .Select(file => new ManagedFile(file, Path.Combine(root, NormalizeRelativePath(file.Path))))
            .ToList();

        var filesToDownload = managedFiles
            .Where(file => !File.Exists(file.TargetPath) || !HashesMatch(file.TargetPath, file.File.Sha256))
            .ToList();

        var totalBytes = Math.Max(1L, filesToDownload.Sum(item => Math.Max(item.File.Size, 1)));
        long downloadedBytes = 0;
        var downloadedFiles = 0;
        var startedAt = DateTime.UtcNow;

        if (filesToDownload.Count == 0)
        {
            progress?.Report(new FileSyncProgress(100, "Файлы сборки уже актуальны", "Все файлы сборки уже актуальны."));
        }

        for (var fileIndex = 0; fileIndex < filesToDownload.Count; fileIndex++)
        {
            var item = filesToDownload[fileIndex];
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);

            var fileUri = manifest.ResolveUri(item.File.Url);
            var tempFile = item.TargetPath + ".download";
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }

            using var response = await httpClient.GetAsync(fileUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);

            await using (var output = File.Create(tempFile))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloadedBytes += read;
                    progress?.Report(new FileSyncProgress(
                        Math.Clamp(downloadedBytes * 100d / totalBytes, 0, 100),
                        $"Загрузка сборки {FormatBytes(downloadedBytes)} из {FormatBytes(totalBytes)}",
                        null));
                }

                await output.FlushAsync(cancellationToken);
            }

            if (!HashesMatch(tempFile, item.File.Sha256))
            {
                File.Delete(tempFile);
                throw new InvalidOperationException($"Хэш не совпал после загрузки файла {item.File.Path}");
            }

            File.Move(tempFile, item.TargetPath, true);
            downloadedFiles++;
            progress?.Report(new FileSyncProgress(
                Math.Clamp(downloadedBytes * 100d / totalBytes, 0, 100),
                $"Обновлено файлов {downloadedFiles} из {filesToDownload.Count}",
                $"Обновлен {item.File.Path}"));
        }

        if (manifest.Game.DeleteOrphans)
        {
            DeleteOrphans(root, managedFiles.Select(item => item.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase), manifest.Game.PreservePaths);
        }

        progress?.Report(new FileSyncProgress(
            100,
            filesToDownload.Count == 0
                ? "Сборка уже обновлена"
                : $"Сборка обновлена, загружено {downloadedFiles} файлов за {FormatBytesPerSecond(downloadedBytes, DateTime.UtcNow - startedAt)}",
            "Синхронизация завершена."));
        return new FileSyncSummary(downloadedFiles, managedFiles.Count - downloadedFiles);
    }

    private static void DeleteOrphans(string root, HashSet<string> managedFiles, IReadOnlyCollection<string> preservePaths)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (managedFiles.Contains(file))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (preservePaths.Any(path => relative.StartsWith(NormalizeRelativePath(path).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            File.Delete(file);
        }
    }

    private static string NormalizeRelativePath(string value)
    {
        return value.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
    }

    private static bool HashesMatch(string path, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return File.Exists(path);
        }

        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var actual = Convert.ToHexString(sha256.ComputeHash(stream));
        return actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
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

    private sealed record ManagedFile(DistributionFile File, string TargetPath);
}

public sealed record FileSyncProgress(double Percentage, string Message, string? LogLine);
public sealed record FileSyncSummary(int DownloadedFiles, int SkippedFiles);
