using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        // Управляемую Java ставим под версии, которые реально умеем доставлять: 17 (Forge 1.20.1)
        // и 21 (NeoForge 1.21.1). Папка установки версионная (java-{major}), сборки не конфликтуют.
        if (!manifest.Runtime.RequiresInstalledRuntime && (manifest.Runtime.JavaVersion == 17 || manifest.Runtime.JavaVersion == 21))
        {
            var javaInstaller = new JavaRuntimeInstallService(httpClient);
            await javaInstaller.EnsureManagedJavaAsync(root, manifest.Runtime, ScaleProgress(progress, JavaStart, JavaEnd), cancellationToken);
        }

        var versionJson = Path.Combine(root, "versions", manifest.Runtime.MainVersionId, $"{manifest.Runtime.MainVersionId}.json");
        Directory.CreateDirectory(root);

        // Полная автономия: если задан готовый Forge-рантайм и он ещё не установлен — скачиваем и
        // распаковываем его (versions/ + libraries/) ВМЕСТО запуска Forge-инсталлера. Тогда установка
        // не обращается к Forge maven вообще.
        if (!IsForgeRuntimeComplete(root, versionJson, manifest) && !string.IsNullOrWhiteSpace(manifest.Runtime.PrebuiltRuntimeUrl))
        {
            await InstallPrebuiltRuntimeAsync(root, manifest, ScaleProgress(progress, VanillaStart, ForgeEnd), cancellationToken);
        }

        await EnsureVanillaVersionAsync(root, manifest.Runtime, manifest.Modpack.MinecraftVersion, ScaleProgress(progress, VanillaStart, VanillaEnd), cancellationToken);

        if (!IsForgeRuntimeComplete(root, versionJson, manifest))
        {
            if (!manifest.Runtime.AutoInstallForge)
            {
                throw new InvalidOperationException($"Runtime {manifest.Runtime.MainVersionId} is not installed and autoInstallForge is disabled.");
            }

            // version.json может существовать, но без пропатченных jar-ов (client-srg/extra, forge-client),
            // которые генерирует инсталлер лоадера. Тогда игра падает в FML с «Invalid paths argument,
            // contained no existing paths». Сносим неполный лоадер и ставим заново — самолечение битых установок.
            TryRemoveIncompleteModLoader(root, manifest.Runtime.MainVersionId, IsNeoForgeLoader(manifest));
            await InstallModLoaderAsync(root, manifest, settings, ScaleProgress(progress, ForgeStart, ForgeEnd), cancellationToken);
        }
        else
        {
            progress?.Report(new FileSyncProgress(65, $"{LoaderDisplayName(manifest)} {manifest.Modpack.LoaderVersion} уже установлен", null));
        }

        await EnsureVanillaLibrariesAndAssetsAsync(root, manifest.Runtime, manifest.Modpack.MinecraftVersion, ScaleProgress(progress, AssetsStart, AssetsEnd), cancellationToken);
        progress?.Report(new FileSyncProgress(100, "Runtime installation complete (100%)", null));
    }

    // Forge считается установленным, только если есть version.json И все локально-генерируемые
    // артефакты лоадера. Проверок две, потому что одной не хватает:
    //   1) артефакты с ПУСТЫМ url в version.json — их создаёт инсталлер, мы их не качаем;
    //   2) жёстко заданные jar-ы лоадера и пропатченного клиента.
    // Вторая появилась после разбора саппорт-логов: в version.json Forge 1.20.1 (47.4.x) НЕТ НИ
    // ОДНОГО артефакта с пустым url, а client-srg/client-extra/forge-client там не перечислены
    // вовсе — Forge подставляет их через FML-аргументы. Из-за этого проверка №1 всегда говорила
    // «установлено», самолечение не запускалось никогда, и игрок с побитой установкой бесконечно
    // получал «Invalid paths argument, contained no existing paths» (37 бандлов от одного игрока
    // за вечер).
    private static bool IsForgeRuntimeComplete(string root, string versionJsonPath, ModpackManifest manifest)
    {
        if (!File.Exists(versionJsonPath))
        {
            return false;
        }

        if (!AreGeneratedLoaderArtifactsPresent(root, manifest))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(versionJsonPath));
            if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
            {
                return true; // не можем проверить состав — не блокируем запуск
            }

            foreach (var library in libraries.EnumerateArray())
            {
                if (!library.TryGetProperty("downloads", out var downloads) ||
                    !downloads.TryGetProperty("artifact", out var artifact))
                {
                    continue;
                }

                var path = artifact.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var url = artifact.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                // Пустой url = артефакт генерирует сам Forge-инсталлер (мы его не качаем). Его отсутствие = неполная установка.
                if (string.IsNullOrWhiteSpace(url))
                {
                    var fullPath = Path.Combine(root, "libraries", path.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(fullPath))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch
        {
            return true; // битый/нестандартный version.json — не ломаем запуск из-за самой проверки
        }
    }

    /// <summary>
    /// Проверяет jar-ы, которые Forge/NeoForge-инсталлер генерирует локально и которых нет
    /// в <c>libraries</c> version.json. Именно их отсутствие роняет игру в FML ещё до логов.
    /// Возвращает true, если проверить нечем (неизвестная версия лоадера) — проверка не должна
    /// сама блокировать запуск.
    /// </summary>
    private static bool AreGeneratedLoaderArtifactsPresent(string root, ModpackManifest manifest)
    {
        var loaderVersion = (manifest.Modpack.LoaderVersion ?? string.Empty).Trim();
        if (loaderVersion.Length == 0)
        {
            return true;
        }

        var neoForge = IsNeoForgeLoader(manifest);
        var libraries = Path.Combine(root, "libraries");

        // У Forge каталог версии — "<mc>-<loader>" (1.20.1-47.4.10), у NeoForge — просто "<loader>"
        // (21.1.233). Манифест может уже содержать полную форму — тогда ничего не склеиваем.
        var minecraftVersion = (manifest.Modpack.MinecraftVersion ?? string.Empty).Trim();
        var loaderFolder = loaderVersion;
        if (!neoForge && minecraftVersion.Length > 0 && !loaderVersion.StartsWith(minecraftVersion + "-", StringComparison.Ordinal))
        {
            loaderFolder = minecraftVersion + "-" + loaderVersion;
        }

        var loaderClientJar = neoForge
            ? Path.Combine(libraries, "net", "neoforged", "neoforge", loaderFolder, $"neoforge-{loaderFolder}-client.jar")
            : Path.Combine(libraries, "net", "minecraftforge", "forge", loaderFolder, $"forge-{loaderFolder}-client.jar");

        if (!File.Exists(loaderClientJar))
        {
            return false;
        }

        // Пропатченный клиент лежит в libraries/net/minecraft/client/<версия-штамп>/, а штамп
        // (1.20.1-20230612.114412) нам неоткуда взять — ищем маской. Проверяем только Forge:
        // раскладка NeoForge отличается, а ложное «не установлено» загонит игрока в вечную
        // переустановку — это хуже, чем не поймать редкий случай.
        if (neoForge)
        {
            return true;
        }

        var clientRoot = Path.Combine(libraries, "net", "minecraft", "client");
        if (!Directory.Exists(clientRoot))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(clientRoot, "client-*-srg.jar", SearchOption.AllDirectories).Any()
                   && Directory.EnumerateFiles(clientRoot, "client-*-extra.jar", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return true; // не смогли прочитать каталог — не блокируем запуск из-за самой проверки
        }
    }

    // Loader = "neoforge" в манифесте сборки → отдельная ветка установки/самолечения. Любое другое
    // значение (включая пустое и "forge") идёт по прежнему Forge-пути — поведение Forge-сборок не меняется.
    private static bool IsNeoForgeLoader(ModpackManifest manifest) =>
        string.Equals(manifest.Modpack.Loader, "neoforge", StringComparison.OrdinalIgnoreCase);

    private static string LoaderDisplayName(ModpackManifest manifest) =>
        IsNeoForgeLoader(manifest) ? "NeoForge" : "Forge";

    private static void TryRemoveIncompleteModLoader(string root, string mainVersionId, bool neoForge)
    {
        // У Forge пропатченные артефакты лежат в libraries/net/minecraftforge, у NeoForge — в
        // libraries/net/neoforged. Чистим правильную папку, иначе самолечение не сработает.
        var loaderLibraries = neoForge
            ? Path.Combine(root, "libraries", "net", "neoforged")
            : Path.Combine(root, "libraries", "net", "minecraftforge");

        var targets = new[]
        {
            Path.Combine(root, "versions", mainVersionId),
            loaderLibraries,
            Path.Combine(root, "libraries", "net", "minecraft", "client")
        };

        foreach (var target in targets)
        {
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }
            }
            catch
            {
                // Если что-то занято — Forge-инсталлер всё равно перезапишет нужное.
            }
        }
    }

    // Устанавливает мод-лоадер инсталлером (java -jar installer.jar --installClient <root>). Этот путь
    // общий для Forge и NeoForge: NeoForge-инсталлер — форк Forge-инсталлера и принимает тот же флаг.
    // Поля манифеста forgeInstaller* переиспользуются под NeoForge-инсталлер (бэкенд кладёт его туда же).
    private async Task InstallModLoaderAsync(string root, ModpackManifest manifest, UserSettings settings, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken)
    {
        var loaderName = LoaderDisplayName(manifest);

        if (string.IsNullOrWhiteSpace(manifest.Runtime.ForgeInstallerUrl))
        {
            throw new InvalidOperationException($"forgeInstallerUrl is required when autoInstallForge is enabled ({loaderName}).");
        }

        // Инсталлер исполняется как java -jar, поэтому его целостность обязана быть проверяемой.
        if (string.IsNullOrWhiteSpace(manifest.Runtime.ForgeInstallerSha256))
        {
            throw new InvalidOperationException($"forgeInstallerSha256 is required when autoInstallForge is enabled: the {loaderName} installer is executed and must be hash-verified.");
        }

        var cacheRoot = Path.Combine(root, ".launcher", "cache");
        Directory.CreateDirectory(cacheRoot);
        var installerPath = Path.Combine(cacheRoot, Path.GetFileName(new Uri(manifest.Runtime.ForgeInstallerUrl).AbsolutePath));
        await DownloadFileAsync(manifest.Runtime.ForgeInstallerUrl, installerPath, manifest.Runtime.ForgeInstallerSize, manifest.Runtime.ForgeInstallerSha256, $"Downloading {loaderName} installer", progress, cancellationToken);

        var profilesPath = Path.Combine(root, "launcher_profiles.json");
        if (!File.Exists(profilesPath))
        {
            await File.WriteAllTextAsync(profilesPath, "{\"profiles\":{},\"settings\":{},\"version\":3}", cancellationToken);
        }

        progress?.Report(new FileSyncProgress(0, $"Installing {loaderName} {manifest.Modpack.LoaderVersion} (0%)", $"{loaderName} installer started."));
        // Инсталлер NeoForge 1.21.1 требует Java 21; берём управляемую Java нужной версии сборки.
        var javaPath = ResolveConsoleJava(root, string.IsNullOrWhiteSpace(settings.JavaExecutable) ? manifest.Runtime.JavaExecutable : settings.JavaExecutable, manifest.Runtime.JavaVersion);
        var firstResult = await RunForgeInstallerAsync(root, installerPath, javaPath, $"{loaderName} {manifest.Modpack.LoaderVersion}", progress, cancellationToken);
        if (firstResult.ExitCode != 0)
        {
            progress?.Report(new FileSyncProgress(20, $"{loaderName} installer failed, retrying with a fresh installer (20%)", firstResult.ToLogLine()));
            if (File.Exists(installerPath))
            {
                File.Delete(installerPath);
            }

            await DownloadFileAsync(manifest.Runtime.ForgeInstallerUrl, installerPath, manifest.Runtime.ForgeInstallerSize, manifest.Runtime.ForgeInstallerSha256, $"Downloading {loaderName} installer", progress, cancellationToken);
            var secondResult = await RunForgeInstallerAsync(root, installerPath, javaPath, $"{loaderName} {manifest.Modpack.LoaderVersion}", progress, cancellationToken);
            if (secondResult.ExitCode != 0)
            {
                // Установщик во время --installClient сам ходит на серверы Mojang за mappings и клиентом.
                // У части игроков эти домены не резолвятся (провайдер/DNS), и раньше такой отказ доезжал
                // до игрока как «Проблема с Java» с советами чинить путь к Java — чинили не то.
                if (!string.IsNullOrEmpty(secondResult.UnreachableHost))
                {
                    throw new InvalidOperationException(
                        $"{loaderName} installer could not reach {secondResult.UnreachableHost}. " +
                        $"Установщику {loaderName} нужен доступ к серверам Mojang, а домен {secondResult.UnreachableHost} " +
                        $"с этого компьютера не открывается.{Environment.NewLine}" +
                        $"stdout: {secondResult.StdoutPath}");
                }

                throw new InvalidOperationException(
                    $"{loaderName} installer failed with code {secondResult.ExitCode}.{Environment.NewLine}" +
                    $"Java: {javaPath}{Environment.NewLine}" +
                    $"stdout: {secondResult.StdoutPath}{Environment.NewLine}" +
                    $"stderr: {secondResult.StderrPath}{Environment.NewLine}" +
                    secondResult.ErrorText);
            }
        }

        var loaderVersionJson = Path.Combine(root, "versions", manifest.Runtime.MainVersionId, $"{manifest.Runtime.MainVersionId}.json");
        if (!File.Exists(loaderVersionJson))
        {
            throw new FileNotFoundException(
                $"{loaderName} installer finished, but version JSON was not created: {loaderVersionJson}. " +
                $"Проверь, что mainVersionId сборки точно совпадает с профилем, который создаёт инсталлер.");
        }

        progress?.Report(new FileSyncProgress(100, $"{loaderName} {manifest.Modpack.LoaderVersion} installed (100%)", $"{loaderName} installer finished."));
    }

    private async Task InstallPrebuiltRuntimeAsync(string root, ModpackManifest manifest, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken)
    {
        // Готовый рантайм содержит исполняемые версии/библиотеки, которые затем запускает Java,
        // поэтому его целостность обязана быть проверяемой — fail-closed при отсутствии хэша.
        if (string.IsNullOrWhiteSpace(manifest.Runtime.PrebuiltRuntimeSha256))
        {
            throw new InvalidOperationException("prebuiltRuntimeSha256 is required when prebuiltRuntimeUrl is set: the runtime is executed and must be hash-verified.");
        }

        var url = manifest.ResolveUri(manifest.Runtime.PrebuiltRuntimeUrl).ToString();
        var cacheRoot = Path.Combine(root, ".launcher", "cache");
        Directory.CreateDirectory(cacheRoot);
        var bundlePath = Path.Combine(cacheRoot, "forge-prebuilt-runtime.zip");

        progress?.Report(new FileSyncProgress(0, "Загрузка готового Forge-рантайма", null));
        await DownloadFileAsync(
            url,
            bundlePath,
            manifest.Runtime.PrebuiltRuntimeSize,
            manifest.Runtime.PrebuiltRuntimeSha256,
            "Загрузка Forge-рантайма",
            ScaleProgress(progress, 0, 85),
            cancellationToken);

        progress?.Report(new FileSyncProgress(85, "Распаковка Forge-рантайма", null));
        var fullRoot = Path.GetFullPath(root);
        using (var archive = ZipFile.OpenRead(bundlePath))
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // папка
                }

                var destination = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
                if (!destination.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
                    && !destination.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Prebuilt runtime entry escapes install directory: {entry.FullName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
        }

        progress?.Report(new FileSyncProgress(100, "Forge-рантайм установлен", "Готовый Forge-рантайм распакован."));
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
        // Адрес того же файла у Mojang. Заполнен только когда его пришлось искать в манифесте версий:
        // при заданном versionJsonUrl поиск пропускается, и запасным остаётся зеркало.
        var mojangVersionUrl = string.Empty;
        var versionJsonSha1 = string.Empty;
        if (string.IsNullOrWhiteSpace(versionUrl))
        {
            var manifestUrl = string.IsNullOrWhiteSpace(runtime.MinecraftVersionManifestUrl)
                ? MojangVersionManifestUrl
                : runtime.MinecraftVersionManifestUrl;
            // Это САМЫЙ первый запрос установки, и он идёт на piston-meta. У игрока с заблокированным
            // Mojang всё падало уже здесь, поэтому зеркала пробуем и для него: файл кладётся в корень
            // зеркала под своим именем (version_manifest_v2.json).
            var manifestCandidates = BuildVanillaCandidates(runtime, "version_manifest_v2.json", manifestUrl, MojangVersionManifestUrl);
            // Кэш переживает перезапуск намеренно: запись про уже вышедшую версию (1.20.1) неизменна,
            // а повторная установка на заблокированной сети не должна снова упираться в piston-meta.
            var manifestJsonPath = Path.Combine(root, ".launcher", "cache", "version_manifest_v2.json");
            await DownloadFileAsync(manifestCandidates, manifestJsonPath, 0, string.Empty, string.Empty, null, cancellationToken);
            using var versionManifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestJsonPath, cancellationToken));

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
            mojangVersionUrl = versionUrl;
            versionJsonSha1 = versionEntry.TryGetProperty("sha1", out var versionSha1Element) ? versionSha1Element.GetString() ?? string.Empty : string.Empty;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(versionJsonPath)!);
        await DownloadFileAsync(
            BuildVanillaCandidates(runtime, $"versions/{minecraftVersion}/{minecraftVersion}.json", versionUrl, mojangVersionUrl),
            versionJsonPath,
            0,
            string.Empty,
            $"Downloading Minecraft {minecraftVersion} JSON",
            ScaleProgress(progress, 5, 35),
            cancellationToken,
            versionJsonSha1);

        using var versionDocument = JsonDocument.Parse(await File.ReadAllTextAsync(versionJsonPath, cancellationToken));
        var client = versionDocument.RootElement.GetProperty("downloads").GetProperty("client");
        var clientSha1 = client.TryGetProperty("sha1", out var clientSha1Element) ? clientSha1Element.GetString() ?? string.Empty : string.Empty;
        await DownloadFileAsync(
            BuildVanillaCandidates(
                runtime,
                $"versions/{minecraftVersion}/{minecraftVersion}.jar",
                runtime.MinecraftClientUrl,
                client.GetProperty("url").GetString()),
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
            BuildVanillaCandidates(
                runtime,
                $"assets/indexes/{assetIndexId}.json",
                runtime.MinecraftAssetIndexUrl,
                assetIndex.GetProperty("url").GetString()),
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
            var manifestAssetUrl = string.IsNullOrWhiteSpace(runtime.MinecraftAssetsBaseUrl)
                ? null
                : $"{runtime.MinecraftAssetsBaseUrl.TrimEnd('/')}/{prefix}/{hash}";
            if (!File.Exists(target) || (size > 0 && new FileInfo(target).Length != size))
            {
                assetsToDownload.Add(new AssetDownload(
                    BuildVanillaCandidates(
                        runtime,
                        $"assets/objects/{prefix}/{hash}",
                        manifestAssetUrl,
                        $"https://resources.download.minecraft.net/{prefix}/{hash}"),
                    target,
                    size,
                    hash));
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
                await DownloadFileAsync(asset.Urls, asset.TargetPath, asset.Size, string.Empty, string.Empty, null, cancellationToken, asset.Sha1);
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
        var mojangUrl = urlElement.GetString();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        var manifestUrl = string.IsNullOrWhiteSpace(runtime.MinecraftLibrariesBaseUrl)
            ? null
            : $"{runtime.MinecraftLibrariesBaseUrl.TrimEnd('/')}/{relativePath}";
        if (string.IsNullOrWhiteSpace(manifestUrl) && string.IsNullOrWhiteSpace(mojangUrl))
        {
            return;
        }

        var target = Path.Combine(root, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar));
        var size = artifact.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
        var sha1 = artifact.TryGetProperty("sha1", out var sha1Element) ? sha1Element.GetString() ?? string.Empty : string.Empty;
        await DownloadFileAsync(
            BuildVanillaCandidates(runtime, $"libraries/{relativePath}", manifestUrl, mojangUrl),
            target,
            size,
            string.Empty,
            $"Downloading library {Path.GetFileName(relativePath)}",
            progress,
            cancellationToken,
            sha1);
    }

    private Task DownloadFileAsync(string url, string targetPath, long expectedSize, string expectedSha256, string message, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken, string expectedSha1 = "") =>
        DownloadFileAsync([url], targetPath, expectedSize, expectedSha256, message, progress, cancellationToken, expectedSha1);

    /// <summary>
    /// Качает файл, пробуя адреса по порядку: первый рабочий выигрывает. Разные адреса — это одно и то
    /// же содержимое (зеркало и оригинал), поэтому и проверки размера/хэша, и докачка общие для всех.
    /// </summary>
    private async Task DownloadFileAsync(IReadOnlyList<string> urls, string targetPath, long expectedSize, string expectedSha256, string message, IProgress<FileSyncProgress>? progress, CancellationToken cancellationToken, string expectedSha1 = "")
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

        var candidates = urls.Where(candidate => !string.IsNullOrWhiteSpace(candidate)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException($"Не задан адрес для загрузки файла {Path.GetFileName(targetPath)}.");
        }

        foreach (var url in candidates)
        {
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

            if (candidates.Count > 1)
            {
                // Недокачанный кусок от предыдущего адреса выбрасываем. Обычно зеркала отдают
                // побайтно одно и то же, но не всегда: если у зеркала оказалась другая ревизия файла,
                // докачка поверх неё даёт склейку двух разных файлов и провал проверки sha.
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (IOException)
                    {
                        // Файл занят — следующая попытка разберётся сама по размеру/хэшу.
                    }
                }

                progress?.Report(new FileSyncProgress(0, "Пробую запасной адрес", $"Адрес недоступен, перехожу к следующему: {url}"));
            }
        }

        // В сообщении перечисляем ВСЕ адреса: по хосту в тексте ErrorClassifier отличает блокировку
        // серверов Mojang от обычного сбоя сети, и терять эту улику при наличии зеркал нельзя.
        throw new IOException(
            $"Не удалось загрузить файл после нескольких попыток: {string.Join(", ", candidates)}",
            lastError);
    }

    /// <summary>
    /// Порядок адресов для ванильного файла: зеркала из <c>mojangMirrorBaseUrls</c>, затем точечная
    /// ссылка из манифеста (<c>clientUrl</c>, <c>librariesBaseUrl</c> и т.п.), затем оригинальный
    /// адрес Mojang.
    ///
    /// Точечные ссылки раньше ЗАМЕНЯЛИ Mojang, а не дополняли: заполнил поле — и падение своего
    /// хостинга ломало установку всем, без запасного варианта. Теперь это просто первый кандидат.
    /// <paramref name="mirrorRelativePath"/> — путь внутри папки игры (versions/…, libraries/…, assets/…).
    /// </summary>
    private static List<string> BuildVanillaCandidates(
        ManifestRuntimeInfo runtime,
        string mirrorRelativePath,
        string? manifestUrl,
        string? mojangUrl)
    {
        var candidates = new List<string>();
        foreach (var mirror in runtime.MojangMirrorBaseUrls)
        {
            if (!string.IsNullOrWhiteSpace(mirror))
            {
                candidates.Add($"{mirror.TrimEnd('/')}/{mirrorRelativePath.TrimStart('/')}");
            }
        }

        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            candidates.Add(manifestUrl);
        }

        if (!string.IsNullOrWhiteSpace(mojangUrl))
        {
            candidates.Add(mojangUrl);
        }

        return candidates;
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

        // InvalidDataException = несовпадение размера/хэша скачанного файла. Это восстановимо:
        // при размере — докачка с места обрыва, при хэше — temp уже удалён и качаем заново.
        // Поэтому такие ошибки тоже повторяем, а не валим установку с первого обрыва ассета.
        if (exception is HttpRequestException or IOException or InvalidDataException)
        {
            return true;
        }

        return exception.InnerException is not null && IsTransientDownloadError(exception.InnerException, cancellationToken);
    }

    private static string GetInstallRoot(LauncherConfiguration configuration, ModpackManifest manifest, UserSettings settings)
    {
        // При мульти-сборочном каталоге изолируем сборки по id, чтобы кастомный путь пользователя
        // не сваливал разные сборки (Forge/NeoForge) в одну папку. Одиночный режим — путь как был.
        var catalogModpackId = configuration.IsMultiModpackCatalog ? manifest.Modpack.Id : null;
        return settings.ResolveInstallRoot(manifest.Install.Root, configuration.GetDistributionRoot(), catalogModpackId);
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

        return new ForgeInstallerResult(process.ExitCode, stdoutPath, stderrPath, error, DetectUnreachableHost(output, error));
    }

    // Ищет в выводе установщика домен, который не резолвится. UnknownHostException попадает в stdout
    // (stderr у падений инсталлера обычно пустой), плюс он сам печатает шапку вида
    // «Host: piston-meta.mojang.com [Unknown]» — по ней ловим случай, когда до исключения не дошло.
    private static string DetectUnreachableHost(string stdout, string stderr)
    {
        foreach (var text in new[] { stdout, stderr })
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var match = Regex.Match(text, @"UnknownHostException:\s*([A-Za-z0-9._-]+)");
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        // Шапку смотрим только по хостам, с которых установщик реально качает файлы. Мёртвые
        // authserver/sessionserver Mojang не резолвятся и на полностью исправной машине — по ним
        // нельзя судить о блокировке (проверено на логе успешной установки).
        foreach (Match header in Regex.Matches(stdout ?? string.Empty, @"Host:\s*([A-Za-z0-9._-]+)\s*\[Unknown\]"))
        {
            var host = header.Groups[1].Value;
            if (InstallerDownloadHosts.Any(known => host.Equals(known, StringComparison.OrdinalIgnoreCase)))
            {
                return host;
            }
        }

        return string.Empty;
    }

    private static readonly string[] InstallerDownloadHosts =
    [
        "piston-meta.mojang.com",
        "piston-data.mojang.com",
        "launchermeta.mojang.com",
        "libraries.minecraft.net",
        "resources.download.minecraft.net",
        "maven.minecraftforge.net",
        "files.minecraftforge.net",
        "maven.neoforged.net"
    ];

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

    private static string ResolveConsoleJava(string root, string configuredJava, int managedJavaMajor = 17)
    {
        var candidates = JavaValidationService.ResolveConsoleJavaCandidates(root, configuredJava, managedJavaMajor);
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

    private sealed record AssetDownload(IReadOnlyList<string> Urls, string TargetPath, long Size, string Sha1);
    private sealed record ForgeInstallerResult(int ExitCode, string StdoutPath, string StderrPath, string ErrorText, string UnreachableHost = "")
    {
        public string ToLogLine() => $"Forge installer failed with code {ExitCode}. stderr: {StderrPath}";
    }
}
