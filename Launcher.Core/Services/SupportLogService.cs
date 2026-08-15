using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Launcher.App.Platform;

namespace Launcher.App.Services;

public sealed class SupportLogService(HttpClient httpClient)
{
    private const long MaxTextLogBytes = 2L * 1024L * 1024L;
    private const long MaxCrashReportBytes = 5L * 1024L * 1024L;

    public async Task<SupportLogPackage> CreatePackageAsync(
        string installRoot,
        string currentErrorLogPath,
        string clientId,
        string username,
        string launcherVersion,
        string modpackVersion,
        string errorTitle,
        CancellationToken cancellationToken = default,
        DateTime? sessionStartedUtc = null)
    {
        installRoot = string.IsNullOrWhiteSpace(installRoot) || installRoot == "-"
            ? Path.Combine(LauncherPaths.GetApplicationDataRoot(), "ForgeLauncher")
            : installRoot;

        var supportRoot = Path.Combine(installRoot, ".launcher", "support");
        Directory.CreateDirectory(supportRoot);

        var packagePath = Path.Combine(supportRoot, $"support-log-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var includedFiles = new List<string>();

        await using var fileStream = new FileStream(packagePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(fileStream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);

        await WriteTextEntryAsync(
            archive,
            "launcher-context.txt",
            BuildContext(clientId, username, launcherVersion, modpackVersion, installRoot, errorTitle),
            cancellationToken);
        includedFiles.Add("launcher-context.txt");

        var launcherLogRoot = Path.Combine(installRoot, ".launcher", "logs");
        AddLatestMatchingFile(archive, launcherLogRoot, "launcher-error-*.log", "launcher", MaxTextLogBytes, includedFiles);
        AddLatestMatchingFile(archive, launcherLogRoot, "launcher-20*.log", "launcher", MaxTextLogBytes, includedFiles);
        AddLatestMatchingFile(archive, launcherLogRoot, "forge-installer-*.stderr.log", "forge", MaxTextLogBytes, includedFiles);
        AddLatestMatchingFile(archive, launcherLogRoot, "forge-installer-*.stdout.log", "forge", MaxTextLogBytes, includedFiles);

        // Крэш-логи самого лаунчера (App) и бутстраппера: без них ранние/тихие краши не видны.
        AddFileEntry(archive, Path.Combine(installRoot, ".launcher", "launcher-crash.log"), "launcher/launcher-crash.log", MaxTextLogBytes, includedFiles);
        AddFileEntry(
            archive,
            Path.Combine(
                LauncherPaths.GetLocalApplicationDataRoot(),
                "TerraFirmaGregModernLauncher",
                "bootstrapper-error.log"),
            "bootstrapper/bootstrapper-error.log",
            MaxTextLogBytes,
            includedFiles);

        if (!string.IsNullOrWhiteSpace(currentErrorLogPath) && File.Exists(currentErrorLogPath))
        {
            AddFileEntry(archive, currentErrorLogPath, "launcher/current-error.log", MaxTextLogBytes, includedFiles);
        }

        var gameLogRoot = Path.Combine(installRoot, "logs");
        AddFileEntry(archive, Path.Combine(gameLogRoot, "latest.log"), "minecraft/latest.log", MaxTextLogBytes, includedFiles);
        AddFileEntry(archive, Path.Combine(gameLogRoot, "debug.log"), "minecraft/debug.log", MaxTextLogBytes, includedFiles);
        // КЛЮЧЕВОЕ для ранних крашей: stderr игры пишется до старта log4j, когда latest.log ещё пуст.
        AddFileEntry(archive, Path.Combine(gameLogRoot, "minecraft-stderr.log"), "minecraft/minecraft-stderr.log", MaxTextLogBytes, includedFiles);
        AddFileEntry(archive, Path.Combine(gameLogRoot, "minecraft-diagnostic.stderr.log"), "minecraft/minecraft-diagnostic.stderr.log", MaxTextLogBytes, includedFiles);
        AddFileEntry(archive, Path.Combine(gameLogRoot, "minecraft-diagnostic.stdout.log"), "minecraft/minecraft-diagnostic.stdout.log", MaxTextLogBytes, includedFiles);
        // Крэш-репорт от ПРОШЛОЙ сессии кладём в отдельную папку. Файл в crash-reports живёт вечно,
        // и при разборе бандлов он выглядел уликой текущего запуска: за август 2026 таким оказался
        // каждый четвёртый бандл с крэш-репортом, вплоть до файлов двухнедельной давности.
        AddLatestMatchingFile(archive, Path.Combine(installRoot, "crash-reports"), "crash-*.txt", "minecraft/crash-reports", MaxCrashReportBytes, includedFiles, sessionStartedUtc, "minecraft/crash-reports-old");
        // Нативные краши (0xC0000409 и т.п.) оставляют дамп JVM в корне папки установки.
        AddLatestMatchingFile(archive, installRoot, "hs_err_pid*.log", "minecraft/hs_err", MaxCrashReportBytes, includedFiles, sessionStartedUtc, "minecraft/hs_err-old");

        return new SupportLogPackage(packagePath, includedFiles.Count);
    }

    public async Task<SupportLogUploadResult> UploadAsync(
        string uploadUrl,
        string packagePath,
        string clientId,
        string username,
        string launcherVersion,
        string modpackVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uploadUrl))
        {
            return new SupportLogUploadResult(false, string.Empty, "Адрес отправки логов не настроен.");
        }

        await using var fileStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        content.Add(fileContent, "log", Path.GetFileName(packagePath));
        content.Add(new StringContent(clientId), "clientId");
        content.Add(new StringContent(username), "username");
        content.Add(new StringContent(launcherVersion), "launcherVersion");
        content.Add(new StringContent(modpackVersion), "modpackVersion");
        content.Add(new StringContent(Environment.OSVersion.VersionString), "osVersion");

        using var response = await httpClient.PostAsync(uploadUrl, content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new SupportLogUploadResult(false, string.Empty, $"Сервер вернул HTTP {(int)response.StatusCode}.");
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            var success = root.TryGetProperty("success", out var successElement) && successElement.GetBoolean();
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty;
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty;
            return new SupportLogUploadResult(success, id, message);
        }
        catch (JsonException)
        {
            return new SupportLogUploadResult(false, string.Empty, "Сервер ответил не JSON-данными.");
        }
    }

    private static string BuildContext(
        string clientId,
        string username,
        string launcherVersion,
        string modpackVersion,
        string installRoot,
        string errorTitle)
    {
        return new StringBuilder()
            .AppendLine($"Created: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"ClientId: {clientId}")
            .AppendLine($"Username: {username}")
            .AppendLine($"LauncherVersion: {launcherVersion}")
            .AppendLine($"ModpackVersion: {modpackVersion}")
            .AppendLine($"InstallRoot: {installRoot}")
            .AppendLine($"OS: {Environment.OSVersion.VersionString}")
            .AppendLine($"Error: {errorTitle}")
            .ToString();
    }

    private static async Task WriteTextEntryAsync(ZipArchive archive, string entryName, string text, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }

    private static void AddLatestMatchingFile(
        ZipArchive archive,
        string directory,
        string pattern,
        string entryDirectory,
        long maxBytes,
        List<string> includedFiles,
        DateTime? sessionStartedUtc = null,
        string? staleEntryDirectory = null)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var file = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .FirstOrDefault();

        if (file is null)
        {
            return;
        }

        // Тот же запас на расхождение часов, что и в CrashAnalyzerService.
        var isStale = sessionStartedUtc is not null &&
                      staleEntryDirectory is not null &&
                      file.LastWriteTimeUtc < sessionStartedUtc.Value - TimeSpan.FromMinutes(2);
        var targetDirectory = isStale ? staleEntryDirectory! : entryDirectory;

        AddFileEntry(archive, file.FullName, $"{targetDirectory}/{file.Name}", maxBytes, includedFiles);
    }

    private static void AddFileEntry(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        long maxBytes,
        List<string> includedFiles)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return;
        }

        var fileInfo = new FileInfo(sourcePath);
        var normalizedEntryName = entryName.Replace('\\', '/');
        var entry = archive.CreateEntry(normalizedEntryName, CompressionLevel.Optimal);

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = entry.Open();

        if (fileInfo.Length <= maxBytes)
        {
            input.CopyTo(output);
        }
        else
        {
            var marker = Encoding.UTF8.GetBytes($"[Файл обрезан до последних {maxBytes / 1024 / 1024} MB]\n\n");
            output.Write(marker);
            input.Seek(-maxBytes, SeekOrigin.End);
            input.CopyTo(output);
        }

        includedFiles.Add(normalizedEntryName);
    }
}

public sealed record SupportLogPackage(string Path, int IncludedFileCount);

public sealed record SupportLogUploadResult(bool Success, string Id, string Message);
