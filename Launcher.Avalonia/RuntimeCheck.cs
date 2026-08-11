using System.Diagnostics;
using System.Net.Http;
using Launcher.App.Configuration;
using Launcher.App.Models;
using Launcher.App.Platform;
using Launcher.App.Services;
using Path = System.IO.Path;

namespace Launcher.Avalonia;

/// <summary>
/// Консольная проверка управляемой Java (<c>--check-runtime</c>): скачивает рантайм по манифесту
/// сборки, распаковывает и запускает <c>java -version</c>.
/// </summary>
/// <remarks>
/// Отдельный режим нужен потому, что на Linux и macOS Java приезжает в <c>tar.gz</c>, а после
/// распаковки ей обязателен бит запуска — ни один из этих шагов не выполняется на Windows,
/// то есть до первого живого прогона они были написаны вслепую.
/// </remarks>
internal static class RuntimeCheck
{
    /// <summary>
    /// <c>--check-install</c> и <c>--check-launch</c>: ставит сборку целиком, а при запросе
    /// ещё и запускает игру и смотрит, прожила ли она заданное время.
    /// </summary>
    public static async Task<int> RunInstallAsync(bool launchGame, int watchSeconds)
    {
        try
        {
            var configuration = LauncherConfiguration.Load(AppContext.BaseDirectory);
            var settings = UserSettings.Load(configuration.GetUserSettingsPath());
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

            var (manifest, name) = await ResolveModpackAsync(httpClient, configuration, settings);
            var installRoot = ModpackCatalogLogic.ResolveEffectiveInstallRoot(configuration, manifest, settings);
            Console.WriteLine($"Сборка: {name}, папка: {installRoot}");

            var space = DiskSpaceService.CheckInstallSpace(installRoot, manifest);
            Console.WriteLine($"Место на диске: {space.Message}");
            if (!space.IsOk)
            {
                Console.WriteLine("INSTALL_CHECK=FAIL не хватает места");
                return 1;
            }

            // Прогресс печатаем по шагам в 5%: иначе вывод тонет в тысячах строк.
            var lastReported = -10;
            IProgress<FileSyncProgress> Step(string stage) => new Progress<FileSyncProgress>(report =>
            {
                var rounded = (int)(report.Percentage / 5) * 5;
                if (rounded > lastReported)
                {
                    lastReported = rounded;
                    Console.WriteLine($"  [{stage}] {rounded,3}% {report.Message}");
                }
            });

            var started = DateTime.UtcNow;
            var outcome = await new GameInstallOrchestrator(httpClient).EnsureGameFilesAsync(
                configuration,
                manifest,
                null,
                settings,
                new GameInstallCallbacks(
                    message => Console.WriteLine($"  {message}"),
                    Step("java"),
                    Step("сборка"),
                    Step("файлы"),
                    () => Task.CompletedTask));

            Console.WriteLine($"Итог установки: {outcome.StatusText} ({outcome.Mode}, {outcome.DurationMs} мс)");
            Console.WriteLine($"Заняло: {(DateTime.UtcNow - started).TotalMinutes:F1} мин");

            var modsDirectory = Path.Combine(installRoot, "mods");
            var modCount = Directory.Exists(modsDirectory) ? Directory.GetFiles(modsDirectory, "*.jar").Length : 0;
            Console.WriteLine($"Модов в папке: {modCount}");
            Console.WriteLine($"INSTALL_CHECK={(modCount > 0 ? "PASS" : "FAIL")}");

            if (!launchGame)
            {
                return modCount > 0 ? 0 : 1;
            }

            return await LaunchAndWatchAsync(configuration, manifest, settings, installRoot, watchSeconds);
        }
        catch (Exception exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<int> LaunchAndWatchAsync(
        LauncherConfiguration configuration,
        ModpackManifest manifest,
        UserSettings settings,
        string installRoot,
        int watchSeconds)
    {
        var launchManifest = LauncherManifestFactory.CreateArchiveModeLaunchManifest(manifest, configuration);

        var javaCheck = await JavaValidationService.ValidateJavaAsync(
            installRoot, launchManifest, settings, CancellationToken.None);
        Console.WriteLine($"Java: {javaCheck.Message}");
        if (!javaCheck.IsOk)
        {
            Console.WriteLine("LAUNCH_CHECK=FAIL java не готова");
            return 1;
        }

        // Ник нужен настоящий: с «Player» сервер не пустит, а нам важен сам запуск.
        if (!UsernameRules.IsValid(settings.Username))
        {
            settings.Username = "LinuxTest";
        }

        var effective = LauncherConfiguration.Load(AppContext.BaseDirectory);
        effective.IsMultiModpackCatalog = configuration.IsMultiModpackCatalog;
        effective.DistributionRoot = installRoot;

        Console.WriteLine("Запускаю Minecraft...");
        var result = await new MinecraftLaunchService().LaunchAsync(effective, launchManifest, settings);
        Console.WriteLine($"Процесс: {result.Process.Id}, {result.FileName}");

        // Ждём либо выхода процесса, либо контрольного времени — что наступит раньше.
        await Task.WhenAny(
            result.Process.WaitForExitAsync(),
            Task.Delay(TimeSpan.FromSeconds(watchSeconds)));

        await Task.Delay(500);

        if (!result.Process.HasExited)
        {
            Console.WriteLine($"Игра жива через {watchSeconds} с — считаем запуск удавшимся.");
            Console.WriteLine("LAUNCH_CHECK=PASS");
            try { result.Process.Kill(entireProcessTree: true); } catch { /* уже вышла */ }
            return 0;
        }

        Console.WriteLine($"Игра завершилась, код {result.Process.ExitCode}");
        var analysis = CrashAnalyzerService.Analyze(installRoot, result.Process.ExitCode);
        Console.WriteLine($"Разбор: [{analysis.Category}] {analysis.Summary}");
        Console.WriteLine($"Код: {analysis.ExitCodeDescription}");
        Console.WriteLine($"Улика: {analysis.Evidence}");
        Console.WriteLine("LAUNCH_CHECK=FAIL");
        return 1;
    }

    private static async Task<(ModpackManifest Manifest, string Name)> ResolveModpackAsync(
        HttpClient httpClient, LauncherConfiguration configuration, UserSettings settings)
    {
        if (configuration.UsesCatalog())
        {
            var catalog = await new CatalogClient(httpClient).GetCatalogAsync(configuration.CatalogUrl);
            var entry = ModpackCatalogLogic.ChoosePreferred(catalog.Modpacks, settings.SelectedModpackId)
                        ?? throw new InvalidOperationException("В каталоге нет сборок");
            var manifest = await new ModpackManifestClient(httpClient).GetManifestAsync(entry.ManifestUrl);
            return (manifest, $"{entry.Name} ({entry.Id})");
        }

        var resolution = await new ModpackManifestResolver(new ModpackManifestClient(httpClient))
            .ResolveAsync(configuration.ModpackManifestUrl, configuration.GetCachedModpackManifestPath());
        return (resolution.Manifest, resolution.Manifest.Modpack.Name);
    }

    private static int Fail(Exception exception)
    {
        var info = global::Launcher.App.ErrorClassifier.Classify(exception);
        Console.WriteLine($"CHECK=FAIL {info.Title}: {info.Summary}");
        Console.WriteLine(exception);
        return 1;
    }

    public static async Task<int> RunAsync()
    {
        try
        {
            Console.WriteLine($"ОС: {HostPlatform.MojangOsName}, профиль: {LauncherProfile.DataFolderName}");

            // Способ установки определяет, как лаунчер будет обновляться. Печатаем, чтобы это
            // можно было проверить на живой системе, а не только тестом.
            var updatePlan = LauncherInstallation.BuildPlan(Environment.ProcessPath);
            Console.WriteLine($"Установка: {updatePlan.Kind}, самообновление: {(updatePlan.CanSelfUpdate ? "да" : "нет")}");
            Console.WriteLine($"Что делать при обновлении: {updatePlan.Instruction}");
            Console.WriteLine($"HOME={Environment.GetEnvironmentVariable("HOME")}");
            Console.WriteLine($"XDG_CONFIG_HOME={Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")}");
            Console.WriteLine($"ApplicationData='{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}'");
            Console.WriteLine($"UserProfile='{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}'");
            Console.WriteLine($"Раскрытие %AppData%\\ForgeLauncher → '{LauncherPaths.ExpandFull("%AppData%\\ForgeLauncher")}'");

            var configuration = LauncherConfiguration.Load(AppContext.BaseDirectory);
            var settings = UserSettings.Load(configuration.GetUserSettingsPath());
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

            ModpackManifest? manifest = null;
            if (configuration.UsesCatalog())
            {
                var catalog = await new CatalogClient(httpClient).GetCatalogAsync(configuration.CatalogUrl);
                var entry = ModpackCatalogLogic.ChoosePreferred(catalog.Modpacks, settings.SelectedModpackId);
                if (entry is null)
                {
                    Console.WriteLine("RUNTIME_CHECK=FAIL в каталоге нет сборок");
                    return 1;
                }

                Console.WriteLine($"Сборка: {entry.Name} ({entry.Id})");
                manifest = await new ModpackManifestClient(httpClient).GetManifestAsync(entry.ManifestUrl);
            }
            else
            {
                var resolution = await new ModpackManifestResolver(new ModpackManifestClient(httpClient))
                    .ResolveAsync(configuration.ModpackManifestUrl, configuration.GetCachedModpackManifestPath());
                manifest = resolution.Manifest;
            }

            var installRoot = ModpackCatalogLogic.ResolveEffectiveInstallRoot(configuration, manifest, settings);
            Console.WriteLine($"Папка установки: {installRoot}");
            Console.WriteLine($"Java по манифесту: {manifest?.Runtime.JavaVersion}");

            var javaPath = await new JavaRuntimeInstallService(httpClient).EnsureManagedJavaAsync(
                installRoot,
                manifest!.Runtime,
                new Progress<FileSyncProgress>(report => Console.WriteLine($"  {report.Percentage,3}% {report.Message}")),
                CancellationToken.None);

            Console.WriteLine($"Java: {javaPath}");
            if (!File.Exists(javaPath))
            {
                Console.WriteLine("RUNTIME_CHECK=FAIL исполняемого файла нет");
                return 1;
            }

            // Главная проверка Unix: без бита запуска процесс не стартует вовсе.
            if (!HostPlatform.IsWindows)
            {
                var mode = File.GetUnixFileMode(javaPath);
                Console.WriteLine($"Права: {mode}");
                if ((mode & UnixFileMode.UserExecute) == 0)
                {
                    Console.WriteLine("RUNTIME_CHECK=FAIL нет бита запуска у java");
                    return 1;
                }
            }

            using var process = Process.Start(new ProcessStartInfo(javaPath, "-version")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });

            if (process is null)
            {
                Console.WriteLine("RUNTIME_CHECK=FAIL java не запустилась");
                return 1;
            }

            // java -version пишет в stderr — это не ошибка, так было всегда.
            var version = (await process.StandardError.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();
            Console.WriteLine(version);

            var ok = process.ExitCode == 0 && version.Length > 0;
            Console.WriteLine($"RUNTIME_CHECK={(ok ? "PASS" : "FAIL")} exit={process.ExitCode}");
            return ok ? 0 : 1;
        }
        catch (Exception exception)
        {
            var info = global::Launcher.App.ErrorClassifier.Classify(exception);
            Console.WriteLine($"RUNTIME_CHECK=FAIL {info.Title}: {info.Summary}");
            Console.WriteLine(exception);
            return 1;
        }
    }
}
