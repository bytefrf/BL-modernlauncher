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
using Launcher.App.Platform;
using Launcher.App.Services;
using Launcher.App.Theming;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using Media = System.Windows.Media;

namespace Launcher.App;

public partial class MainWindow : Window, IDisposable
{
    private const string TelemetryUrl = "https://bl-modern.ru/api/telemetry.php";
    private const string SupportLogsUrl = "https://bl-modern.ru/api/support_logs.php";
    // Адрес живёт в клиенте — им же пользуется Avalonia-версия.
    private const string SupportTicketsUrl = Services.SupportChatClient.DefaultEndpoint;
    private const int LaunchSuccessThresholdSeconds = 45;
    // Единый HttpClient имеет таймаут 20 минут (для крупных загрузок). Для лёгких запросов
    // (новости/статистика/телеметрия) используем отдельный короткий таймаут, иначе зависший
    // endpoint блокирует инициализацию почти на 20 минут.
    private const int LightRequestTimeoutSeconds = 15;

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
    private readonly CatalogClient _catalogClient;
    private readonly NewsClient _newsClient;
    private readonly PlayerStatsClient _playerStatsClient;
    private readonly OptionalModsService _optionalModsService;
    private readonly SkinClient _skinClient;
    private readonly MinecraftServerPinger _serverPinger = new();
    private readonly TelemetryClient _telemetryClient;
    private readonly SupportLogService _supportLogService;
    private readonly SupportChatClient _supportChatClient;
    private readonly LauncherSelfUpdateService _launcherSelfUpdateService;
    private readonly DiscordPresenceService _discordPresence;
    private LauncherConfiguration? _configuration;
    private LauncherManifest? _manifest;
    private ModpackManifest? _modpackManifest;
    private CatalogManifest? _catalog;
    private string _selectedModpackId = string.Empty;
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
    private Drawing.Icon? _trayIconImage;
    private bool _allowClose;
    private bool _isBusy;
    private bool _launcherUpdateAvailable;
    private string _launcherUpdateVersion = string.Empty;
    // Профиль игрока (часы/ачивки) и звуки интерфейса. Профиль лежит в отдельном файле, чтобы
    // его можно было позже синхронизировать с сайтом, не трогая настройки.
    private PlayerProfile _playerProfile = new();
    private readonly LauncherSoundService _soundService = new();
    private readonly Queue<AchievementToastContent> _achievementToastQueue = new();
    private bool _achievementToastRunning;
    private SiteLevel? _siteLevel;
    private DateTime _lastSiteSyncUtc = DateTime.MinValue;
    public MainWindow()
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, LauncherThemeCatalog.DefaultThemeId);
        ApplyResponsiveFixedWindowSize();
        _manifestClient = new ManifestClient(_httpClient);
        _modpackManifestClient = new ModpackManifestClient(_httpClient);
        _catalogClient = new CatalogClient(_httpClient);
        _newsClient = new NewsClient(_httpClient);
        _playerStatsClient = new PlayerStatsClient(_httpClient);
        _optionalModsService = new OptionalModsService(_httpClient);
        _skinClient = new SkinClient(_httpClient);
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
    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        AnimatePlayButtonPress();
        await RunSafeAsync(() => PlayAsync());
    }
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
    private void VkButton_Click(object sender, RoutedEventArgs e) => OpenExternalUrl("https://vk.com/blmodern");

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
        LauncherThemeBrushes.ApplyTheme(Resources, _userSettings.ThemeId);
        EnsureTelemetryIdentity();
        _playerProfile = PlayerProfile.Load(_configuration.GetPlayerProfilePath());
        _soundService.Enabled = _userSettings.SoundEnabled;
        UsernameTextBox.Text = _userSettings.Username;
        UsernameTextBox.TextChanged += (_, _) =>
        {
            UpdateProfileAvatar();
            UpdateUsernameIndicator();
        };
        UpdateUsernameIndicator();
        RefreshProfileUi();
        SetActiveTab(account: false);

        // Достижения и уровень — с сайта (тот же движок, что в кабинете). В фоне, чтобы не держать запуск.
        _ = RunBackgroundAsync(() => RefreshSiteAchievementsAsync(force: true));
        _ = RunBackgroundAsync(() => RefreshSkinAsync(force: true));

        // Отладочный показ тоста: проверить вид и звук, не играя сессию.
        if (Environment.GetCommandLineArgs().Any(argument => argument.Equals("--test-achievement", StringComparison.OrdinalIgnoreCase)))
        {
            EnqueueAchievementToast(new AchievementToastContent("⏱", "Время в игре VI", "Достичь 100 ч · +120 XP"));
            EnqueueAchievementToast(new AchievementToastContent("💎", "Расхититель", "500 сундуков и 50 000 блоков · +180 XP"));
        }
        UpdateDisplayedInstallPath();
        AppendLog($"Config: {_configuration.ConfigPath}");
        AppendLog($"Settings: {_configuration.GetUserSettingsPath()}");

        if (_configuration.UsesCatalog())
        {
            await InitializeCatalogAsync();
        }
        else if (_configuration.UsesDirectModpackArchive())
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

    // Целевая ширина декодирования фона ≈ ширина окна в физических пикселях (с учётом DPI), с разумным
    // потолком. Меньше декодировать нельзя (будет мыло), больше — бессмысленно (окно фиксированной ширины).
    private int ResolveBackgroundDecodeWidth()
    {
        try
        {
            var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var logicalWidth = ActualWidth > 0 ? ActualWidth : Width;
            var pixels = (int)Math.Ceiling(Math.Max(logicalWidth, 800) * dpiScale);
            return Math.Clamp(pixels, 1000, 2400);
        }
        catch
        {
            return 1600;
        }
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
            // Скриншоты-фоны бывают 3440x1377/2560x1440 — в полном разрешении они едят десятки МБ
            // и дорого масштабируются каждый кадр. Декодируем под ширину окна (с запасом), это
            // кратно снижает память и нагрузку на отрисовку фона.
            bitmap.DecodePixelWidth = ResolveBackgroundDecodeWidth();
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
        var targetSize = GetForcedWindowSize() ?? SelectWindowSize(workArea.Width, workArea.Height);
        Width = targetSize.Width;
        Height = targetSize.Height;
        MinWidth = targetSize.Width;
        MaxWidth = targetSize.Width;
        MinHeight = targetSize.Height;
        MaxHeight = targetSize.Height;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    /// <summary>
    /// Размер окна подбирается ПЛАВНО от рабочей области, а не ступенями. Раньше было четыре
    /// фиксированных пресета, из-за чего на разных мониторах интерфейс заметно «прыгал» и
    /// появлялись пустоты. Пропорции держим близко к 16:9 и вписываемся в рабочую область.
    /// </summary>
    private static System.Windows.Size SelectWindowSize(double workWidth, double workHeight)
    {
        const double minWidth = 980;
        const double minHeight = 560;
        const double maxWidth = 1440;
        const double maxHeight = 810;

        // Оставляем поля вокруг окна, чтобы оно не липло к краям и к панели задач.
        var availableWidth = Math.Max(minWidth, workWidth - 80);
        var availableHeight = Math.Max(minHeight, workHeight - 80);

        var width = Math.Clamp(workWidth * 0.78, minWidth, maxWidth);
        var height = Math.Clamp(width * 9 / 16, minHeight, maxHeight);

        // Если по высоте не влезли — пересчитываем ширину от высоты, сохраняя пропорции.
        if (height > availableHeight)
        {
            height = availableHeight;
            width = Math.Clamp(height * 16 / 9, minWidth, maxWidth);
        }

        return new System.Windows.Size(Math.Min(width, availableWidth), Math.Min(height, availableHeight));
    }

    /// <summary>
    /// Отладочный размер окна: `--window-size=1040x600`. Нужен, чтобы проверять вёрстку под разные
    /// разрешения, не меняя разрешение экрана.
    /// </summary>
    private static System.Windows.Size? GetForcedWindowSize()
    {
        foreach (var argument in Environment.GetCommandLineArgs())
        {
            if (!argument.StartsWith("--window-size=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = argument["--window-size=".Length..].Split('x', 'X');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], out var width) &&
                double.TryParse(parts[1], out var height) &&
                width > 200 && height > 200)
            {
                return new System.Windows.Size(width, height);
            }
        }

        return null;
    }

    private async Task VerifyFilesAsync()
    {
        await RefreshMetadataAsync();
        await EnsureGameFilesAsync(forceArchiveInstall: true, operationId: Guid.NewGuid().ToString("N"), trigger: "verify");
        UpdatePrimaryActionButton();
    }

    // Вход на конкретный сервер с карточки: тот же сценарий, что и «Играть», только игра сразу
    // подключается к серверу. Проверки ника и установки сборки при этом никуда не деваются.
    private async void JoinServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string host } || string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        await RunSafeAsync(() => PlayAsync(host));
    }

    private async Task PlayAsync(string? quickPlayServer = null)
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
            var launchManifest = (_modpackManifest is not null || _configuration.UsesDirectModpackArchive())
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
                _soundService.PlayReady();
                return;
            }

            // Установку с дефолтным ником разрешаем (файлы качать не мешает), а вот запуск — нет:
            // иначе игрок заходит на сервер как «Player».
            if (!EnsureUsernameSelected())
            {
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
            var result = await launcher.LaunchAsync(effectiveConfiguration, launchManifest, _userSettings, quickPlayServer);
            if (!string.IsNullOrWhiteSpace(quickPlayServer))
            {
                AppendLog($"Quick play: connecting to {quickPlayServer}");
            }

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

            if (_userSettings.CloseOnGameStart)
            {
                // Пользователь выбрал закрывать лаунчер при запуске игры. Сама игра запущена отдельным
                // процессом (UseShellExecute) и продолжит работать; мониторинг краша при этом отключается.
                ExitApplication();
                return;
            }

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

        if (_configuration.UsesCatalog() && _catalog is not null)
        {
            await SelectModpackAsync(string.IsNullOrWhiteSpace(_selectedModpackId) ? _catalog.Modpacks[0].Id : _selectedModpackId);
        }
        else if (_configuration.UsesDirectModpackArchive())
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
        RefreshServerStatsInBackground(showLoading: true);
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

            // Цепочка «сайт → кэш → вшитый резерв» живёт в ядре: она не про интерфейс,
            // и Avalonia-версия использует ровно её же.
            var resolution = await new ModpackManifestResolver(_modpackManifestClient)
                .ResolveAsync(_configuration.ModpackManifestUrl, cachedManifestPath, AppendLog);
            _modpackManifest = resolution.Manifest;

            if (resolution.Source != ModpackManifestSource.Remote)
            {
                SetStatus($"{resolution.Reason}, using {(resolution.Source == ModpackManifestSource.Cached ? "cached" : "embedded")} manifest");
                ShowNewsFallback(resolution.Reason == "Server timeout"
                    ? "Сайт долго не отвечает. Лаунчер использует последний сохраненный манифест или встроенный резерв."
                    : "Не удалось подключиться к сайту. Лаунчер использует последний сохраненный манифест или встроенный резерв.");
            }

            Title = _modpackManifest.Launcher.Title;
            ServerTitleTextBlock.Text = _modpackManifest.Launcher.Title;
            RefreshServerStatsInBackground(showLoading: true);
            UpdateDisplayedInstallPath();
            FooterTextBlock.Text = $"{_modpackManifest.Modpack.Name} {_modpackManifest.Modpack.Version}";
            _ = RunBackgroundAsync(RefreshNewsAsync);
            _ = RunBackgroundAsync(RefreshOptionalModsCatalogAsync);
            SetStatus("Modpack manifest loaded");
            AppendLog($"Modpack manifest loaded: {_modpackManifest.Modpack.Name} {_modpackManifest.Modpack.Version}");
            UpdatePrimaryActionButton();
            return;
        }

        RefreshServerStatsInBackground(showLoading: true);
        UpdateDisplayedInstallPath();
        ShowNewsFallback("Новости доступны при запуске через modpack-manifest.json.");
        SetStatus("Archive mode is ready");
        AppendLog($"Direct archive mode: {_configuration.ModpackArchiveUrl}");
        UpdatePrimaryActionButton();
    }

    private static readonly Media.Brush ModpackSelectedBg = CreateFrozenBrush(0x2E, 0x46, 0x3A);
    private static readonly Media.Brush ModpackSelectedBorder = CreateFrozenBrush(0xE0, 0xB2, 0x4F);
    private static readonly Media.Brush ModpackNormalBorder = CreateFrozenBrush(0x3A, 0x4A, 0x42);

    private async Task InitializeCatalogAsync()
    {
        SetStatus("Загрузка каталога сборок...");
        try
        {
            _catalog = await _catalogClient.GetCatalogAsync(_configuration!.CatalogUrl);
        }
        catch (Exception exception)
        {
            AppendLog($"Catalog load failed: {exception.Message}");
            _configuration!.IsMultiModpackCatalog = false; // фолбэк = одиночный режим, путь не изолируем
            if (_configuration!.UsesModpackManifest())
            {
                await RefreshModpackManifestAsync(); // фолбэк на одиночный манифест
            }
            else
            {
                ShowNewsFallback("Не удалось загрузить каталог сборок. Проверьте интернет и повторите.");
                SetStatus("Каталог недоступен");
            }
            return;
        }

        if (_catalog.Modpacks.Count == 0)
        {
            ShowNewsFallback("В каталоге пока нет доступных сборок.");
            SetStatus("Каталог пуст");
            return;
        }

        var hasMultiplePacks = ModpackCatalogLogic.ShouldIsolateInstallRoots(_catalog.Modpacks);
        _configuration!.IsMultiModpackCatalog = hasMultiplePacks;
        ModpackDropdownHost.Visibility = hasMultiplePacks ? Visibility.Visible : Visibility.Collapsed;
        // В режиме каталога имя сборки показывает выпадающая кнопка — большой дубль-заголовок прячем.
        ServerTitleTextBlock.Visibility = hasMultiplePacks ? Visibility.Collapsed : Visibility.Visible;

        var selected = ModpackCatalogLogic.ChoosePreferred(_catalog.Modpacks, _userSettings.SelectedModpackId);
        if (selected is not null)
        {
            await SelectModpackAsync(selected.Id);
        }
    }

    private async Task SelectModpackAsync(string id)
    {
        var entry = _catalog?.Modpacks.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return;
        }

        _selectedModpackId = entry.Id;
        _userSettings.SelectedModpackId = entry.Id;
        SaveUserSettings();
        RebuildModpackSidebar();

        SetStatus($"Загрузка сборки: {entry.Name}");
        try
        {
            _modpackManifest = await _modpackManifestClient.GetManifestAsync(entry.ManifestUrl);
        }
        catch (Exception exception)
        {
            AppendLog($"Modpack manifest load failed ({entry.Id}): {exception.Message}");
            ShowNewsFallback($"Не удалось загрузить сборку «{entry.Name}».");
            SetStatus("Ошибка загрузки сборки");
            return;
        }

        Title = _modpackManifest.Launcher.Title;
        var displayName = string.IsNullOrWhiteSpace(entry.Name) ? _modpackManifest.Launcher.Title : entry.Name;
        ServerTitleTextBlock.Text = displayName;
        ModpackDropdownText.Text = displayName;
        UpdateDisplayedInstallPath();
        FooterTextBlock.Text = $"{_modpackManifest.Modpack.Name} {_modpackManifest.Modpack.Version}";
        RefreshServerStatsInBackground(showLoading: true);
        _ = RunBackgroundAsync(RefreshNewsAsync);
        _ = RunBackgroundAsync(RefreshOptionalModsCatalogAsync);
        UpdatePrimaryActionButton();
        SetStatus("Сборка загружена");
        AppendLog($"Selected modpack: {entry.Id} ({_modpackManifest.Modpack.Version})");
    }

    private async void ModpackItem_Click(object sender, RoutedEventArgs e)
    {
        ModpackDropdownButton.IsChecked = false; // закрыть выпадающий список после выбора
        if (sender is FrameworkElement { Tag: string id } && !id.Equals(_selectedModpackId, StringComparison.OrdinalIgnoreCase))
        {
            await RunSafeAsync(() => SelectModpackAsync(id));
        }
    }

    private void RebuildModpackSidebar()
    {
        if (_catalog is null)
        {
            return;
        }

        var activeBg = ResolveThemeBrush("PanelAltBrush", ModpackSelectedBg);
        var activeBorder = ResolveThemeBrush("AccentBrush", ModpackSelectedBorder);
        var idleBorder = ResolveThemeBrush("StrokeBrush", ModpackNormalBorder);
        ModpackListPanel.ItemsSource = _catalog.Modpacks
            .Select(m =>
            {
                var selected = m.Id.Equals(_selectedModpackId, StringComparison.OrdinalIgnoreCase);
                var status = GetModpackStatus(m.Id);
                var playtime = GetModpackPlaytimeText(m.Id);
                var icon = TryLoadModpackIcon(m.IconUrl);

                return new ModpackListItem(
                    m.Id,
                    m.Name,
                    m.Description ?? string.Empty,
                    string.IsNullOrWhiteSpace(m.Description) ? Visibility.Collapsed : Visibility.Visible,
                    string.IsNullOrWhiteSpace(m.Name) ? "?" : char.ToUpperInvariant(m.Name[0]).ToString(),
                    icon,
                    icon is null ? Visibility.Collapsed : Visibility.Visible,
                    status?.Text ?? string.Empty,
                    status?.Brush ?? Media.Brushes.Gray,
                    status is null ? Visibility.Collapsed : Visibility.Visible,
                    playtime,
                    string.IsNullOrEmpty(playtime) ? Visibility.Collapsed : Visibility.Visible,
                    selected ? activeBg : Media.Brushes.Transparent,
                    selected ? activeBorder : idleBorder);
            })
            .ToList();
    }

    private sealed record ModpackStatus(string Text, Media.Brush Brush);

    /// <summary>
    /// Статус сборки для карточки. Показываем его только там, где он достоверен: для выбранной
    /// сборки (у неё загружен манифест) и для сборок с явно заданной игроком папкой. Для остальных
    /// папку пришлось бы угадывать, а ложное «Не установлена» хуже отсутствия бейджа.
    /// </summary>
    /// <summary>
    /// Метка состояния сборки в списке. Само состояние вычисляет ядро, здесь остаётся
    /// только подобрать кисть под тему.
    /// </summary>
    private ModpackStatus? GetModpackStatus(string modpackId)
    {
        var state = ModpackCatalogLogic.GetInstallState(modpackId, _selectedModpackId, _primaryActionState, _userSettings);
        if (state == ModpackInstallState.Unknown)
        {
            return null;
        }

        var brush = state switch
        {
            ModpackInstallState.UpdateAvailable => ResolveThemeBrush("AccentBrush", Media.Brushes.Goldenrod),
            ModpackInstallState.Installed => ServerOnlineBrush,
            _ => ResolveThemeBrush("MutedBrush", Media.Brushes.Gray)
        };

        return new ModpackStatus(ModpackCatalogLogic.GetInstallStateText(state), brush);
    }

    // Проверка маркера живёт в ядре: раньше здесь была вторая копия, которая не раскрывала
    // %AppData% на Linux и macOS.
    private static bool IsModpackInstalledAt(string root, string modpackId)
        => ModpackInstallMarker.IsInstalledAt(root, modpackId);

    private string GetModpackPlaytimeText(string modpackId)
    {
        if (_playerProfile.Modpacks.TryGetValue(modpackId, out var stats) && stats.PlaySeconds >= 60)
        {
            return $"{FormatPlaytime(stats.PlaySeconds)} в игре";
        }

        return string.Empty;
    }

    private readonly Dictionary<string, ImageSource> _modpackIconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Обложка сборки из каталога. Грузится асинхронно самим WPF; ошибки сети/формата гасим,
    /// тогда в карточке остаётся буквенная заглушка.
    /// </summary>
    private ImageSource? TryLoadModpackIcon(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (_modpackIconCache.TryGetValue(url, out var cached))
        {
            return cached;
        }

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = 104;
            image.EndInit();
            image.DownloadFailed += (_, _) => AppendLog($"Modpack icon download failed: {url}");
            image.DecodeFailed += (_, _) => AppendLog($"Modpack icon decode failed: {url}");

            _modpackIconCache[url] = image;
            return image;
        }
        catch (Exception exception)
        {
            AppendLog($"Modpack icon unavailable ({url}): {exception.Message}");
            return null;
        }
    }

    public sealed record ModpackListItem(
        string Id,
        string Name,
        string Description,
        Visibility DescriptionVisibility,
        string InitialLetter,
        ImageSource? IconSource,
        Visibility IconVisibility,
        string StatusText,
        Media.Brush StatusBrush,
        Visibility StatusVisibility,
        string PlaytimeText,
        Visibility PlaytimeVisibility,
        Media.Brush BackgroundBrush,
        Media.Brush BorderBrushColor);

    private LauncherManifest CreateArchiveModeLaunchManifest()
        => LauncherManifestFactory.CreateArchiveModeLaunchManifest(_modpackManifest, _configuration);

    private async Task<InstallOperationTelemetry> EnsureGameFilesAsync(bool forceArchiveInstall = false, string? operationId = null, string trigger = "manual")
    {
        if (_configuration is null)
        {
            throw new InvalidOperationException("Launcher configuration is not loaded.");
        }

        SaveUserSettings();
        // Время операции теперь измеряет сам оркестратор и возвращает в результате.
        // В режиме каталога ModpackManifestUrl пуст, но манифест выбранной сборки загружен — это тоже archive-режим.
        var useArchiveMode = _modpackManifest is not null || _configuration.UsesDirectModpackArchive();
        var mode = useArchiveMode ? "archive" : "sync";
        _ = TrackTelemetryAsync("install_started", new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["force"] = forceArchiveInstall,
            ["trigger"] = trigger,
            ["operationId"] = operationId
        });

        if (useArchiveMode)
        {
            EnsureEnoughDiskSpace();
        }

        // Порядок действий переехал в ядро (GameInstallOrchestrator) — окну остаётся показать
        // результат. Тексты и события телеметрии сохранены прежними.
        var outcome = await new GameInstallOrchestrator(_httpClient).EnsureGameFilesAsync(
            _configuration,
            _modpackManifest,
            _manifest,
            _userSettings,
            new GameInstallCallbacks(
                AppendLog,
                ScaleProgress(0, 45),
                ScaleProgress(45, 100),
                new Progress<FileSyncProgress>(UpdateProgress),
                SyncOptionalModsAsync),
            forceArchiveInstall,
            useArchiveMode ? null : CreateEffectiveConfiguration());

        LauncherProgressBar.Value = 100;

        if (useArchiveMode)
        {
            SetStatus(outcome.StatusText);
            FooterTextBlock.Text = outcome.StatusText;
            _ = TrackTelemetryAsync("install_completed", new Dictionary<string, object?>
            {
                ["mode"] = outcome.Mode,
                ["installed"] = outcome.Installed,
                ["durationMs"] = outcome.DurationMs,
                ["force"] = forceArchiveInstall,
                ["trigger"] = trigger,
                ["operationId"] = operationId,
                ["changedFiles"] = outcome.ChangedFiles
            });
            UpdatePrimaryActionButton();
            return new InstallOperationTelemetry(outcome.Mode, outcome.Installed, outcome.DurationMs, outcome.ChangedFiles);
        }

        // В режиме синхронизации строка состояния короткая, а в подвале — с числом файлов.
        SetStatus("Сборка обновлена");
        FooterTextBlock.Text = outcome.StatusText;
        _ = TrackTelemetryAsync("install_completed", new Dictionary<string, object?>
        {
            ["mode"] = "sync",
            ["downloadedFiles"] = outcome.ChangedFiles,
            ["skippedFiles"] = outcome.SkippedFiles,
            ["durationMs"] = outcome.DurationMs,
            ["force"] = forceArchiveInstall,
            ["trigger"] = trigger,
            ["operationId"] = operationId,
            ["changedFiles"] = outcome.ChangedFiles
        });
        UpdatePrimaryActionButton();
        return new InstallOperationTelemetry("sync", outcome.Installed, outcome.DurationMs, outcome.ChangedFiles);
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
            _modpackManifest?.Runtime.MemoryMbMax ?? 16384,
            _configuration.IsMultiModpackCatalog ? _modpackManifest?.Modpack.Id : null)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            var username = GetUsername();
            _userSettings = window.Settings;
            _userSettings.Username = username;
            SaveUserSettings();
            LauncherThemeBrushes.ApplyTheme(Resources, _userSettings.ThemeId);
            _soundService.Enabled = _userSettings.SoundEnabled;
            RefreshProfileUi();
            SetActiveTab(_accountTabActive);
            RebuildModpackSidebar();
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
        // Пакет обновления — исполняемый код, который распакуется и запустится. Качаем только по HTTPS,
        // чтобы исключить MITM-подмену при незащищённом транспорте.
        if (!packageUri.IsFile && !packageUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Обновление лаунчера должно скачиваться по HTTPS.");
        }

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

    // Фоновые обновления (статус серверов, новости) НЕ должны блокироваться флагом _isBusy и не трогают
    // кнопки — иначе при вызове из переключения сборки (которое уже в RunSafeAsync) они бы не выполнились.
    private async Task RunBackgroundAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            AppendLog($"Background task error: {exception.Message}");
        }
    }

    // Реализация переехала в ядро вместе с разрешением манифеста — тем же кодом
    // пользуется Avalonia-версия.
    private static bool IsNetworkNameResolutionError(HttpRequestException exception)
        => ModpackManifestResolver.IsNetworkNameResolutionError(exception);

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

    // Авто-отправка крэш-бандла без участия игрока: при краше игры сам собирает пакет логов
    // (latest.log/stderr/hs_err/crash-report) и шлёт на сервер. Так причину видно даже если игрок
    // не нажмёт «Отправить в поддержку». Уважает отключённую телеметрию.
    private async Task AutoSendCrashBundleAsync(string installRoot, string errorTitle)
    {
        if (!_userSettings.TelemetryEnabled)
        {
            return;
        }

        try
        {
            EnsureTelemetryIdentity();
            var username = await Dispatcher.InvokeAsync(GetUsername);
            var launcherVersion = GetLauncherVersion();
            var modpackVersion = GetTelemetryModpackVersion();

            var package = await _supportLogService.CreatePackageAsync(
                installRoot,
                string.Empty,
                _userSettings.ClientId,
                username,
                launcherVersion,
                modpackVersion,
                errorTitle,
                CancellationToken.None);

            var upload = await _supportLogService.UploadAsync(
                SupportLogsUrl,
                package.Path,
                _userSettings.ClientId,
                username,
                launcherVersion,
                modpackVersion,
                CancellationToken.None);

            await Dispatcher.InvokeAsync(() => AppendLog(upload.Success
                ? $"Crash bundle auto-sent ({package.IncludedFileCount} files): {upload.Id}"
                : $"Crash bundle auto-send rejected: {upload.Message}"));
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() => AppendLog($"Crash bundle auto-send error: {exception.Message}"));
        }
    }

    private string WriteErrorLog(ErrorInfo errorInfo, Exception exception)
    {
        // Формат лога общий с Avalonia-версией (ErrorReport в ядре): саппорт-бандлы с Windows,
        // Linux и macOS разбираются одинаково.
        var installRoot = _configuration is null
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForgeLauncher")
            : GetEffectiveInstallRoot();

        var logPath = ErrorReport.Write(
            installRoot,
            errorInfo,
            exception,
            GetLauncherVersion(),
            GetTelemetryModpackVersion(),
            _configuration?.ModpackManifestUrl ?? "-",
            out var failure);

        if (failure is not null)
        {
            AppendLog($"Error log write failed: {failure}");
        }

        return logPath;
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
            CatalogUrl = _configuration.CatalogUrl,
            // Флаг мульти-сборки нужен launch-сервису, чтобы запуск брал ту же per-pack папку, что и установка.
            IsMultiModpackCatalog = _configuration.IsMultiModpackCatalog,
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

        // Папка по умолчанию для текущей сборки (с учётом подпапки при кастомном пути), но БЕЗ
        // её персонального override — это и есть «куда поставится, если поле оставить пустым».
        var catalogModpackId = _configuration.IsMultiModpackCatalog ? _modpackManifest?.Modpack.Id : null;
        return _userSettings.ResolveInstallRoot(
            _modpackManifest?.Install.Root ?? string.Empty,
            _configuration.GetDistributionRoot(),
            catalogModpackId,
            ignoreModpackOverride: true);
    }

    private string GetEffectiveInstallRoot()
        => ModpackCatalogLogic.ResolveEffectiveInstallRoot(_configuration, _modpackManifest, _userSettings);

    private void UpdateDisplayedInstallPath()
    {
    }

    private void UpdatePrimaryActionButton()
    {
        _primaryActionState = GetPrimaryActionState();
        PlayButton.Content = PrimaryActionResolver.GetButtonText(_primaryActionState);

        // Статус на карточке выбранной сборки берётся из этого же состояния — держим их в синхроне.
        RebuildModpackSidebar();
    }

    private PrimaryActionState GetPrimaryActionState()
    {
        _launcherUpdateAvailable = IsLauncherUpdateAvailable(out _launcherUpdateVersion);

        // Маркер читаем здесь: ядру передаём уже прочитанное значение, чтобы решение
        // оставалось чистой функцией и его можно было проверить тестами.
        string? installedVersion = null;
        var markerPath = GetArchiveVersionMarkerPath();
        if (File.Exists(markerPath))
        {
            installedVersion = File.ReadAllText(markerPath).Trim();
        }

        return PrimaryActionResolver.Resolve(
            _modpackManifest,
            _configuration,
            installedVersion,
            _launcherUpdateAvailable);
    }

    private string GetArchiveVersionMarkerPath()
    {
        return Path.Combine(GetEffectiveInstallRoot(), ".launcher", "modpack.version");
    }

    private string GetExpectedArchiveVersion()
        => PrimaryActionResolver.GetExpectedArchiveVersion(_modpackManifest, _configuration);

    // Сравнение версий и определение обновления лаунчера переехали в ядро.
    private bool IsLauncherUpdateAvailable(out string remoteVersion)
        => PrimaryActionResolver.IsLauncherUpdateAvailable(_modpackManifest, GetLauncherVersion(), out remoteVersion);

    private static bool IsRemoteVersionNewer(string localVersion, string remoteVersion)
        => PrimaryActionResolver.IsRemoteVersionNewer(localVersion, remoteVersion);

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
        SetNewsPlainText("Получаем последние записи с сайта.");
        OpenNewsButton.Visibility = Visibility.Collapsed;
        NewsImageBorder.Visibility = Visibility.Collapsed;

        try
        {
            using var cts = CreateLightRequestCts();
            var news = await _newsClient.GetNewsAsync(newsUrl, cts.Token);
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

    // Дефолтные сервера (tfgm) — используются, если у выбранной сборки нет своих серверов в манифесте.
    private static readonly ServerDefinition[] DefaultGameServers =
    [
        new("BL-MODERN-TFGM-1", "play.bl-modern.ru"),
        new("BL-MODERN-TFGM-2", "tfgm2.bl-modern.ru")
    ];

    // Сервера показываем по ВЫБРАННОЙ сборке (из её манифеста). При переключении сборки список
    // обновляется автоматически — RefreshServerStatsInBackground вызывается в SelectModpackAsync.
    private IReadOnlyList<ServerDefinition> GetGameServers()
    {
        var servers = _modpackManifest?.Servers;
        if (servers is { Count: > 0 })
        {
            var mapped = servers
                .Where(server => !string.IsNullOrWhiteSpace(server.Host))
                .Select(server => new ServerDefinition(
                    string.IsNullOrWhiteSpace(server.Name) ? server.Host : server.Name,
                    server.Host.Trim()))
                .ToList();
            if (mapped.Count > 0)
            {
                return mapped;
            }
        }

        return DefaultGameServers;
    }

    private void HomeTab_Click(object sender, RoutedEventArgs e) => SetActiveTab(account: false);
    private void AccountTab_Click(object sender, RoutedEventArgs e) => SetActiveTab(account: true);

    private bool _accountTabActive;

    // Цвет берём из активной темы (DynamicResource-ключи заполняет LauncherThemeCatalog), а не хардкодим,
    // иначе подсветка не совпадает с выбранной темой.
    private Media.Brush ResolveThemeBrush(string key, Media.Brush fallback) =>
        TryFindResource(key) as Media.Brush ?? fallback;

    // Переключение вкладок «Главная» / «Личный кабинет»: показываем нужную страницу и подсвечиваем активную.
    private void SetActiveTab(bool account)
    {
        if (HomePage is null || AccountPage is null)
        {
            return;
        }

        _accountTabActive = account;
        HomePage.Visibility = account ? Visibility.Collapsed : Visibility.Visible;
        AccountPage.Visibility = account ? Visibility.Visible : Visibility.Collapsed;

        var activeBg = ResolveThemeBrush("SoftButtonBackgroundBrush", ModpackSelectedBg);
        var activeBorder = ResolveThemeBrush("AccentBrush", ModpackSelectedBorder);
        AccountTabButton.Background = account ? activeBg : Media.Brushes.Transparent;
        AccountTabButton.BorderBrush = account ? activeBorder : Media.Brushes.Transparent;
        HomeTabButton.Background = account ? Media.Brushes.Transparent : activeBg;
        HomeTabButton.BorderBrush = account ? Media.Brushes.Transparent : activeBorder;

        // Кабинет всегда открывается с профиля, а не с середины списка достижений. ScrollToTop
        // сразу после смены Visibility не срабатывает (у ScrollViewer ещё нет раскладки), поэтому
        // откладываем до готовности layout.
        if (account)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => AccountScrollViewer?.ScrollToTop());
            // Подтягиваем достижения и скин с сайта (внутри троттлинг / проверка смены ника).
            _ = RefreshSiteAchievementsAsync();
            _ = RefreshSkinAsync();
        }

        // Мягкое появление страницы вместо мгновенной подмены.
        FadeIn(account ? AccountPage : HomePage);
    }

    // Шаг колеса в кабинете. По умолчанию WPF прокручивает три «строки» (~50px) за щелчок, и на
    // длинной странице с ачивками это ощущается рывками — берём шаг помельче, как в настройках.
    private const double AccountWheelScrollStep = 34d;

    // ================= НИК ИГРОКА =================
    // 44 игрока из 161 в саппорт-логах заходили под дефолтным «Player», и 29 из них так и не
    // исправили: поле ника жило только в кабинете, на главной его не было видно. Поэтому ник
    // показан внизу главного экрана, а запуск с дефолтным/некорректным ником блокируется.

    private const string DefaultUsername = "Player";

    /// <summary>
    /// Проверка ника нужна ровно для одного: не пустить на сервер заглушку «Player» (под ней играли
    /// 44 игрока из 161). Поэтому она не должна быть строже, чем сам сервер: точка и дефис в никах
    /// встречаются у реальных игроков (например Dr.J.Mengele — 112 часов на двух серверах), и запрет
    /// на них означал бы, что человек вообще не может запустить игру. Кириллицу не пускаем: игроки с
    /// такими никами на серверах не появляются, то есть это как раз неверно введённый ник.
    /// </summary>
    // Правила ника переехали в ядро — их использует и Avalonia-версия.
    private static bool IsUsernameValid(string? username) => UsernameRules.IsValid(username);

    private void UpdateUsernameIndicator()
    {
        if (UsernameIndicatorTextBlock is null || UsernameTextBox is null)
        {
            return;
        }

        var username = UsernameTextBox.Text?.Trim() ?? string.Empty;
        var valid = IsUsernameValid(username);

        UsernameIndicatorTextBlock.Text = valid ? $"Ник: {username}" : "Ник не выбран";
        UsernameIndicatorIcon.Text = valid ? "👤" : "⚠";
        UsernameIndicatorTextBlock.SetResourceReference(
            System.Windows.Controls.TextBlock.ForegroundProperty,
            valid ? "TextBrush" : "AccentBrush");

        if (UsernameHintTextBlock is not null)
        {
            UsernameHintTextBlock.Text = username.Length == 0
                ? "Введи ник — под ним тебя увидят на сервере."
                : valid
                    ? "3–16 символов: латиница, цифры, подчёркивание, тире, точка"
                    : username.Equals(DefaultUsername, StringComparison.OrdinalIgnoreCase)
                        ? "«Player» — это заглушка. Впиши свой ник, иначе на сервере будет каша."
                        : "Не подходит: нужно 3–16 символов — латиница, цифры, подчёркивание, тире, точка";
            UsernameHintTextBlock.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty,
                valid ? "MutedBrush" : "AccentBrush");
        }
    }

    private void UsernameIndicator_Click(object sender, RoutedEventArgs e) => FocusUsernameField();

    // ================= СКИН ИГРОКА =================
    // Тот же API, что у кабинета на сайте: GET публичный, загрузка требует пароль от аккаунта.
    // Пароль запрашивается в окне смены скина и нигде не сохраняется.

    private byte[]? _currentSkin;
    private string _currentSkinModel = "classic";
    private string _lastSkinNickname = string.Empty;

    private async Task RefreshSkinAsync(bool force = false)
    {
        var nickname = GetUsername();
        if (!IsUsernameValid(nickname))
        {
            ApplySkinToUi(null);
            return;
        }

        if (!force && nickname.Equals(_lastSkinNickname, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var cts = CreateLightRequestCts();
            var info = await _skinClient.GetInfoAsync(nickname, cts.Token);
            _currentSkinModel = info?.Model ?? "classic";
            var skin = info is null || info.HasSkin ? await _skinClient.GetSkinAsync(nickname, cts.Token) : null;

            _currentSkin = skin;
            _lastSkinNickname = nickname;
            ApplySkinToUi(skin);
            AppendLog(skin is null ? "Skin: not set." : $"Skin loaded ({_currentSkinModel}, {skin.Length} bytes).");
        }
        catch (Exception exception)
        {
            AppendLog($"Skin load failed: {exception.Message}");
        }
    }

    private void ApplySkinToUi(byte[]? skin)
    {
        if (SkinPreviewImage is null)
        {
            return;
        }

        var body = skin is null ? null : SkinRenderer.RenderBody(skin, _currentSkinModel == "slim");
        SkinPreviewImage.Source = body;
        SkinEmptyTextBlock.Visibility = body is null ? Visibility.Visible : Visibility.Collapsed;

        var head = skin is null ? null : SkinRenderer.RenderHead(skin);
        if (head is null)
        {
            ProfileAvatarSkinEllipse.Visibility = Visibility.Collapsed;
            ProfileAvatarTextBlock.Visibility = Visibility.Visible;
        }
        else
        {
            ProfileAvatarBrush.ImageSource = head;
            ProfileAvatarSkinEllipse.Visibility = Visibility.Visible;
            ProfileAvatarTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void ChangeSkinButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureUsernameSelected())
        {
            return;
        }

        var window = new SkinWindow(_skinClient, GetUsername(), _currentSkin, _currentSkinModel, _userSettings.ThemeId)
        {
            Owner = this
        };

        window.ShowDialog();
        if (window.SkinChanged)
        {
            _ = RunBackgroundAsync(() => RefreshSkinAsync(force: true));
            _ = TrackTelemetryAsync("skin_changed");
        }
    }

    // ================= ДОПОЛНИТЕЛЬНЫЕ МОДЫ =================
    // Ставятся только моды из каталога, одобренного администрацией (проверка SHA-256). Файлы
    // хранятся в .launcher/optional-mods (переживает обновление) и раскладываются в mods/ после
    // каждой установки — иначе их стирала бы очистка пак-папок при апдейте.

    private OptionalModsCatalog? _optionalModsCatalog;

    private async Task RefreshOptionalModsCatalogAsync()
    {
        _optionalModsCatalog = null;
        if (OptionalModsButton is not null)
        {
            OptionalModsButton.Visibility = Visibility.Collapsed;
        }

        var catalogUrl = _modpackManifest?.OptionalModsUrl;
        if (string.IsNullOrWhiteSpace(catalogUrl))
        {
            return;
        }

        try
        {
            using var cts = CreateLightRequestCts();
            // Локальный путь оставляем как есть: ResolveUri превратил бы его в file:// URI,
            // который HttpClient не умеет. Относительные адреса по-прежнему достраиваем от манифеста.
            var expanded = Environment.ExpandEnvironmentVariables(catalogUrl);
            var resolvedUrl = File.Exists(expanded)
                ? Path.GetFullPath(expanded)
                : _modpackManifest!.ResolveUri(catalogUrl).ToString();
            var catalog = await _optionalModsService.GetCatalogAsync(resolvedUrl, cts.Token);
            if (catalog is null || catalog.Mods.Count == 0)
            {
                AppendLog("Optional mods catalog is empty.");
                return;
            }

            _optionalModsCatalog = catalog;
            if (OptionalModsButton is not null)
            {
                OptionalModsButton.Visibility = Visibility.Visible;
            }

            AppendLog($"Optional mods available: {catalog.Mods.Count}");

            ApplyDefaultOptionalMods(catalog);
        }
        catch (Exception exception)
        {
            AppendLog($"Optional mods catalog failed: {exception.Message}");
        }
    }

    // Моды с defaultOn включаем один раз — дальше уважаем выбор игрока, даже если он их снял.
    private void ApplyDefaultOptionalMods(OptionalModsCatalog catalog)
    {
        var modpackId = GetOptionalModsKey();
        if (_userSettings.OptionalModsInitialized.Contains(modpackId, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var defaults = catalog.Mods.Where(mod => mod.DefaultOn).Select(mod => mod.Id).ToList();
        _userSettings.OptionalModsInitialized.Add(modpackId);
        if (defaults.Count > 0)
        {
            _userSettings.OptionalMods[modpackId] = defaults;
        }

        SaveUserSettings();
    }

    private string GetOptionalModsKey() =>
        !string.IsNullOrWhiteSpace(_selectedModpackId) ? _selectedModpackId : _modpackManifest?.Modpack.Id ?? "default";

    private IReadOnlyList<string> GetSelectedOptionalMods() =>
        _userSettings.OptionalMods.TryGetValue(GetOptionalModsKey(), out var selected) ? selected : [];

    private async void OptionalModsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_optionalModsCatalog is null || _configuration is null)
        {
            return;
        }

        var window = new OptionalModsWindow(
            _optionalModsService,
            _optionalModsCatalog,
            GetSelectedOptionalMods().ToList(),
            GetEffectiveInstallRoot(),
            _modpackManifest?.Modpack.Name ?? _configuration.LauncherName,
            _userSettings.ThemeId)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            _userSettings.OptionalMods[GetOptionalModsKey()] = window.SelectedIds.ToList();
            SaveUserSettings();
            SetStatus($"Дополнительные моды обновлены: включено {window.SelectedIds.Count}");
            AppendLog($"Optional mods selected: {string.Join(", ", window.SelectedIds)}");
            _ = TrackTelemetryAsync("optional_mods_changed", new Dictionary<string, object?>
            {
                ["count"] = window.SelectedIds.Count,
                ["modpackId"] = GetOptionalModsKey()
            });
        }
    }

    /// <summary>
    /// Возвращает выбранные моды в mods/ после установки/обновления сборки (очистка пак-папок их
    /// сносит). Ошибки не должны мешать запуску — логируем и идём дальше.
    /// </summary>
    private async Task SyncOptionalModsAsync()
    {
        if (_optionalModsCatalog is null)
        {
            return;
        }

        var selected = GetSelectedOptionalMods();
        if (selected.Count == 0)
        {
            return;
        }

        try
        {
            var root = GetEffectiveInstallRoot();
            var result = await _optionalModsService.SyncAsync(root, _optionalModsCatalog, selected, null, CancellationToken.None);
            AppendLog($"Optional mods synced: {result.Installed} installed, {result.Removed} removed.");
        }
        catch (Exception exception)
        {
            AppendLog($"Optional mods sync failed: {exception.Message}");
            SetStatus($"Не удалось добавить дополнительные моды: {exception.Message}");
        }
    }

    private void FocusUsernameField()
    {
        SetActiveTab(account: true);
        AccountScrollViewer?.ScrollToTop();
        UsernameTextBox.Focus();
        UsernameTextBox.SelectAll();
    }

    /// <summary>
    /// Пускать в игру только с осмысленным ником. Возвращает false, если запуск нужно прервать —
    /// игрока при этом перекидывает на поле ника с понятным объяснением.
    /// </summary>
    private bool EnsureUsernameSelected()
    {
        if (IsUsernameValid(UsernameTextBox.Text))
        {
            return true;
        }

        FocusUsernameField();
        UpdateUsernameIndicator();
        SetStatus("Сначала выбери ник — под ним тебя увидят на сервере");
        AppendLog("Launch blocked: username is not set.");
        return false;
    }

    private void AccountScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        var deltaSteps = e.Delta / 120d;
        var offset = AccountScrollViewer.VerticalOffset - deltaSteps * AccountWheelScrollStep;
        AccountScrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, AccountScrollViewer.ScrollableHeight));
    }

    private static void FadeIn(UIElement element)
    {
        element.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // ================= ПРОФИЛЬ ИГРОКА И ДОСТИЖЕНИЯ =================

    /// <summary>
    /// Записывает завершённую сессию в профиль, проверяет ачивки и обновляет вкладку.
    /// Вызывается из фонового мониторинга процесса игры, поэтому UI трогаем через Dispatcher.
    /// </summary>
    private void RecordPlaySession(TimeSpan runtime, bool crashed, DateTime startedAtLocal)
    {
        if (_configuration is null)
        {
            return;
        }

        try
        {
            var previousCrashed = _playerProfile.LastSessionCrashed;
            var modpackId = !string.IsNullOrWhiteSpace(_selectedModpackId)
                ? _selectedModpackId
                : _modpackManifest?.Modpack.Id ?? "default";
            var modpackName = _modpackManifest?.Modpack.Name ?? modpackId;

            _playerProfile.ClientId = _userSettings.ClientId;
            _playerProfile.RecordSession(modpackId, modpackName, runtime, crashed, startedAtLocal);
            _playerProfile.Save(_configuration.GetPlayerProfilePath());

            Dispatcher.Invoke(() =>
            {
                RefreshProfileUi();
                // После сессии статистика на сервере изменилась — тянем достижения заново и,
                // если что-то открылось, показываем тост.
                _ = RefreshSiteAchievementsAsync(force: true);
            });
        }
        catch (Exception exception)
        {
            Dispatcher.Invoke(() => AppendLog($"Profile update failed: {exception.Message}"));
        }
    }

    private void RefreshProfileUi()
    {
        UpdateProfileAvatar();
        RefreshProfileSummary();
        RefreshProfileStats();
        RefreshProfileModpacks();
        RefreshAchievements();
    }

    private void UpdateProfileAvatar()
    {
        if (ProfileAvatarTextBlock is null)
        {
            return;
        }

        var name = UsernameTextBox?.Text?.Trim();
        ProfileAvatarTextBlock.Text = string.IsNullOrEmpty(name)
            ? "?"
            : char.ToUpperInvariant(name[0]).ToString();
    }

    private void RefreshProfileSummary()
    {
        if (ProfileSummaryTextBlock is null)
        {
            return;
        }

        var parts = new List<string>();

        // Сначала то, что пришло с сайта: уровень, XP до следующего и место по времени.
        if (_siteLevel is not null)
        {
            parts.Add($"{_siteLevel.Title} · ур. {_siteLevel.Level}");
            parts.Add($"до {_siteLevel.Level + 1} ур.: {_siteLevel.Need - _siteLevel.Into} XP");
        }

        if (_playerProfile.TotalLaunches == 0 && _siteLevel is null)
        {
            ProfileSummaryTextBlock.Text = IsUsernameValid(UsernameTextBox?.Text)
                ? "Статистика появится после первого запуска игры."
                : "Укажи ник — и здесь появятся твои достижения с сайта.";
            return;
        }
        var favorite = _playerProfile.FavoriteModpackName;
        if (!string.IsNullOrWhiteSpace(favorite))
        {
            parts.Add($"Любимая сборка: {favorite}");
        }

        if (_playerProfile.FirstLaunchUtc is { } first)
        {
            parts.Add($"С нами с {first.ToLocalTime():dd.MM.yyyy}");
        }

        if (_playerProfile.LongestSessionSeconds > 0)
        {
            parts.Add($"Самая долгая сессия: {FormatPlaytime(_playerProfile.LongestSessionSeconds)}");
        }

        ProfileSummaryTextBlock.Text = string.Join("  ·  ", parts);
    }

    private void RefreshProfileStats()
    {
        if (ProfileStatsPanel is null)
        {
            return;
        }

        var tiles = new List<ProfileStatTile>();

        // Первыми — данные с сайта: уровень и достижения там же, что в кабинете.
        if (_siteLevel is not null)
        {
            tiles.Add(new($"ур. {_siteLevel.Level}", _siteLevel.Title));
            tiles.Add(new($"{_siteAchievements.Count(a => a.Earned)} / {_siteAchievements.Count}", "достижений"));
            tiles.Add(new(_siteLevel.TotalXp.ToString("N0"), "XP всего"));
        }

        tiles.Add(new(FormatPlaytime(_playerProfile.TotalPlaySeconds), "в игре через лаунчер"));
        tiles.Add(new(_playerProfile.TotalLaunches.ToString(), "запусков игры"));
        tiles.Add(new(FormatDays(_playerProfile.GetLiveStreakDays()), "дней подряд"));
        tiles.Add(new(_playerProfile.DistinctModpackCount.ToString(), "сборок опробовано"));

        ProfileStatsPanel.ItemsSource = tiles;
    }

    private void RefreshProfileModpacks()
    {
        if (ProfileModpackPanel is null || ProfileModpackHeader is null)
        {
            return;
        }

        var played = _playerProfile.Modpacks
            .Where(pair => pair.Value.PlaySeconds > 0)
            .OrderByDescending(pair => pair.Value.PlaySeconds)
            .ToList();

        if (played.Count == 0)
        {
            ProfileModpackHeader.Visibility = Visibility.Collapsed;
            ProfileModpackPanel.ItemsSource = null;
            return;
        }

        var max = played[0].Value.PlaySeconds;
        ProfileModpackHeader.Visibility = Visibility.Visible;
        ProfileModpackPanel.ItemsSource = played
            .Select(pair => new ProfileModpackRow(
                string.IsNullOrWhiteSpace(pair.Value.Name) ? pair.Key : pair.Value.Name,
                FormatPlaytime(pair.Value.PlaySeconds),
                max <= 0 ? 0 : pair.Value.PlaySeconds * 100.0 / max))
            .ToList();
    }

    // Достижения берём с сайта: там они считаются из игровой статистики (часы, убийства, блоки),
    // и лаунчер показывает ровно то же, что кабинет. Пока данных нет — панель пустая с подсказкой.
    private IReadOnlyList<SiteAchievement> _siteAchievements = [];

    private void RefreshAchievements()
    {
        if (AchievementsPanel is null || AchievementsCounterTextBlock is null)
        {
            return;
        }

        var accent = ResolveThemeBrush("AccentBrush", Media.Brushes.Goldenrod);
        var muted = ResolveThemeBrush("MutedBrush", Media.Brushes.Gray);
        var stroke = ResolveThemeBrush("StrokeBrush", Media.Brushes.DimGray);

        var cards = _siteAchievements
            .Select((achievement, index) => new AchievementCard(
                achievement.Icon,
                achievement.Name,
                achievement.Description,
                achievement.Earned
                    ? $"Получено · +{achievement.Xp} XP"
                    : FormatAchievementProgress(achievement),
                achievement.Progress * 100,
                achievement.Earned ? 1.0 : 0.55,
                achievement.Earned ? accent : muted,
                achievement.Earned ? accent : stroke,
                $"{achievement.Name}: {achievement.Description} (+{achievement.Xp} XP)",
                achievement.Earned,
                index))
            // Полученные — вперёд, дальше по близости к цели: видно, что вот-вот откроется.
            .OrderByDescending(card => card.Unlocked)
            .ThenByDescending(card => card.Unlocked ? 0 : card.Percent)
            .ThenBy(card => card.Order)
            .ToList();

        AchievementsPanel.ItemsSource = cards;
        AchievementsCounterTextBlock.Text = cards.Count == 0
            ? "нет данных"
            : $"{cards.Count(card => card.Unlocked)} из {cards.Count}";
    }

    private static string FormatAchievementProgress(SiteAchievement achievement)
    {
        if (achievement.Group == "special")
        {
            return $"Не получено · +{achievement.Xp} XP";
        }

        var unit = string.IsNullOrEmpty(achievement.Unit) ? string.Empty : " " + achievement.Unit;
        return $"{FormatMetric(achievement.Current)} / {FormatMetric(achievement.Target)}{unit}";
    }

    private static string FormatMetric(double value) =>
        Math.Abs(value % 1) < 0.05 ? ((long)Math.Round(value)).ToString("N0") : value.ToString("0.#");

    /// <summary>
    /// Тянет достижения игрока с сайта и перерисовывает вкладку. Троттлинг на 5 минут, чтобы
    /// переключение вкладок не долбило API; force — после игровой сессии, когда статистика точно
    /// изменилась.
    /// </summary>
    private async Task RefreshSiteAchievementsAsync(bool force = false)
    {
        var nickname = GetUsername();
        if (!IsUsernameValid(nickname))
        {
            _siteAchievements = [];
            _siteLevel = null;
            RefreshAchievements();
            return;
        }

        if (!force && DateTime.UtcNow - _lastSiteSyncUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        try
        {
            using var cts = CreateLightRequestCts();
            var response = await _playerStatsClient.GetAsync(nickname, cts.Token);
            if (response is null || !response.Found)
            {
                AppendLog($"Site achievements: player '{nickname}' not found.");
                _siteAchievements = [];
                _siteLevel = null;
                RefreshAchievements();
                RefreshProfileStats();
                RefreshProfileSummary();
                return;
            }

            _siteAchievements = SiteAchievementEngine.Build(response);
            _siteLevel = SiteAchievementEngine.BuildLevel(_siteAchievements);
            _lastSiteSyncUtc = DateTime.UtcNow;

            var unlocked = TrackNewSiteAchievements();
            RefreshAchievements();
            RefreshProfileStats();
            RefreshProfileSummary();
            AppendLog($"Site achievements synced: {_siteAchievements.Count(a => a.Earned)}/{_siteAchievements.Count}, level {_siteLevel.Level}.");

            foreach (var achievement in unlocked.Take(3))
            {
                EnqueueAchievementToast(new AchievementToastContent(achievement.Icon, achievement.Name, $"{achievement.Description} · +{achievement.Xp} XP"));
            }
        }
        catch (Exception exception)
        {
            AppendLog($"Site achievements sync failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Сравнивает полученные достижения с тем, что лаунчер уже видел, и возвращает новые.
    /// Первая синхронизация проходит молча — иначе игрок получил бы полсотни тостов подряд.
    /// </summary>
    private IReadOnlyList<SiteAchievement> TrackNewSiteAchievements()
    {
        var earned = _siteAchievements.Where(achievement => achievement.Earned).ToList();
        var firstSync = !_playerProfile.SiteSynced;
        var fresh = earned
            .Where(achievement => !_playerProfile.SiteAchievements.ContainsKey(achievement.Id))
            .ToList();

        foreach (var achievement in earned)
        {
            _playerProfile.SiteAchievements.TryAdd(achievement.Id, DateTime.UtcNow);
        }

        _playerProfile.SiteSynced = true;
        _playerProfile.ClientId = _userSettings.ClientId;
        TrySaveProfile();

        return firstSync ? [] : fresh;
    }

    private void TrySaveProfile()
    {
        try
        {
            if (_configuration is not null)
            {
                _playerProfile.Save(_configuration.GetPlayerProfilePath());
            }
        }
        catch (Exception exception)
        {
            AppendLog($"Profile save failed: {exception.Message}");
        }
    }

    private void EnqueueAchievementToast(AchievementToastContent content)
    {
        _achievementToastQueue.Enqueue(content);
        if (!_achievementToastRunning)
        {
            _ = ShowAchievementToastsAsync();
        }
    }

    /// <summary>Показывает накопленные ачивки по очереди, чтобы тосты не наезжали друг на друга.</summary>
    private async Task ShowAchievementToastsAsync()
    {
        if (AchievementToast is null)
        {
            return;
        }

        _achievementToastRunning = true;
        try
        {
            while (_achievementToastQueue.Count > 0)
            {
                var content = _achievementToastQueue.Dequeue();
                AchievementToastIcon.Text = content.Icon;
                AchievementToastTitle.Text = content.Title;
                AchievementToastDescription.Text = content.Description;

                AchievementToast.Visibility = Visibility.Visible;
                _soundService.PlayAchievement();
                AnimateToast(show: true);
                await Task.Delay(TimeSpan.FromSeconds(4.5));
                AnimateToast(show: false);
                await Task.Delay(TimeSpan.FromMilliseconds(320));
                AchievementToast.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception exception)
        {
            AppendLog($"Achievement toast failed: {exception.Message}");
        }
        finally
        {
            _achievementToastRunning = false;
        }
    }

    private void AnimateToast(bool show)
    {
        var duration = TimeSpan.FromMilliseconds(show ? 280 : 300);
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };

        AchievementToast.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = show ? 1 : 0,
            Duration = duration,
            EasingFunction = ease
        });

        AchievementToastTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = show ? 0 : 18,
            Duration = duration,
            EasingFunction = ease
        });
    }

    /// <summary>Короткое «нажатие» главной кнопки — тактильная отдача на клик.</summary>
    private void AnimatePlayButtonPress()
    {
        if (PlayButtonScale is null)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            To = 0.96,
            Duration = TimeSpan.FromMilliseconds(90),
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        PlayButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        PlayButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static string FormatPlaytime(long seconds)
    {
        if (seconds < 60)
        {
            return "0 мин";
        }

        if (seconds < 3600)
        {
            return $"{seconds / 60} мин";
        }

        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        return minutes == 0 ? $"{hours} ч" : $"{hours} ч {minutes} мин";
    }

    private static string FormatDays(int days) => days.ToString();

    public sealed record AchievementToastContent(string Icon, string Title, string Description);

    public sealed record ProfileStatTile(string Value, string Caption);

    public sealed record ProfileModpackRow(string Name, string TimeText, double Percent);

    public sealed record AchievementCard(
        string Icon,
        string Title,
        string Description,
        string ProgressText,
        double Percent,
        double CardOpacity,
        Media.Brush TitleBrush,
        Media.Brush BorderBrushColor,
        string Tooltip,
        bool Unlocked,
        int Order);

    private static readonly Media.Brush ServerOnlineBrush = CreateFrozenBrush(0x3F, 0xB9, 0x50);
    private static readonly Media.Brush ServerOfflineBrush = CreateFrozenBrush(0x6E, 0x76, 0x81);

    private void ServerStatsTimer_Tick(object? sender, EventArgs e) => RefreshServerStatsInBackground();

    private int _serverRefreshGeneration;

    // Обновляет статус серверов В ФОНЕ: переключение сборки не должно висеть на пинге серверов
    // (особенно когда сервер недоступен — пинг ждёт таймаут). Устаревший результат отбрасывается по
    // generation, чтобы при быстром переключении не показать карточки предыдущей сборки.
    private void RefreshServerStatsInBackground(bool showLoading = false)
    {
        var servers = GetGameServers();
        var generation = ++_serverRefreshGeneration;
        if (showLoading)
        {
            ServerListPanel.ItemsSource = null;
            ServerSubtitleTextBlock.Text = "Получаем статус серверов…";
        }

        _ = RunBackgroundAsync(async () =>
        {
            var items = await Task.WhenAll(servers.Select(GetServerStatusAsync));
            if (generation != _serverRefreshGeneration)
            {
                return; // пользователь уже переключил сборку — не перетираем актуальные карточки
            }

            ServerListPanel.ItemsSource = items;
            UpdateServerSummary(items);
            if (!_serverStatsTimer.IsEnabled)
            {
                _serverStatsTimer.Start();
            }
        });
    }

    private async Task<ServerStatusItem> GetServerStatusAsync(ServerDefinition server)
    {
        try
        {
            // Пингуем сервер напрямую (Minecraft Server List Ping) — реальный онлайн каждого сервера,
            // без бэкенда/мода. SRV резолвится внутри пингера.
            using var cts = CreateLightRequestCts();
            var ping = await _serverPinger.PingAsync(server.Host, cts.Token);
            return BuildServerStatusItem(server, ping);
        }
        catch (Exception exception)
        {
            AppendLog($"Server ping failed ({server.Host}): {exception.Message}");
            return BuildServerStatusItem(server, null);
        }
    }

    private static ServerStatusItem BuildServerStatusItem(ServerDefinition server, MinecraftPingResult? ping)
    {
        // Сервер ответил на пинг = онлайн. Не ответил = недоступен.
        if (ping is null)
        {
            return new ServerStatusItem(server.DisplayName, "Не отвечает", "Недоступен", "—", "", ServerOfflineBrush, false, 0, server.Host);
        }

        var detail = ping.PlayersMax > 0 ? $"{server.Host} · {ping.PlayersOnline}/{ping.PlayersMax}" : server.Host;
        return new ServerStatusItem(server.DisplayName, detail, "Онлайн", ping.PlayersOnline.ToString(), PlayersCaption(ping.PlayersOnline), ServerOnlineBrush, true, ping.PlayersOnline, server.Host);
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
        int OnlinePlayers,
        string Host);

    // Единый источник новостей: новости общие для ВСЕХ сборок, поэтому newsUrl из манифеста конкретной
    // сборки не используется (иначе у сборок без newsUrl был бы 404, а у разных — разные новости).
    private const string CommonNewsUrl = "https://bl-modern.ru/api/rss.php";

    private string ResolveNewsUrl(ModpackManifest manifest) => CommonNewsUrl;

    private void ShowNewsItem(NewsItem newsItem, string newsFeedUrl)
    {
        NewsTitleTextBlock.Text = newsItem.Title;
        NewsDateTextBlock.Text = newsItem.CreatedAt == default
            ? string.Empty
            : newsItem.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        // Сайт присылает описание в Markdown — разбираем его, а картинку из разметки отдаём
        // штатному блоку изображения, если своей (enclosure/image_url) у новости нет.
        var body = MarkdownRenderer.ExtractFirstImage(newsItem.Description ?? string.Empty, out var markdownImage);
        if (string.IsNullOrWhiteSpace(body))
        {
            SetNewsPlainText("Описание новости не заполнено.");
        }
        else
        {
            SetNewsMarkdown(body);
        }

        _currentNewsUrl = ResolveOptionalNewsLink(newsItem.Url, newsFeedUrl);
        OpenNewsButton.Visibility = string.IsNullOrWhiteSpace(_currentNewsUrl) ? Visibility.Collapsed : Visibility.Visible;

        var imageSource = string.IsNullOrWhiteSpace(newsItem.ImageUrl) ? markdownImage : newsItem.ImageUrl;
        var imageUrl = ResolveOptionalNewsLink(imageSource, newsFeedUrl);
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
        SetNewsPlainText(message);
    }

    /// <summary>Служебное сообщение в блоке новостей (ошибка, «загружается») — без разметки.</summary>
    private void SetNewsPlainText(string message)
    {
        NewsDescriptionPanel.Children.Clear();
        var text = new System.Windows.Controls.TextBlock
        {
            Text = message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        text.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "MutedBrush");
        NewsDescriptionPanel.Children.Add(text);
    }

    private void SetNewsMarkdown(string markdown)
    {
        NewsDescriptionPanel.Children.Clear();
        try
        {
            foreach (var element in MarkdownRenderer.Render(markdown, OpenExternalUrl))
            {
                NewsDescriptionPanel.Children.Add(element);
            }
        }
        catch (Exception exception)
        {
            // Кривая разметка не должна ломать окно: показываем текст как есть.
            AppendLog($"News markdown render failed: {exception.Message}");
            SetNewsPlainText(markdown);
        }
    }

    // Реализация переехала в ядро: тем же правилом пользуется Avalonia-версия.
    private static string ResolveOptionalNewsLink(string value, string baseUrl)
        => NewsLinkResolver.Resolve(value, baseUrl);

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
            RecordPlaySession(runtime, crashed: process.ExitCode != 0, startedAtLocal: startedAt.ToLocalTime());
            // Analyze читает логи синхронно — уводим с UI-потока, чтобы не подвесить окно.
            var analysis = await Task.Run(() => CrashAnalyzerService.Analyze(installRoot, process.ExitCode));

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
                        ["exitCodeHex"] = analysis.ExitCodeHex,
                        ["exitCodeDescription"] = analysis.ExitCodeDescription,
                        ["runtimeSeconds"] = (int)runtime.TotalSeconds,
                        ["crashCategory"] = analysis.Category,
                        ["summary"] = analysis.Summary,
                        ["signature"] = analysis.Signature,
                        ["evidence"] = analysis.Evidence,
                        ["hasCrashReport"] = analysis.HasCrashReport,
                        ["hasHsErr"] = analysis.HasHsErr,
                        ["logTail"] = analysis.LogTail
                    });

                    _ = AutoSendCrashBundleAsync(installRoot, $"Краш игры ({analysis.Category})");

                    // Игра успела запуститься, но потом упала — пользователю тоже нужен анализ краша.
                    await Dispatcher.InvokeAsync(() =>
                    {
                        AppendLog($"Crash Assistant: {analysis.Summary}");
                        SetStatus("Minecraft crashed");
                        System.Windows.MessageBox.Show(this, analysis.Details, "Анализатор краша", MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }

                return;
            }

            _ = TrackTelemetryAsync("launch_failed", new Dictionary<string, object?>
            {
                ["launchAttemptId"] = launchAttemptId,
                ["stage"] = "early_exit",
                ["exitCode"] = process.ExitCode,
                ["exitCodeHex"] = analysis.ExitCodeHex,
                ["exitCodeDescription"] = analysis.ExitCodeDescription,
                ["runtimeSeconds"] = (int)runtime.TotalSeconds,
                ["installDurationMs"] = installDurationMs,
                ["summary"] = analysis.Summary,
                ["crashCategory"] = analysis.Category,
                ["signature"] = analysis.Signature,
                ["evidence"] = analysis.Evidence,
                ["hasCrashReport"] = analysis.HasCrashReport,
                ["hasHsErr"] = analysis.HasHsErr,
                ["logTail"] = analysis.LogTail,
                ["javaSource"] = javaSource
            });

            _ = AutoSendCrashBundleAsync(installRoot, $"Ранний выход игры ({analysis.Category})");

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

        _trayIconImage = LoadTrayIcon();
        _trayIcon = new WinForms.NotifyIcon
        {
            Text = "BL-modern TFGM",
            Icon = _trayIconImage,
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

        // Клонируем системную иконку, чтобы её можно было безопасно диспозить в Dispose()
        // (общий статический SystemIcons.Application диспозить нельзя).
        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
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
        // Снимаем иконку трея заранее: её скрытое окно WinForms иначе может пережить завершение WPF.
        try { _trayIcon?.Dispose(); _trayIcon = null; } catch { /* выход не должен падать из-за трея */ }
        Close(); // → OnClosed → Dispose() (таймеры, Discord-пайп, HttpClient)
        // Гарантируем завершение процесса, не полагаясь только на ShutdownMode: фоновые ожидания
        // (named pipe Discord, RegisterWaitForSingleObject) и скрытые окна трея раньше оставляли
        // Launcher.App висеть в диспетчере задач даже после выхода из трея.
        System.Windows.Application.Current?.Shutdown();
        Environment.Exit(0);
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
        _backgroundRotationTimer.Stop();
        _serverStatsTimer.Stop();
        _trayIcon?.Dispose();
        _trayIconImage?.Dispose();
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

    private static readonly object LogFileLock = new();
    private static string? _logFilePath;

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Debug.WriteLine(line);
        WriteLogLineToFile(line);
    }

    // Постоянный файловый лог: в Release-сборке Debug.WriteLine никуда не пишет,
    // поэтому диагностику обычных проблем без файла собрать невозможно.
    private static void WriteLogLineToFile(string line)
    {
        try
        {
            var path = ResolveLogFilePath();
            lock (LogFileLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Логирование не должно ронять приложение.
        }
    }

    private static string ResolveLogFilePath()
    {
        if (_logFilePath is not null)
        {
            return _logFilePath;
        }

        var logRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ForgeLauncher",
            ".launcher",
            "logs");
        Directory.CreateDirectory(logRoot);
        _logFilePath = Path.Combine(logRoot, $"launcher-{DateTime.Now:yyyyMMdd}.log");
        return _logFilePath;
    }

    private static CancellationTokenSource CreateLightRequestCts() =>
        new(TimeSpan.FromSeconds(LightRequestTimeoutSeconds));

    private async Task TrackTelemetryAsync(string eventName, Dictionary<string, object?>? properties = null)
    {
        if (!_userSettings.TelemetryEnabled)
        {
            return;
        }

        EnsureTelemetryIdentity();

        try
        {
            using var cts = CreateLightRequestCts();
            await _telemetryClient.SendEventAsync(
                TelemetryUrl,
                _userSettings.ClientId,
                GetLauncherVersion(),
                GetTelemetryModpackVersion(),
                // На Windows значение прежнее (OSVersion.VersionString), см. HostPlatform.OsDescription.
                HostPlatform.OsDescription,
                eventName,
                properties,
                cts.Token);
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

// PrimaryActionState переехал в ядро (Launcher.App.Services): состояние главной кнопки
// вычисляется из данных и одинаково нужно обоим интерфейсам.
