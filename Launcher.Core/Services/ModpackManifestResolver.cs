using System.Net.Http;
using System.Net.Sockets;
using Launcher.App.Configuration;
using Launcher.App.Models;
using Launcher.App.Platform;

namespace Launcher.App.Services;

/// <summary>Откуда в итоге взялся манифест сборки.</summary>
public enum ModpackManifestSource
{
    /// <summary>Свежий, скачан с сайта.</summary>
    Remote,

    /// <summary>Последний удачно скачанный, сохранённый на диске.</summary>
    Cached,

    /// <summary>Вшитый в лаунчер резерв.</summary>
    Embedded
}

/// <param name="Manifest">Полученный манифест.</param>
/// <param name="Source">Откуда он взялся.</param>
/// <param name="Reason">Почему не удалось взять свежий; <c>null</c>, если всё в порядке.</param>
public sealed record ModpackManifestResolution(
    ModpackManifest Manifest,
    ModpackManifestSource Source,
    string? Reason);

/// <summary>
/// Достаёт манифест сборки по цепочке «сайт → кэш на диске → вшитый резерв».
/// </summary>
/// <remarks>
/// Вынесено из <c>MainWindow.xaml.cs</c>: логика здесь не про интерфейс, а окну от неё нужен
/// только результат и текст для строки состояния. Благодаря этому её можно переиспользовать
/// в Avalonia-версии и проверять без запуска окна.
/// </remarks>
public sealed class ModpackManifestResolver(ModpackManifestClient client)
{
    public async Task<ModpackManifestResolution> ResolveAsync(
        string manifestUrl,
        string cachePath,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var manifest = await client.GetManifestAsync(manifestUrl, cancellationToken);
            await client.SaveManifestCacheAsync(manifest, cachePath, cancellationToken);
            log?.Invoke($"Modpack manifest cache updated: {cachePath}");
            return new ModpackManifestResolution(manifest, ModpackManifestSource.Remote, null);
        }
        catch (HttpRequestException exception) when (IsNetworkNameResolutionError(exception))
        {
            log?.Invoke($"Modpack manifest network error: {exception.Message}");
            return await LoadFallbackAsync(cachePath, "Server is unavailable", log, cancellationToken);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Отмена по нашему токену — это не сбой сети, её пробрасываем наверх.
            log?.Invoke($"Modpack manifest timeout: {exception.Message}");
            return await LoadFallbackAsync(cachePath, "Server timeout", log, cancellationToken);
        }
    }

    private async Task<ModpackManifestResolution> LoadFallbackAsync(
        string cachePath,
        string reason,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            var cached = await client.GetCachedManifestAsync(cachePath, cancellationToken);
            log?.Invoke($"Using cached modpack manifest: {cachePath}");
            return new ModpackManifestResolution(cached, ModpackManifestSource.Cached, reason);
        }
        catch (Exception cacheException)
        {
            log?.Invoke($"Cached modpack manifest unavailable: {cacheException.Message}");
            var embedded = await client.GetEmbeddedDefaultManifestAsync(cancellationToken);
            log?.Invoke("Using embedded modpack manifest.");
            return new ModpackManifestResolution(embedded, ModpackManifestSource.Embedded, reason);
        }
    }

    /// <summary>
    /// Отличает «не резолвится имя хоста» от прочих сетевых ошибок: только в этом случае
    /// имеет смысл молча уходить на кэш, а не показывать игроку ошибку.
    /// </summary>
    public static bool IsNetworkNameResolutionError(HttpRequestException exception)
    {
        return exception.InnerException is SocketException socketException
            && socketException.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain;
    }
}

/// <summary>
/// Собирает манифест запуска для режима прямого архива (когда каталог сборок не используется).
/// </summary>
public static class LauncherManifestFactory
{
    public static LauncherManifest CreateArchiveModeLaunchManifest(
        ModpackManifest? modpackManifest,
        LauncherConfiguration? configuration)
    {
        var runtime = modpackManifest?.Runtime;
        return new LauncherManifest
        {
            Launcher = new LauncherUpdateInfo
            {
                Version = "1.0.52"
            },
            Game = new GameDistributionInfo
            {
                ModpackId = modpackManifest?.Modpack.Id ?? string.Empty,
                Version = modpackManifest?.Modpack.Version ?? configuration?.ModpackVersion ?? "Modpack",
                Description = modpackManifest?.Modpack.Description ?? "Direct archive modpack",
                MainVersionId = runtime?.MainVersionId ?? "1.20.1-forge-47.3.29",
                // Раньше здесь было жёстко "javaw.exe" — на Linux и macOS такого файла нет.
                JavaExecutable = string.IsNullOrWhiteSpace(runtime?.JavaExecutable)
                    ? HostPlatform.JavawExecutableName
                    : runtime!.JavaExecutable,
                JavaMajorVersion = (runtime?.JavaVersion ?? 0) > 0 ? runtime!.JavaVersion : 17,
                JavaArguments = runtime?.JvmArgs ?? ["-XX:+UseG1GC"],
                GameArguments = runtime?.GameArgs ?? []
            }
        };
    }
}
