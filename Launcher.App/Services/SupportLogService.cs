using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

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
        CancellationToken cancellationToken = default)
    {
        installRoot = string.IsNullOrWhiteSpace(installRoot) || installRoot == "-"
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForgeLauncher")
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
        AddLatestMatchingFile(archive, launcherLogRoot, "forge-installer-*.stderr.log", "forge", MaxTextLogBytes, includedFiles);
        AddLatestMatchingFile(archive, launcherLogRoot, "forge-installer-*.stdout.log", "forge", MaxTextLogBytes, includedFiles);

        if (!string.IsNullOrWhiteSpace(currentErrorLogPath) && File.Exists(currentErrorLogPath))
        {
            AddFileEntry(archive, currentErrorLogPath, "launcher/current-error.log", MaxTextLogBytes, includedFiles);
        }

        AddFileEntry(archive, Path.Combine(installRoot, "logs", "latest.log"), "minecraft/latest.log", MaxTextLogBytes, includedFiles);
        AddLatestMatchingFile(archive, Path.Combine(installRoot, "crash-reports"), "crash-*.txt", "minecraft/crash-reports", MaxCrashReportBytes, includedFiles);

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
        List<string> includedFiles)
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

        AddFileEntry(archive, file.FullName, $"{entryDirectory}/{file.Name}", maxBytes, includedFiles);
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
