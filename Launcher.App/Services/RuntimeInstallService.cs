using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.App.Configuration;
using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed class RuntimeInstallService(HttpClient httpClient)
{
    private const string MojangVersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const int AssetDownloadConcurrency = 16;
    private const int DownloadAttemptsPerUrl = 3;
    private static readonly TimeSpan ForgeInstallerTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private const double JavaStart = 0;
    private const double JavaEnd = 15;
    private const double VanillaStart = 15;
    private const double VanillaEnd = 35;
    private const double ForgeStart = 35;
    private const double ForgeEnd = 65;
    private const double AssetsStart = 65;
    private const double AssetsEnd = 100;

    public async Task EnsureRuntimeAsync(
        LauncherConfiguration configuration,
        ModpackManifest? manifest,
        UserSettings settings,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (manifest is null)
        {
            return;
        }

        var root = GetInstallRoot(configuration, manifest, settings);
        if (!manifest.Runtime.RequiresInstalledRuntime && manifest.Runtime.JavaVersion == 17)
        {
            var javaInstaller = new JavaRuntimeInstallService(httpClient);
            await javaInstaller.EnsureJava17Async(root, manifest.Runtime, ScaleProgress(progress, JavaStart, JavaEnd), cancellationToken);
        }

        var versionJson = Path.Combine(root, "versions", manifest.Runtime.MainVersionId, $"{manifest.Runtime.MainVersionId}.json");
        Directory.CreateDirectory(root);
        await EnsureVanillaVersionAsync(root, manifest.Runtime, manifest.Modpack.MinecraftVersion, ScaleProgress(progress, VanillaStart, VanillaEnd), cancellationToken);

        if (!File.Exists(versionJson))
        {
            if (!manifest.Runtime.AutoInstallForge)
            {
                throw new InvalidOperationException($"Runtime {manifest.Runtime.MainVersionId} is not installed and autoInstallForge is disabled.");
            }

            await InstallForgeAsync(root, manifest, settings, ScaleProgress(progress, ForgeStart, ForgeEnd), cancellationToken);
        }
        else
        {
            progress?.Report(new FileSyncProgress(65, $"Forge {manifest.Modpack.LoaderVersion} уже установлен", null));
        }

        await EnsureVanillaLibrariesAndAssetsAsync(root, manifest.Runtime, manifest.Modpack.MinecraftVersion, ScaleProgress(progress, AssetsStart, AssetsEnd), cancellationToken);
        progress?.Report(new FileSyncProgress(100, "Runtime installation complete (100%)", null));
    }

    private async Task InstallForgeAsync(string root, ModpackManifest manifest, UserSettings settings, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifest.Runtime.ForgeInstallerUrl))
        {
            throw new InvalidOperationException("forgeInstallerUrl is required when autoInstallForge is enabled.");
        }

        // Forge-инсталлер исполняется как java -jar, поэтому его целостность обязана быть проверяемой.
        if (string.IsNullOrWhiteSpace(manifest.Runtime.ForgeInstallerSha256))
        {
            throw new InvalidOperationException("forgeInstallerSha256 is required when autoInstallForge is enabled: the installer is executed and must be hash-verified.");
        }

        var cacheRoot = Path.Combine(root, ".launcher", "cache");
        Directory.CreateDirectory(cacheRoot);
        var installerPath = Path.Combine(cacheRoot, Path.GetFileName(new Uri(manifest.Runtime.ForgeInstallerUrl).AbsolutePath));
        await DownloadFileAsync(manifest.Runtime.ForgeInstallerUrl, installerPath, manifest.Runtime.ForgeInstallerSize, manifest.Runtime.ForgeInstallerSha256, "Downloading Forge installer", progress, cancellationToken);

        var profilesPath = Path.Combine(root, "launcher_profiles.json");
        if (!File.Exists(profilesPath))
        {
            await File.WriteAllTextAsync(profilesPath, "{\"profiles\":{},\"settings\":{},\"version\":3}", cancellationToken);
        }

        progress?.Report(new FileSyncProgress(0, $"Installing Forge {manifest.Modpack.LoaderVersion} (0%)", "Forge installer started."));
        var javaPath = ResolveConsoleJava(root, string.IsNullOrWhiteSpace(settings.JavaExecutable) ? manifest.Runtime.JavaExecutable : settings.JavaExecutable);
        var firstResult = await RunForgeInstallerAsync(root, installerPath, javaPath, manifest.Modpack.LoaderVersion, progress, cancellationToken);
        if (firstResult.ExitCode != 0)
        {
            progress?.Report(new FileSyncProgress(20, "Forge installer failed, retrying with a fresh installer (20%)", firstResult.ToLogLine()));
            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
            }

            await DownloadFileAsync(manifest.Runtime.ForgeInstallerUrl, installerPath, manifest.Runtime.ForgeInstallerSize, manifest.Runtime.ForgeInstallerSha256, "Downloading Forge installer", progress, cancellationToken);
            var secondResult = await RunForgeInstallerAsync(root, installerPath, javaPath, manifest.Modpack.LoaderVersion, progress, cancellationToken);
            if (secondResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Forge installer failed with code {secondResult.ExitCode}.{Environment.NewLine}" +
                    $"Java: {javaPath}{Environment.NewLine}" +
                    $"stdout: {secondResult.StdoutPath}{Environment.NewLine}" +
                    $"stderr: {secondResult.StderrPath}{Environment.NewLine}" +
                    secondResult.ErrorText);
            }
        }

        var forgeVersionJson = Path.Combine(root, "versions", manifest.Runtime.MainVersionId, $"{manifest.Runtime.MainVersionId}.json");
        if (!File.Exists(forgeVersionJson))
        {
            throw new FileNotFoundException($"Forge installer finished, but version JSON was not created: {forgeVersionJson}");
        }

        progress?.Report(new FileSyncProgress(100, $"Forge {manifest.Modpack.LoaderVersion} installed (100%)", "Forge installer finished."));
    }

    private async Task EnsureVanillaVersionAsync(string root, ManifestRuntimeInfo runtime, string minecraftVersion, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken)
    {
        var versionJsonPath = Path.Combine(root, "versions", minecraftVersion, $"{minecraftVersion}.json");
        var versionJarPath = Path.Combine(root, "versions", minecraftVersion, $"{minecraftVersion}.jar");
        if (File.Exists(versionJsonPath) && File.Exists(versionJarPath))
        {
            return;
        }

        progress?.Report(new FileSyncProgress(0, $"Loading Minecraft {minecraftVersion} metadata (0%)", null));
        var versionUrl = runtime.MinecraftVersionJsonUrl;
        var versionJsonSha1 = string.Empty;
        if (string.IsNullOrWhiteSpace(versionUrl))
        {
            var manifestUrl = string.IsNullOrWhiteSpace(runtime.MinecraftVersionManifestUrl)
                ? MojangVersionManifestUrl
                : runtime.MinecraftVersionManifestUrl;
            using var manifestResponse = await httpClient.GetAsync(manifestUrl, cancellationToken);
            manifestResponse.EnsureSuccessStatusCode();
            await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var versionManifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);

            var versionEntry = versionManifest.RootElement
                .GetProperty("versions")
                .EnumerateArray()
                .FirstOrDefault(item => item.GetProperty("id").GetString() == minecraftVersion);
            if (versionEntry.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException($"Версия Minecraft {minecraftVersion} не найдена в манифесте Mojang.");
            }

            versionUrl = versionEntry.GetProperty("url").GetString()
                ?? throw new InvalidOperationException($"Minecraft {minecraftVersion} metadata URL was not found.");
            versionJsonSha1 = versionEntry.TryGetProperty("sha1", out var versionSha1Element) ? versionSha1Element.GetString() ?? string.Empty : string.Empty;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(versionJsonPath)!);
        await DownloadFileAsync(versionUrl, versionJsonPath, 0, string.Empty, $"Downloading Minecraft {minecraftVersion} JSON", ScaleProgress(progress, 5, 35), cancellationToken, versionJsonSha1);

        using var versionDocument = JsonDocument.Parse(await File.ReadAllTextAsync(versionJsonPath, cancellationToken));
        var client = versionDocument.RootElement.GetProperty("downloads").GetProperty("client");
        var clientSha1 = client.TryGetProperty("sha1", out var clientSha1Element) ? clientSha1Element.GetString() ?? string.Empty : string.Empty;
        await DownloadFileAsync(
            string.IsNullOrWhiteSpace(runtime.MinecraftClientUrl)
                ? client.GetProperty("url").GetString() ?? throw new InvalidOperationException("Minecraft client URL missing.")
                : runtime.MinecraftClientUrl,
            versionJarPath,
            client.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
            string.Empty,
            $"Downloading Minecraft {minecraftVersion} client",
            ScaleProgress(progress, 35, 100),
            cancellationToken,
            clientSha1);
    }

    private async Task EnsureVanillaLibrariesAndAssetsAsync(string root, ManifestRuntimeInfo runtime, string minecraftVersion, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken)
    {
        var versionJsonPath = Path.Combine(root, "versions", minecraftVersion, $"{minecraftVersion}.json");
        using var versionDocument = JsonDocument.Parse(await File.ReadAllTextAsync(versionJsonPath, cancellationToken));
        var versionRoot = versionDocument.RootElement;

        if (versionRoot.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateArray())
            {
                if (library.TryGetProperty("downloads", out var downloads))
                {
                    if (downloads.TryGetProperty("artifact", out var artifact))
                    {
                        await DownloadLibraryAsync(root, artifact, runtime, ScaleProgress(progress, 0, 30), cancellationToken);
                    }

                    if (downloads.TryGetProperty("classifiers", out var classifiers))
                    {
                        foreach (var classifier in classifiers.EnumerateObject())
                        {
                            if (classifier.Name.Contains("windows", StringComparison.OrdinalIgnoreCase))
                            {
                                await DownloadLibraryAsync(root, classifier.Value, runtime, ScaleProgress(progress, 0, 30), cancellationToken);
                            }
                        }
                    }
                }
            }
        }

        var assetIndex = versionRoot.GetProperty("assetIndex");
        var assetIndexId = assetIndex.GetProperty("id").GetString() ?? "legacy";
        var assetIndexPath = Path.Combine(root, "assets", "indexes", $"{assetIndexId}.json");
        var assetIndexSha1 = assetIndex.TryGetProperty("sha1", out var assetIndexSha1Element) ? assetIndexSha1Element.GetString() ?? string.Empty : string.Empty;
        await DownloadFileAsync(
            string.IsNullOrWhiteSpace(runtime.MinecraftAssetIndexUrl)
                ? assetIndex.GetProperty("url").GetString() ?? throw new InvalidOperationException("Asset index URL missing.")
                : runtime.MinecraftAssetIndexUrl,
            assetIndexPath,
            assetIndex.TryGetProperty("size", out var assetIndexSize) ? assetIndexSize.GetInt64() : 0,
            string.Empty,
            $"Downloading asset index {assetIndexId}",
            ScaleProgress(progress, 30, 35),
            cancellationToken,
            assetIndexSha1);

        using var assetDocument = JsonDocument.Parse(await File.ReadAllTextAsync(assetIndexPath, cancellationToken));
        var objects = assetDocument.RootElement.GetProperty("objects").EnumerateObject().ToList();
        var assetsToDownload = new List<AssetDownload>();
        foreach (var item in objects)
        {
            var hash = item.Value.GetProperty("hash").GetString() ?? string.Empty;
            var size = item.Value.TryGetProperty("size", out var objectSize) ? objectSize.GetInt64() : 0;
            if (string.IsNullOrWhiteSpace(hash))
            {
                continue;
            }

            var prefix = hash[..2];
            var target = Path.Combine(root, "assets", "objects", prefix, hash);
            var url = string.IsNullOrWhiteSpace(runtime.MinecraftAssetsBaseUrl)
                ? $"https://resources.download.minecraft.net/{prefix}/{hash}"
                : $"{runtime.MinecraftAssetsBaseUrl.TrimEnd('/')}/{prefix}/{hash}";
            if (!File.Exists(target) || (size > 0 && new FileInfo(target).Length != size))
            {
                assetsToDownload.Add(new AssetDownload(url, target, size, hash));
            }
        }

        await DownloadAssetsAsync(assetsToDownload, ScaleProgress(progress, 35, 100), cancellationToken);
    }

    private async Task DownloadAssetsAsync(
        IReadOnlyCollection<AssetDownload> assets,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (assets.Count == 0)
        {
            progress?.Report(new FileSyncProgress(100, "Assets are already installed", null));
            return;
        }

        var completed = 0;
        var semaphore = new SemaphoreSlim(AssetDownloadConcurrency);
        var tasks = assets.Select(async asset =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await DownloadFileAsync(asset.Url, asset.TargetPath, asset.Size, string.Empty, string.Empty, null, cancellationToken, asset.Sha1);
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new FileSyncProgress(
                    Math.Clamp(current * 100d / assets.Count, 0, 100),
                    $"Downloading assets {current}/{assets.Count} ({current * 100 / assets.Count}%)",
                    current % 100 == 0 || current == assets.Count ? $"Assets downloaded: {current}/{assets.Count}" : null));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task DownloadLibraryAsync(string root, JsonElement artifact, ManifestRuntimeInfo runtime, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken)
    {
        if (!artifact.TryGetProperty("path", out var pathElement) || !artifact.TryGetProperty("url", out var urlElement))
        {
            return;
        }

        var relativePath = pathElement.GetString();
        var url = string.IsNullOrWhiteSpace(runtime.MinecraftLibrariesBaseUrl)
            ? urlElement.GetString()
            : $"{runtime.MinecraftLibrariesBaseUrl.TrimEnd('/')}/{relativePath}";
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var target = Path.Combine(root, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar));
        var size = artifact.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
        var sha1 = artifact.TryGetProperty("sha1", out var sha1Element) ? sha1Element.GetString() ?? string.Empty : string.Empty;
        await DownloadFileAsync(url, target, size, string.Empty, $"Downloading library {Path.GetFileName(relativePath)}", progress, cancellationToken, sha1);
    }

    private async Task DownloadFileAsync(string url, string targetPath, long expectedSize, string expectedSha256, string message, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken, string expectedSha1 = "")
    {
        if (File.Exists(targetPath)
            && (expectedSize <= 0 || new FileInfo(targetPath).Length == expectedSize)
            && (string.IsNullOrWhiteSpace(expectedSha256) || Sha256Matches(targetPath, expectedSha256))
            && (string.IsNullOrWhiteSpace(expectedSha1) || Sha1Matches(targetPath, expectedSha1)))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = targetPath + ".download";
        Exception? lastError = null;

        // Resume имеет смысл только если результат можно проверить (по размеру или хэшу),
        // иначе можно докачать поверх чужого недокачанного файла и получить мусор.
        var canValidate = expectedSize > 0 || !string.IsNullOrWhiteSpace(expectedSha256) || !string.IsNullOrWhiteSpace(expectedSha1);
        if (!canValidate && File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        for (var attempt = 1; attempt <= DownloadAttemptsPerUrl; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadFileAttemptAsync(url, targetPath, tempPath, expectedSize, expectedSha256, expectedSha1, message, progress, cancellationToken);
                return;
            }
            catch (Exception ex) when (IsTransientDownloadError(ex, cancellationToken))
            {
                lastError = ex;
                progress?.Report(new FileSyncProgress(0, $"Сбой загрузки, повторяю попытку {attempt}/{DownloadAttemptsPerUrl}", string.IsNullOrWhiteSpace(message) ? ex.Message : $"{message}: {ex.Message}"));
                if (attempt < DownloadAttemptsPerUrl)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
        }

        throw new IOException($"Не удалось загрузить файл после нескольких попыток: {url}", lastError);
    }

    private async Task DownloadFileAttemptAsync(
        string url,
        string targetPath,
        string tempPath,
        long expectedSize,
        string expectedSha256,
        string expectedSha1,
        string message,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(tempPath) && expectedSize > 0 && new FileInfo(tempPath).Length > expectedSize)
        {
            File.Delete(tempPath);
        }

        var existingBytes = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingBytes > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
        }

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var resumed = existingBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (existingBytes > 0 && !resumed)
        {
            existingBytes = 0;
            File.Delete(tempPath);
        }

        var totalBytes = ResolveTotalDownloadBytes(response, expectedSize, existingBytes);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using (var output = new FileStream(tempPath, existingBytes > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long downloaded = existingBytes;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                if (progress is not null && !string.IsNullOrWhiteSpace(message))
                {
                    var percentage = totalBytes > 0
                        ? Math.Clamp(downloaded * 100d / totalBytes, 0, 100)
                        : 50;
                    progress.Report(new FileSyncProgress(percentage, $"{message}: {FormatBytes(downloaded)} ({percentage:0}%)", null));
                }
            }
        }

        if (expectedSize > 0 && new FileInfo(tempPath).Length != expectedSize)
        {
            throw new InvalidDataException($"Downloaded file size mismatch for {url}. Partial file will be resumed on next attempt.");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256) && !Sha256Matches(tempPath, expectedSha256))
        {
            File.Delete(tempPath);
            throw new InvalidDataException($"Downloaded file SHA-256 mismatch for {url}.");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha1) && !Sha1Matches(tempPath, expectedSha1))
        {
            File.Delete(tempPath);
            throw new InvalidDataException($"Downloaded file SHA-1 mismatch for {url}.");
        }

        File.Move(tempPath, targetPath, true);
    }

    private static long ResolveTotalDownloadBytes(HttpResponseMessage response, long expectedSize, long existingBytes)
    {
        if (expectedSize > 0)
        {
            return expectedSize;
        }

        if (response.Content.Headers.ContentRange?.Length is long contentRangeLength)
        {
            return contentRangeLength;
        }

        return Math.Max(1, existingBytes + (response.Content.Headers.ContentLength ?? 0));
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

    private static string GetInstallRoot(LauncherConfiguration configuration, ModpackManifest manifest, UserSettings settings)
    {
        return settings.ResolveInstallRoot(manifest.Install.Root, configuration.GetDistributionRoot());
    }

    private static async Task<ForgeInstallerResult> RunForgeInstallerAsync(
        string root,
        string installerPath,
        string javaPath,
        string forgeVersion,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var logRoot = Path.Combine(root, ".launcher", "logs");
        Directory.CreateDirectory(logRoot);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var stdoutPath = Path.Combine(logRoot, $"forge-installer-{timestamp}.stdout.log");
        var stderrPath = Path.Combine(logRoot, $"forge-installer-{timestamp}.stderr.log");

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "-Djava.awt.headless=true", "-jar", installerPath, "--installClient", root }
        }) ?? throw new InvalidOperationException("Could not start Forge installer.");

        var stdoutBuilder = new System.Text.StringBuilder();
        var stderrBuilder = new System.Text.StringBuilder();
        var outputTask = PumpOutputAsync(process.StandardOutput, stdoutBuilder, progress, $"Forge {forgeVersion}", cancellationToken);
        var errorTask = PumpOutputAsync(process.StandardError, stderrBuilder, progress, $"Forge {forgeVersion}", cancellationToken);
        var heartbeatTask = ReportForgeHeartbeatAsync(process, forgeVersion, progress, cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = process.WaitForExitAsync(timeoutCts.Token);
        var timeoutTask = Task.Delay(ForgeInstallerTimeout, cancellationToken);

        if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            await File.WriteAllTextAsync(stdoutPath, stdoutBuilder.ToString(), cancellationToken);
            await File.WriteAllTextAsync(stderrPath, stderrBuilder.ToString(), cancellationToken);
            return new ForgeInstallerResult(-1, stdoutPath, stderrPath, $"Forge installer timeout after {ForgeInstallerTimeout.TotalMinutes:0} minutes.");
        }

        timeoutCts.Cancel();

        var output = await outputTask;
        var error = await errorTask;
        await heartbeatTask;
        await File.WriteAllTextAsync(stdoutPath, output, cancellationToken);
        await File.WriteAllTextAsync(stderrPath, error, cancellationToken);

        return new ForgeInstallerResult(process.ExitCode, stdoutPath, stderrPath, error);
    }

    private static async Task<string> PumpOutputAsync(
        StreamReader reader,
        System.Text.StringBuilder builder,
        IProgress<FileSyncProgress>? progress,
        string prefix,
        CancellationToken cancellationToken)
    {
        var reportedLines = 0;
        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            builder.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(line) && ShouldReportForgeOutputLine(line, ref reportedLines))
            {
                progress?.Report(new FileSyncProgress(35, $"{prefix}: {TrimStatusLine(line)} (35%)", line));
            }
        }

        return builder.ToString();
    }

    private static async Task ReportForgeHeartbeatAsync(
        Process process,
        string forgeVersion,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var step = 0;
        while (!process.HasExited && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            if (process.HasExited)
            {
                break;
            }

            step++;
            var seconds = (int)(DateTime.UtcNow - startedAt).TotalSeconds;
            var percent = Math.Min(95, 10 + seconds / 6d);
            progress?.Report(new FileSyncProgress(percent, $"Installing Forge {forgeVersion}: {seconds}s ({percent:0}%)", null));
        }
    }

    private static string TrimStatusLine(string value)
    {
        const int maxLength = 110;
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static bool ShouldReportForgeOutputLine(string line, ref int reportedLines)
    {
        var normalized = line.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.StartsWith("Injecting profile", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Successfully installed", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Downloading libraries", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Considering minecraft client jar", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Building Processors", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Downloaded Mojang mappings", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("You can delete this installer file now", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.StartsWith("Patching ", StringComparison.OrdinalIgnoreCase))
        {
            reportedLines++;
            return reportedLines % 200 == 0;
        }

        if (normalized.StartsWith("  Downloading library from ", StringComparison.OrdinalIgnoreCase))
        {
            reportedLines++;
            return reportedLines % 10 == 0;
        }

        return false;
    }

    private static IProgress<FileSyncProgress>? ScaleProgress(IProgress<FileSyncProgress>? progress, double start, double end)
    {
        if (progress is null)
        {
            return null;
        }

        return new Progress<FileSyncProgress>(item =>
        {
            var scaled = start + Math.Clamp(item.Percentage, 0, 100) * (end - start) / 100d;
            progress.Report(item with
            {
                Percentage = scaled,
                Message = AddPercent(item.Message, scaled)
            });
        });
    }

    private static string AddPercent(string message, double percentage)
    {
        return message.Contains('%', StringComparison.Ordinal)
            ? message
            : $"{message} ({percentage:0}%)";
    }

    private static string ResolveConsoleJava(string root, string configuredJava)
    {
        var candidates = JavaValidationService.ResolveConsoleJavaCandidates(root, configuredJava);
        return candidates.FirstOrDefault(path => !Path.IsPathRooted(path) || File.Exists(path)) ?? "java.exe";
    }

    private static bool Sha256Matches(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Sha1Matches(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        using var sha1 = SHA1.Create();
        return Convert.ToHexString(sha1.ComputeHash(stream)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatBytes(long bytes)
    {
        return bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d:0.0} KB";
    }

    private sealed record AssetDownload(string Url, string TargetPath, long Size, string Sha1);
    private sealed record ForgeInstallerResult(int ExitCode, string StdoutPath, string StderrPath, string ErrorText)
    {
        public string ToLogLine() => $"Forge installer failed with code {ExitCode}. stderr: {StderrPath}";
    }
}
