using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Launcher.Bootstrapper;

internal static class Program
{
    private static FileStream? _instanceLockStream;

    [STAThread]
    private static async Task Main(string[] args)
    {
        WinForms.Application.EnableVisualStyles();
        WinForms.Application.SetCompatibleTextRenderingDefault(false);

        using var httpClient = new HttpClient();

        var bootstrapperDirectory = AppContext.BaseDirectory;
        var configuration = LauncherConfiguration.Load(bootstrapperDirectory);
        var stateStore = new BootstrapperStateStore(configuration.LauncherName);

        if (args.Contains("--download-update-in-background", StringComparer.OrdinalIgnoreCase))
        {
            var installDirectory = stateStore.LoadInstallDirectory();
            if (!string.IsNullOrWhiteSpace(installDirectory))
            {
                await TryDownloadLauncherUpdateForNextRunAsync(httpClient, configuration, installDirectory);
            }

            return;
        }

        if (!TryAcquireSingleInstanceLock())
        {
            return;
        }

        await EnsureVisualCppRuntimeAsync(httpClient);

        var launcherInstallDirectory = EnsureLauncherInstallDirectory(stateStore, configuration);
        Directory.CreateDirectory(launcherInstallDirectory);

        ApplyPendingLauncherUpdate(launcherInstallDirectory);
        await EnsureLauncherInstalledAsync(httpClient, configuration, launcherInstallDirectory, bootstrapperDirectory);

        var launcherPath = Path.Combine(launcherInstallDirectory, configuration.LauncherExecutable);
        if (!File.Exists(launcherPath))
        {
            throw new FileNotFoundException($"Launcher executable was not found: {launcherPath}");
        }

        var launchArguments = string.Join(" ", args.Select(QuoteIfNeeded));
        Process.Start(new ProcessStartInfo
        {
            FileName = launcherPath,
            Arguments = launchArguments,
            WorkingDirectory = launcherInstallDirectory,
            UseShellExecute = true
        });

        StartBackgroundUpdater();

        void StartBackgroundUpdater()
        {
            try
            {
                var currentExe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                {
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = currentExe,
                    Arguments = "--download-update-in-background",
                    WorkingDirectory = bootstrapperDirectory,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch
            {
            }
        }
    }

    private static bool TryAcquireSingleInstanceLock()
    {
        try
        {
            var lockDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TerraFirmaGregModernLauncher");
            Directory.CreateDirectory(lockDirectory);

            var lockPath = Path.Combine(lockDirectory, "bootstrapper.instance.lock");
            _instanceLockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureLauncherInstallDirectory(BootstrapperStateStore stateStore, LauncherConfiguration configuration)
    {
        var existing = stateStore.LoadInstallDirectory();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var defaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            SanitizeFolderName(configuration.LauncherName));

        var selectedDirectory = PromptInstallDirectory(configuration.LauncherName, defaultDirectory);
        if (string.IsNullOrWhiteSpace(selectedDirectory))
        {
            throw new OperationCanceledException("Папка установки лаунчера не выбрана.");
        }

        var fullPath = Path.GetFullPath(selectedDirectory);
        stateStore.SaveInstallDirectory(fullPath);
        return fullPath;
    }

    private static string PromptInstallDirectory(string launcherName, string defaultDirectory)
    {
        using var form = new InstallDirectoryForm(launcherName, defaultDirectory);
        return form.ShowDialog() == WinForms.DialogResult.OK
            ? form.SelectedDirectory
            : string.Empty;
    }

    private static async Task EnsureLauncherInstalledAsync(HttpClient httpClient, LauncherConfiguration configuration, string installDirectory, string bootstrapperDirectory)
    {
        var launcherPath = Path.Combine(installDirectory, configuration.LauncherExecutable);
        if (File.Exists(launcherPath))
        {
            var availableUpdate = await TryGetLauncherUpdateAsync(httpClient, configuration);
            if (availableUpdate is not null)
            {
                var localVersion = ResolveInstalledLauncherVersion(launcherPath);
                if (IsRemoteVersionNewer(localVersion, availableUpdate.Version))
                {
                    await RunWithProgressAsync(
                        "Обновление лаунчера",
                        progress => DownloadAndExtractLauncherPackageAsync(httpClient, availableUpdate, installDirectory, progress));
                }
            }

            return;
        }

        var localPackagePath = TryFindLocalLauncherPackage(bootstrapperDirectory);
        if (!string.IsNullOrWhiteSpace(localPackagePath) && File.Exists(localPackagePath))
        {
            ZipFile.ExtractToDirectory(localPackagePath, installDirectory, true);
            if (File.Exists(launcherPath))
            {
                return;
            }
        }

        var launcherUpdate = await TryGetLauncherUpdateAsync(httpClient, configuration)
                             ?? throw new InvalidOperationException("Не удалось получить пакет лаунчера из манифеста.");

        await RunWithProgressAsync(
            "Установка лаунчера",
            progress => DownloadAndExtractLauncherPackageAsync(httpClient, launcherUpdate, installDirectory, progress));
    }

    private static async Task TryDownloadLauncherUpdateForNextRunAsync(HttpClient httpClient, LauncherConfiguration configuration, string installDirectory)
    {
        try
        {
            var launcherUpdate = await TryGetLauncherUpdateAsync(httpClient, configuration);
            if (launcherUpdate is null)
            {
                return;
            }

            var launcherPath = Path.Combine(installDirectory, configuration.LauncherExecutable);
            var localVersion = ResolveInstalledLauncherVersion(launcherPath);
            if (!IsRemoteVersionNewer(localVersion, launcherUpdate.Version))
            {
                return;
            }

            var pendingRoot = Path.Combine(installDirectory, ".launcher-update");
            Directory.CreateDirectory(pendingRoot);
            var zipPath = Path.Combine(pendingRoot, "launcher-update.zip");
            var versionPath = Path.Combine(pendingRoot, "launcher-update.version");
            var hashPath = Path.Combine(pendingRoot, "launcher-update.sha256");

            if (File.Exists(versionPath) &&
                File.Exists(zipPath) &&
                string.Equals(File.ReadAllText(versionPath).Trim(), launcherUpdate.Version, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(launcherUpdate.Sha256) || HashesMatch(zipPath, launcherUpdate.Sha256))
                {
                    return;
                }
            }

            await DownloadLauncherPackageAsync(httpClient, launcherUpdate, zipPath);
            File.WriteAllText(versionPath, launcherUpdate.Version);
            File.WriteAllText(hashPath, launcherUpdate.Sha256 ?? string.Empty);
        }
        catch
        {
        }
    }

    private static void ApplyPendingLauncherUpdate(string installDirectory)
    {
        var pendingRoot = Path.Combine(installDirectory, ".launcher-update");
        var zipPath = Path.Combine(pendingRoot, "launcher-update.zip");
        var hashPath = Path.Combine(pendingRoot, "launcher-update.sha256");

        if (!File.Exists(zipPath))
        {
            return;
        }

        var expectedHash = File.Exists(hashPath) ? File.ReadAllText(hashPath).Trim() : string.Empty;
        if (!string.IsNullOrWhiteSpace(expectedHash) && !HashesMatch(zipPath, expectedHash))
        {
            SafeDeleteDirectory(pendingRoot);
            return;
        }

        ZipFile.ExtractToDirectory(zipPath, installDirectory, true);
        SafeDeleteDirectory(pendingRoot);
    }

    private static async Task DownloadAndExtractLauncherPackageAsync(HttpClient httpClient, ResolvedLauncherUpdate launcherUpdate, string installDirectory, IProgress<BootstrapProgress>? progress = null)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"launcher-bootstrap-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadLauncherPackageAsync(httpClient, launcherUpdate, tempZip, progress);
            progress?.Report(new BootstrapProgress(-1, "Распаковка файлов лаунчера..."));
            ZipFile.ExtractToDirectory(tempZip, installDirectory, true);
            progress?.Report(new BootstrapProgress(100, "Готово, запуск лаунчера..."));
        }
        finally
        {
            if (File.Exists(tempZip))
            {
                File.Delete(tempZip);
            }
        }
    }

    private static async Task DownloadLauncherPackageAsync(HttpClient httpClient, ResolvedLauncherUpdate launcherUpdate, string targetZipPath, IProgress<BootstrapProgress>? progress = null)
    {
        var packageUri = launcherUpdate.ResolvePackageUri();
        using var response = await httpClient.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using (var stream = await response.Content.ReadAsStreamAsync())
        await using (var file = File.Create(targetZipPath))
        {
            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            var lastReport = -1d;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                downloaded += read;

                if (progress is null)
                {
                    continue;
                }

                if (totalBytes is > 0)
                {
                    var percent = Math.Clamp(downloaded * 100d / totalBytes.Value, 0, 100);
                    if (percent - lastReport >= 1 || percent >= 100)
                    {
                        lastReport = percent;
                        progress.Report(new BootstrapProgress(
                            percent,
                            $"Загрузка лаунчера... {percent:0}%  ({FormatMb(downloaded)} / {FormatMb(totalBytes.Value)} МБ)"));
                    }
                }
                else
                {
                    progress.Report(new BootstrapProgress(-1, $"Загрузка лаунчера... {FormatMb(downloaded)} МБ"));
                }
            }

            await file.FlushAsync();
        }

        if (!string.IsNullOrWhiteSpace(launcherUpdate.Sha256) && !HashesMatch(targetZipPath, launcherUpdate.Sha256))
        {
            File.Delete(targetZipPath);
            throw new InvalidOperationException("Launcher update package hash mismatch.");
        }
    }

    private static string FormatMb(long bytes) => (bytes / (1024d * 1024d)).ToString("0.#");

    /// <summary>Проверяет наличие Visual C++ 2015–2022 (x64) и предлагает установку, если его нет.</summary>
    private static async Task EnsureVisualCppRuntimeAsync(HttpClient httpClient)
    {
        if (IsVisualCppRuntimePresent())
        {
            return;
        }

        var choice = WinForms.MessageBox.Show(
            "Для запуска лаунчера нужен системный компонент Microsoft Visual C++ 2015–2022 (x64).\n\n" +
            "Без него лаунчер не откроется. Установить его сейчас?\n(потребуется подтверждение прав администратора)",
            "Нужен компонент Visual C++",
            WinForms.MessageBoxButtons.YesNo,
            WinForms.MessageBoxIcon.Information);

        if (choice != WinForms.DialogResult.Yes)
        {
            return;
        }

        var installerPath = Path.Combine(Path.GetTempPath(), $"vc_redist.x64-{Guid.NewGuid():N}.exe");
        try
        {
            await RunWithProgressAsync("Установка Visual C++", async progress =>
            {
                progress.Report(new BootstrapProgress(-1, "Загрузка Microsoft Visual C++ Redistributable..."));
                using (var response = await httpClient.GetAsync(
                           "https://aka.ms/vs/17/release/vc_redist.x64.exe",
                           HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync();
                    await using var file = File.Create(installerPath);
                    await stream.CopyToAsync(file);
                    await file.FlushAsync();
                }

                progress.Report(new BootstrapProgress(-1, "Установка компонента (подтвердите запрос прав)..."));
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = "/install /passive /norestart",
                    UseShellExecute = true
                });

                if (process is not null)
                {
                    await process.WaitForExitAsync();
                }
            });

            if (!IsVisualCppRuntimePresent())
            {
                WinForms.MessageBox.Show(
                    "Компонент не установился. Установите его вручную и запустите лаунчер снова:\n" +
                    "https://aka.ms/vs/17/release/vc_redist.x64.exe",
                    "Visual C++ не установлен",
                    WinForms.MessageBoxButtons.OK,
                    WinForms.MessageBoxIcon.Warning);
            }
        }
        catch (Exception exception)
        {
            WinForms.MessageBox.Show(
                "Не удалось установить компонент автоматически.\n" +
                "Скачайте и установите вручную:\nhttps://aka.ms/vs/17/release/vc_redist.x64.exe\n\n" +
                exception.Message,
                "Ошибка установки Visual C++",
                WinForms.MessageBoxButtons.OK,
                WinForms.MessageBoxIcon.Warning);
        }
        finally
        {
            try
            {
                if (File.Exists(installerPath))
                {
                    File.Delete(installerPath);
                }
            }
            catch
            {
            }
        }
    }

    private static bool IsVisualCppRuntimePresent()
    {
        // Официальный признак установки: ключ установщика VC++ Runtimes x64 со значением Installed = 1.
        // Проверяем оба представления реестра (на x64 redist пишет в WOW6432Node).
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
                if (key?.GetValue("Installed") is int installed && installed == 1)
                {
                    return true;
                }
            }
            catch
            {
                // недоступно — пробуем другое представление / фолбэк
            }
        }

        // Фолбэк: наличие самой библиотеки в System32 (x64-процесс видит нативную папку).
        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return File.Exists(Path.Combine(system32, "vcruntime140.dll"))
                   && File.Exists(Path.Combine(system32, "msvcp140.dll"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Показывает окно прогресса на время загрузки/установки лаунчера.</summary>
    private static Task RunWithProgressAsync(string title, Func<IProgress<BootstrapProgress>, Task> work)
    {
        Exception? captured = null;
        using var form = new BootstrapperProgressForm(title);
        form.Shown += async (_, _) =>
        {
            var progress = new Progress<BootstrapProgress>(form.UpdateProgress);
            try
            {
                await work(progress);
            }
            catch (Exception exception)
            {
                captured = exception;
            }
            finally
            {
                form.Close();
            }
        };

        WinForms.Application.Run(form);
        return captured is null ? Task.CompletedTask : Task.FromException(captured);
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
        }
    }

    private static async Task<ResolvedLauncherUpdate?> TryGetLauncherUpdateAsync(HttpClient httpClient, LauncherConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.ModpackManifestUrl))
        {
            var modpackManifest = await DownloadModpackManifestAsync(httpClient, configuration.ModpackManifestUrl);
            if (!string.IsNullOrWhiteSpace(modpackManifest.Launcher.Version) &&
                !string.IsNullOrWhiteSpace(modpackManifest.Launcher.PackageUrl))
            {
                return new ResolvedLauncherUpdate(
                    modpackManifest.Launcher.Version,
                    modpackManifest.Launcher.PackageUrl,
                    modpackManifest.Launcher.Sha256,
                    modpackManifest.SourceUri);
            }
        }

        if (!string.IsNullOrWhiteSpace(configuration.ManifestUrl))
        {
            var manifest = await DownloadLegacyManifestAsync(httpClient, configuration.ManifestUrl);
            if (!string.IsNullOrWhiteSpace(manifest.Launcher.Version) &&
                !string.IsNullOrWhiteSpace(manifest.Launcher.PackageUrl))
            {
                return new ResolvedLauncherUpdate(
                    manifest.Launcher.Version,
                    manifest.Launcher.PackageUrl,
                    manifest.Launcher.Sha256,
                    manifest.SourceUri);
            }
        }

        return null;
    }

    private static string ResolveInstalledLauncherVersion(string launcherPath)
    {
        if (!File.Exists(launcherPath))
        {
            return "0.0.0";
        }

        var info = FileVersionInfo.GetVersionInfo(launcherPath);
        if (!string.IsNullOrWhiteSpace(info.ProductVersion))
        {
            return info.ProductVersion.Trim();
        }

        if (!string.IsNullOrWhiteSpace(info.FileVersion))
        {
            return info.FileVersion.Trim();
        }

        return "0.0.0";
    }

    private static bool IsRemoteVersionNewer(string local, string remote)
    {
        if (Version.TryParse(local, out var localVersionParsed) && Version.TryParse(remote, out var remoteVersionParsed))
        {
            return remoteVersionParsed > localVersionParsed;
        }

        return !string.Equals(local, remote, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HashesMatch(string path, string expectedHash)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var actual = Convert.ToHexString(sha256.ComputeHash(stream));
        return actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteIfNeeded(string value)
    {
        return value.Contains(' ') ? $"\"{value}\"" : value;
    }

    private static string SanitizeFolderName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }

    private static string? TryFindLocalLauncherPackage(string bootstrapperDirectory)
    {
        var directCandidate = Path.Combine(bootstrapperDirectory, "TerraFirmaGregModern-launcher-release.zip");
        if (File.Exists(directCandidate))
        {
            return directCandidate;
        }

        return Directory.EnumerateFiles(bootstrapperDirectory, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static async Task<LegacyLauncherManifest> DownloadLegacyManifestAsync(HttpClient httpClient, string manifestUrl)
    {
        var uri = new Uri(manifestUrl, UriKind.Absolute);
        using var response = await httpClient.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var manifest = await JsonSerializer.DeserializeAsync<LegacyLauncherManifest>(stream, JsonOptions()) ??
                       throw new InvalidOperationException("Server returned an empty launcher manifest.");
        manifest.SourceUri = uri;
        return manifest;
    }

    private static async Task<BootstrapperModpackManifest> DownloadModpackManifestAsync(HttpClient httpClient, string manifestUrl)
    {
        var uri = new Uri(manifestUrl, UriKind.Absolute);
        using var response = await httpClient.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        var manifest = await JsonSerializer.DeserializeAsync<BootstrapperModpackManifest>(stream, JsonOptions()) ??
                       throw new InvalidOperationException("Server returned an empty modpack manifest.");
        manifest.SourceUri = uri;
        return manifest;
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
    }
}

sealed class LauncherConfiguration
{
    private const string EmbeddedConfigResourceName = "Launcher.Bootstrapper.launcher.config.json";

    public string LauncherName { get; set; } = "Forge Launcher";
    public string ManifestUrl { get; set; } = string.Empty;
    public string ModpackManifestUrl { get; set; } = "https://bl-modern.ru/download/modpack-manifest.json";
    public string ModpackArchiveUrl { get; set; } = string.Empty;
    public string ModpackVersion { get; set; } = string.Empty;
    public string ModpackArchiveSha256 { get; set; } = string.Empty;
    public string DistributionRoot { get; set; } = "%AppData%\\ForgeLauncher";
    public string LauncherExecutable { get; set; } = "Launcher.App.exe";

    public static LauncherConfiguration Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "launcher.config.json");
        if (File.Exists(path))
        {
            return Deserialize(File.ReadAllText(path)) ?? CreateEmbeddedDefault();
        }

        return CreateEmbeddedDefault();
    }

    private static LauncherConfiguration CreateEmbeddedDefault()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedConfigResourceName);
        if (stream is null)
        {
            return new LauncherConfiguration();
        }

        using var reader = new StreamReader(stream);
        return Deserialize(reader.ReadToEnd()) ?? new LauncherConfiguration();
    }

    private static LauncherConfiguration? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<LauncherConfiguration>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        });
    }
}

sealed class BootstrapperStateStore(string launcherName)
{
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        SanitizeFolderNameLocal(launcherName),
        "bootstrapper-state.json");

    public string LoadInstallDirectory()
    {
        if (!File.Exists(_statePath))
        {
            return string.Empty;
        }

        try
        {
            var state = JsonSerializer.Deserialize<BootstrapperState>(File.ReadAllText(_statePath));
            return state?.InstallDirectory ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void SaveInstallDirectory(string installDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var state = new BootstrapperState { InstallDirectory = installDirectory };
        File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
    }

    private static string SanitizeFolderNameLocal(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '_' : character));
    }
}

sealed class BootstrapperState
{
    public string InstallDirectory { get; set; } = string.Empty;
}

/// <summary>Прогресс загрузки/установки. Percent &lt; 0 означает неопределённый этап (marquee).</summary>
readonly record struct BootstrapProgress(double Percent, string Text);

sealed class BootstrapperProgressForm : WinForms.Form
{
    private readonly WinForms.Label _statusLabel;
    private readonly WinForms.ProgressBar _progressBar;

    public BootstrapperProgressForm(string title)
    {
        Text = title;
        StartPosition = WinForms.FormStartPosition.CenterScreen;
        FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = true;
        BackColor = System.Drawing.Color.FromArgb(30, 23, 18);
        ForeColor = System.Drawing.Color.FromArgb(255, 247, 234);
        ClientSize = new System.Drawing.Size(560, 170);

        var titleLabel = new WinForms.Label
        {
            AutoSize = false,
            Left = 24,
            Top = 22,
            Width = 512,
            Height = 30,
            Font = new System.Drawing.Font("Segoe UI Semibold", 14f, System.Drawing.FontStyle.Bold),
            ForeColor = ForeColor,
            Text = title
        };

        _statusLabel = new WinForms.Label
        {
            AutoSize = false,
            Left = 24,
            Top = 64,
            Width = 512,
            Height = 24,
            Font = new System.Drawing.Font("Segoe UI", 10f),
            ForeColor = System.Drawing.Color.FromArgb(210, 214, 203),
            Text = "Подготовка..."
        };

        _progressBar = new WinForms.ProgressBar
        {
            Left = 24,
            Top = 104,
            Width = 512,
            Height = 24,
            Minimum = 0,
            Maximum = 100,
            Style = WinForms.ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30
        };

        Controls.Add(titleLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_progressBar);
    }

    public void UpdateProgress(BootstrapProgress progress)
    {
        if (progress.Percent < 0)
        {
            if (_progressBar.Style != WinForms.ProgressBarStyle.Marquee)
            {
                _progressBar.Style = WinForms.ProgressBarStyle.Marquee;
            }
        }
        else
        {
            if (_progressBar.Style != WinForms.ProgressBarStyle.Continuous)
            {
                _progressBar.Style = WinForms.ProgressBarStyle.Continuous;
            }

            _progressBar.Value = (int)Math.Clamp(progress.Percent, 0, 100);
        }

        if (!string.IsNullOrWhiteSpace(progress.Text))
        {
            _statusLabel.Text = progress.Text;
        }
    }
}

sealed class InstallDirectoryForm : WinForms.Form
{
    private readonly WinForms.TextBox _pathTextBox;
    private readonly WinForms.Button _continueButton;

    public InstallDirectoryForm(string launcherName, string defaultDirectory)
    {
        Text = "Папка установки";
        StartPosition = WinForms.FormStartPosition.CenterScreen;
        FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        BackColor = System.Drawing.Color.FromArgb(30, 23, 18);
        ForeColor = System.Drawing.Color.FromArgb(255, 247, 234);
        ClientSize = new System.Drawing.Size(620, 220);

        var titleLabel = new WinForms.Label
        {
            AutoSize = false,
            Left = 20,
            Top = 18,
            Width = 580,
            Height = 28,
            Font = new System.Drawing.Font("Segoe UI Semibold", 13f, System.Drawing.FontStyle.Bold),
            ForeColor = ForeColor,
            Text = $"Установка {launcherName}"
        };

        var hintLabel = new WinForms.Label
        {
            AutoSize = false,
            Left = 20,
            Top = 52,
            Width = 580,
            Height = 20,
            Font = new System.Drawing.Font("Segoe UI", 9.5f),
            ForeColor = System.Drawing.Color.FromArgb(210, 214, 203),
            Text = "Выберите папку, в которую будет установлен лаунчер."
        };

        _pathTextBox = new WinForms.TextBox
        {
            Left = 20,
            Top = 95,
            Width = 430,
            Height = 34,
            BorderStyle = WinForms.BorderStyle.FixedSingle,
            BackColor = System.Drawing.Color.FromArgb(20, 15, 12),
            ForeColor = ForeColor,
            Font = new System.Drawing.Font("Segoe UI", 10.5f),
            Text = defaultDirectory
        };

        var browseButton = new WinForms.Button
        {
            Left = 462,
            Top = 92,
            Width = 138,
            Height = 38,
            Text = "Обзор...",
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = System.Drawing.Color.FromArgb(116, 75, 48),
            ForeColor = ForeColor
        };
        browseButton.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(175, 113, 73);
        browseButton.FlatAppearance.BorderSize = 1;
        browseButton.Click += (_, _) => BrowseForFolder();

        _continueButton = new WinForms.Button
        {
            Left = 360,
            Top = 162,
            Width = 115,
            Height = 38,
            Text = "Продолжить",
            DialogResult = WinForms.DialogResult.OK,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = System.Drawing.Color.FromArgb(224, 178, 79),
            ForeColor = System.Drawing.Color.FromArgb(37, 25, 7)
        };
        _continueButton.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(245, 208, 122);
        _continueButton.FlatAppearance.BorderSize = 1;

        var cancelButton = new WinForms.Button
        {
            Left = 485,
            Top = 162,
            Width = 115,
            Height = 38,
            Text = "Отмена",
            DialogResult = WinForms.DialogResult.Cancel,
            FlatStyle = WinForms.FlatStyle.Flat,
            BackColor = System.Drawing.Color.FromArgb(53, 38, 29),
            ForeColor = ForeColor
        };
        cancelButton.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(120, 82, 53);
        cancelButton.FlatAppearance.BorderSize = 1;

        AcceptButton = _continueButton;
        CancelButton = cancelButton;

        Controls.Add(titleLabel);
        Controls.Add(hintLabel);
        Controls.Add(_pathTextBox);
        Controls.Add(browseButton);
        Controls.Add(_continueButton);
        Controls.Add(cancelButton);
    }

    public string SelectedDirectory => _pathTextBox.Text.Trim();

    private void BrowseForFolder()
    {
        var start = Directory.Exists(SelectedDirectory)
            ? SelectedDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Выберите папку установки",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = start
        };

        if (dialog.ShowDialog(this) == WinForms.DialogResult.OK)
        {
            _pathTextBox.Text = dialog.SelectedPath;
        }
    }

    protected override void OnFormClosing(WinForms.FormClosingEventArgs e)
    {
        if (DialogResult == WinForms.DialogResult.OK && string.IsNullOrWhiteSpace(SelectedDirectory))
        {
            WinForms.MessageBox.Show(
                this,
                "Укажите папку установки.",
                "BL-modern TFGM",
                WinForms.MessageBoxButtons.OK,
                WinForms.MessageBoxIcon.Information);
            e.Cancel = true;
            return;
        }

        base.OnFormClosing(e);
    }
}

sealed class BootstrapperModpackManifest
{
    public BootstrapperLauncherInfo Launcher { get; set; } = new();

    [JsonIgnore]
    public Uri? SourceUri { get; set; }
}

sealed class BootstrapperLauncherInfo
{
    public string Title { get; set; } = "Forge Launcher";
    public string NewsUrl { get; set; } = string.Empty;
    public string SupportUrl { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

sealed class LegacyLauncherManifest
{
    public LegacyLauncherUpdateInfo Launcher { get; set; } = new();

    [JsonIgnore]
    public Uri? SourceUri { get; set; }
}

sealed class LegacyLauncherUpdateInfo
{
    public string Version { get; set; } = "1.0.0";
    public string PackageUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

sealed record ResolvedLauncherUpdate(string Version, string PackageUrl, string Sha256, Uri? SourceUri)
{
    public Uri ResolvePackageUri()
    {
        if (Uri.TryCreate(PackageUrl, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }

        if (SourceUri is null)
        {
            throw new InvalidOperationException("Cannot build relative launcher update URL without manifest URL.");
        }

        return new Uri(SourceUri, PackageUrl);
    }
}
