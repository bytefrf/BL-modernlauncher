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
    public static async Task<int> RunAsync()
    {
        try
        {
            Console.WriteLine($"ОС: {HostPlatform.MojangOsName}, профиль: {LauncherProfile.DataFolderName}");
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
