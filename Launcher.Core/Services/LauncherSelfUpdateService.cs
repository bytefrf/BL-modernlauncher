using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using Launcher.App.Platform;

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

        if (!HostPlatform.IsWindows)
        {
            ApplyUpdateAndRestartUnix(packagePath, installDirectory, executablePath, currentProcessId, tempRoot);
            return;
        }

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

    /// <summary>
    /// Linux и macOS: тот же сценарий на POSIX-шелле. Распаковку делает <c>unzip</c>, а если его нет —
    /// сам лаунчер заранее раскладывает файлы во временную папку, и скрипту остаётся их перенести.
    /// Права на исполнение выставляются заново: zip их не переносит, а без них перезапуск невозможен.
    /// </summary>
    private void ApplyUpdateAndRestartUnix(
        string packagePath,
        string installDirectory,
        string executablePath,
        int currentProcessId,
        string tempRoot)
    {
        // Распаковываем сами, средствами .NET: так не зависим от наличия unzip в системе.
        var stagingRoot = Path.Combine(tempRoot, $"staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, stagingRoot, overwriteFiles: true);

        var scriptPath = Path.Combine(tempRoot, $"apply-update-{Guid.NewGuid():N}.sh");
        var script = $"""
            #!/bin/sh
            while kill -0 {currentProcessId} 2>/dev/null; do sleep 0.3; done
            sleep 1
            staging={EscapeShell(stagingRoot)}
            install_dir={EscapeShell(installDirectory)}
            executable={EscapeShell(executablePath)}
            mkdir -p "$install_dir"
            # -a сохраняет права; точка после staging копирует и скрытые файлы.
            cp -a "$staging"/. "$install_dir"/
            rm -rf "$staging"
            rm -f {EscapeShell(packagePath)}
            chmod +x "$executable" 2>/dev/null
            cd "$install_dir"
            "$executable" &
            rm -f "$0"
            """;

        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            ArgumentList = { scriptPath },
            UseShellExecute = false,
            CreateNoWindow = true,
            // Не держим рабочей папкой каталог установки: он сейчас будет перезаписан.
            WorkingDirectory = Path.GetTempPath()
        });
    }

    private static string EscapeShell(string value)
    {
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
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
