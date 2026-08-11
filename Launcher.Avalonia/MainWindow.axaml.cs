using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Platform;
using Launcher.App;
using Launcher.App.Configuration;
using Launcher.App.Models;
using Launcher.App.Platform;
using Launcher.App.Services;
using Launcher.App.Theming;
using Launcher.Avalonia.Rendering;
using Launcher.Avalonia.Theming;
using Launcher.Avalonia.ViewModels;
// Avalonia.Controls.Shapes.Path (фигура) конфликтует с System.IO.Path (пути).
// Фиксируем Path как работу с путями — фигура нужна только в разметке.
using Path = System.IO.Path;

namespace Launcher.Avalonia;

public partial class MainWindow : Window
{
    // Адреса те же, что и у WPF-версии: обе версии пишут в один приёмник.
    private const string SupportLogsUrl = "https://bl-modern.ru/api/support_logs.php";
    private const string TelemetryUrl = "https://bl-modern.ru/api/telemetry.php";

    // Сколько игра должна прожить, чтобы запуск считался удавшимся. Выход раньше — это провал
    // запуска (не докачались файлы, не та Java), позже — краш уже в игре.
    private const int LaunchSuccessThresholdSeconds = 45;

    // Discord Rich Presence: те же значения, что и в WPF-версии.
    private const string DiscordAppId = "1511335634533613598";
    private const string DiscordIdleDetails = "TerraFirmaGreg-Modern";
    private const string DiscordIdleState = "В лаунчере";
    private const string DiscordPlayingDetails = "TerraFirmaGreg-Modern";
    private const string DiscordPlayingState = "В игре";

    private readonly MainWindowViewModel _viewModel = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly LauncherSoundService _soundService = new();
    private readonly DiscordPresenceService _discordPresence = new(DiscordAppId, largeImageKey: "logo");

    // Профиль игрока (время в игре, сессии, уже показанные достижения) держим в памяти:
    // его правит и запись сессии, и синхронизация достижений.
    private PlayerProfile _playerProfile = new();

    // Тосты показываем по одному: два одновременно перекрыли бы друг друга.
    private readonly Queue<(string Icon, string Title, string Description)> _achievementToasts = new();
    private bool _achievementToastRunning;

    private LauncherConfiguration? _configuration;
    private UserSettings _userSettings = new();
    private ModpackManifest? _modpackManifest;
    private CatalogManifest? _catalog;
    private string? _selectedModpackId;
    private PrimaryActionState _primaryActionState = PrimaryActionState.Play;
    private OptionalModsCatalog? _optionalModsCatalog;
    // Установка и запуск идут долго: пока они не закончились, повторное нажатие кнопки
    // запустило бы вторую установку в ту же папку.
    private bool _busy;
    // Закрытие окна разрешено только через ExitApplication: иначе крестик просто прятал бы
    // окно, а приложение оставалось бы в памяти.
    private bool _allowClose;

    // Слайдшоу фона: источники картинок, текущий индекс и какой из двух слоёв сейчас виден.
    private IReadOnlyList<Func<Stream>> _backgroundImages = [];
    private int _backgroundIndex = -1;
    private bool _backgroundLayerAActive = true;
    private readonly DispatcherTimer _backgroundTimer = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Тема применяется до показа окна, иначе первый кадр отрисуется цветами Fluent.
        LauncherThemeBrushes.ApplyTheme(Resources, LauncherThemeCatalog.DefaultThemeId);

        ConfigureWindowChrome();
        LoadBackground();
        SelectTab(home: true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Размер считаем только после открытия: до этого момента сведения об экране
        // и коэффициент масштабирования ещё недоступны.
        ApplyResponsiveFixedWindowSize();

        // --error-demo показывает окно ошибки на живом примере. Ключ нужен потому, что ошибки
        // разметки Avalonia вылезают только в рантайме, а ронять установку на каждой ОС ради
        // проверки одного окна — дорого.
        if (Environment.GetCommandLineArgs().Any(arg => arg.Equals("--error-demo", StringComparison.OrdinalIgnoreCase)))
        {
            _ = ShowErrorDemoAsync();
            return;
        }

        // --screenshot=<папка>: снять каждое окно в PNG и выйти. Так вёрстку на macOS и Linux
        // видно из CI, не имея этих машин под рукой.
        var screenshotDir = Environment.GetCommandLineArgs()
            .FirstOrDefault(arg => arg.StartsWith("--screenshot=", StringComparison.OrdinalIgnoreCase));
        if (screenshotDir is not null)
        {
            _ = CaptureScreenshotsAsync(screenshotDir["--screenshot=".Length..]);
            return;
        }

        _ = InitializeAsync();
    }

    /// <summary>
    /// Загрузка данных при старте: настройки, каталог сборок, манифест выбранной сборки.
    /// Всю работу делают сервисы ядра — те же, что и в WPF-версии.
    /// </summary>
    private async Task InitializeAsync()
    {
        try
        {
            _configuration = LauncherConfiguration.Load(AppContext.BaseDirectory);
            _userSettings = UserSettings.Load(_configuration.GetUserSettingsPath());
            _playerProfile = PlayerProfile.Load(_configuration.GetPlayerProfilePath());
            _soundService.Enabled = _userSettings.SoundEnabled;

            LauncherThemeBrushes.ApplyTheme(Resources, _userSettings.ThemeId);
            SetUsername(_userSettings.Username);
            ShowProfileBadge();

            if (_configuration.UsesCatalog())
            {
                await InitializeCatalogAsync();
            }
            else if (_configuration.UsesModpackManifest())
            {
                await LoadSingleModpackAsync();
            }
            else
            {
                SetStatus("Не настроен ни каталог, ни манифест сборки");
            }

            _ = TrackTelemetryAsync("launcher_started");
            _ = TrackTelemetryAsync("system_info", BuildSystemInfoProperties());
            _ = InitializeDiscordPresenceAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"Ошибка запуска: {exception.Message}");
        }
    }

    private async Task InitializeCatalogAsync()
    {
        SetStatus("Загрузка каталога сборок...");
        try
        {
            _catalog = await _catalogClient().GetCatalogAsync(_configuration!.CatalogUrl);
        }
        catch (Exception exception)
        {
            // Каталог недоступен — откатываемся на одиночный манифест, как в WPF-версии.
            _configuration!.IsMultiModpackCatalog = false;
            if (_configuration.UsesModpackManifest())
            {
                await LoadSingleModpackAsync();
            }
            else
            {
                SetStatus($"Каталог недоступен: {exception.Message}");
            }

            return;
        }

        if (_catalog.Modpacks.Count == 0)
        {
            SetStatus("В каталоге пока нет доступных сборок");
            return;
        }

        _configuration!.IsMultiModpackCatalog = ModpackCatalogLogic.ShouldIsolateInstallRoots(_catalog.Modpacks);
        ShowModpackSelector(_configuration.IsMultiModpackCatalog);

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
        _userSettings.Save(_configuration!.GetUserSettingsPath());

        SetStatus($"Загрузка сборки: {entry.Name}");
        try
        {
            _modpackManifest = await new ModpackManifestClient(_httpClient).GetManifestAsync(entry.ManifestUrl);
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось загрузить сборку «{entry.Name}»: {exception.Message}");
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(entry.Name) ? _modpackManifest.Launcher.Title : entry.Name;
        this.FindControl<TextBlock>("ModpackDropdownText")!.Text = displayName;
        SetWindowTitle(_modpackManifest.Launcher.Title);

        ShowModpackDetails();
        RebuildModpackList();
        UpdatePrimaryActionButton();
        SetStatus("Сборка загружена");

        // Новости и статусы серверов грузим в фоне: они не должны задерживать показ сборки.
        _ = RefreshNewsAsync();
        _ = RefreshServersAsync();
        _ = RefreshOptionalModsCatalogAsync();
    }

    /// <summary>
    /// Пингует серверы выбранной сборки. Пинг идёт напрямую по протоколу Minecraft,
    /// поэтому показывает реальный онлайн без участия бэкенда.
    /// </summary>
    private async Task RefreshServersAsync()
    {
        var summary = this.FindControl<TextBlock>("ServerSummaryTextBlock")!;
        var servers = GameServerStatusService.GetServers(_modpackManifest);
        summary.Text = "Получаем статус серверов...";

        var pinger = new MinecraftServerPinger();
        var statuses = await Task.WhenAll(servers.Select(async server =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var ping = await pinger.PingAsync(server.Host, cts.Token);
                return GameServerStatusService.BuildStatus(server, ping);
            }
            catch
            {
                // Недоступный сервер — обычное дело, показываем как «не отвечает».
                return GameServerStatusService.BuildStatus(server, null);
            }
        }));

        _viewModel.Servers.Clear();
        foreach (var status in statuses)
        {
            _viewModel.Servers.Add(new ServerListItem
            {
                Name = status.Name,
                Host = status.Host,
                Detail = status.Detail,
                StatusText = status.StatusText,
                PlayersText = status.PlayersText,
                PlayersCaption = status.PlayersCaption,
                IsOnline = status.IsOnline,
                StatusBrush = status.IsOnline ? Brushes.MediumSeaGreen : FindBrush("MutedBrush", Brushes.Gray)
            });
        }

        summary.Text = GameServerStatusService.BuildSummary(statuses);
    }

    /// <summary>Запуск с подключением к выбранному серверу (quick play).</summary>
    private async void JoinServerButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string host } && !string.IsNullOrWhiteSpace(host))
        {
            await StartGameAsync(host);
        }
    }

    /// <summary>Загружает последнюю новость и рисует её разметку.</summary>
    private async Task RefreshNewsAsync()
    {
        var titleText = this.FindControl<TextBlock>("NewsTitleTextBlock")!;
        var dateText = this.FindControl<TextBlock>("NewsDateTextBlock")!;
        var panel = this.FindControl<StackPanel>("NewsDescriptionPanel")!;

        var newsUrl = _modpackManifest?.Launcher.NewsUrl;
        if (string.IsNullOrWhiteSpace(newsUrl))
        {
            titleText.Text = "Новости недоступны";
            return;
        }

        try
        {
            var items = await new NewsClient(_httpClient).GetNewsAsync(newsUrl);
            var latest = items.FirstOrDefault();
            if (latest is null)
            {
                titleText.Text = "Новостей пока нет";
                return;
            }

            titleText.Text = latest.Title;
            dateText.Text = latest.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

            // Первая картинка показывается отдельным блоком сверху, а не внутри текста.
            var body = MarkdownParser.ExtractFirstImage(latest.Description, out var inlineImageUrl);
            var imageSource = string.IsNullOrWhiteSpace(latest.ImageUrl) ? inlineImageUrl : latest.ImageUrl;

            // Адреса в ленте бывают относительными — без приведения к абсолютным картинка
            // не грузится, а кнопка «Читать» ведёт в никуда.
            var imageUrl = NewsLinkResolver.Resolve(imageSource, newsUrl);

            panel.Children.Clear();
            foreach (var control in MarkdownView.Render(body, this, OpenExternalLink))
            {
                panel.Children.Add(control);
            }

            await ShowNewsImageAsync(imageUrl);

            var newsLink = NewsLinkResolver.Resolve(latest.Url, newsUrl);
            var openButton = this.FindControl<Button>("OpenNewsButton")!;
            openButton.IsVisible = !string.IsNullOrWhiteSpace(newsLink);
            openButton.Tag = newsLink;
        }
        catch (Exception exception)
        {
            titleText.Text = "Не удалось загрузить новости";
            dateText.Text = exception.Message;
        }
    }

    private async Task ShowNewsImageAsync(string imageUrl)
    {
        var border = this.FindControl<Border>("NewsImageBorder")!;
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            border.IsVisible = false;
            return;
        }

        try
        {
            var uri = _modpackManifest is not null ? _modpackManifest.ResolveUri(imageUrl) : new Uri(imageUrl);
            await using var stream = await _httpClient.GetStreamAsync(uri);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;

            this.FindControl<Image>("NewsImage")!.Source = new Bitmap(buffer);
            border.IsVisible = true;
        }
        catch
        {
            // Картинка не обязательна — текст новости важнее.
            border.IsVisible = false;
        }
    }

    /// <summary>Открывает ссылку в браузере пользователя средствами самой системы.</summary>
    private void OpenExternalLink(string url)
    {
        try
        {
            // UseShellExecute — единственный способ, работающий и на Windows, и на Linux, и на macOS.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось открыть ссылку: {exception.Message}");
        }
    }

    /// <summary>Одиночный режим: манифест берётся напрямую, каталога нет.</summary>
    private async Task LoadSingleModpackAsync()
    {
        SetStatus("Загрузка манифеста сборки...");
        var resolution = await new ModpackManifestResolver(new ModpackManifestClient(_httpClient))
            .ResolveAsync(_configuration!.ModpackManifestUrl, _configuration.GetCachedModpackManifestPath());

        _modpackManifest = resolution.Manifest;
        SetWindowTitle(_modpackManifest.Launcher.Title);
        ShowModpackSelector(false);

        ShowModpackDetails();
        UpdatePrimaryActionButton();
        SetStatus(resolution.Source == ModpackManifestSource.Remote
            ? "Сборка загружена"
            : $"{resolution.Reason} — используется сохранённый манифест");
    }

    /// <summary>
    /// Название и версия сборки. Как в WPF-версии: в подвале — «Название Версия», а крупный
    /// заголовок показывается только в одиночном режиме, иначе он дублировал бы выпадающий список.
    /// </summary>
    private void ShowModpackDetails()
    {
        if (_modpackManifest is null)
        {
            return;
        }

        var modpack = _modpackManifest.Modpack;
        this.FindControl<TextBlock>("ServerTitleTextBlock")!.Text = modpack.Name;
        this.FindControl<TextBlock>("FooterTextBlock")!.Text = $"{modpack.Name} {modpack.Version}";
    }

    /// <summary>
    /// Показывает либо выпадающий список сборок (каталог), либо крупный заголовок (одиночный режим).
    /// </summary>
    private void ShowModpackSelector(bool useCatalog)
    {
        this.FindControl<ToggleButton>("ModpackDropdownButton")!.IsVisible = useCatalog;
        this.FindControl<TextBlock>("ServerTitleTextBlock")!.IsVisible = !useCatalog;
    }

    private void RebuildModpackList()
    {
        if (_catalog is null)
        {
            return;
        }

        _viewModel.Modpacks.Clear();
        foreach (var entry in _catalog.Modpacks)
        {
            var state = ModpackCatalogLogic.GetInstallState(entry.Id, _selectedModpackId, _primaryActionState, _userSettings);
            _viewModel.Modpacks.Add(new ModpackListItem
            {
                Id = entry.Id,
                Name = entry.Name,
                Description = entry.Description,
                StatusText = ModpackCatalogLogic.GetInstallStateText(state),
                StatusBrush = ResolveStateBrush(state)
            });
        }
    }

    private IBrush ResolveStateBrush(ModpackInstallState state) => state switch
    {
        ModpackInstallState.UpdateAvailable => FindBrush("AccentBrush", Brushes.Goldenrod),
        ModpackInstallState.Installed => Brushes.MediumSeaGreen,
        _ => FindBrush("MutedBrush", Brushes.Gray)
    };

    private IBrush FindBrush(string key, IBrush fallback)
        => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;

    /// <summary>
    /// Подпись главной кнопки. Состояние вычисляет ядро — тем же кодом, что и WPF-версия.
    /// </summary>
    private void UpdatePrimaryActionButton()
    {
        string? installedVersion = null;
        var installRoot = ModpackCatalogLogic.ResolveEffectiveInstallRoot(_configuration, _modpackManifest, _userSettings);
        var marker = ModpackInstallMarker.GetPath(installRoot);
        if (File.Exists(marker))
        {
            installedVersion = File.ReadAllText(marker).Trim();
        }

        var launcherUpdate = PrimaryActionResolver.IsLauncherUpdateAvailable(
            _modpackManifest, GetLauncherVersion(), out _);

        _primaryActionState = PrimaryActionResolver.Resolve(
            _modpackManifest, _configuration, installedVersion, launcherUpdate);

        this.FindControl<Button>("PlayButton")!.Content = PrimaryActionResolver.GetButtonText(_primaryActionState);
    }

    private static string GetLauncherVersion()
    {
        var versionFile = Path.Combine(AppContext.BaseDirectory, "launcher.version");
        if (File.Exists(versionFile))
        {
            var value = File.ReadAllText(versionFile).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    private CatalogClient _catalogClient() => new(_httpClient);

    /// <summary>
    /// В изолированном профиле пишем это прямо в заголовок окна: иначе тестовый запуск легко
    /// спутать с рабочим и решить, что лаунчер «потерял» настройки и сборку.
    /// </summary>
    private void ShowProfileBadge() => SetWindowTitle(this.FindControl<TextBlock>("WindowTitleTextBlock")!.Text ?? string.Empty);

    /// <summary>
    /// Заголовок окна с пометкой профиля. Вызывается везде, где заголовок меняется, —
    /// иначе загрузка сборки затёрла бы пометку.
    /// </summary>
    private void SetWindowTitle(string text)
    {
        var label = this.FindControl<TextBlock>("WindowTitleTextBlock")!;
        if (LauncherProfile.IsIsolated)
        {
            label.Text = $"{text}  ·  ТЕСТОВЫЙ ПРОФИЛЬ «{LauncherProfile.Name}»";
            label.Foreground = FindBrush("AccentBrush", Brushes.Goldenrod);
            Title = $"{text} — тестовый профиль";
            return;
        }

        label.Text = text;
        Title = text;
    }

    private void SetStatus(string text)
        => this.FindControl<TextBlock>("FooterTextBlock")!.Text = text;

    private void SetUsername(string username)
        => this.FindControl<TextBlock>("UsernameIndicatorTextBlock")!.Text =
            $"Ник: {(string.IsNullOrWhiteSpace(username) ? "Player" : username)}";

    /// <summary>
    /// Подбирает размер окна под рабочую область экрана и фиксирует его — перенос
    /// одноимённого метода из WPF-версии.
    /// </summary>
    /// <remarks>
    /// Отличие Avalonia: рабочая область экрана измеряется в ФИЗИЧЕСКИХ пикселях, а Width/Height
    /// окна — в логических. Без деления на <see cref="TopLevel.RenderScaling"/> на экране
    /// с масштабом 150% окно получалось в полтора раза больше экрана, и правая часть
    /// интерфейса (новости, кнопки действий) уезжала за край.
    /// </remarks>
    private void ApplyResponsiveFixedWindowSize()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var scaling = RenderScaling <= 0 ? 1 : RenderScaling;
        var workWidth = screen.WorkingArea.Width / scaling;
        var workHeight = screen.WorkingArea.Height / scaling;

        var target = GetForcedWindowSize() ?? SelectWindowSize(workWidth, workHeight);

        // Ограничения ставим ДО размера: иначе окно сначала примет старое значение,
        // а Min/Max тут же его переопределят.
        MinWidth = target.Width;
        MaxWidth = target.Width;
        MinHeight = target.Height;
        MaxHeight = target.Height;
        Width = target.Width;
        Height = target.Height;

        // Position задаётся в физических пикселях, поэтому логический размер возвращаем обратно.
        Position = new PixelPoint(
            screen.WorkingArea.X + (int)((screen.WorkingArea.Width - target.Width * scaling) / 2),
            screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - target.Height * scaling) / 2));

        // Диагностика вёрстки: `--diag` печатает, из каких чисел сложился размер окна.
        // Без неё разбираться, почему содержимое не влезает, приходится на глаз по скриншотам.
        if (Environment.GetCommandLineArgs().Contains("--diag", StringComparer.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(
                $"DIAG RenderScaling={RenderScaling} " +
                $"WorkingArea={screen.WorkingArea.Width}x{screen.WorkingArea.Height} " +
                $"ScreenScaling={screen.Scaling} " +
                $"workLogical={workWidth:F0}x{workHeight:F0} " +
                $"target={target.Width:F0}x{target.Height:F0} " +
                $"ClientSize={ClientSize.Width:F0}x{ClientSize.Height:F0}");
        }
    }

    /// <summary>
    /// Размер окна подбирается ПЛАВНО от рабочей области, а не ступенями. Раньше было четыре
    /// фиксированных пресета, из-за чего на разных мониторах интерфейс заметно «прыгал» и
    /// появлялись пустоты. Пропорции держим близко к 16:9 и вписываемся в рабочую область.
    /// </summary>
    private static Size SelectWindowSize(double workWidth, double workHeight)
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

        return new Size(Math.Min(width, availableWidth), Math.Min(height, availableHeight));
    }

    /// <summary>
    /// Отладочный размер окна: <c>--window-size=1040x600</c>. Нужен, чтобы проверять вёрстку под
    /// разные разрешения, не меняя разрешение экрана.
    /// </summary>
    private static Size? GetForcedWindowSize()
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
                return new Size(width, height);
            }
        }

        return null;
    }

    /// <summary>
    /// Рамка окна. На Windows рисуем свою, как в WPF-версии. На Linux и macOS оставляем
    /// системную: окно без нативных кнопок там ведёт себя непривычно и ломает жесты
    /// оконного менеджера, а на macOS ещё и лишается «светофора».
    /// </summary>
    private void ConfigureWindowChrome()
    {
        var titleBar = this.FindControl<Border>("CustomTitleBar")!;

        if (HostPlatform.IsWindows)
        {
            SystemDecorations = SystemDecorations.BorderOnly;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            titleBar.IsVisible = true;
            return;
        }

        SystemDecorations = SystemDecorations.Full;
        ExtendClientAreaToDecorationsHint = false;
        titleBar.IsVisible = false;
    }

    /// <summary>
    /// Фон окна — слайдшоу из скриншотов сборки, как в WPF: смена каждые 30 секунд
    /// с перекрёстным затуханием.
    /// </summary>
    /// <remarks>
    /// Картинки берутся из папки «скриншоты» рядом с исполняемым файлом, если она есть,
    /// иначе из вшитых в сборку — так их можно заменить, не пересобирая лаунчер.
    /// </remarks>
    private void LoadBackground()
    {
        _backgroundImages = ResolveBackgroundImages();
        if (_backgroundImages.Count == 0)
        {
            return;
        }

        ShowNextBackgroundImage();

        if (_backgroundImages.Count > 1)
        {
            _backgroundTimer.Interval = TimeSpan.FromSeconds(30);
            _backgroundTimer.Tick += (_, _) => ShowNextBackgroundImage();
            _backgroundTimer.Start();
        }
    }

    /// <summary>Список источников фона: внешняя папка приоритетнее вшитых картинок.</summary>
    private static IReadOnlyList<Func<Stream>> ResolveBackgroundImages()
    {
        var externalDirectory = Path.Combine(AppContext.BaseDirectory, "скриншоты");
        if (Directory.Exists(externalDirectory))
        {
            var files = Directory.EnumerateFiles(externalDirectory)
                .Where(path => Path.GetExtension(path) is
                    { } ext && (ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count > 0)
            {
                return files.Select<string, Func<Stream>>(path => () => File.OpenRead(path)).ToList();
            }
        }

        try
        {
            var assets = AssetLoader
                .GetAssets(new Uri("avares://Launcher.Avalonia/Assets/Screenshots"), null)
                .OrderBy(uri => uri.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (assets.Count > 0)
            {
                return assets.Select<Uri, Func<Stream>>(uri => () => AssetLoader.Open(uri)).ToList();
            }
        }
        catch
        {
            // Нет вшитых скриншотов — останется одиночный фон ниже.
        }

        // Запасной вариант: одна картинка, если скриншотов нет вовсе.
        return [() => AssetLoader.Open(new Uri("avares://Launcher.Avalonia/Assets/background.png"))];
    }

    private void ShowNextBackgroundImage()
    {
        if (_backgroundImages.Count == 0)
        {
            return;
        }

        _backgroundIndex = (_backgroundIndex + 1) % _backgroundImages.Count;

        try
        {
            using var stream = _backgroundImages[_backgroundIndex]();
            var brush = new ImageBrush(new Bitmap(stream))
            {
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };

            var layerA = this.FindControl<Rectangle>("BackgroundLayerA")!;
            var layerB = this.FindControl<Rectangle>("BackgroundLayerB")!;

            // Новую картинку кладём в скрытый слой и меняем прозрачность — переход
            // делает Transitions в разметке.
            var fadeIn = _backgroundLayerAActive ? layerB : layerA;
            var fadeOut = _backgroundLayerAActive ? layerA : layerB;

            fadeIn.Fill = brush;
            fadeIn.Opacity = 1;
            fadeOut.Opacity = 0;
            _backgroundLayerAActive = !_backgroundLayerAActive;
        }
        catch
        {
            // Битый файл фона не должен ронять окно.
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void MinimizeButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => ExitApplication();

    /// <summary>
    /// Прячет окно в трей — лаунчер продолжает работать, чтобы отследить завершение игры.
    /// </summary>
    private void MinimizeToTray()
    {
        (Application.Current as App)?.SetTrayVisible(true);
        Hide();
        ShowInTaskbar = false;
    }

    /// <summary>
    /// Возвращает окно из трея. Нужен после краша игры: разбор нельзя показывать поверх
    /// спрятанного окна — игрок его просто не увидит.
    /// </summary>
    private void RestoreFromTray()
    {
        (Application.Current as App)?.SetTrayVisible(false);
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Полный выход: снимаем иконку из трея, иначе она останется висеть.</summary>
    private void ExitApplication()
    {
        _allowClose = true;
        (Application.Current as App)?.SetTrayVisible(false);

        // Соединение с Discord держит именованный канал: без освобождения он остаётся
        // висеть до сборки мусора.
        _discordPresence.Dispose();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
            return;
        }

        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Крестик закрывает лаунчер целиком, но выход через ExitApplication должен пройти.
        if (!_allowClose)
        {
            e.Cancel = true;
            ExitApplication();
            return;
        }

        base.OnClosing(e);
    }

    private void HomeTab_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) => SelectTab(home: true);

    private async void AccountTab_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        SelectTab(home: false);
        // Данные кабинета обновляем при каждом открытии: ник и статистика могли измениться.
        try
        {
            await RefreshAccountAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось обновить кабинет: {exception.Message}");
        }
    }

    /// <summary>
    /// Переключение вкладок. Активная подсвечивается фоном — в WPF это делал тот же приём
    /// с назначением кисти напрямую, а не отдельным состоянием кнопки.
    /// </summary>
    private void SelectTab(bool home)
    {
        this.FindControl<Grid>("HomePage")!.IsVisible = home;
        this.FindControl<Grid>("AccountPage")!.IsVisible = !home;

        var active = this.TryFindResource("SoftButtonBackgroundBrush", out var brush) ? brush as IBrush : null;
        this.FindControl<Button>("HomeTabButton")!.Background = home ? active : Brushes.Transparent;
        this.FindControl<Button>("AccountTabButton")!.Background = home ? Brushes.Transparent : active;
    }

    private async void ModpackItem_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        this.FindControl<ToggleButton>("ModpackDropdownButton")!.IsChecked = false;
        if (sender is Button { Tag: string id } && !id.Equals(_selectedModpackId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await SelectModpackAsync(id);
            }
            catch (Exception exception)
            {
                SetStatus($"Ошибка выбора сборки: {exception.Message}");
            }
        }
    }

    private async void OptionalModsButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_configuration is null || _modpackManifest is null || _optionalModsCatalog is null)
        {
            return;
        }

        var modpackId = _modpackManifest.Modpack.Id;
        _userSettings.OptionalMods.TryGetValue(modpackId, out var selected);

        var dialog = new OptionalModsWindow(
            new OptionalModsService(_httpClient),
            _optionalModsCatalog,
            selected ?? [],
            ModpackCatalogLogic.ResolveEffectiveInstallRoot(_configuration, _modpackManifest, _userSettings),
            _modpackManifest.Modpack.Name,
            _userSettings.ThemeId);

        await dialog.ShowDialog(this);
        if (dialog.SelectedIds is null)
        {
            return;
        }

        _userSettings.OptionalMods[modpackId] = [.. dialog.SelectedIds];
        if (!_userSettings.OptionalModsInitialized.Contains(modpackId, StringComparer.OrdinalIgnoreCase))
        {
            _userSettings.OptionalModsInitialized.Add(modpackId);
        }

        _userSettings.Save(_configuration.GetUserSettingsPath());
        SetStatus($"Дополнительные моды сохранены: {dialog.SelectedIds.Count}");
    }

    /// <summary>
    /// Каталог одобренных модов. Пусто — кнопка «Моды» не показывается: это безопасный
    /// дефолт, как в WPF-версии.
    /// </summary>
    private async Task RefreshOptionalModsCatalogAsync()
    {
        var button = this.FindControl<Button>("OptionalModsButton")!;
        _optionalModsCatalog = null;
        button.IsVisible = false;

        var url = _modpackManifest?.OptionalModsUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var catalog = await new OptionalModsService(_httpClient).GetCatalogAsync(url);
            if (catalog is { Mods.Count: > 0 })
            {
                _optionalModsCatalog = catalog;
                button.IsVisible = true;
            }
        }
        catch
        {
            // Недоступный каталог модов не должен мешать игре — просто прячем кнопку.
        }
    }

    private async void SupportButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_configuration is null)
        {
            return;
        }

        var dialog = new SupportWindow(
            new SupportChatClient(_httpClient),
            SupportChatClient.DefaultEndpoint,
            _userSettings,
            _configuration.GetUserSettingsPath(),
            _userSettings.ClientId,
            _userSettings.Username,
            GetLauncherVersion(),
            _modpackManifest?.Modpack.Version ?? string.Empty,
            _userSettings.ThemeId);

        await dialog.ShowDialog(this);
    }

    private async void SettingsButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_configuration is null)
        {
            return;
        }

        var runtime = _modpackManifest?.Runtime;
        var dialog = new SettingsWindow(
            _userSettings,
            ModpackCatalogLogic.ResolveEffectiveInstallRoot(_configuration, _modpackManifest, _userSettings),
            runtime?.MemoryMbDefault ?? 4096,
            runtime?.MemoryMbMin ?? 1024,
            runtime?.MemoryMbMax ?? 12288);

        await dialog.ShowDialog(this);
        if (dialog.Result is null)
        {
            return;
        }

        // Ник хранится в тех же настройках, но меняется отдельным окном — переносим его вручную,
        // иначе «Сохранить» в настройках сбросил бы ник на значение по умолчанию.
        dialog.Result.Username = _userSettings.Username;
        dialog.Result.SelectedModpackId = _userSettings.SelectedModpackId;
        dialog.Result.ModpackInstallRoots = _userSettings.ModpackInstallRoots;
        dialog.Result.OptionalMods = _userSettings.OptionalMods;
        dialog.Result.OptionalModsInitialized = _userSettings.OptionalModsInitialized;
        dialog.Result.ClientId = _userSettings.ClientId;
        dialog.Result.SupportEmail = _userSettings.SupportEmail;

        _userSettings = dialog.Result;
        _userSettings.Save(_configuration.GetUserSettingsPath());

        LauncherThemeBrushes.ApplyTheme(Resources, _userSettings.ThemeId);
        // Галочку звука надо перенести в сам сервис, иначе она сохраняется, но ни на что не влияет.
        _soundService.Enabled = _userSettings.SoundEnabled;
        UpdatePrimaryActionButton();
        SetStatus("Настройки сохранены");

        _ = TrackTelemetryAsync("settings_saved", new Dictionary<string, object?>
        {
            ["memoryMb"] = _userSettings.MemoryMb,
            ["themeId"] = _userSettings.ThemeId,
            ["soundEnabled"] = _userSettings.SoundEnabled
        });

        if (dialog.RequestedIntegrityCheck)
        {
            await VerifyFilesAsync();
        }
    }

    /// <summary>
    /// «Проверить целостность»: переустанавливает файлы сборки поверх текущих. Чинит случай,
    /// когда часть файлов не докачалась или повреждена, а по версии всё «на месте».
    /// </summary>
    private async Task VerifyFilesAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await InstallGameFilesAsync(Guid.NewGuid().ToString("N"), "verify", force: true);
            UpdatePrimaryActionButton();
        }
        catch (Exception exception)
        {
            await ShowLauncherErrorAsync(exception);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OpenNewsButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrWhiteSpace(url))
        {
            _ = TrackTelemetryAsync("news_opened");
            OpenExternalLink(url);
        }
    }

    private async void UsernameIndicator_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => await FocusUsernameFieldAsync();

    /// <summary>
    /// Ник правится прямо в кабинете, как в WPF. Сохраняем только корректный —
    /// иначе игрок ушёл бы на сервер под «Player» или под пустым ником.
    /// </summary>
    private void UsernameTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_configuration is null || sender is not TextBox box)
        {
            return;
        }

        var value = (box.Text ?? string.Empty).Trim();
        var problem = UsernameRules.Describe(value);
        var hint = this.FindControl<TextBlock>("UsernameHintTextBlock")!;
        hint.Text = problem ?? "3–16 символов: латиница, цифры, подчёркивание, тире, точка";
        hint.Foreground = problem is null
            ? FindBrush("MutedBrush", Brushes.Gray)
            : FindBrush("AccentBrush", Brushes.Goldenrod);

        if (problem is not null)
        {
            return;
        }

        _userSettings.Username = value;
        _userSettings.Save(_configuration.GetUserSettingsPath());
        SetUsername(value);
        this.FindControl<TextBlock>("ProfileAvatarTextBlock")!.Text = value[..1].ToUpperInvariant();
    }

    private void LinkButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrWhiteSpace(url))
        {
            OpenExternalLink(url);
        }
    }

    private async void ChangeSkinButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!UsernameRules.IsValid(_userSettings.Username))
        {
            await FocusUsernameFieldAsync();
            SetStatus("Сначала выбери ник — скин привязан к нему");
            return;
        }

        byte[]? current = null;
        var model = "classic";
        try
        {
            var client = new SkinClient(_httpClient);
            var info = await client.GetInfoAsync(_userSettings.Username);
            model = info?.Model ?? "classic";
            current = info is null || info.HasSkin ? await client.GetSkinAsync(_userSettings.Username) : null;
        }
        catch
        {
            // Без текущего скина окно всё равно откроется — загрузить новый можно.
        }

        var dialog = new SkinWindow(new SkinClient(_httpClient), _userSettings.Username, current, model, _userSettings.ThemeId);
        await dialog.ShowDialog(this);

        if (dialog.SkinChanged)
        {
            await RefreshSkinAsync();
            SetStatus("Скин обновлён");
        }
    }

    /// <summary>
    /// Наполняет личный кабинет: аватар, ник, плитки статистики, время по сборкам
    /// и достижения с сайта.
    /// </summary>
    private async Task RefreshAccountAsync()
    {
        if (_configuration is null)
        {
            return;
        }

        var profile = PlayerProfile.Load(_configuration.GetPlayerProfilePath());

        this.FindControl<TextBox>("UsernameTextBox")!.Text = _userSettings.Username;
        this.FindControl<TextBlock>("ProfileAvatarTextBlock")!.Text =
            string.IsNullOrWhiteSpace(_userSettings.Username) ? "P" : _userSettings.Username[..1].ToUpperInvariant();

        // Достижения считает сайт из игровой статистики — лаунчер показывает ровно то же,
        // что и личный кабинет на сайте. Нет данных (нет сети или ник не найден) — панель пустая.
        IReadOnlyList<SiteAchievement> achievements = [];
        SiteLevel? level = null;
        if (UsernameRules.IsValid(_userSettings.Username))
        {
            try
            {
                var response = await new PlayerStatsClient(_httpClient).GetAsync(_userSettings.Username);
                if (response is not null)
                {
                    achievements = SiteAchievementEngine.Build(response);
                    level = SiteAchievementEngine.BuildLevel(achievements);

                    // Не больше трёх тостов подряд — как в WPF: остальное игрок увидит в списке.
                    foreach (var unlocked in TrackNewSiteAchievements(achievements).Take(3))
                    {
                        EnqueueAchievementToast(
                            unlocked.Icon, unlocked.Name, $"{unlocked.Description} · +{unlocked.Xp} XP");
                    }
                }
            }
            catch
            {
                // Статистика с сайта необязательна — локальные данные покажем в любом случае.
            }
        }

        _viewModel.ProfileStats.Clear();
        foreach (var tile in ProfileViewBuilder.BuildStatTiles(profile, level, achievements))
        {
            _viewModel.ProfileStats.Add(new StatTileItem { Value = tile.Value, Caption = tile.Caption });
        }

        var rows = ProfileViewBuilder.BuildModpackRows(profile);
        _viewModel.ProfileModpacks.Clear();
        foreach (var row in rows)
        {
            _viewModel.ProfileModpacks.Add(new ModpackTimeItem { Name = row.Name, TimeText = row.TimeText, Percent = row.Percent });
        }

        this.FindControl<TextBlock>("ProfileModpackHeader")!.IsVisible = rows.Count > 0;

        var accent = FindBrush("AccentBrush", Brushes.Goldenrod);
        var muted = FindBrush("MutedBrush", Brushes.Gray);
        var stroke = FindBrush("StrokeBrush", Brushes.DimGray);

        _viewModel.Achievements.Clear();
        foreach (var achievement in ProfileViewBuilder.SortForDisplay(achievements))
        {
            _viewModel.Achievements.Add(new AchievementItem
            {
                Icon = achievement.Icon,
                Title = achievement.Name,
                Description = achievement.Description,
                ProgressText = ProfileViewBuilder.BuildAchievementProgressText(achievement),
                Tooltip = $"{achievement.Name}: {achievement.Description} (+{achievement.Xp} XP)",
                Percent = ProfileViewBuilder.BuildAchievementPercent(achievement),
                CardOpacity = achievement.Earned ? 1 : 0.55,
                TitleBrush = achievement.Earned ? accent : muted,
                BorderBrush = achievement.Earned ? accent : stroke
            });
        }

        this.FindControl<TextBlock>("AchievementsCounterTextBlock")!.Text =
            $"{achievements.Count(a => a.Earned)} из {achievements.Count}";

        this.FindControl<TextBlock>("ProfileSummaryTextBlock")!.Text = profile.TotalLaunches > 0
            ? $"Запусков: {profile.TotalLaunches} · в игре {ProfileViewBuilder.FormatPlaytime(profile.TotalPlaySeconds)}"
            : "Статистика появится после первого запуска игры.";

        await RefreshSkinAsync();
    }

    /// <summary>
    /// Подтягивает скин игрока с сайта и рисует превью фигуры и аватарку.
    /// </summary>
    private async Task RefreshSkinAsync()
    {
        var preview = this.FindControl<Image>("SkinPreviewImage")!;
        var empty = this.FindControl<TextBlock>("SkinEmptyTextBlock")!;

        if (!UsernameRules.IsValid(_userSettings.Username))
        {
            preview.Source = null;
            empty.IsVisible = true;
            return;
        }

        try
        {
            var client = new SkinClient(_httpClient);
            var info = await client.GetInfoAsync(_userSettings.Username);
            var model = info?.Model ?? "classic";
            var bytes = info is null || info.HasSkin ? await client.GetSkinAsync(_userSettings.Username) : null;

            var body = SkinImage.RenderBody(bytes, model == "slim");
            preview.Source = body;
            empty.IsVisible = body is null;

            // Аватарка: голова из скина вместо буквы ника.
            var head = SkinImage.RenderHead(bytes);
            var avatarLetter = this.FindControl<TextBlock>("ProfileAvatarTextBlock")!;
            var avatarImage = this.FindControl<Ellipse>("ProfileAvatarSkin")!;
            if (head is null)
            {
                avatarImage.IsVisible = false;
                avatarLetter.IsVisible = true;
            }
            else
            {
                avatarImage.Fill = new ImageBrush(head) { Stretch = Stretch.UniformToFill };
                avatarImage.IsVisible = true;
                avatarLetter.IsVisible = false;
            }
        }
        catch
        {
            // Скин необязателен — без него кабинет работает как раньше.
            preview.Source = null;
            empty.IsVisible = true;
        }
    }

    /// <summary>
    /// Перекидывает в кабинет и ставит курсор в поле ника — так же, как WPF-версия.
    /// Отдельного окна для ника нет: он правится прямо в кабинете.
    /// </summary>
    private async Task FocusUsernameFieldAsync()
    {
        SelectTab(home: false);
        try
        {
            await RefreshAccountAsync();
        }
        catch
        {
            // Даже если статистика не подтянулась, к полю ника доступ дать надо.
        }

        this.FindControl<ScrollViewer>("AccountScrollViewer")?.ScrollToHome();
        var box = this.FindControl<TextBox>("UsernameTextBox")!;
        box.Focus();
        box.SelectAll();
    }

    /// <summary>
    /// Главная кнопка: установка, обновление или запуск игры — в зависимости от состояния.
    /// Всю работу делают сервисы ядра, окно только показывает ход и результат.
    /// </summary>
    private async void PlayButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => await StartGameAsync(null);

    /// <param name="quickPlayServer">
    /// Адрес сервера для немедленного подключения или <c>null</c> для обычного запуска.
    /// </param>
    private async Task StartGameAsync(string? quickPlayServer)
    {
        var button = this.FindControl<Button>("PlayButton")!;
        if (_configuration is null || _busy)
        {
            return;
        }

        _busy = true;
        button.IsEnabled = false;

        // Один идентификатор на всю попытку запуска: по нему события установки, старта и краша
        // сшиваются в одну историю на стороне сервера.
        var launchAttemptId = Guid.NewGuid().ToString("N");

        try
        {
            if (_primaryActionState == PrimaryActionState.LauncherUpdate)
            {
                await UpdateLauncherAsync();
                return;
            }

            var launchManifest = LauncherManifestFactory.CreateArchiveModeLaunchManifest(_modpackManifest, _configuration);

            await InstallGameFilesAsync(launchAttemptId, "launch");
            UpdatePrimaryActionButton();

            // После установки/обновления запуск не делаем — как и в WPF-версии,
            // игрок нажимает кнопку второй раз, уже «Играть».
            if (_primaryActionState != PrimaryActionState.Play)
            {
                // Звук «готово»: установка длинная, игрок за это время уходит в другое окно.
                _soundService.PlayReady();
                return;
            }

            // Запуск с ником по умолчанию не даём: иначе игрок заходит на сервер как «Player».
            // Как в WPF: перекидываем на поле ника в кабинете с понятным объяснением.
            if (!UsernameRules.IsValid(_userSettings.Username))
            {
                await FocusUsernameFieldAsync();
                SetStatus("Сначала выбери ник — под ним тебя увидят на сервере");
                return;
            }

            var installRoot = ModpackCatalogLogic.ResolveEffectiveInstallRoot(_configuration, _modpackManifest, _userSettings);

            SetStatus("Проверка Java...");
            var javaCheck = await JavaValidationService.ValidateJavaAsync(
                installRoot, launchManifest, _userSettings, CancellationToken.None);
            if (!javaCheck.IsOk)
            {
                SetStatus(javaCheck.Message);
                return;
            }

            SetStatus(quickPlayServer is null ? "Запуск игры..." : $"Запуск и подключение к {quickPlayServer}...");
            var result = await new MinecraftLaunchService()
                .LaunchAsync(CreateEffectiveConfiguration(), launchManifest, _userSettings, quickPlayServer);

            SetStatus(quickPlayServer is null
                ? $"Minecraft запущен (PID {result.Process.Id})"
                : $"Minecraft запущен, подключение к {quickPlayServer}");

            _ = TrackTelemetryAsync("launch_process_started", new Dictionary<string, object?>
            {
                ["launchAttemptId"] = launchAttemptId,
                ["processId"] = result.Process.Id,
                ["memoryMb"] = _userSettings.MemoryMb
            });

            if (_userSettings.CloseOnGameStart)
            {
                // Игрок выбрал закрывать лаунчер при запуске. Игра идёт отдельным процессом
                // и продолжит работать. Мониторинг краша при этом невозможен — как и в WPF.
                ExitApplication();
                return;
            }

            UpdateDiscordPresence(DiscordPlayingDetails, DiscordPlayingState);
            MinimizeToTray();

            // Ждём завершения игры в фоне: краш нужно разобрать и показать игроку.
            _ = MonitorMinecraftProcessAsync(result.Process, installRoot, launchAttemptId);
        }
        catch (Exception exception)
        {
            // Как в WPF: голое сообщение исключения игроку ничего не объясняет, поэтому
            // показываем разбор ошибки с понятными шагами.
            SetStatus("Ошибка");
            await ShowLauncherErrorAsync(exception);
        }
        finally
        {
            _busy = false;
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// Ждёт завершения игры и разбирает причину, если она упала. Логика повторяет WPF-версию:
    /// продержался дольше порога — запуск считается успешным; упал раньше — это провал запуска.
    /// </summary>
    private async Task MonitorMinecraftProcessAsync(
        System.Diagnostics.Process process, string installRoot, string launchAttemptId)
    {
        var startedAt = DateTime.UtcNow;
        var successConfirmed = false;

        try
        {
            var exitTask = process.WaitForExitAsync();
            var successDelayTask = Task.Delay(TimeSpan.FromSeconds(LaunchSuccessThresholdSeconds));

            if (await Task.WhenAny(exitTask, successDelayTask) == successDelayTask && !process.HasExited)
            {
                successConfirmed = true;
                _ = TrackTelemetryAsync("launch_succeeded", new Dictionary<string, object?>
                {
                    ["launchAttemptId"] = launchAttemptId,
                    ["startupSeconds"] = LaunchSuccessThresholdSeconds
                });
            }

            await exitTask;
            UpdateDiscordPresence(DiscordIdleDetails, DiscordIdleState);
            var runtime = DateTime.UtcNow - startedAt;

            // Сессия засчитывается в любом случае — и удачная, и закончившаяся крашем:
            // от этого зависят время по сборкам и достижения.
            RecordPlaySession(runtime, crashed: process.ExitCode != 0, startedAtLocal: startedAt.ToLocalTime());

            // Analyze читает логи синхронно — уводим с потока интерфейса, чтобы не подвесить окно.
            var analysis = await Task.Run(() => CrashAnalyzerService.Analyze(installRoot, process.ExitCode));

            _ = TrackTelemetryAsync("game_session_ended", new Dictionary<string, object?>
            {
                ["launchAttemptId"] = launchAttemptId,
                ["exitCode"] = process.ExitCode,
                ["runtimeSeconds"] = (int)runtime.TotalSeconds,
                ["graceful"] = process.ExitCode == 0
            });

            if (process.ExitCode == 0 && successConfirmed)
            {
                return;
            }

            if (successConfirmed)
            {
                // Игра успела запуститься и упала позже — разбор нужен так же, как при раннем выходе.
                _ = TrackTelemetryAsync("game_session_crashed", BuildCrashProperties(launchAttemptId, analysis, process.ExitCode, runtime));
                _ = AutoSendCrashBundleAsync(installRoot, $"Краш игры ({analysis.Category})");
            }
            else
            {
                var properties = BuildCrashProperties(launchAttemptId, analysis, process.ExitCode, runtime);
                properties["stage"] = "early_exit";
                _ = TrackTelemetryAsync("launch_failed", properties);
                _ = AutoSendCrashBundleAsync(installRoot, $"Ранний выход игры ({analysis.Category})");
            }

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                RestoreFromTray();
                SetStatus($"Игра завершилась с ошибкой: {analysis.Summary}");
                await ShowCrashWindowAsync(analysis, installRoot);
            });
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetStatus($"Ошибка наблюдения за игрой: {exception.Message}"));
        }
        finally
        {
            process.Dispose();
        }
    }

    private static Dictionary<string, object?> BuildCrashProperties(
        string launchAttemptId, CrashAnalysisResult analysis, int exitCode, TimeSpan runtime)
        => new()
        {
            ["launchAttemptId"] = launchAttemptId,
            ["exitCode"] = exitCode,
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
        };

    /// <summary>
    /// Показывает разбор краша тем же окном, что и ошибки лаунчера: игроку незачем знать,
    /// на каком этапе сломалось — ему нужны причина и шаги.
    /// </summary>
    private async Task ShowCrashWindowAsync(CrashAnalysisResult analysis, string installRoot)
    {
        // Details — готовый текст анализатора: раскладываем его на строки-шаги, чтобы окно
        // выглядело как обычный разбор ошибки, а не как простыня.
        var actions = analysis.Details
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.Equals(analysis.Summary, StringComparison.Ordinal))
            .ToArray();

        var info = new ErrorInfo(
            "Игра вылетела",
            analysis.Summary,
            actions.Length > 0 ? actions : ["Отправь лог в поддержку — разберёмся по нему."],
            $"Категория: {analysis.Category}{Environment.NewLine}" +
            $"Код выхода: {analysis.ExitCodeDescription}{Environment.NewLine}" +
            $"Найдено: {analysis.Evidence}{Environment.NewLine}{Environment.NewLine}" +
            analysis.LogTail);

        var logPath = analysis.CrashReportPath ?? analysis.LatestLogPath;
        var dialog = new ErrorWindow(
            info, logPath, _userSettings.ThemeId, () => SendSupportLogAsync(info, logPath));

        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Отправляет пакет логов сразу после краша, не дожидаясь действий игрока: иначе причина
    /// известна только тому, кто нажмёт кнопку. Уважает отключённую телеметрию.
    /// </summary>
    private async Task AutoSendCrashBundleAsync(string installRoot, string errorTitle)
    {
        if (!_userSettings.TelemetryEnabled)
        {
            return;
        }

        try
        {
            EnsureTelemetryIdentity();
            var supportLogService = new SupportLogService(_httpClient);

            var package = await supportLogService.CreatePackageAsync(
                installRoot,
                string.Empty,
                _userSettings.ClientId,
                GetTelemetryUsername(),
                GetLauncherVersion(),
                GetTelemetryModpackVersion(),
                errorTitle,
                CancellationToken.None);

            await supportLogService.UploadAsync(
                SupportLogsUrl,
                package.Path,
                _userSettings.ClientId,
                GetTelemetryUsername(),
                GetLauncherVersion(),
                GetTelemetryModpackVersion(),
                CancellationToken.None);
        }
        catch
        {
            // Молча: игрок в этот момент читает разбор краша, и сообщение про неудачную
            // отправку архива ему ничем не поможет. Кнопка «Отправить лог» остаётся.
        }
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
        catch
        {
            // Discord может быть не запущен — Rich Presence не критичен.
        }
    }

    private void UpdateDiscordPresence(string details, string state)
        => _ = Task.Run(async () =>
        {
            try
            {
                await _discordPresence.SetPresenceAsync(details, state);
            }
            catch
            {
                // Discord закрыли во время игры — не наша забота.
            }
        });

    /// <summary>
    /// Записывает завершённую сессию в профиль и обновляет кабинет. Вызывается из фонового
    /// наблюдения за игрой, поэтому интерфейс трогаем через диспетчер.
    /// </summary>
    private void RecordPlaySession(TimeSpan runtime, bool crashed, DateTime startedAtLocal)
    {
        if (_configuration is null)
        {
            return;
        }

        try
        {
            var modpackId = !string.IsNullOrWhiteSpace(_selectedModpackId)
                ? _selectedModpackId
                : _modpackManifest?.Modpack.Id ?? "default";
            var modpackName = _modpackManifest?.Modpack.Name ?? modpackId;

            _playerProfile.ClientId = _userSettings.ClientId;
            _playerProfile.RecordSession(modpackId, modpackName, runtime, crashed, startedAtLocal);
            _playerProfile.Save(_configuration.GetPlayerProfilePath());

            // После сессии статистика на сайте изменилась — тянем достижения заново,
            // и если что-то открылось, покажем тост.
            Dispatcher.UIThread.Post(() => _ = RefreshAccountAsync());
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() => SetStatus($"Не удалось обновить профиль: {exception.Message}"));
        }
    }

    /// <summary>
    /// Сравнивает полученные достижения с тем, что лаунчер уже видел, и возвращает новые.
    /// Первая синхронизация проходит молча — иначе игрок получил бы полсотни тостов подряд.
    /// </summary>
    private IReadOnlyList<SiteAchievement> TrackNewSiteAchievements(IReadOnlyList<SiteAchievement> achievements)
    {
        var earned = achievements.Where(achievement => achievement.Earned).ToList();
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

        try
        {
            if (_configuration is not null)
            {
                _playerProfile.Save(_configuration.GetPlayerProfilePath());
            }
        }
        catch
        {
            // Не сохранился профиль — в худшем случае тост покажется ещё раз.
        }

        return firstSync ? [] : fresh;
    }

    private void EnqueueAchievementToast(string icon, string title, string description)
    {
        _achievementToasts.Enqueue((icon, title, description));
        if (!_achievementToastRunning)
        {
            _ = ShowAchievementToastsAsync();
        }
    }

    /// <summary>
    /// Показывает очередь тостов по одному: появление, 4,5 секунды на чтение, исчезновение.
    /// Плавность даёт переход Opacity, объявленный в разметке.
    /// </summary>
    private async Task ShowAchievementToastsAsync()
    {
        _achievementToastRunning = true;
        var toast = this.FindControl<Border>("AchievementToast")!;

        try
        {
            while (_achievementToasts.Count > 0)
            {
                var (icon, title, description) = _achievementToasts.Dequeue();
                this.FindControl<TextBlock>("AchievementToastIcon")!.Text = icon;
                this.FindControl<TextBlock>("AchievementToastTitle")!.Text = title;
                this.FindControl<TextBlock>("AchievementToastDescription")!.Text = description;

                toast.IsVisible = true;
                toast.Opacity = 1;
                _soundService.PlayAchievement();

                await Task.Delay(TimeSpan.FromSeconds(4.5));
                toast.Opacity = 0;
                await Task.Delay(TimeSpan.FromMilliseconds(320));
                toast.IsVisible = false;
            }
        }
        finally
        {
            _achievementToastRunning = false;
        }
    }

    /// <summary>
    /// Сводка о машине игрока. Размер экрана берём у Avalonia: <c>SystemParameters</c> из WPF
    /// на Linux и macOS не существует.
    /// </summary>
    private Dictionary<string, object?> BuildSystemInfoProperties()
    {
        var properties = SystemInfoCollector.Collect();
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is not null)
            {
                properties["screenWidth"] = screen.Bounds.Width;
                properties["screenHeight"] = screen.Bounds.Height;
            }
        }
        catch
        {
            // Сведения об экране необязательны и недоступны в headless-среде.
        }

        return properties;
    }

    /// <summary>
    /// Отправляет событие телеметрии. Молчит, если игрок её отключил.
    /// </summary>
    private async Task TrackTelemetryAsync(string eventName, Dictionary<string, object?>? properties = null)
    {
        if (!_userSettings.TelemetryEnabled)
        {
            return;
        }

        EnsureTelemetryIdentity();

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await new TelemetryClient(_httpClient).SendEventAsync(
                TelemetryUrl,
                _userSettings.ClientId,
                GetLauncherVersion(),
                GetTelemetryModpackVersion(),
                Environment.OSVersion.VersionString,
                eventName,
                properties,
                cts.Token);
        }
        catch
        {
            // Телеметрия не должна мешать игре: недоступный сервер статистики — не повод
            // показывать игроку ошибку.
        }
    }

    /// <summary>
    /// Скачивает и применяет обновление самого лаунчера, затем перезапускается.
    /// </summary>
    private async Task UpdateLauncherAsync()
    {
        if (_modpackManifest is null || string.IsNullOrWhiteSpace(_modpackManifest.Launcher.PackageUrl))
        {
            SetStatus("Для лаунчера не указан адрес пакета обновления.");
            return;
        }

        var progressBar = this.FindControl<ProgressBar>("LauncherProgressBar")!;
        progressBar.Value = 0;
        SetStatus("Обновление лаунчера 0%");

        var packageUri = _modpackManifest.ResolveUri(_modpackManifest.Launcher.PackageUrl);

        // Пакет обновления — исполняемый код, который распакуется и запустится. Качаем только
        // по HTTPS, чтобы исключить подмену на незащищённом транспорте.
        if (!packageUri.IsFile && !packageUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Обновление лаунчера должно скачиваться по HTTPS.");
            return;
        }

        var progress = new Progress<LauncherSelfUpdateProgress>(report =>
        {
            progressBar.Value = report.Percentage;
            SetStatus(report.Message);
        });

        var package = await new LauncherSelfUpdateService(_httpClient).DownloadUpdatePackageAsync(
            packageUri, _modpackManifest.Launcher.Sha256, progress, CancellationToken.None);

        progressBar.Value = 100;
        SetStatus("Перезапуск лаунчера...");

        var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        var currentProcessPath = Environment.ProcessPath ?? currentProcess.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentProcessPath))
        {
            SetStatus("Не удалось определить путь к текущему лаунчеру.");
            return;
        }

        new LauncherSelfUpdateService(_httpClient).ApplyUpdateAndRestart(
            package.PackagePath, AppContext.BaseDirectory, currentProcessPath, currentProcess.Id);

        _allowClose = true;
        Close();
    }

    /// <summary>
    /// Ставит или обновляет файлы игры через общий с WPF оркестратор.
    /// </summary>
    private async Task InstallGameFilesAsync(string operationId = "", string trigger = "launch", bool force = false)
    {
        var progressBar = this.FindControl<ProgressBar>("LauncherProgressBar")!;

        _ = TrackTelemetryAsync("install_started", new Dictionary<string, object?>
        {
            ["trigger"] = trigger,
            ["operationId"] = operationId,
            ["force"] = force
        });

        // Место на диске проверяем ДО загрузки: сборка весит гигабайты, и упереться в конец
        // диска на 90% установки — худший из вариантов.
        var spaceCheck = DiskSpaceService.CheckInstallSpace(
            ModpackCatalogLogic.ResolveEffectiveInstallRoot(_configuration!, _modpackManifest, _userSettings),
            _modpackManifest);
        SetStatus(spaceCheck.Message);
        if (!spaceCheck.IsOk)
        {
            throw new InvalidOperationException(spaceCheck.Message);
        }

        // Прогресс приходит из фоновых потоков — Avalonia сам возвращает его в поток интерфейса
        // через Progress<T>, созданный здесь.
        IProgress<FileSyncProgress> Scaled(double from, double to) => new Progress<FileSyncProgress>(p =>
        {
            progressBar.Value = from + (to - from) * (p.Percentage / 100.0);
            if (!string.IsNullOrWhiteSpace(p.Message))
            {
                SetStatus(p.Message);
            }
        });

        var outcome = await new GameInstallOrchestrator(_httpClient).EnsureGameFilesAsync(
            _configuration!,
            _modpackManifest,
            null,
            _userSettings,
            new GameInstallCallbacks(
                _ => { },
                Scaled(0, 45),
                Scaled(45, 100),
                Scaled(0, 100),
                // Доп. моды ещё не перенесены — возвращать в mods/ пока нечего.
                () => Task.CompletedTask),
            forceArchiveInstall: force);

        progressBar.Value = 100;
        SetStatus(outcome.StatusText);

        _ = TrackTelemetryAsync("install_completed", new Dictionary<string, object?>
        {
            ["mode"] = outcome.Mode,
            ["installed"] = outcome.Installed,
            ["durationMs"] = outcome.DurationMs,
            ["changedFiles"] = outcome.ChangedFiles,
            ["trigger"] = trigger,
            ["operationId"] = operationId,
            ["force"] = force
        });
    }

    /// <summary>
    /// Конфигурация с подставленной папкой установки выбранной сборки — её ждёт запуск игры.
    /// </summary>
    private LauncherConfiguration CreateEffectiveConfiguration()
    {
        var effective = LauncherConfiguration.Load(AppContext.BaseDirectory);
        effective.IsMultiModpackCatalog = _configuration!.IsMultiModpackCatalog;
        effective.DistributionRoot = ModpackCatalogLogic.ResolveEffectiveInstallRoot(
            _configuration, _modpackManifest, _userSettings);
        return effective;
    }

    /// <summary>
    /// Режим <c>--screenshot=&lt;папка&gt;</c>: открывает каждое окно и сохраняет его в PNG,
    /// затем завершает приложение.
    /// </summary>
    /// <remarks>
    /// Сеть здесь НЕ трогаем: ни каталог, ни новости, ни переписка с поддержкой не грузятся.
    /// Проверяется вёрстка, а прогон в CI не должен ходить на боевой сайт.
    /// </remarks>
    private async Task CaptureScreenshotsAsync(string directory)
    {
        var failures = 0;
        try
        {
            Directory.CreateDirectory(directory);

            var themeId = LauncherThemeCatalog.DefaultThemeId;
            LauncherThemeBrushes.ApplyTheme(Resources, themeId);
            SetStatus("Снимок вёрстки");

            // Тост достижения попадает в снимок главного окна: иначе его вёрстку никак
            // не проверить, не сыграв настоящую сессию. Звук при этом не нужен.
            // Переход прозрачности здесь ОТКЛЮЧАЕМ: на macOS-раннере он не успевает
            // отрисоваться, и тост выпадал из снимка, хотя на Windows попадал.
            _soundService.Enabled = false;
            var toast = this.FindControl<Border>("AchievementToast")!;
            toast.Transitions = null;
            this.FindControl<TextBlock>("AchievementToastIcon")!.Text = "⏱";
            this.FindControl<TextBlock>("AchievementToastTitle")!.Text = "Время в игре VI";
            this.FindControl<TextBlock>("AchievementToastDescription")!.Text = "Достичь 100 ч · +120 XP";
            toast.IsVisible = true;
            toast.Opacity = 1;

            // Главное окно уже открыто — снимаем как есть.
            failures += await CaptureAsync(this, Path.Combine(directory, "01-main.png"), close: false);

            var errorException = new HttpRequestException(
                "Этот хост неизвестен. (bl-modern.ru:443)",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
            var errorInfo = ErrorClassifier.Classify(errorException);

            var windows = new (Window Window, string Name)[]
            {
                (new ErrorWindow(errorInfo, "/tmp/launcher-error.log", themeId), "02-error"),
                (new SettingsWindow(), "03-settings"),
                (new SupportWindow(), "04-support"),
                (new SkinWindow(), "05-skin"),
                (new OptionalModsWindow(), "06-optional-mods")
            };

            foreach (var (window, name) in windows)
            {
                window.Show(this);
                failures += await CaptureAsync(window, Path.Combine(directory, $"{name}.png"), close: false);

                // Настройки длиннее экрана: без второго кадра нижние разделы (установка, Java,
                // окно игры, статистика) вообще не видно.
                if (window is SettingsWindow settings)
                {
                    settings.FindControl<ScrollViewer>("SettingsScrollViewer")?.ScrollToEnd();
                    failures += await CaptureAsync(window, Path.Combine(directory, $"{name}-bottom.png"), close: false);
                }

                window.Close();
            }

            Console.WriteLine($"SCREENSHOTS_RESULT={(failures == 0 ? "PASS" : "FAIL")} dir={directory} failed={failures}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"SCREENSHOTS_RESULT=FAIL {exception.Message}");
            failures++;
        }
        finally
        {
            Environment.ExitCode = failures == 0 ? 0 : 1;
            _allowClose = true;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                (Application.Current as App)?.SetTrayVisible(false);
                desktop.Shutdown();
            }
        }
    }

    /// <summary>Сохраняет окно в PNG. Возвращает 1, если снять не удалось.</summary>
    private static async Task<int> CaptureAsync(Window window, string path, bool close)
    {
        try
        {
            // Окну нужен полный проход разметки: сразу после Show размеры ещё нулевые,
            // и RenderTargetBitmap отдал бы пустой кадр.
            await Task.Delay(TimeSpan.FromMilliseconds(900));

            var width = (int)Math.Round(window.Bounds.Width);
            var height = (int)Math.Round(window.Bounds.Height);
            if (width <= 0 || height <= 0)
            {
                Console.WriteLine($"SCREENSHOT_FAIL {Path.GetFileName(path)}: окно без размеров");
                return 1;
            }

            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(path);

            Console.WriteLine($"SCREENSHOT_OK {Path.GetFileName(path)} {width}x{height}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"SCREENSHOT_FAIL {Path.GetFileName(path)}: {exception.Message}");
            return 1;
        }
        finally
        {
            if (close)
            {
                window.Close();
            }
        }
    }

    /// <summary>
    /// Режим <c>--error-demo</c>: окно ошибки на примере отказа DNS у сайта лаунчера — той самой
    /// ошибки, которая чаще всего приходит в поддержку.
    /// </summary>
    private async Task ShowErrorDemoAsync()
    {
        _configuration = LauncherConfiguration.Load(AppContext.BaseDirectory);
        _userSettings = UserSettings.Load(_configuration.GetUserSettingsPath());
        LauncherThemeBrushes.ApplyTheme(Resources, _userSettings.ThemeId);
        SetStatus("Демонстрация окна ошибки");

        await ShowLauncherErrorAsync(new HttpRequestException(
            "Этот хост неизвестен. (bl-modern.ru:443)",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)));
    }

    /// <summary>
    /// Показывает разобранную ошибку вместо голого текста исключения — как в WPF-версии.
    /// Сначала пишет лог рядом со сборкой, чтобы игроку было что приложить к обращению.
    /// </summary>
    private async Task ShowLauncherErrorAsync(Exception exception)
    {
        var errorInfo = ErrorClassifier.Classify(exception);
        var logPath = ErrorReport.Write(
            ResolveErrorInstallRoot(),
            errorInfo,
            exception,
            GetLauncherVersion(),
            GetTelemetryModpackVersion(),
            _configuration?.ModpackManifestUrl ?? "-",
            out _);

        var dialog = new ErrorWindow(
            errorInfo, logPath, _userSettings.ThemeId, () => SendSupportLogAsync(errorInfo, logPath));
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Собирает архив логов и отправляет его в поддержку. Если сервер не принял — архив
    /// остаётся на диске и его папка открывается: игрок сможет приложить файл вручную.
    /// </summary>
    private async Task<SupportLogSendResult> SendSupportLogAsync(ErrorInfo errorInfo, string logPath)
    {
        EnsureTelemetryIdentity();

        var supportLogService = new SupportLogService(_httpClient);
        var package = await supportLogService.CreatePackageAsync(
            ResolveErrorInstallRoot(),
            logPath,
            _userSettings.ClientId,
            GetTelemetryUsername(),
            GetLauncherVersion(),
            GetTelemetryModpackVersion(),
            errorInfo.Title,
            CancellationToken.None);

        try
        {
            var upload = await supportLogService.UploadAsync(
                SupportLogsUrl,
                package.Path,
                _userSettings.ClientId,
                GetTelemetryUsername(),
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
            return new SupportLogSendResult(false, package.Path, $"Архив создан, но отправить его не удалось: {exception.Message}");
        }
    }

    /// <summary>
    /// Папка, рядом с которой лежат логи. До загрузки конфигурации её ещё неоткуда взять —
    /// тогда берём папку лаунчера по умолчанию, как в WPF-версии.
    /// </summary>
    private string ResolveErrorInstallRoot()
        => _configuration is null
            ? Path.Combine(LauncherPaths.GetApplicationDataRoot(), LauncherProfile.DataFolderName)
            : ModpackCatalogLogic.ResolveEffectiveInstallRoot(_configuration, _modpackManifest, _userSettings);

    /// <summary>Без clientId поддержка не свяжет обращение с логами этого же игрока.</summary>
    private void EnsureTelemetryIdentity()
    {
        if (!string.IsNullOrWhiteSpace(_userSettings.ClientId))
        {
            return;
        }

        _userSettings.ClientId = Guid.NewGuid().ToString();
        if (_configuration is not null)
        {
            _userSettings.Save(_configuration.GetUserSettingsPath());
        }
    }

    private string GetTelemetryUsername()
        => string.IsNullOrWhiteSpace(_userSettings.Username) ? "Player" : _userSettings.Username.Trim();

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

        return "unknown";
    }

    /// <summary>Открывает папку с файлом системным файловым менеджером.</summary>
    private static void OpenDirectory(string path)
    {
        var directory = File.Exists(path) ? Path.GetDirectoryName(path) : path;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch
        {
            // Открыть папку — удобство, а не обязательное условие: путь уже показан в окне.
        }
    }

    private void NotImplementedYet(string what)
    {
        this.FindControl<TextBlock>("FooterTextBlock")!.Text = $"{what}: логика ещё не перенесена из WPF-версии";
    }
}
