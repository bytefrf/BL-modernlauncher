using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Launcher.App.Configuration;
using Launcher.App.Models;
using Launcher.App.Platform;

namespace Launcher.App.Services;

public sealed class MinecraftLaunchService
{
    /// <param name="quickPlayServer">
    /// Адрес сервера («host» или «host:порт»). Если задан — игра стартует сразу с подключением к нему
    /// (механизм Quick Play, есть в Minecraft начиная с 1.20). Пусто — обычный запуск в главное меню.
    /// </param>
    public Task<LaunchResult> LaunchAsync(LauncherConfiguration configuration, LauncherManifest manifest, UserSettings settings, string? quickPlayServer = null)
    {
        var root = ResolveLaunchRoot(configuration, manifest, settings);
        Directory.CreateDirectory(root);

        var version = LoadVersion(root, manifest.Game.MainVersionId);
        var javaPath = ResolveJavaPath(root, string.IsNullOrWhiteSpace(settings.JavaExecutable) ? manifest.Game.JavaExecutable : settings.JavaExecutable, manifest.Game.JavaMajorVersion);
        var nativesDirectory = PrepareNatives(root, version);
        var classpath = BuildClasspath(root, version);
        var arguments = BuildArguments(root, manifest, settings, version, nativesDirectory, classpath, quickPlayServer);

        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = root,
            UseShellExecute = false,
            // Перехватываем stderr игры: ранние ошибки JVM (до инициализации log4j) не попадают
            // в logs/latest.log, и без этого крэш-анализатор их не видит.
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add($"@{WriteLaunchArgFile(root, arguments, "launch.args")}");

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Не удалось запустить Minecraft.");
        StartStderrDrain(process, root);
        return Task.FromResult(new LaunchResult(javaPath, string.Join(" ", arguments.Select(QuoteIfNeeded)), process));
    }

    public async Task<DiagnosticLaunchResult> LaunchForDiagnosticsAsync(LauncherConfiguration configuration, LauncherManifest manifest, UserSettings settings, TimeSpan timeout)
    {
        var root = ResolveLaunchRoot(configuration, manifest, settings);
        Directory.CreateDirectory(root);

        var version = LoadVersion(root, manifest.Game.MainVersionId);
        var javaPath = ResolveJavaPath(root, string.IsNullOrWhiteSpace(settings.JavaExecutable) ? manifest.Game.JavaExecutable : settings.JavaExecutable, manifest.Game.JavaMajorVersion);
        // Диагностике нужен вывод в консоль, поэтому бесконсольный javaw заменяем на java.
        if (Path.GetFileName(javaPath).Equals(HostPlatform.JavawExecutableName, StringComparison.OrdinalIgnoreCase)
            && !HostPlatform.JavawExecutableName.Equals(HostPlatform.JavaExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            javaPath = Path.Combine(Path.GetDirectoryName(javaPath)!, HostPlatform.JavaExecutableName);
        }

        var nativesDirectory = PrepareNatives(root, version);
        var classpath = BuildClasspath(root, version);
        var arguments = BuildArguments(root, manifest, settings, version, nativesDirectory, classpath);

        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add($"@{WriteLaunchArgFile(root, arguments, "launch-diagnostic.args")}");

        var stdoutPath = Path.Combine(root, "logs", "minecraft-diagnostic.stdout.log");
        var stderrPath = Path.Combine(root, "logs", "minecraft-diagnostic.stderr.log");
        Directory.CreateDirectory(Path.GetDirectoryName(stdoutPath)!);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Minecraft.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var waitTask = process.WaitForExitAsync();
        var exited = await Task.WhenAny(waitTask, Task.Delay(timeout)) == waitTask;
        if (!exited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        await File.WriteAllTextAsync(stdoutPath, stdout);
        await File.WriteAllTextAsync(stderrPath, stderr);

        return new DiagnosticLaunchResult(javaPath, string.Join(" ", arguments.Select(QuoteIfNeeded)), exited, process.ExitCode, stdoutPath, stderrPath);
    }

    // Запуск должен использовать ту же папку, что и установка: в каталог-режиме — per-pack
    // (кастомный путь + id сборки), иначе папка установки и запуска разойдутся.
    private static string ResolveLaunchRoot(LauncherConfiguration configuration, LauncherManifest manifest, UserSettings settings)
    {
        var catalogModpackId = configuration.IsMultiModpackCatalog && !string.IsNullOrWhiteSpace(manifest.Game.ModpackId)
            ? manifest.Game.ModpackId
            : null;
        return settings.ResolveInstallRoot(string.Empty, configuration.GetDistributionRoot(), catalogModpackId);
    }

    private static ResolvedVersion LoadVersion(string root, string versionId)
    {
        var versionsRoot = Path.Combine(root, "versions");
        var versionPath = Path.Combine(versionsRoot, versionId, $"{versionId}.json");
        if (!File.Exists(versionPath))
        {
            throw new FileNotFoundException($"Не найден version JSON: {versionPath}");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(versionPath));
        var current = ParseVersion(document.RootElement, versionId, versionsRoot);
        if (!string.IsNullOrWhiteSpace(current.InheritsFrom))
        {
            var parent = LoadVersion(root, current.InheritsFrom);
            return Merge(parent, current);
        }

        return current;
    }

    private static ResolvedVersion ParseVersion(JsonElement root, string versionId, string versionsRoot)
    {
        var version = new ResolvedVersion
        {
            Id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? versionId : versionId,
            MainClass = root.TryGetProperty("mainClass", out var mainClassElement) ? mainClassElement.GetString() : null,
            InheritsFrom = root.TryGetProperty("inheritsFrom", out var inheritsFromElement) ? inheritsFromElement.GetString() : null,
            Assets = root.TryGetProperty("assets", out var assetsElement) ? assetsElement.GetString() : null,
            Type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : "release",
            Jar = root.TryGetProperty("jar", out var jarElement) ? jarElement.GetString() : null,
            HasOwnJar = File.Exists(Path.Combine(versionsRoot, versionId, $"{versionId}.jar")),
            AssetIndexId = root.TryGetProperty("assetIndex", out var assetIndexElement) && assetIndexElement.TryGetProperty("id", out var assetIndexId)
                ? assetIndexId.GetString()
                : null
        };

        if (root.TryGetProperty("minecraftArguments", out var legacyArgumentsElement))
        {
            version.LegacyGameArguments = legacyArgumentsElement.GetString();
        }

        if (root.TryGetProperty("libraries", out var librariesElement) && librariesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var libraryElement in librariesElement.EnumerateArray())
            {
                version.Libraries.Add(ParseLibrary(libraryElement));
            }
        }

        if (root.TryGetProperty("arguments", out var argumentsElement))
        {
            if (argumentsElement.TryGetProperty("game", out var gameArgs) && gameArgs.ValueKind == JsonValueKind.Array)
            {
                version.GameArguments.AddRange(ParseArgumentList(gameArgs));
            }

            if (argumentsElement.TryGetProperty("jvm", out var jvmArgs) && jvmArgs.ValueKind == JsonValueKind.Array)
            {
                version.JvmArguments.AddRange(ParseArgumentList(jvmArgs));
            }
        }

        return version;
    }

    private static LibrarySpec ParseLibrary(JsonElement element)
    {
        var library = new LibrarySpec
        {
            Name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty
        };

        if (element.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in rulesElement.EnumerateArray())
            {
                library.Rules.Add(ParseRule(rule));
            }
        }

        if (element.TryGetProperty("downloads", out var downloadsElement))
        {
            if (downloadsElement.TryGetProperty("artifact", out var artifactElement) && artifactElement.TryGetProperty("path", out var artifactPath))
            {
                library.ArtifactPath = artifactPath.GetString();
            }

            if (downloadsElement.TryGetProperty("classifiers", out var classifiersElement) && classifiersElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in classifiersElement.EnumerateObject())
                {
                    if (property.Value.TryGetProperty("path", out var classifierPath))
                    {
                        library.Classifiers[property.Name] = classifierPath.GetString() ?? string.Empty;
                    }
                }
            }
        }

        if (element.TryGetProperty("natives", out var nativesElement) && nativesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in nativesElement.EnumerateObject())
            {
                library.Natives[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return library;
    }

    private static IEnumerable<ConditionalArgument> ParseArgumentList(JsonElement element)
    {
        foreach (var entry in element.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                yield return new ConditionalArgument { Values = [entry.GetString() ?? string.Empty] };
                continue;
            }

            var argument = new ConditionalArgument();
            if (entry.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rule in rulesElement.EnumerateArray())
                {
                    argument.Rules.Add(ParseRule(rule));
                }
            }

            if (entry.TryGetProperty("value", out var valueElement))
            {
                if (valueElement.ValueKind == JsonValueKind.String)
                {
                    argument.Values.Add(valueElement.GetString() ?? string.Empty);
                }
                else if (valueElement.ValueKind == JsonValueKind.Array)
                {
                    argument.Values.AddRange(valueElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty));
                }
            }

            yield return argument;
        }
    }

    private static RuleSpec ParseRule(JsonElement element)
    {
        var rule = new RuleSpec
        {
            Action = element.TryGetProperty("action", out var actionElement) ? actionElement.GetString() ?? "allow" : "allow",
            OsName = element.TryGetProperty("os", out var osElement) && osElement.TryGetProperty("name", out var osNameElement)
                ? osNameElement.GetString()
                : null,
            OsArch = element.TryGetProperty("os", out var osArchParent) && osArchParent.TryGetProperty("arch", out var osArchElement)
                ? osArchElement.GetString()
                : null
        };

        if (element.TryGetProperty("features", out var featuresElement) && featuresElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in featuresElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.True || property.Value.ValueKind == JsonValueKind.False)
                {
                    rule.Features[property.Name] = property.Value.GetBoolean();
                }
            }
        }

        return rule;
    }

    private static ResolvedVersion Merge(ResolvedVersion parent, ResolvedVersion child)
    {
        return new ResolvedVersion
        {
            Id = child.Id,
            MainClass = child.MainClass ?? parent.MainClass,
            Assets = child.Assets ?? parent.Assets,
            Type = child.Type ?? parent.Type,
            Jar = child.Jar ?? parent.Jar,
            HasOwnJar = child.HasOwnJar,
            ParentJarVersionId = parent.GetJarVersionId(),
            AssetIndexId = child.AssetIndexId ?? parent.AssetIndexId,
            LegacyGameArguments = child.LegacyGameArguments ?? parent.LegacyGameArguments,
            Libraries = [.. parent.Libraries, .. child.Libraries],
            GameArguments = [.. parent.GameArguments, .. child.GameArguments],
            JvmArguments = [.. parent.JvmArguments, .. child.JvmArguments]
        };
    }

    private static string ResolveJavaPath(string root, string configuredValue, int managedJavaMajor)
    {
        var requestedJava = string.IsNullOrWhiteSpace(configuredValue) ? HostPlatform.JavawExecutableName : configuredValue;
        // Настройка может указывать на бесконсольный javaw (только Windows) — версию и кандидатов
        // ищем по обычному java, лежащему рядом.
        var consoleValue = Path.GetFileName(requestedJava).Equals(HostPlatform.JavawExecutableName, StringComparison.OrdinalIgnoreCase)
            && !HostPlatform.JavawExecutableName.Equals(HostPlatform.JavaExecutableName, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(requestedJava) ?? string.Empty, HostPlatform.JavaExecutableName)
            : requestedJava;

        foreach (var candidate in JavaValidationService.ResolveConsoleJavaCandidates(root, consoleValue, managedJavaMajor))
        {
            if (!Path.IsPathRooted(candidate) || File.Exists(candidate))
            {
                return JavaValidationService.GetPreferredLaunchJavaPath(candidate);
            }
        }

        return requestedJava;
    }

    private static string PrepareNatives(string root, ResolvedVersion version)
    {
        var nativesContainer = Path.Combine(root, ".launcher", "natives");
        CleanupOldNatives(nativesContainer);

        var nativesRoot = Path.Combine(nativesContainer, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nativesRoot);
        var fullNativesRoot = Path.GetFullPath(nativesRoot);

        foreach (var library in version.Libraries.Where(IsAllowed))
        {
            if (!library.Natives.TryGetValue(HostPlatform.MojangOsName, out var nativeKey))
            {
                continue;
            }

            nativeKey = nativeKey.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
            if (!library.Classifiers.TryGetValue(nativeKey, out var relativePath))
            {
                continue;
            }

            var fullPath = Path.Combine(root, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            using var archive = ZipFile.OpenRead(fullPath);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(nativesRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsWithinDirectory(target, fullNativesRoot))
                {
                    throw new InvalidOperationException($"Native entry escapes natives directory: {entry.FullName}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }
        }

        return nativesRoot;
    }

    private static void CleanupOldNatives(string nativesContainer)
    {
        if (!Directory.Exists(nativesContainer))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(nativesContainer))
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
                // Каталог может быть занят ещё работающим экземпляром игры — пропускаем.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsWithinDirectory(string candidateFullPath, string rootFullPath)
    {
        var normalizedRoot = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidateFullPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || candidateFullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildClasspath(string root, ResolvedVersion version)
    {
        // Дедуп библиотек по maven-координате (group:artifact[:classifier]), оставляя ПОСЛЕДНЮЮ запись.
        // При inheritsFrom список = [vanilla(parent) .. loader(child)], поэтому версия лоадера побеждает.
        // Без дедупа NeoForge (BootstrapLauncher 2.x) падает «Duplicate key … gson» — одна и та же jar
        // дважды на classpath. Ванильные лаунчеры дедупят так же; для Forge это безопасно (убирает дубли).
        var pathByCoordinate = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var coordinateOrder = new List<string>();
        foreach (var library in version.Libraries.Where(IsAllowed))
        {
            if (string.IsNullOrWhiteSpace(library.ArtifactPath))
            {
                continue;
            }

            var coordinate = LibraryCoordinateKey(library);
            if (pathByCoordinate.ContainsKey(coordinate))
            {
                coordinateOrder.Remove(coordinate);
            }

            pathByCoordinate[coordinate] = library.ArtifactPath;
            coordinateOrder.Add(coordinate);
        }

        var entries = new List<string>();
        foreach (var coordinate in coordinateOrder)
        {
            var artifactPath = Path.Combine(root, "libraries", pathByCoordinate[coordinate].Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(artifactPath))
            {
                entries.Add(artifactPath);
            }
        }

        if (version.MainClass == "cpw.mods.bootstraplauncher.BootstrapLauncher")
        {
            return string.Join(Path.PathSeparator, entries);
        }

        var jarVersionId = version.GetJarVersionId();
        var versionJarPath = Path.Combine(root, "versions", jarVersionId, $"{jarVersionId}.jar");
        if (!File.Exists(versionJarPath))
        {
            throw new FileNotFoundException($"Не найден JAR версии: {versionJarPath}");
        }

        entries.Add(versionJarPath);
        return string.Join(Path.PathSeparator, entries);
    }

    // Ключ дедупа classpath: group:artifact[:classifier] без версии. Нестандартное имя → путь как есть.
    private static string LibraryCoordinateKey(LibrarySpec library)
    {
        if (!string.IsNullOrWhiteSpace(library.Name))
        {
            var parts = library.Name.Split(':');
            if (parts.Length >= 2)
            {
                var classifier = parts.Length >= 4 ? parts[3] : string.Empty;
                return $"{parts[0]}:{parts[1]}:{classifier}".ToLowerInvariant();
            }
        }

        return (library.ArtifactPath ?? library.Name).ToLowerInvariant();
    }

    private static List<string> BuildArguments(string root, LauncherManifest manifest, UserSettings settings, ResolvedVersion version, string nativesDirectory, string classpath, string? quickPlayServer = null)
    {
        var quickPlayTarget = (quickPlayServer ?? string.Empty).Trim();
        var useQuickPlay = quickPlayTarget.Length > 0;
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = settings.Username,
            ["version_name"] = version.Id,
            ["game_directory"] = root,
            ["assets_root"] = Path.Combine(root, "assets"),
            ["assets_index_name"] = version.AssetIndexId ?? version.Assets ?? "legacy",
            ["auth_uuid"] = CreateOfflineUuid(settings.Username),
            ["auth_access_token"] = "0",
            ["clientid"] = "0",
            ["user_type"] = "offline",
            ["version_type"] = version.Type ?? "release",
            ["natives_directory"] = nativesDirectory,
            ["launcher_name"] = "CustomForgeLauncher",
            ["launcher_version"] = manifest.Launcher.Version,
            ["classpath"] = classpath,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = Path.Combine(root, "libraries"),
            ["user_properties"] = "{}",
            ["game_assets"] = Path.Combine(root, "assets", "virtual", "legacy"),
            ["resolution_width"] = settings.ResolutionWidth.ToString(),
            ["resolution_height"] = settings.ResolutionHeight.ToString(),
            ["quickPlayMultiplayer"] = quickPlayTarget,
            ["quickPlayPath"] = string.Empty,
            ["quickPlaySingleplayer"] = string.Empty,
            ["quickPlayRealms"] = string.Empty
        };

        var maxHeapMb = ClampHeapToPhysicalMemory(settings.MemoryMb);
        var initialHeapMb = Math.Min(1024, maxHeapMb);
        var arguments = new List<string> { $"-Xms{initialHeapMb}M", $"-Xmx{maxHeapMb}M" };

        if (HostPlatform.IsMacOS)
        {
            // Без этого GLFW не может создать окно: на macOS работа с интерфейсом разрешена только
            // из главного потока. Игра иначе падает сразу после запуска.
            arguments.Add("-XstartOnFirstThread");
            // Имя процесса в Dock и в меню — иначе там будет «java».
            arguments.Add("-Xdock:name=Minecraft");
        }

        arguments.AddRange(manifest.Game.JavaArguments.Select(value => ReplaceVariables(value, variables)));
        arguments.AddRange(SplitAdditionalArguments(settings.JvmArguments).Select(value => ReplaceVariables(value, variables)));

        var featureFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = settings.UseCustomResolution,
            // has_quick_plays_support включает аргумент --quickPlayPath (лог быстрого входа) — он нам
            // не нужен и требует путь, поэтому остаётся выключенным даже при быстром входе.
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = useQuickPlay,
            ["is_quick_play_realms"] = false
        };

        if (version.JvmArguments.Count > 0)
        {
            arguments.AddRange(ResolveConditionalArguments(version.JvmArguments, variables, featureFlags));
        }
        else
        {
            arguments.Add($"-Djava.library.path={nativesDirectory}");
            arguments.Add("-cp");
            arguments.Add(classpath);
        }

        if (string.IsNullOrWhiteSpace(version.MainClass))
        {
            throw new InvalidOperationException("В version JSON не найден mainClass.");
        }

        arguments.Add(version.MainClass);

        if (version.GameArguments.Count > 0)
        {
            arguments.AddRange(ResolveConditionalArguments(version.GameArguments, variables, featureFlags));
        }
        else if (!string.IsNullOrWhiteSpace(version.LegacyGameArguments))
        {
            arguments.AddRange(SplitLegacyArguments(version.LegacyGameArguments).Select(value => ReplaceVariables(value, variables)));
        }

        arguments.AddRange(manifest.Game.GameArguments.Select(value => ReplaceVariables(value, variables)));
        arguments.AddRange(SplitAdditionalArguments(settings.GameArguments).Select(value => ReplaceVariables(value, variables)));

        // Обычно аргумент приходит из version.json по фиче is_quick_play_multiplayer. Но у сборок со
        // старым или урезанным version.json его может не быть — тогда дописываем сами. Неизвестные
        // аргументы Minecraft игнорирует, так что для версий без Quick Play это безопасно.
        if (useQuickPlay && !arguments.Contains("--quickPlayMultiplayer", StringComparer.Ordinal))
        {
            arguments.Add("--quickPlayMultiplayer");
            arguments.Add(quickPlayTarget);
        }

        return arguments;
    }

    // Modded Minecraft требует МНОГО off-heap памяти (текстуры, нативы LWJGL, буферы видеодрайвера).
    // Раньше брали до 80% RAM под -Xmx — на больших значениях драйверу/текстурам не оставалось места,
    // отсюда краши драйвера (EXCEPTION_ACCESS_VIOLATION в nvoglv64.dll) и нативный OutOfMemoryError на
    // загрузке текстур. Теперь резервируем фиксированный минимум 3.5 ГБ (или 25% RAM, что больше) под
    // нативку, а под heap отдаём остальное. На 16 ГБ потолок ≈ 12 ГБ, на 8 ГБ ≈ 4.5 ГБ.
    private static int ClampHeapToPhysicalMemory(int requestedMb)
    {
        var requested = Math.Max(512, requestedMb);
        try
        {
            var totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (totalBytes > 0)
            {
                var totalMb = (int)Math.Min(int.MaxValue, totalBytes / (1024L * 1024L));
                var reserveMb = Math.Max(3584, totalMb / 4);
                var safeCeilingMb = Math.Max(1024, totalMb - reserveMb);
                return Math.Min(requested, safeCeilingMb);
            }
        }
        catch
        {
            // Если не удалось определить память — используем запрошенное значение как есть.
        }

        return requested;
    }

    private static IEnumerable<string> SplitAdditionalArguments(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var argument in SplitLegacyArguments(value))
        {
            yield return argument;
        }
    }

    private static IEnumerable<string> ResolveConditionalArguments(
        IEnumerable<ConditionalArgument> items,
        Dictionary<string, string> variables,
        IReadOnlyDictionary<string, bool> featureFlags)
    {
        foreach (var item in items)
        {
            if (!IsAllowed(item.Rules, featureFlags))
            {
                continue;
            }

            foreach (var value in item.Values)
            {
                yield return ReplaceVariables(value, variables);
            }
        }
    }

    private static IEnumerable<string> SplitLegacyArguments(string value)
    {
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var symbol in value)
        {
            if (symbol == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(symbol) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(symbol);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static bool IsAllowed(LibrarySpec library) => IsAllowed(library.Rules, EmptyFeatures);

    private static bool IsAllowed(IReadOnlyCollection<RuleSpec> rules, IReadOnlyDictionary<string, bool> featureFlags)
    {
        if (rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules)
        {
            var osApplies = string.IsNullOrWhiteSpace(rule.OsName) || rule.OsName.Equals(HostPlatform.MojangOsName, StringComparison.OrdinalIgnoreCase);
            var archApplies = string.IsNullOrWhiteSpace(rule.OsArch) || ArchMatches(rule.OsArch);
            var featuresApply = rule.Features.Count == 0 || rule.Features.All(feature =>
            {
                var actual = featureFlags.TryGetValue(feature.Key, out var value) && value;
                return actual == feature.Value;
            });

            var applies = osApplies && archApplies && featuresApply;
            if (applies)
            {
                allowed = rule.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    private static string ReplaceVariables(string input, IReadOnlyDictionary<string, string> variables)
    {
        var output = input;
        foreach (var pair in variables)
        {
            output = output.Replace("${" + pair.Key + "}", pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        return output;
    }

    private static string CreateOfflineUuid(string username)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes($"OfflinePlayer:{username}"));
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x30); // версия 3 (UUIDv3, как в vanilla offline)
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); // вариант RFC 4122
        return $"{Convert.ToHexString(bytes.AsSpan(0, 4))}-{Convert.ToHexString(bytes.AsSpan(4, 2))}-{Convert.ToHexString(bytes.AsSpan(6, 2))}-{Convert.ToHexString(bytes.AsSpan(8, 2))}-{Convert.ToHexString(bytes.AsSpan(10, 6))}".ToLowerInvariant();
    }

    private static string QuoteIfNeeded(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    // Непрерывно сливаем stderr игры в файл на фоновом потоке. Это обязательно при RedirectStandardError:
    // если не читать пайп, его 64-КБ буфер переполнится и игра зависнет на записи в stderr.
    private static void StartStderrDrain(Process process, string root)
    {
        var logPath = Path.Combine(root, "logs", "minecraft-stderr.log");
        _ = Task.Run(async () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                await using var file = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                await process.StandardError.BaseStream.CopyToAsync(file);
            }
            catch
            {
                // Диагностический поток не должен влиять на игру — молча игнорируем ошибки слива.
            }
        });
    }

    private static string WriteLaunchArgFile(string root, IReadOnlyList<string> arguments, string fileName)
    {
        var launcherDirectory = Path.Combine(root, ".launcher");
        Directory.CreateDirectory(launcherDirectory);
        var argFilePath = Path.Combine(launcherDirectory, fileName);

        var builder = new StringBuilder();
        foreach (var argument in arguments)
        {
            builder.AppendLine(EscapeArgFileToken(argument));
        }

        // ВАЖНО: пишем байты через GetBytes, а НЕ File.WriteAllText(..., encoding). WriteAllText
        // добавляет преамбулу (BOM) кодировки, и у игроков с включённой в Windows опцией
        // «Beta: Unicode UTF-8 worldwide language support» (ACP = 65001) в начало argfile попадал
        // UTF-8 BOM. Java читала его как часть первого аргумента и падала с
        // "ClassNotFoundException: ﻿-Xms1024M". GetBytes преамбулу не добавляет никогда.
        File.WriteAllBytes(argFilePath, ResolveArgFileEncoding().GetBytes(builder.ToString()));
        return argFilePath;
    }

    [SupportedOSPlatform("windows")]
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetACP();

    // JDK 17 читает @argfile в системной ANSI-кодировке Windows (например cp1251 на русской ОС),
    // а .NET по умолчанию пишет файл в UTF-8. Из-за этого путь с не-ASCII символами
    // (например C:\Users\Ильнар\...) в argfile искажался, и Java не находила jar'ы модулей
    // (bootstraplauncher/securejarhandler) → "Could not find or load main class ... BootstrapLauncher",
    // выход с кодом 1 и без логов. У игроков с латинским именем пользователя проблемы нет — отсюда
    // "у части игроков". Пишем argfile в той же кодировке, что читает JRE.
    private static Encoding ResolveArgFileEncoding()
    {
        // На Linux и macOS понятия ANSI-кодировки нет: JVM читает argfile в UTF-8, и путь
        // с кириллицей отрабатывает корректно. Без преамбулы — BOM ломает первый аргумент.
        if (!HostPlatform.IsWindows)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        try
        {
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetACP());
        }
        catch
        {
            // Без преамбулы: Encoding.UTF8 её содержит, а BOM в argfile ломает первый аргумент.
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }

    // В Java @argfile обратный слэш — escape-символ, поэтому каждый токен оборачиваем в кавычки
    // и экранируем \ и " (иначе пути вида C:\Users теряют слэши).
    private static string EscapeArgFileToken(string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }

    private static bool ArchMatches(string arch)
    {
        var current = RuntimeInformation.OSArchitecture;
        return arch.Trim().ToLowerInvariant() switch
        {
            "x86" or "i386" or "i686" => current == Architecture.X86,
            "x86_64" or "amd64" or "x64" => current == Architecture.X64,
            "arm64" or "aarch64" => current == Architecture.Arm64,
            "arm" or "arm32" => current == Architecture.Arm,
            _ => true // неизвестная строка архитектуры — не отфильтровываем, чтобы не потерять нужные артефакты
        };
    }

    private sealed class ResolvedVersion
    {
        public string Id { get; set; } = string.Empty;
        public string? MainClass { get; set; }
        public string? InheritsFrom { get; set; }
        public string? Assets { get; set; }
        public string? Type { get; set; }
        public string? Jar { get; set; }
        public bool HasOwnJar { get; set; }
        public string? ParentJarVersionId { get; set; }
        public string? AssetIndexId { get; set; }
        public string? LegacyGameArguments { get; set; }
        public List<LibrarySpec> Libraries { get; set; } = [];
        public List<ConditionalArgument> GameArguments { get; set; } = [];
        public List<ConditionalArgument> JvmArguments { get; set; } = [];
        public string GetJarVersionId() => HasOwnJar ? Id : (!string.IsNullOrWhiteSpace(Jar) ? Jar : ParentJarVersionId ?? Id);
    }

    private sealed class LibrarySpec
    {
        public string Name { get; set; } = string.Empty;
        public string? ArtifactPath { get; set; }
        public Dictionary<string, string> Natives { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Classifiers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RuleSpec> Rules { get; } = [];
    }

    private sealed class ConditionalArgument
    {
        public List<string> Values { get; init; } = [];
        public List<RuleSpec> Rules { get; } = [];
    }

    private sealed class RuleSpec
    {
        public string Action { get; set; } = "allow";
        public string? OsName { get; set; }
        public string? OsArch { get; set; }
        public Dictionary<string, bool> Features { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly IReadOnlyDictionary<string, bool> EmptyFeatures = new Dictionary<string, bool>();
}

public sealed record LaunchResult(string FileName, string ArgumentsPreview, Process Process);

public sealed record DiagnosticLaunchResult(string FileName, string ArgumentsPreview, bool Exited, int ExitCode, string StdoutPath, string StderrPath);
