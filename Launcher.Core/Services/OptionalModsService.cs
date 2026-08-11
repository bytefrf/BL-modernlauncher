using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Launcher.App.Models;

namespace Launcher.App.Services;

/// <summary>
/// Дополнительные моды, одобренные администрацией: загрузка каталога, скачивание с проверкой
/// SHA-256 и раскладка в mods/.
///
/// Ключевая деталь: при обновлении сборки папка mods очищается (updateResetPaths), поэтому сами
/// jar-файлы лежат в `.launcher/optional-mods` (эта папка в preservePaths и переживает апдейт), а
/// в mods/ кладутся копии. Синхронизация вызывается после каждой установки/обновления и перед
/// запуском, так что выбор игрока не теряется.
///
/// Удаляем из mods/ ТОЛЬКО то, что сами туда положили (список в installed.json) — чужие файлы и
/// моды самой сборки не трогаем.
/// </summary>
public sealed class OptionalModsService(HttpClient httpClient)
{
    private const string StoreDirectoryName = "optional-mods";
    private const string StateFileName = "installed.json";

    public async Task<OptionalModsCatalog?> GetCatalogAsync(string catalogUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogUrl))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(catalogUrl);
        // file:// тоже читаем с диска: HttpClient такую схему не поддерживает.
        if (Uri.TryCreate(expanded, UriKind.Absolute, out var maybeFile) && maybeFile.IsFile && File.Exists(maybeFile.LocalPath))
        {
            expanded = maybeFile.LocalPath;
        }

        OptionalModsCatalog? catalog;
        if (File.Exists(expanded))
        {
            await using var file = File.OpenRead(Path.GetFullPath(expanded));
            catalog = await JsonSerializer.DeserializeAsync<OptionalModsCatalog>(file, JsonOptions(), cancellationToken);
        }
        else
        {
            var uri = new Uri(catalogUrl, UriKind.Absolute);
            using var response = await httpClient.GetAsync(uri, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            catalog = await JsonSerializer.DeserializeAsync<OptionalModsCatalog>(stream, JsonOptions(), cancellationToken);
            if (catalog is not null)
            {
                catalog.SourceUri = uri;
            }
        }

        if (catalog is null)
        {
            return null;
        }

        // Отсекаем битые записи сразу: без url/sha256 мод всё равно нельзя поставить безопасно.
        catalog.Mods = catalog.Mods.Where(mod => mod.IsValid && mod.Enabled).ToList();
        return catalog;
    }

    /// <summary>
    /// Приводит mods/ в соответствие с выбором игрока. Возвращает, сколько модов стоит сейчас.
    /// </summary>
    public async Task<OptionalModsSyncResult> SyncAsync(
        string installRoot,
        OptionalModsCatalog catalog,
        IReadOnlyCollection<string> selectedIds,
        IProgress<FileSyncProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var storeDirectory = Path.Combine(installRoot, ".launcher", StoreDirectoryName);
        var modsDirectory = Path.Combine(installRoot, "mods");
        Directory.CreateDirectory(storeDirectory);
        Directory.CreateDirectory(modsDirectory);

        var state = LoadState(storeDirectory);
        var selected = catalog.Mods
            .Where(mod => selectedIds.Contains(mod.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        // 1. Убираем из mods/ то, что ставили раньше, а сейчас не выбрано.
        var removed = 0;
        foreach (var installed in state.Items.ToList())
        {
            if (selected.Any(mod => mod.Id.Equals(installed.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            TryDeleteFile(Path.Combine(modsDirectory, installed.FileName));
            state.Items.Remove(installed);
            removed++;
        }

        // 2. Докладываем выбранные: сначала в хранилище (переживает обновление), потом копия в mods/.
        var installedCount = 0;
        var index = 0;
        foreach (var mod in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            progress?.Report(new FileSyncProgress(
                selected.Count == 0 ? 100 : index * 100 / selected.Count,
                $"Дополнительные моды: {mod.Name}",
                null));

            var storedPath = Path.Combine(storeDirectory, SanitizeFileName(mod.FileName));
            if (!FileMatchesHash(storedPath, mod.Sha256))
            {
                await DownloadAsync(mod, storedPath, cancellationToken);
                if (!FileMatchesHash(storedPath, mod.Sha256))
                {
                    TryDeleteFile(storedPath);
                    throw new InvalidOperationException($"Мод «{mod.Name}»: файл не совпал с контрольной суммой, установка отменена.");
                }
            }

            var targetPath = Path.Combine(modsDirectory, SanitizeFileName(mod.FileName));
            if (!FileMatchesHash(targetPath, mod.Sha256))
            {
                File.Copy(storedPath, targetPath, overwrite: true);
            }

            state.Items.RemoveAll(item => item.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase));
            state.Items.Add(new InstalledOptionalMod
            {
                Id = mod.Id,
                FileName = SanitizeFileName(mod.FileName),
                Sha256 = mod.Sha256
            });
            installedCount++;
        }

        SaveState(storeDirectory, state);
        return new OptionalModsSyncResult(installedCount, removed);
    }

    private async Task DownloadAsync(OptionalModEntry mod, string targetPath, CancellationToken cancellationToken)
    {
        // Локальный путь/зеркало на диске — как в остальных клиентах лаунчера (манифест, новости).
        var localPath = Environment.ExpandEnvironmentVariables(mod.Url);
        if (File.Exists(localPath))
        {
            File.Copy(Path.GetFullPath(localPath), targetPath, overwrite: true);
            return;
        }

        using var response = await httpClient.GetAsync(mod.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var temporaryPath = targetPath + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(temporaryPath))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        File.Move(temporaryPath, targetPath, overwrite: true);
    }

    private static InstalledOptionalMods LoadState(string storeDirectory)
    {
        var path = Path.Combine(storeDirectory, StateFileName);
        if (!File.Exists(path))
        {
            return new InstalledOptionalMods();
        }

        try
        {
            return JsonSerializer.Deserialize<InstalledOptionalMods>(File.ReadAllText(path), JsonOptions()) ?? new InstalledOptionalMods();
        }
        catch
        {
            // Битый список — начинаем с чистого: хуже, чем лишний jar в mods, ничего не случится.
            return new InstalledOptionalMods();
        }
    }

    private static void SaveState(string storeDirectory, InstalledOptionalMods state)
    {
        var path = Path.Combine(storeDirectory, StateFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions()));
    }

    private static bool FileMatchesHash(string path, string expectedHash)
    {
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return hash.Equals(expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Файл занят (игра запущена) — уберём при следующей синхронизации.
        }
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value.Trim();
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

public sealed record OptionalModsSyncResult(int Installed, int Removed);
