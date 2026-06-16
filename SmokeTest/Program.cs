using Launcher.App.Configuration;
using Launcher.App.Models;
using Launcher.App.Services;

var distributionRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForgeLauncher");
var configuration = new LauncherConfiguration
{
    LauncherName = "Smoke Test",
    ManifestUrl = "http://127.0.0.1:8080/launcher-manifest.json",
    DistributionRoot = distributionRoot,
    LauncherExecutable = "Launcher.App.exe",
    LauncherVersionFile = "launcher.version"
};

using var httpClient = new HttpClient();
var manifestClient = new ManifestClient(httpClient);
var manifest = await manifestClient.GetManifestAsync(configuration.ManifestUrl);

var syncService = new FileSyncService(httpClient);
var syncSummary = await syncService.SyncAsync(configuration, manifest, null, CancellationToken.None);
Console.WriteLine($"SYNC_OK downloaded={syncSummary.DownloadedFiles} skipped={syncSummary.SkippedFiles}");

var userSettings = new UserSettings
{
    Username = "Player",
    MemoryMb = 4096
};

var launchService = new MinecraftLaunchService();
if (args.Contains("--diagnostic", StringComparer.OrdinalIgnoreCase))
{
    var result = await launchService.LaunchForDiagnosticsAsync(configuration, manifest, userSettings, TimeSpan.FromSeconds(90));
    Console.WriteLine($"DIAGNOSTIC_LAUNCH file={result.FileName}");
    Console.WriteLine($"exited={result.Exited} exitCode={result.ExitCode}");
    Console.WriteLine($"stdout={result.StdoutPath}");
    Console.WriteLine($"stderr={result.StderrPath}");
    Console.WriteLine(result.ArgumentsPreview);
}
else
{
    var result = await launchService.LaunchAsync(configuration, manifest, userSettings);
    Console.WriteLine($"LAUNCH_OK file={result.FileName}");
    Console.WriteLine(result.ArgumentsPreview);
}
