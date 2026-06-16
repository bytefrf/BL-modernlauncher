using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Launcher.App.Configuration;
using Launcher.App.Models;
using Launcher.App.Services;
using Launcher.App.Theming;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using Media = System.Windows.Media;

namespace Launcher.App;

public partial class MainWindow : Window, IDisposable
{
    private const string ServerStatsUrl = "https://bl-modern.ru/api/stats.php";
    private const string TelemetryUrl = "https://bl-modern.ru/api/telemetry.php";
    private const string SupportLogsUrl = "https://bl-modern.ru/api/support_logs.php";
    private const string SupportTicketsUrl = "https://bl-modern.ru/api/support_tickets.php";
    private const int LaunchSuccessThresholdSeconds = 45;

    // Discord Application ID (Developer Portal → New Application → General → Application ID).
    // Пока не задан — Rich Presence просто отключён и ни на что не влияет.
    private const string DiscordAppId = "1511335634533613598";
    private const string DiscordIdleDetails = "TerraFirmaGreg-Modern";
    private const string DiscordIdleState = "В лаунчере";
    private const string DiscordPlayingDetails = "TerraFirmaGreg-Modern";
    private const string DiscordPlayingState = "В игре";
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(20),
        DefaultRequestVersion = HttpVersion.Version11,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
    };
    private readonly ManifestClient _manifestClient;
    private readonly ModpackManifestClient _modpackManifestClient;
    private readonly NewsClient _newsClient;
    private readonly ServerStatsClient _serverStatsClient;
    private readonly TelemetryClient _telemetryClient;
    private readonly SupportLogService _supportLogService;
    private readonly SupportChatClient _supportChatClient;
    private readonly LauncherSelfUpdateService _launcherSelfUpdateService;
    private readonly DiscordPresenceService _discordPresence;
    private LauncherConfiguration? _configuration;
    private LauncherManifest? _manifest;
    private ModpackManifest? _modpackManifest;
    private UserSettings _userSettings = new();
    private readonly DispatcherTimer _backgroundRotationTimer = new();
    private readonly DispatcherTimer _serverStatsTimer = new();
    private readonly Random _backgroundRandom = new();
    private List<string> _backgroundImagePaths = [];
    private int _currentBackgroundIndex = -1;
    private bool _isBackgroundLayerAActive = true;
    private string _currentNewsUrl = string.Empty;
    private PrimaryActionState _primaryActionState = PrimaryActionState.Play;
    private WinForms.NotifyIcon? _trayIcon;
    private bool _allowClose;
    private bool _isBusy;
    private bool _launcherUpdateAvailable;
    private string _launcherUpdateVersion = string.Empty;
    public MainWindow()
    {
        InitializeComponent();
        LauncherThemeCatalog.ApplyTheme(Resources, LauncherThemeCatalog.DefaultThemeId);
        ApplyResponsiveFixedWindowSize();
        _manifestClient = new ManifestClient(_httpClient);
        _modpackManifestClient = new ModpackManifestClient(_httpClient);
        _newsClient = new NewsClient(_httpClient);
        _serverStatsClient = new ServerStatsClient(_httpClient);
        _telemetryClient = new TelemetryClient(_httpClient);
        _supportLogService = new SupportLogService(_httpClient);
        _supportChatClient = new SupportChatClient(_httpClient);
        _launcherSelfUpdateService = new LauncherSelfUpdateService(_httpClient);
        _discordPresence = new DiscordPresenceService(DiscordAppId, largeImageKey: "logo");
        _backgroundRotationTimer.Interval = TimeSpan.FromSeconds(30);
        _backgroundRotationTimer.Tick += BackgroundRotationTimer_Tick;
        _serverStatsTimer.Interval = TimeSpan.FromMinutes(5);
        _serverStatsTimer.Tick += ServerStatsTimer_Tick;
        InitializeTrayIcon();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RunSafeAsync(InitializeAsync);
    private async void PlayButton_Click(object sender, RoutedEventArgs e) => await RunSafeAsync(PlayAsync);
    private async void SettingsButton_Click(object sender, RoutedEventArgs e) => await RunSafeAsync(OpenSettingsAsync);
    private void OpenNewsButton_Click(object sender, RoutedEventArgs e) => OpenCurrentNews();
    private void SupportButton_Click(object sender, RoutedEventArgs e) => OpenSupportWindow();
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => MinimizeToTray("Лаунчер свернут в трей. Minecraft продолжит работать.");
    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1)
        {
            return;
        }

        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
    private void WebsiteButton_Click(object sender, RoutedEventArgs e) => OpenExternalUrl("https://bl-modern.ru/");
    private void BoostyButton_Click(object sender, RoutedEventArgs e) => OpenExternalUrl("https://boosty.to/bytef");
    private void DiscordButton_Click(object sender, RoutedEventArgs e) => OpenExternalUrl("https://discord.gg/FpV9bRggvt");

    private async Task InitializeAsync()
    {
        _configuration = LauncherConfiguration.Load(AppContext.BaseDirectory);
        Title = _configuration.LauncherName;
        WindowTitleTextBlock.Text = _configuration.LauncherName;
        ServerTitleTextBlock.Text = _configuration.LauncherName;

        if (Environment.GetCommandLineArgs().Any(argument => argument.Equals("--test-crash", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Тестовая ошибка лаунчера: проверка окна ошибки, записи лога и кнопки отправки.");
        }

        InitializeBackgroundSlideshow();
        _userSettings = UserSettings.Load(_configuration.GetUserSettingsPath());
        LauncherThemeCatalog.ApplyTheme(Resources, _userSettings.ThemeId);
        EnsureTelemetryIdentity();
        UsernameTextBox.Text = _userSettings.Username;
        UpdateDisplayedInstallPath();
        AppendLog($"Config: {_configuration.ConfigPath}");
        AppendLog($"Settings: {_configuration.GetUserSettingsPath()}");

        if (_configuration.UsesDirectModpackArchive())
        {
            await RefreshModpackManifestAsync();
        }
        else
        {
            await RefreshManifestAsync();
        }

        _ = TrackTelemetryAsync("launcher_started");
        _ = TrackTelemetryAsync("system_info", BuildSystemInfoProperties());
        _ = InitializeDiscordPresenceAsync();
    }

    private static Dictionary<string, object?> BuildSystemInfoProperties()
    {
        var properties = SystemInfoCollector.Collect();
        try
        {
            properties["screenWidth"] = (int)SystemParameters.PrimaryScreenWidth;
            properties["screenHeight"] = (int)SystemParameters.PrimaryScreenHeight;
        }
        catch
        {
            // SystemParameters недоступны в headless-среде — пропускаем.
        }

        return properties;
    }

    private async Task InitializeDiscordPresenceAsync()
    {
        try
        {
            if (await _discordPresence.TryConnectAsync())
            {
                await _discordPresence.SetPresenceAsync(DiscordIdleDetails, DiscordIdleState);
            }
        }
        catch (Exception exception)
        {
            AppendLog($"Discord presence init failed: {exception.Message}");
        }
    }

    private void UpdateDiscordPresence(string details, string state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _discordPresence.SetPresenceAsync(details, state);
            }
            catch
            {
                // Discord может быть закрыт — Rich Presence не критичен.
            }
        });
    }

    private void BackgroundRotationTimer_Tick(object? sender, EventArgs e)
    {
        ShowNextBackgroundImage();
    }

    private void InitializeBackgroundSlideshow()
    {
        var screenshotsDirectory = ResolveScreenshotsDirectory();
        if (string.IsNullOrWhiteSpace(screenshotsDirectory) || !Directory.Exists(screenshotsDirectory))
        {
            BackgroundImageBrushA.ImageSource = null;
            BackgroundImageBrushB.ImageSource = null;
            BackgroundLayerA.Opacity = 1;
            BackgroundLayerB.Opacity = 0;
            _backgroundRotationTimer.Stop();
            return;
        }

        _backgroundImagePaths = Directory
            .EnumerateFiles(screenshotsDirectory)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _currentBackgroundIndex = -1;

        if (_backgroundImagePaths.Count == 0)
        {
            BackgroundImageBrushA.ImageSource = null;
            BackgroundImageBrushB.ImageSource = null;
            BackgroundLayerA.Opacity = 1;
            BackgroundLayerB.Opacity = 0;
            _backgroundRotationTimer.Stop();
            return;
        }

        ShowNextBackgroundImage();

        if (_backgroundImagePaths.Count > 1)
        {
            _backgroundRotationTimer.Start();
        }
        else
        {
            _backgroundRotationTimer.Stop();
        }
    }

    private string? ResolveScreenshotsDirectory()
    {
        var externalDirectory = Path.Combine(AppContext.BaseDirectory, "скриншоты");
        if (Directory.Exists(externalDirectory) && Directory.EnumerateFiles(externalDirectory).Any())
        {
            return externalDirectory;
        }

        return ExtractEmbeddedScreenshots();
    }

    private string? ExtractEmbeddedScreenshots()
    {
        const string resourcePrefix = "Launcher.App.Screenshots.";
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly
            .GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (resourceNames.Count == 0)
        {
            return null;
        }

        var targetDirectory = Path.Combine(
            Path.GetTempPath(),
            "TerraFirmaGregModernLauncher",
            "embedded-screenshots");
        Directory.CreateDirectory(targetDirectory);

        foreach (var resourceName in resourceNames)
        {
            var fileName = resourceName[resourcePrefix.Length..];
            var targetPath = Path.Combine(targetDirectory, fileName);
            if (File.Exists(targetPath))
            {
                continue;
            }

            using var resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream is null)
            {
                continue;
            }

            using var fileStream = File.Create(targetPath);
            resourceStream.CopyTo(fileStream);
        }

        return targetDirectory;
    }

    private void ShowNextBackgroundImage()
    {
        if (_backgroundImagePaths.Count == 0)
        {
            BackgroundImageBrushA.ImageSource = null;
            BackgroundImageBrushB.ImageSource = null;
            BackgroundLayerA.Opacity = 1;
            BackgroundLayerB.Opacity = 0;
            return;
        }

        if (_backgroundImagePaths.Count == 1)
        {
            _currentBackgroundIndex = 0;
        }
        else
        {
            var nextIndex = _backgroundRandom.Next(_backgroundImagePaths.Count - 1);
            if (nextIndex >= _currentBackgroundIndex && _currentBackgroundIndex >= 0)
            {
                nextIndex++;
            }

            _currentBackgroundIndex = nextIndex;
        }

        SetBackgroundImage(_backgroundImagePaths[_currentBackgroundIndex]);
    }

    private void SetBackgroundImage(string imagePath)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();

            if (_isBackgroundLayerAActive && BackgroundImageBrushA.ImageSource is null)
            {
                BackgroundImageBrushA.ImageSource = bitmap;
                BackgroundLayerA.Opacity = 1;
                BackgroundLayerB.Opacity = 0;
                return;
            }

            if (!_isBackgroundLayerAActive && BackgroundImageBrushB.ImageSource is null)
            {
                BackgroundImageBrushB.ImageSource = bitmap;
                BackgroundLayerA.Opacity = 0;
                BackgroundLayerB.Opacity = 1;
                return;
            }

            var fadeInLayer = _isBackgroundLayerAActive ? BackgroundLayerB : BackgroundLayerA;
            var fadeOutLayer = _isBackgroundLayerAActive ? BackgroundLayerA : BackgroundLayerB;
            var fadeInBrush = _isBackgroundLayerAActive ? BackgroundImageBrushB : BackgroundImageBrushA;

            fadeInBrush.ImageSource = bitmap;
            fadeInLayer.BeginAnimation(UIElement.OpacityProperty, null);
            fadeOutLayer.BeginAnimation(UIElement.OpacityProperty, null);

            var duration = TimeSpan.FromMilliseconds(900);
            var fadeInAnimation = new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeOutAnimation = new DoubleAnimation(1, 0, duration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            fadeInLayer.BeginAnimation(UIElement.OpacityProperty, fadeInAnimation);
            fadeOutLayer.BeginAnimation(UIElement.OpacityProperty, fadeOutAnimation);
            _isBackgroundLayerAActive = !_isBackgroundLayerAActive;
        }
        catch (Exception exception)
        {
            AppendLog($"Background image load failed: {exception.Message}");
        }
    }

    private void ApplyResponsiveFixedWindowSize()
    {
        var workArea = SystemParameters.WorkArea;
        var targetSize = SelectWindowSize(workArea.Width, workArea.Height);
        Width = targetSize.Width;
        Height = targetSize.Height;
        MinWidth = targetSize.Width;
        MaxWidth = targetSize.Width;
        MinHeight = targetSize.Height;
        MaxHeight = targetSize.Height;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    private static System.Windows.Size SelectWindowSize(double workWidth, double workHeight)
    {
        if (workWidth >= 1400 && workHeight >= 860)
        {
            return new System.Windows.Size(1360, 720);
        }

        if (workWidth >= 1240 && workHeight >= 760)
        {
            return new System.Windows.Size(1200, 640);
        }

        if (workWidth >= 1080 && workHeight >= 700)
        {
            return new System.Windows.Size(1040, 600);
        }

        return new System.Windows.Size(
            Math.Max(900, Math.Min(980, workWidth - 40)),
            Math.Max(540, Math.Min(580, workHeight - 40)));
    }

    private async Task VerifyFilesAsync()
    {
        await RefreshMetadataAsync();
        await EnsureGameFilesAsync(forceArchiveInstall: true, operationId: Guid.NewGuid().ToString("N"), trigger: "verify");
        UpdatePrimaryActionButton();
    }

    private async Task PlayAsync()
    {
        var launchAttemptId = Guid.NewGuid().ToString("N");
        var launchStage = "metadata_refresh";
        InstallOperationTelemetry? installTelemetry = null;

        await RefreshMetadataAsync();

        if (_configuration is null)
        {
            throw new InvalidOperationException("Launcher configuration is not loaded.");
        }

        SaveUserSettings();
        _ = TrackTelemetryAsync("play_clicked", new Dictionary<string, object?>
        {
            ["action"] = _primaryActionState.ToString().ToLowerInvariant(),
            ["launchAttemptId"] = launchAttemptId,
            ["memoryMb"] = _userSettings.MemoryMb,
            ["customResolution"] = _userSettings.UseCustomResolution
        });
        try
        {
            var launchManifest = _configuration.UsesDirectModpackArchive()
                ? CreateArchiveModeLaunchManifest()
                : _manifest ?? throw new InvalidOperationException("Launcher manifest is not loaded.");

            var effectiveConfiguration = CreateEffectiveConfiguration();
            if (_primaryActionState == PrimaryActionState.LauncherUpdate)
            {
                await UpdateLauncherAsync();
                return;
            }

            launchStage = "install";
            if (_primaryActionState is PrimaryActionState.Install or PrimaryActionState.Update)
            {
                installTelemetry = await EnsureGameFilesAsync(operationId: launchAttemptId, trigger: "play_install_only");
                UpdatePrimaryActionButton();
                return;
            }

            installTelemetry = await EnsureGameFilesAsync(operationId: launchAttemptId, trigger: "launch");

            launchStage = "java_validation";
            var javaCheck = await JavaValidationService.ValidateJavaAsync(GetEffectiveInstallRoot(), launchManifest, _userSettings, CancellationToken.None);
            AppendLog(javaCheck.Message);
            if (!javaCheck.IsOk)
            {
                throw new InvalidOperationException(javaCheck.Message);
            }

            launchStage = "process_start";
            _ = TrackTelemetryAsync("launch_started", new Dictionary<string, object?>
            {
                ["launchAttemptId"] = launchAttemptId,
                ["installDurationMs"] = installTelemetry?.DurationMs,
                ["installMode"] = installTelemetry?.Mode ?? "unknown",
                ["installChangedFiles"] = installTelemetry?.ChangedFiles ?? 0,
                ["memoryMb"] = _userSettings.MemoryMb,
                ["javaSource"] = DetectJavaSource(launchManifest.Game.JavaExecutable)
            });

            var launcher = new MinecraftLaunchService();
            var result = await launcher.LaunchAsync(effectiveConfiguration, launchManifest, _userSettings);
            AppendLog($"Launch: {result.FileName}");
            AppendLog(result.ArgumentsPreview);

            _ = TrackTelemetryAsync("launch_process_started", new Dictionary<string, object?>
            {
                ["launchAttemptId"] = launchAttemptId,
                ["processId"] = result.Process.Id,
                ["installDurationMs"] = installTelemetry?.DurationMs,
                ["javaSource"] = DetectJavaSource(result.FileName)
            });

            FooterTextBlock.Text = "Minecraft launched";
            UpdateDiscordPresence(DiscordPlayingDetails, DiscordPlayingState);
            MinimizeToTray("Minecraft запущен. Лаунчер свернут в трей.");
            _ = MonitorMinecraftProcessAsync(
                result.Process,
                GetEffectiveInstallRoot(),
                launchAttemptId,
                installTelemetry?.DurationMs ?? 0,
                DetectJavaSource(result.FileName));
        }
        catch (Exception exception)
        {
            await TrackLaunchFailureBeforeProcessAsync(exception, launchAttemptId, launchStage, installTelemetry);
            throw;
        }
    }

    private async Task RefreshMetadataAsync()
    {
        if (_configuration is null)
        {
            throw new InvalidOperationException("Launcher configuration is not loaded.");
        }

        if (_configuration.UsesDirectModpackArchive())
        {
            await RefreshModpackManifestAsync();
        }
        else
        {
            await RefreshManifestAsync();
        }
    }

    private async Task RefreshManifestAsync()
    {
        if (_configuration is null)
        {
            throw new InvalidOperationException("Launcher configuration is not loaded.");
        }

        SetStatus("Loading launcher manifest...");
        _manifest = await _manifestClient.GetManifestAsync(_configuration.ManifestUrl);
        FooterTextBlock.Text = $"Pack {_manifest.Game.Version}";
        AppendLog($"Launcher manifest loaded: {_manifest.Game.Version}");
        await RefreshServerStatsAsync();
        UpdateDisplayedInstallPath();
        ShowNewsFallback("Новости появятся после подключения newsUrl в манифесте сборки.");
        SetStatus("Manifest loaded");
        UpdatePrimaryActionButton();
    }

    private async Task RefreshModpackManifestAsync()
    {
        if (_configuration is null)
        {
            throw new InvalidOperationException("Launcher configuration is not loaded.");
        }

        if (_configuration.UsesModpackManifest())
        {
            var cachedManifestPath = _configuration.GetCachedModpackManifestPath();
            SetStatus("Loading modpack manifest...");
            try
            {
                _modpackManifest = await _modpackManifestClient.GetManifestAsync(_configuration.ModpackManifestUrl);
                await _modpackManifestClient.SaveManifestCacheAsync(_modpackManifest, cachedManifestPath);
                AppendLog($"Modpack manifest cache updated: {cachedManifestPath}");
            }
            catch (HttpRequestException exception) when (IsNetworkNameResolutionError(exception))
            {
                AppendLog($"Modpack manifest network error: {exception.Message}");
                _modpackManifest = await LoadFallbackModpackManifestAsync(cachedManifestPath, "Server is unavailable");
                ShowNewsFallback("Не удалось подключиться к сайту. Лаунчер использует последний сохраненный манифест или встроенный резерв.");
            }
            catch (TaskCanceledException exception)
            {
                AppendLog($"Modpack manifest timeout: {exception.Message}");
                _modpackManifest = await LoadFallbackModpackManifestAsync(cachedManifestPath, "Server timeout");
                ShowNewsFallback("Сайт долго не отвечает. Лаунчер использует последний сохраненный манифест или встроенный резерв.");
            }

            Title = _modpackManifest.Launcher.Title;
            ServerTitleTextBlock.Text = _modpackManifest.Launcher.Title;
            await RefreshServerStatsAsync();
            UpdateDisplayedInstallPath();
            FooterTextBlock.Text = $"{_modpackManifest.Modpack.Name} {_modpackManifest.Modpack.Version}";
            await RefreshNewsAsync();
            SetStatus("Modpack manifest loaded");
            AppendLog($"Modpack manifest loaded: {_modpackManifest.Modpack.Name} {_modpackManifest.Modpack.Version}");
            UpdatePrimaryActionButton();
            return;
        }

        await RefreshServerStatsAsync();
        UpdateDisplayedInstallPath();
        ShowNewsFallback("Новости доступны при запуске через modpack-manifest.json.");
        SetStatus("Archive mode is ready");
        AppendLog($"Direct archive mode: {_configuration.ModpackArchiveUrl}");
        UpdatePrimaryActionButton();
    }

    private async Task<ModpackManifest> LoadFallbackModpackManifestAsync(string cachedManifestPath, string reason)
    {
        try
        {
            var cached = await _modpackManifestClient.GetCachedManifestAsync(cachedManifestPath);
            SetStatus($"{reason}, using cached manifest");
            AppendLog($"Using cached modpack manifest: {cachedManifestPath}");
            return cached;
        }
        catch (Exception cacheException)
        {
            AppendLog($"Cached modpack manifest unavailable: {cacheException.Message}");
            var embedded = await _modpackManifestClient.GetEmbeddedDefaultManifestAsync();
            SetStatus($"{reason}, using embedded manifest");
            AppendLog("Using embedded modpack manifest.");
            return embedded;
        }
    }

    private LauncherManifest CreateArchiveModeLaunchManifest()
    {
        var runtime = _modpackManifest?.Runtime;
        return new LauncherManifest
        {
            Launcher = new LauncherUpdateInfo
            {
                Version = "1.0.52"
            },
            Game = new GameDistributionInfo
            {
                Version = _modpackManifest?.Modpack.Version ?? _configuration?.ModpackVersion ?? "Modpack",
                Description = _modpackManifest?.Modpack.Description ?? "Direct archive modpack",
                MainVersionId = runtime?.MainVersionId ?? "1.20.1-forge-47.3.29",
                JavaExecutable = string.IsNullOrWhiteSpace(runtime?.JavaExecutable) ? "javaw.exe" : runtime!.JavaExecutable,
                JavaArguments = runtime?.JvmArgs ?? ["-XX:+UseG1GC"],
                GameArguments = runtime?.GameArgs ?? []
            }
        };
    }

    private async Task<InstallOperationTelemetry> EnsureGameFilesAsync(bool forceArchiveInstall = false, string? operationId = null, string trigger = "manual")
    {
        if (_configuration is null)
        {
            throw new InvalidOperationException("Launcher configuration is not loaded.");
        }

        SaveUserSettings();
        var overallStopwatch = Stopwatch.StartNew();
        var mode = _configuration.UsesDirectModpackArchive() ? "archive" : "sync";
        _ = TrackTelemetryAsync("install_started", new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["force"] = forceArchiveInstall,
            ["trigger"] = trigger,
            ["operationId"] = operationId
        });

        if (_configuration.UsesDirectModpackArchive())
        {
            EnsureEnoughDiskSpace();
            var runtimeInstaller = new RuntimeInstallService(_httpClient);
            var runtimeProgress = ScaleProgress(0, 45);
            await runtimeInstaller.EnsureRuntimeAsync(_configuration, _modpackManifest, _userSettings, runtimeProgress, CancellationToken.None);

            var installer = new ArchiveInstallService(_httpClient);
            var archiveProgress = ScaleProgress(45, 100);
            var archiveSummary = await installer.InstallAsync(_configuration, _modpackManifest, _userSettings, archiveProgress, CancellationToken.None, forceArchiveInstall);
            AppendLog(archiveSummary.Installed ? $"Archive installed to {archiveSummary.InstallPath}" : $"Archive already installed at {archiveSummary.InstallPath}");
            SetStatus(forceArchiveInstall ? "Проверка модпака завершена" : "Модпак установлен");
            LauncherProgressBar.Value = 100;
            FooterTextBlock.Text = forceArchiveInstall ? "Проверка модпака завершена" : "Модпак установлен";
            var durationMs = overallStopwatch.ElapsedMilliseconds;
            _ = TrackTelemetryAsync("install_completed", new Dictionary<string, object?>
            {
                ["mode"] = "archive",
                ["installed"] = archiveSummary.Installed,
                ["durationMs"] = durationMs,
                ["force"] = forceArchiveInstall,
                ["trigger"] = trigger,
                ["operationId"] = operationId,
                ["changedFiles"] = archiveSummary.Installed ? 1 : 0
            });
            UpdatePrimaryActionButton();
            return new InstallOperationTelemetry("archive", archiveSummary.Installed, durationMs, archiveSummary.Installed ? 1 : 0);
        }

        if (_manifest is null)
        {
            throw new InvalidOperationException("Manifest is not loaded.");
        }

        var sync = new FileSyncService(_httpClient);
        var progress = new Progress<FileSyncProgress>(UpdateProgress);
        var summary = await sync.SyncAsync(CreateEffectiveConfiguration(), _manifest, progress, CancellationToken.None);
        AppendLog($"Sync finished. Downloaded: {summary.DownloadedFiles}, skipped: {summary.SkippedFiles}.");
        SetStatus("Сборка обновлена");
        LauncherProgressBar.Value = 100;
        FooterTextBlock.Text = $"Сборка обновлена, загружено файлов: {summary.DownloadedFiles}";
        var syncDurationMs = overallStopwatch.ElapsedMilliseconds;
        _ = TrackTelemetryAsync("install_completed", new Dictionary<string, object?>
        {
            ["mode"] = "sync",
            ["downloadedFiles"] = summary.DownloadedFiles,
            ["skippedFiles"] = summary.SkippedFiles,
            ["durationMs"] = syncDurationMs,
            ["force"] = forceArchiveInstall,
            ["trigger"] = trigger,
            ["operationId"] = operationId,
            ["changedFiles"] = summary.DownloadedFiles
        });
        UpdatePrimaryActionButton();
        return new InstallOperationTelemetry("sync", summary.DownloadedFiles > 0, syncDurationMs, summary.DownloadedFiles);
    }

    private void EnsureEnoughDiskSpace()
    {
        var check = DiskSpaceService.CheckInstallSpace(GetEffectiveInstallRoot(), _modpackManifest);
        AppendLog(check.Message);
        if (!check.IsOk)
        {
            throw new InvalidOperationException(check.Message);
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (_configuration is null)
        {
            return;
        }

        var window = new SettingsWindow(
            _userSettings,
            GetDefaultInstallRoot(),
            _modpackManifest?.Runtime.MemoryMbDefault ?? 4096,
            _modpackManifest?.Runtime.MemoryMbMin ?? 1024,
            _modpackManifest?.Runtime.MemoryMbMax ?? 16384)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            var username = GetUsername();
            _userSettings = window.Settings;
            _userSettings.Username = username;
            SaveUserSettings();
            LauncherThemeCatalog.ApplyTheme(Resources, _userSettings.ThemeId);
            UpdateDisplayedInstallPath();
            UpdatePrimaryActionButton();
            AppendLog("User settings saved.");
            SetStatus("Settings saved");
            _ = TrackTelemetryAsync("settings_saved", new Dictionary<string, object?>
            {
                ["telemetryEnabled"] = _userSettings.TelemetryEnabled,
                ["customResolution"] = _userSettings.UseCustomResolution
            });

            if (window.RequestedIntegrityCheck)
            {
                await VerifyFilesAsync();
            }
        }
    }

    private async Task UpdateLauncherAsync()
    {
        if (_modpackManifest is null)
        {
            throw new InvalidOperationException("Манифест лаунчера не загружен.");
        }

        if (string.IsNullOrWhiteSpace(_modpackManifest.Launcher.PackageUrl))
        {
            throw new InvalidOperationException("Для лаунчера не указан packageUrl.");
        }

        SaveUserSettings();
        LauncherProgressBar.Value = 0;
        SetStatus("Обновление лаунчера 0%");

        var packageUri = _modpackManifest.ResolveUri(_modpackManifest.Launcher.PackageUrl);
        var progress = new Progress<LauncherSelfUpdateProgress>(updateProgress =>
        {
            LauncherProgressBar.Value = updateProgress.Percentage;
            FooterTextBlock.Text = updateProgress.Message;
        });

        var package = await _launcherSelfUpdateService.DownloadUpdatePackageAsync(
            packageUri,
            _modpackManifest.Launcher.Sha256,
            progress,
            CancellationToken.None);

        LauncherProgressBar.Value = 100;
        FooterTextBlock.Text = "Обновление лаунчера 100%";
        SetStatus("Перезапуск лаунчера...");

        var currentProcess = Process.GetCurrentProcess();
        var currentProcessPath = currentProcess.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentProcessPath))
        {
            throw new InvalidOperationException("Не удалось определить путь к текущему лаунчеру.");
        }

        _launcherSelfUpdateService.ApplyUpdateAndRestart(
            package.PackagePath,
            AppContext.BaseDirectory,
            currentProcessPath,
            currentProcess.Id);

        _allowClose = true;
        Close();
    }

    private string GetDisplayedSource()
    {
        if (_configuration is null)
        {
            return "-";
        }

        if (_configuration.UsesModpackManifest())
        {
            return _configuration.ModpackManifestUrl;
        }

        return _configuration.UsesDirectModpackArchive() ? _configuration.ModpackArchiveUrl : _configuration.ManifestUrl;
    }

    private void UpdateProgress(FileSyncProgress progress)
    {
        LauncherProgressBar.Value = progress.Percentage;
        FooterTextBlock.Text = progress.Message;
        if (!string.IsNullOrWhiteSpace(progress.LogLine))
        {
            AppendLog(progress.LogLine);
        }
    }

    private IProgress<FileSyncProgress> ScaleProgress(double start, double end)
    {
        return new Progress<FileSyncProgress>(progress =>
        {
            var scaled = start + Math.Clamp(progress.Percentage, 0, 100) * (end - start) / 100d;
            UpdateProgress(progress with
            {
                Percentage = scaled,
                Message = progress.Message.Contains('%', StringComparison.Ordinal)
                    ? progress.Message
                    : $"{progress.Message} ({scaled:0}%)"
            });
        });
    }

    private async Task RunSafeAsync(Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _isBusy = true;
            PlayButton.IsEnabled = false;
            SettingsButton.IsEnabled = false;
            OpenNewsButton.IsEnabled = false;
            await action();
        }
        catch (Exception exception)
        {
            AppendLog($"Error: {exception.Message}");
            SetStatus("Error");
            ShowLauncherError(exception);
        }
        finally
        {
            _isBusy = false;
            PlayButton.IsEnabled = true;
            SettingsButton.IsEnabled = true;
            OpenNewsButton.IsEnabled = true;
        }
    }

    private static bool IsNetworkNameResolutionError(HttpRequestException exception)
    {
        return exception.InnerException is SocketException socketException
            && socketException.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain;
    }

    private static string CreateUserErrorMessage(Exception exception)
    {
        if (exception is HttpRequestException httpException && IsNetworkNameResolutionError(httpException))
        {
            return "Не удалось подключиться к сайту лаунчера.\n\nПроверь интернет, DNS или доступность bl-modern.ru, затем нажми «Играть» ещё раз.";
        }

        if (exception is HttpRequestException)
        {
            return $"Ошибка сети при подключении к сайту лаунчера.\n\n{exception.Message}\n\nПопробуй ещё раз позже.";
        }

        if (exception is TaskCanceledException)
        {
            return "Сайт лаунчера слишком долго не отвечает.\n\nПроверь интернет и попробуй ещё раз.";
        }

        return exception.Message;
    }

    private void ShowLauncherError(Exception exception)
    {
        var errorInfo = ErrorClassifier.Classify(exception);
        var logPath = WriteErrorLog(errorInfo, exception);
        var window = new ErrorWindow(errorInfo, logPath, _userSettings.ThemeId, () => SendSupportLogAsync(errorInfo, logPath))
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async Task<SupportLogSendResult> SendSupportLogAsync(ErrorInfo errorInfo, string logPath)
    {
        EnsureTelemetryIdentity();

        var installRoot = _configuration is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForgeLauncher")
            : GetEffectiveInstallRoot();

        var package = await _supportLogService.CreatePackageAsync(
            installRoot,
            logPath,
            _userSettings.ClientId,
            GetUsername(),
            GetLauncherVersion(),
            GetTelemetryModpackVersion(),
            errorInfo.Title,
            CancellationToken.None);

        try
        {
            var upload = await _supportLogService.UploadAsync(
                SupportLogsUrl,
                package.Path,
                _userSettings.ClientId,
                GetUsername(),
                GetLauncherVersion(),
                GetTelemetryModpackVersion(),
                CancellationToken.None);

            if (upload.Success)
            {
                _ = TrackTelemetryAsync("support_log_sent", new Dictionary<string, object?>
                {
                    ["supportId"] = upload.Id,
                    ["includedFiles"] = package.IncludedFileCount
                });

                var idText = string.IsNullOrWhiteSpace(upload.Id) ? string.Empty : $" Номер: {upload.Id}.";
                return new SupportLogSendResult(true, package.Path, $"Лог отправлен в поддержку.{idText}");
            }

            OpenDirectory(package.Path);
            return new SupportLogSendResult(false, package.Path, $"Архив создан, но сервер не принял отправку: {upload.Message}");
        }
        catch (Exception exception)
        {
            OpenDirectory(package.Path);
            AppendLog($"Support log upload failed: {exception.Message}");
            return new SupportLogSendResult(false, package.Path, $"Архив создан, но отправить его не удалось: {exception.Message}");
        }
    }

    private string WriteErrorLog(ErrorInfo errorInfo, Exception exception)
    {
        try
        {
            var installRoot = _configuration is null
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForgeLauncher")
                : GetEffectiveInstallRoot();
            var logRoot = Path.Combine(installRoot, ".launcher", "logs");
            Directory.CreateDirectory(logRoot);

            var logPath = Path.Combine(logRoot, $"launcher-error-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var content = new StringBuilder()
                .AppendLine($"Время: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
                .AppendLine($"Заголовок: {errorInfo.Title}")
                .AppendLine($"Описание: {errorInfo.Summary}")
                .AppendLine()
                .AppendLine("Что сделать:")
                .AppendLine(string.Join(Environment.NewLine, errorInfo.Actions.Select(action => "- " + action)))
                .AppendLine()
                .AppendLine($"Версия лаунчера: {GetLauncherVersion()}")
                .AppendLine($"Версия модпака: {GetTelemetryModpackVersion()}")
                .AppendLine($"Папка установки: {installRoot}")
                .AppendLine($"Манифест: {_configuration?.ModpackManifestUrl ?? "-"}")
                .AppendLine($"ОС: {Environment.OSVersion.VersionString}")
                .AppendLine()
                .AppendLine("Технические детали:")
                .AppendLine(exception.ToString())
                .ToString();

            File.WriteAllText(logPath, content);
            return logPath;
        }
        catch (Exception logException)
        {
            AppendLog($"Error log write failed: {logException.Message}");
            return string.Empty;
        }
    }

    private void SaveUserSettings()
    {
        if (_configuration is null)
        {
            return;
        }

        _userSettings.Username = GetUsername();
        _userSettings.Save(_configuration.GetUserSettingsPath());
    }

    private void EnsureTelemetryIdentity()
    {
        if (string.IsNullOrWhiteSpace(_userSettings.ClientId))
        {
            _userSettings.ClientId = Guid.NewGuid().ToString();
            if (_configuration is not null)
            {
                _userSettings.Save(_configuration.GetUserSettingsPath());
            }
        }
    }

    private string GetUsername()
    {
        return string.IsNullOrWhiteSpace(UsernameTextBox.Text) ? "Player" : UsernameTextBox.Text.Trim();
    }

    private LauncherConfiguration CreateEffectiveConfiguration()
    {
        if (_configuration is null)
        {
            throw new InvalidOperationException("Launcher configuration is not loaded.");
        }

        return new LauncherConfiguration
        {
            LauncherName = _configuration.LauncherName,
            ManifestUrl = _configuration.ManifestUrl,
            ModpackManifestUrl = _configuration.ModpackManifestUrl,
            ModpackArchiveUrl = _configuration.ModpackArchiveUrl,
            ModpackVersion = _configuration.ModpackVersion,
            ModpackArchiveSha256 = _configuration.ModpackArchiveSha256,
            DistributionRoot = GetEffectiveInstallRoot(),
            LauncherExecutable = _configuration.LauncherExecutable,
            LauncherVersionFile = _configuration.LauncherVersionFile
        };
    }

    private string GetDefaultInstallRoot()
    {
        if (_configuration is null)
        {
            return "-";
        }

        return string.IsNullOrWhiteSpace(_modpackManifest?.Install.Root)
            ? _configuration.GetDistributionRoot()
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(_modpackManifest.Install.Root));
    }

    private string GetEffectiveInstallRoot()
    {
        if (_configuration is null)
        {
            return "-";
        }

        return _userSettings.ResolveInstallRoot(_modpackManifest?.Install.Root ?? string.Empty, _configuration.GetDistributionRoot());
    }

    private void UpdateDisplayedInstallPath()
    {
    }

    private void UpdatePrimaryActionButton()
    {
        _primaryActionState = GetPrimaryActionState();
        PlayButton.Content = _primaryActionState switch
        {
            PrimaryActionState.LauncherUpdate => "Обновить лаунчер",
            PrimaryActionState.Install => "Установить",
            PrimaryActionState.Update => "Обновить",
            _ => "Играть"
        };
    }

    private PrimaryActionState GetPrimaryActionState()
    {
        _launcherUpdateAvailable = IsLauncherUpdateAvailable(out _launcherUpdateVersion);
        if (_launcherUpdateAvailable)
        {
            return PrimaryActionState.LauncherUpdate;
        }

        if (_configuration is null || !_configuration.UsesDirectModpackArchive())
        {
            return PrimaryActionState.Play;
        }

        var expectedVersion = GetExpectedArchiveVersion();
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return PrimaryActionState.Play;
        }

        if (_modpackManifest?.Updates.ForceReinstall == true)
        {
            return PrimaryActionState.Update;
        }

        var markerPath = GetArchiveVersionMarkerPath();
        if (!File.Exists(markerPath))
        {
            return PrimaryActionState.Install;
        }

        var installedVersion = File.ReadAllText(markerPath).Trim();
        return installedVersion.Equals(expectedVersion, StringComparison.OrdinalIgnoreCase)
            ? PrimaryActionState.Play
            : PrimaryActionState.Update;
    }

    private string GetArchiveVersionMarkerPath()
    {
        return Path.Combine(GetEffectiveInstallRoot(), ".launcher", "modpack.version");
    }

    private string GetExpectedArchiveVersion()
    {
        if (_modpackManifest is not null)
        {
            return $"{_modpackManifest.Modpack.Id}:{_modpackManifest.Modpack.Version}";
        }

        if (_configuration is null)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(_configuration.ModpackVersion)
            ? _configuration.ModpackArchiveUrl
            : _configuration.ModpackVersion;
    }

    private bool IsLauncherUpdateAvailable(out string remoteVersion)
    {
        remoteVersion = _modpackManifest?.Launcher.Version?.Trim() ?? string.Empty;
        if (_modpackManifest is null ||
            string.IsNullOrWhiteSpace(remoteVersion) ||
            string.IsNullOrWhiteSpace(_modpackManifest.Launcher.PackageUrl))
        {
            return false;
        }

        return IsRemoteVersionNewer(GetLauncherVersion(), remoteVersion);
    }

    private static bool IsRemoteVersionNewer(string localVersion, string remoteVersion)
    {
        var normalizedLocal = NormalizeVersion(localVersion);
        var normalizedRemote = NormalizeVersion(remoteVersion);
        if (Version.TryParse(normalizedLocal, out var localParsed) &&
            Version.TryParse(normalizedRemote, out var remoteParsed))
        {
            return remoteParsed > localParsed;
        }

        return !string.Equals(localVersion, remoteVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var cleaned = value.Trim();
        var plusIndex = cleaned.IndexOf('+');
        if (plusIndex >= 0)
        {
            cleaned = cleaned[..plusIndex];
        }

        var dashIndex = cleaned.IndexOf('-');
        if (dashIndex >= 0)
        {
            cleaned = cleaned[..dashIndex];
        }

        return cleaned;
    }

    private async Task RefreshNewsAsync()
    {
        if (_modpackManifest is null)
        {
            ShowNewsFallback("Манифест сборки пока не загружен.");
            return;
        }

        var newsUrl = ResolveNewsUrl(_modpackManifest);
        if (string.IsNullOrWhiteSpace(newsUrl))
        {
            ShowNewsFallback("Новости не настроены в манифесте.");
            return;
        }

        _currentNewsUrl = newsUrl;
        NewsTitleTextBlock.Text = "Новости загружаются...";
        NewsDateTextBlock.Text = string.Empty;
        NewsDescriptionTextBlock.Text = "Получаем последние записи с сайта.";
        OpenNewsButton.Visibility = Visibility.Collapsed;
        NewsImageBorder.Visibility = Visibility.Collapsed;

        try
        {
            var news = await _newsClient.GetNewsAsync(newsUrl);
            var latest = news.FirstOrDefault();
            if (latest is null)
            {
                ShowNewsFallback("Новостей пока нет.");
                return;
            }

            ShowNewsItem(latest, newsUrl);
        }
        catch (Exception exception)
        {
            AppendLog($"News load failed: {exception.Message}");
            ShowNewsFallback($"Не удалось загрузить новости: {exception.Message}");
        }
    }

    private static readonly ServerDefinition[] GameServers =
    [
        new("Сервер #1 — Основной", "play.bl-modern.ru"),
        new("Сервер #2 — TFGM2", "tfgm2.bl-modern.ru")
    ];

    private static readonly Media.Brush ServerOnlineBrush = CreateFrozenBrush(0x3F, 0xB9, 0x50);
    private static readonly Media.Brush ServerOfflineBrush = CreateFrozenBrush(0x6E, 0x76, 0x81);

    private async void ServerStatsTimer_Tick(object? sender, EventArgs e) =>
        await RunSafeAsync(RefreshServerStatsAsync);

    private async Task RefreshServerStatsAsync()
    {
        var items = await Task.WhenAll(GameServers.Select(GetServerStatusAsync));
        ServerListPanel.ItemsSource = items;
        UpdateServerSummary(items);
        if (!_serverStatsTimer.IsEnabled)
        {
            _serverStatsTimer.Start();
        }
    }

    private async Task<ServerStatusItem> GetServerStatusAsync(ServerDefinition server)
    {
        try
        {
            var stats = await _serverStatsClient.GetStatsAsync(ServerStatsUrl, server.Host);
            return BuildServerStatusItem(server, stats);
        }
        catch (Exception exception)
        {
            AppendLog($"Server stats load failed ({server.Host}): {exception.Message}");
            return BuildServerStatusItem(server, null);
        }
    }

    private static ServerStatusItem BuildServerStatusItem(ServerDefinition server, ServerStats? stats)
    {
        var available = stats is { Success: true };
        var online = stats is { Success: true, Online: true };

        if (!available)
        {
            return new ServerStatusItem(server.DisplayName, "Не отвечает", "Недоступен", "—", "", ServerOfflineBrush, false, 0);
        }

        if (!online)
        {
            return new ServerStatusItem(server.DisplayName, server.Host, "Оффлайн", "0", "игроков", ServerOfflineBrush, false, 0);
        }

        var detail = stats!.Tps > 0 ? $"{server.Host} · TPS {stats.Tps:0.#}" : server.Host;
        return new ServerStatusItem(server.DisplayName, detail, "Онлайн", stats.Players.ToString(), PlayersCaption(stats.Players), ServerOnlineBrush, true, stats.Players);
    }

    private void UpdateServerSummary(IReadOnlyCollection<ServerStatusItem> items)
    {
        if (items.All(item => !item.IsOnline) && items.All(item => item.Detail == "Сервер недоступен"))
        {
            ServerSubtitleTextBlock.Text = "Статус серверов недоступен";
            return;
        }

        var totalPlayers = items.Where(item => item.IsOnline).Sum(item => item.OnlinePlayers);
        var onlineServers = items.Count(item => item.IsOnline);
        ServerSubtitleTextBlock.Text = onlineServers > 0
            ? $"Сейчас в игре {totalPlayers} {PlayersCaption(totalPlayers)} · серверов онлайн: {onlineServers}/{items.Count}"
            : "Серверы сейчас оффлайн";
    }

    private static string PlayersCaption(int players)
    {
        var lastTwo = players % 100;
        if (lastTwo is >= 11 and <= 14)
        {
            return "игроков";
        }

        return (players % 10) switch
        {
            1 => "игрок",
            2 or 3 or 4 => "игрока",
            _ => "игроков"
        };
    }

    private static Media.Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed record ServerDefinition(string DisplayName, string Host);

    public sealed record ServerStatusItem(
        string Name,
        string Detail,
        string StatusText,
        string PlayersText,
        string PlayersCaption,
        Media.Brush StatusBrush,
        bool IsOnline,
        int OnlinePlayers);

    private string ResolveNewsUrl(ModpackManifest manifest)
    {
        var configuredUrl = manifest.Launcher.NewsUrl;
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            return manifest.ResolveUri(configuredUrl).ToString();
        }

        if (manifest.SourceUri is null)
        {
            return string.Empty;
        }

        if (manifest.SourceUri.IsFile)
        {
            return Path.Combine(Path.GetDirectoryName(manifest.SourceUri.LocalPath) ?? AppContext.BaseDirectory, "news.json");
        }

        if (manifest.SourceUri.Host.EndsWith("bl-modern.ru", StringComparison.OrdinalIgnoreCase))
        {
            return "https://bl-modern.ru/api/rss.php";
        }

        return new Uri(manifest.SourceUri, "news.php").ToString();
    }

    private void ShowNewsItem(NewsItem newsItem, string newsFeedUrl)
    {
        NewsTitleTextBlock.Text = newsItem.Title;
        NewsDateTextBlock.Text = newsItem.CreatedAt == default
            ? string.Empty
            : newsItem.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        NewsDescriptionTextBlock.Text = string.IsNullOrWhiteSpace(newsItem.Description)
            ? "Описание новости не заполнено."
            : newsItem.Description;

        _currentNewsUrl = ResolveOptionalNewsLink(newsItem.Url, newsFeedUrl);
        OpenNewsButton.Visibility = string.IsNullOrWhiteSpace(_currentNewsUrl) ? Visibility.Collapsed : Visibility.Visible;

        var imageUrl = ResolveOptionalNewsLink(newsItem.ImageUrl, newsFeedUrl);
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            NewsImageBorder.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imageUrl, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            NewsImage.Source = bitmap;
            NewsImageBorder.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            AppendLog($"News image load failed: {exception.Message}");
            NewsImageBorder.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowNewsFallback(string message)
    {
        _currentNewsUrl = string.Empty;
        NewsImageBorder.Visibility = Visibility.Collapsed;
        OpenNewsButton.Visibility = Visibility.Collapsed;
        NewsTitleTextBlock.Text = "Новости";
        NewsDateTextBlock.Text = string.Empty;
        NewsDescriptionTextBlock.Text = message;
    }

    private static string ResolveOptionalNewsLink(string value, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return new Uri(baseUri, value).ToString();
        }

        return value;
    }

    private void SetStatus(string status)
    {
        FooterTextBlock.Text = status;
    }

    private void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    private void OpenCurrentNews()
    {
        if (!string.IsNullOrWhiteSpace(_currentNewsUrl))
        {
            _ = TrackTelemetryAsync("news_opened");
            OpenExternalUrl(_currentNewsUrl);
        }
    }

    private void OpenSupportWindow()
    {
        EnsureTelemetryIdentity();
        var window = new SupportWindow(
            _supportChatClient,
            SupportTicketsUrl,
            _userSettings,
            _configuration?.GetUserSettingsPath() ?? string.Empty,
            _userSettings.ClientId,
            GetUsername(),
            GetLauncherVersion(),
            GetTelemetryModpackVersion(),
            _userSettings.ThemeId)
        {
            Owner = this
        };

        window.Show();
        _ = TrackTelemetryAsync("support_window_opened");
    }

    private void OpenGameFolder()
    {
        if (_configuration is null)
        {
            return;
        }

        var root = GetEffectiveInstallRoot();
        Directory.CreateDirectory(root);
        OpenPath(root);
    }

    private async Task MonitorMinecraftProcessAsync(Process process, string installRoot, string launchAttemptId, long installDurationMs, string javaSource)
    {
        var startedAt = DateTime.UtcNow;
        var successConfirmed = false;
        try
        {
            var exitTask = process.WaitForExitAsync();
            var successDelayTask = Task.Delay(TimeSpan.FromSeconds(LaunchSuccessThresholdSeconds));
            var completedTask = await Task.WhenAny(exitTask, successDelayTask);
            if (completedTask == successDelayTask && !process.HasExited)
            {
                successConfirmed = true;
                _ = TrackTelemetryAsync("launch_succeeded", new Dictionary<string, object?>
                {
                    ["launchAttemptId"] = launchAttemptId,
                    ["startupSeconds"] = LaunchSuccessThresholdSeconds,
                    ["installDurationMs"] = installDurationMs,
                    ["javaSource"] = javaSource
                });
            }

            await exitTask;
            UpdateDiscordPresence(DiscordIdleDetails, DiscordIdleState);
            var runtime = DateTime.UtcNow - startedAt;
            var analysis = CrashAnalyzerService.Analyze(installRoot, process.ExitCode);

            _ = TrackTelemetryAsync("game_session_ended", new Dictionary<string, object?>
            {
                ["launchAttemptId"] = launchAttemptId,
                ["exitCode"] = process.ExitCode,
                ["runtimeSeconds"] = (int)runtime.TotalSeconds,
                ["installDurationMs"] = installDurationMs,
                ["javaSource"] = javaSource,
                ["graceful"] = process.ExitCode == 0
            });

            if (successConfirmed)
            {
                if (process.ExitCode != 0)
                {
                    _ = TrackTelemetryAsync("game_session_crashed", new Dictionary<string, object?>
                    {
                        ["launchAttemptId"] = launchAttemptId,
                        ["exitCode"] = process.ExitCode,
                        ["runtimeSeconds"] = (int)runtime.TotalSeconds,
                        ["crashCategory"] = analysis.Category,
                        ["summary"] = analysis.Summary,
                        ["signature"] = analysis.Signature,
                        ["evidence"] = analysis.Evidence,
                        ["hasCrashReport"] = analysis.HasCrashReport
                    });
                }

                return;
            }

            _ = TrackTelemetryAsync("launch_failed", new Dictionary<string, object?>
            {
                ["launchAttemptId"] = launchAttemptId,
                ["stage"] = "early_exit",
                ["exitCode"] = process.ExitCode,
                ["runtimeSeconds"] = (int)runtime.TotalSeconds,
                ["installDurationMs"] = installDurationMs,
                ["summary"] = analysis.Summary,
                ["crashCategory"] = analysis.Category,
                ["signature"] = analysis.Signature,
                ["evidence"] = analysis.Evidence,
                ["hasCrashReport"] = analysis.HasCrashReport,
                ["javaSource"] = javaSource
            });

            await Dispatcher.InvokeAsync(() =>
            {
                AppendLog($"Crash Assistant: {analysis.Summary}");
                SetStatus("Minecraft crashed");
                System.Windows.MessageBox.Show(this, analysis.Details, "Анализатор краша", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() => AppendLog($"Crash monitor error: {exception.Message}"));
        }
        finally
        {
            process.Dispose();
        }
    }

    private void InitializeTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Открыть лаунчер", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Открыть папку игры", null, (_, _) => Dispatcher.Invoke(OpenGameFolder));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "BL-modern TFGM",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private Drawing.Icon LoadTrayIcon()
    {
        using var iconStream = typeof(MainWindow).Assembly.GetManifestResourceStream("Launcher.App.launcher.ico");
        if (iconStream is not null)
        {
            return new Drawing.Icon(iconStream);
        }

        return Drawing.SystemIcons.Application;
    }

    private void MinimizeToTray(string message)
    {
        Hide();
        ShowInTaskbar = false;
        if (_trayIcon is not null)
        {
            _trayIcon.BalloonTipTitle = "BL-modern TFGM";
            _trayIcon.BalloonTipText = message;
            _trayIcon.ShowBalloonTip(2500);
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void RestoreFromExternalActivation()
    {
        _allowClose = false;
        ShowFromTray();
    }

    private void ExitApplication()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            MinimizeToTray("Лаунчер свернут в трей.");
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Dispose();
        base.OnClosed(e);
    }

    public void Dispose()
    {
        _trayIcon?.Dispose();
        _discordPresence.Dispose();
        _httpClient.Dispose();
    }

    private static void OpenPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private static void OpenDirectory(string path)
    {
        var directory = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        OpenPath(directory);
    }

    private void AppendLog(string message)
    {
        Debug.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private async Task TrackTelemetryAsync(string eventName, Dictionary<string, object?>? properties = null)
    {
        if (!_userSettings.TelemetryEnabled)
        {
            return;
        }

        EnsureTelemetryIdentity();

        try
        {
            await _telemetryClient.SendEventAsync(
                TelemetryUrl,
                _userSettings.ClientId,
                GetLauncherVersion(),
                GetTelemetryModpackVersion(),
                Environment.OSVersion.VersionString,
                eventName,
                properties);
        }
        catch (Exception exception)
        {
            AppendLog($"Telemetry failed: {exception.Message}");
        }
    }

    private async Task TrackLaunchFailureBeforeProcessAsync(Exception exception, string launchAttemptId, string stage, InstallOperationTelemetry? installTelemetry)
    {
        if (!_userSettings.TelemetryEnabled)
        {
            return;
        }

        try
        {
            await TrackTelemetryAsync("launch_failed", new Dictionary<string, object?>
            {
                ["launchAttemptId"] = launchAttemptId,
                ["stage"] = stage,
                ["failureKind"] = ClassifyFailureKind(exception, stage),
                ["errorType"] = exception.GetType().Name,
                ["message"] = SanitizeTelemetryText(exception.Message, 220),
                ["socketErrorCode"] = TryGetSocketErrorCode(exception),
                ["installDurationMs"] = installTelemetry?.DurationMs,
                ["installMode"] = installTelemetry?.Mode,
                ["installChangedFiles"] = installTelemetry?.ChangedFiles ?? 0
            });
        }
        catch
        {
        }
    }

    private static string ClassifyFailureKind(Exception exception, string stage)
    {
        if (stage.Equals("java_validation", StringComparison.OrdinalIgnoreCase))
        {
            return "java_validation";
        }

        if (stage.Equals("process_start", StringComparison.OrdinalIgnoreCase))
        {
            return "process_start";
        }

        if (exception is TaskCanceledException)
        {
            return "timeout";
        }

        if (exception is UnauthorizedAccessException)
        {
            return "access_denied";
        }

        if (exception is HttpRequestException)
        {
            return "network";
        }

        if (exception is IOException)
        {
            return "io";
        }

        if (exception is InvalidDataException)
        {
            return "data";
        }

        if (stage.Equals("install", StringComparison.OrdinalIgnoreCase))
        {
            return "install";
        }

        return "unexpected";
    }

    private static string DetectJavaSource(string? javaPath)
    {
        if (string.IsNullOrWhiteSpace(javaPath))
        {
            return "unknown";
        }

        var expanded = Environment.ExpandEnvironmentVariables(javaPath);
        if (!Path.IsPathRooted(expanded))
        {
            return "path";
        }

        var lower = expanded.ToLowerInvariant();
        if (lower.Contains("\\.launcher\\runtime\\") || lower.Contains("\\runtime\\"))
        {
            return "bundled";
        }

        if (lower.Contains("\\program files\\java\\") || lower.Contains("\\program files\\eclipse adoptium\\"))
        {
            return "system";
        }

        return "custom";
    }

    private static string SanitizeTelemetryText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = Regex.Replace(value, @"[A-Za-z]:\\[^ \r\n\t]+", "<path>");
        sanitized = Regex.Replace(sanitized, @"https?://\S+", "<url>");
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength] + "...";
    }

    private static int? TryGetSocketErrorCode(Exception exception)
    {
        if (exception is HttpRequestException httpException)
        {
            return TryGetSocketErrorCode(httpException.InnerException ?? exception);
        }

        if (exception is IOException ioException)
        {
            return TryGetSocketErrorCode(ioException.InnerException ?? exception);
        }

        if (exception is SocketException socketException)
        {
            return (int)socketException.SocketErrorCode;
        }

        return null;
    }

    private string GetLauncherVersion()
    {
        var versionFilePath = Path.Combine(AppContext.BaseDirectory, "launcher.version");
        if (File.Exists(versionFilePath))
        {
            var version = File.ReadAllText(versionFilePath).Trim();
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        var assemblyVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(assemblyVersion) ? "unknown" : assemblyVersion;
    }

    private string GetTelemetryModpackVersion()
    {
        if (!string.IsNullOrWhiteSpace(_modpackManifest?.Modpack.Version))
        {
            return _modpackManifest.Modpack.Version;
        }

        if (!string.IsNullOrWhiteSpace(_configuration?.ModpackVersion))
        {
            return _configuration.ModpackVersion;
        }

        if (!string.IsNullOrWhiteSpace(_manifest?.Game.Version))
        {
            return _manifest.Game.Version;
        }

        return "unknown";
    }

    private sealed record InstallOperationTelemetry(string Mode, bool Installed, long DurationMs, int ChangedFiles);
}

internal enum PrimaryActionState
{
    LauncherUpdate,
    Install,
    Update,
    Play
}
