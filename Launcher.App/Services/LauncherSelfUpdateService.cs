using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace Launcher.App.Services;

public sealed class LauncherSelfUpdateService(HttpClient httpClient)
{
    public async Task<LauncherSelfUpdatePackage> DownloadUpdatePackageAsync(
        Uri packageUri,
        string expectedSha256,
        IProgress<LauncherSelfUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "BLModernTFGM", "launcher-update");
        Directory.CreateDirectory(tempRoot);

        var packagePath = Path.Combine(tempRoot, $"launcher-update-{Guid.NewGuid():N}.zip");
        try
        {
            using var response = await httpClient.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;

            // ВАЖНО: файл-поток закрываем В ЭТОМ блоке — до проверки SHA256.
            // Иначе HashesMatch() пытается открыть файл, который ещё открыт на запись (FileShare.None),
            // и падает с "The process cannot access the file ... being used by another process",
            // из-за чего самообновление не проходит.
            await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long downloadedBytes = 0;
                int read;
                while ((read = await responseStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloadedBytes += read;

                    var totalBytesValue = totalBytes.GetValueOrDefault();
                    var percentage = totalBytesValue > 0
                        ? downloadedBytes * 100d / totalBytesValue
                        : 0;

                    progress?.Report(new LauncherSelfUpdateProgress(
                        Math.Clamp(percentage, 0, 100),
                        $"Обновление лаунчера {Math.Clamp(percentage, 0, 100):0}%"));
                }

                await fileStream.FlushAsync(cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256) && !HashesMatch(packagePath, expectedSha256))
            {
                throw new InvalidOperationException("Хэш обновления лаунчера не совпадает.");
            }

            progress?.Report(new LauncherSelfUpdateProgress(100, "Обновление лаунчера 100%"));
            return new LauncherSelfUpdatePackage(packagePath);
        }
        catch
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }

            throw;
        }
    }

    public void ApplyUpdateAndRestart(string packagePath, string installDirectory, string executablePath, int currentProcessId)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "BLModernTFGM", "launcher-update");
        Directory.CreateDirectory(tempRoot);

        var scriptPath = Path.Combine(tempRoot, $"apply-update-{Guid.NewGuid():N}.ps1");
        var script = $$"""
            $processIdToWait = {{currentProcessId}}
            $packagePath = '{{EscapePowerShell(packagePath)}}'
            $installDirectory = '{{EscapePowerShell(installDirectory)}}'
            $executablePath = '{{EscapePowerShell(executablePath)}}'

            while (Get-Process -Id $processIdToWait -ErrorAction SilentlyContinue) {
                Start-Sleep -Milliseconds 300
            }

            Start-Sleep -Seconds 1
            Expand-Archive -LiteralPath $packagePath -DestinationPath $installDirectory -Force
            Remove-Item -LiteralPath $packagePath -Force -ErrorAction SilentlyContinue
            Start-Process -FilePath $executablePath -WorkingDirectory $installDirectory
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;

        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WorkingDirectory = installDirectory
        });
    }

    private static bool HashesMatch(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var actual = Convert.ToHexString(sha256.ComputeHash(stream));
        return actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapePowerShell(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}

public sealed record LauncherSelfUpdatePackage(string PackagePath);

public sealed record LauncherSelfUpdateProgress(double Percentage, string Message);
