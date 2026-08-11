using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Launcher.App.Configuration;
using Launcher.App.Models;
using Launcher.App.Platform;
using Launcher.App.Services;
using Launcher.App.Theming;
using Launcher.Avalonia.Theming;

namespace Launcher.Avalonia;

/// <summary>
/// Настройки лаунчера. Разделы и тексты повторяют WPF-версию.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly string _defaultInstallRoot;
    private readonly int _memoryDefault;
    private bool _ready;

    /// <summary>Настройки после нажатия «Сохранить»; <c>null</c>, если игрок отменил.</summary>
    public UserSettings? Result { get; private set; }

    /// <summary>
    /// Игрок нажал «Проверить целостность». Само окно ничего не переустанавливает —
    /// установка живёт в главном окне, туда же вернётся и прогресс.
    /// </summary>
    public bool RequestedIntegrityCheck { get; private set; }

    public SettingsWindow() : this(new UserSettings(), "-", 4096, 1024, 12288)
    {
    }

    public SettingsWindow(UserSettings settings, string defaultInstallRoot, int memoryDefault, int memoryMin, int memoryMax)
    {
        InitializeComponent();

        _defaultInstallRoot = defaultInstallRoot;
        _memoryDefault = memoryDefault;

        LauncherThemeBrushes.ApplyTheme(Resources, settings.ThemeId);

        var slider = this.FindControl<Slider>("MemorySlider")!;
        slider.Minimum = memoryMin;
        slider.Maximum = memoryMax;

        this.FindControl<TextBlock>("DefaultInstallPathTextBlock")!.Text = defaultInstallRoot;

        // Имя исполняемого файла Java зависит от системы — на Linux и macOS никакого javaw.exe нет.
        this.FindControl<TextBlock>("JavaPathLabel")!.Text =
            $"Путь к {HostPlatform.JavawExecutableName} / {HostPlatform.JavaExecutableName}";

        var themes = this.FindControl<ListBox>("ThemeListBox")!;
        themes.ItemsSource = LauncherThemeCatalog.All;
        themes.SelectedItem = LauncherThemeCatalog.Get(settings.ThemeId);

        ConfigureWindowChrome();
        Populate(settings);
        _ready = true;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Своя рамка окна на Windows и системная на Linux/macOS — тем же правилом, что и главное окно.
    /// </summary>
    private void ConfigureWindowChrome()
    {
        var titleBar = this.FindControl<Border>("CustomTitleBar")!;
        if (HostPlatform.IsWindows)
        {
            SystemDecorations = SystemDecorations.BorderOnly;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            titleBar.IsVisible = true;
            return;
        }

        SystemDecorations = SystemDecorations.Full;
        ExtendClientAreaToDecorationsHint = false;
        titleBar.IsVisible = false;
    }

    private void TitleBar_PointerPressed(object? sender, global::Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void Populate(UserSettings settings)
    {
        this.FindControl<Slider>("MemorySlider")!.Value = settings.MemoryMb > 0 ? settings.MemoryMb : _memoryDefault;
        this.FindControl<TextBox>("InstallPathTextBox")!.Text = settings.InstallRoot;
        this.FindControl<TextBox>("JavaPathTextBox")!.Text = settings.JavaExecutable;
        this.FindControl<TextBox>("JvmArgsTextBox")!.Text = settings.JvmArguments;
        this.FindControl<TextBox>("GameArgsTextBox")!.Text = settings.GameArguments;
        this.FindControl<CheckBox>("CustomResolutionCheckBox")!.IsChecked = settings.UseCustomResolution;
        this.FindControl<TextBox>("ResolutionWidthTextBox")!.Text = settings.ResolutionWidth.ToString(CultureInfo.InvariantCulture);
        this.FindControl<TextBox>("ResolutionHeightTextBox")!.Text = settings.ResolutionHeight.ToString(CultureInfo.InvariantCulture);
        this.FindControl<CheckBox>("CloseOnGameStartCheckBox")!.IsChecked = settings.CloseOnGameStart;
        this.FindControl<CheckBox>("SoundEnabledCheckBox")!.IsChecked = settings.SoundEnabled;
        this.FindControl<CheckBox>("TelemetryEnabledCheckBox")!.IsChecked = settings.TelemetryEnabled;
        UpdateMemoryLabel();
        ValidateInstallPath();
    }

    private void MemorySlider_PropertyChanged(object? sender, global::Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RangeBase.ValueProperty)
        {
            UpdateMemoryLabel();
        }
    }

    private void UpdateMemoryLabel()
    {
        var slider = this.FindControl<Slider>("MemorySlider");
        var label = this.FindControl<TextBlock>("MemoryValueTextBlock");
        if (slider is not null && label is not null)
        {
            label.Text = ((int)slider.Value).ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Тема применяется сразу — иначе выбор «вслепую».</summary>
    private void ThemeListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_ready && sender is ListBox { SelectedItem: LauncherTheme theme })
        {
            LauncherThemeBrushes.ApplyTheme(Resources, theme.Id);
        }
    }

    private async void BrowseInstallPath_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Папка установки сборки",
            AllowMultiple = false
        });

        var picked = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            this.FindControl<TextBox>("InstallPathTextBox")!.Text = picked;
            ValidateInstallPath();
        }
    }

    /// <summary>
    /// Проверяет выбранный путь тем же валидатором, что и WPF-версия: он ловит корень диска,
    /// системные и облачные папки — установка туда снесла бы файлы игрока.
    /// </summary>
    private bool ValidateInstallPath()
    {
        var hint = this.FindControl<TextBlock>("InstallPathHint")!;
        var problem = InstallPathValidator.Validate(this.FindControl<TextBox>("InstallPathTextBox")!.Text);
        hint.Text = problem ?? string.Empty;
        hint.IsVisible = problem is not null;
        return problem is null;
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        _ready = false;
        var defaults = new UserSettings { MemoryMb = _memoryDefault };
        Populate(defaults);
        this.FindControl<ListBox>("ThemeListBox")!.SelectedItem = LauncherThemeCatalog.Get(defaults.ThemeId);
        LauncherThemeBrushes.ApplyTheme(Resources, defaults.ThemeId);
        _ready = true;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!ValidateInstallPath())
        {
            return;
        }

        var settings = new UserSettings
        {
            MemoryMb = (int)this.FindControl<Slider>("MemorySlider")!.Value,
            InstallRoot = (this.FindControl<TextBox>("InstallPathTextBox")!.Text ?? string.Empty).Trim(),
            JavaExecutable = (this.FindControl<TextBox>("JavaPathTextBox")!.Text ?? string.Empty).Trim(),
            JvmArguments = this.FindControl<TextBox>("JvmArgsTextBox")!.Text ?? string.Empty,
            GameArguments = this.FindControl<TextBox>("GameArgsTextBox")!.Text ?? string.Empty,
            UseCustomResolution = this.FindControl<CheckBox>("CustomResolutionCheckBox")!.IsChecked == true,
            CloseOnGameStart = this.FindControl<CheckBox>("CloseOnGameStartCheckBox")!.IsChecked == true,
            SoundEnabled = this.FindControl<CheckBox>("SoundEnabledCheckBox")!.IsChecked == true,
            TelemetryEnabled = this.FindControl<CheckBox>("TelemetryEnabledCheckBox")!.IsChecked == true,
            ThemeId = (this.FindControl<ListBox>("ThemeListBox")!.SelectedItem as LauncherTheme)?.Id
                      ?? LauncherThemeCatalog.DefaultThemeId,
            ResolutionWidth = ParseSize(this.FindControl<TextBox>("ResolutionWidthTextBox")!.Text, 1280),
            ResolutionHeight = ParseSize(this.FindControl<TextBox>("ResolutionHeightTextBox")!.Text, 720)
        };

        Result = settings;
        Close();
    }

    /// <summary>
    /// «Проверить целостность»: сохраняем настройки (путь установки мог быть только что изменён)
    /// и просим главное окно переустановить файлы поверх текущих.
    /// </summary>
    private void VerifyFiles_Click(object? sender, RoutedEventArgs e)
    {
        RequestedIntegrityCheck = true;
        Save_Click(sender, e);
    }

    private void OpenInstallFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var target = ResolveInstallRootFromFields();
            Directory.CreateDirectory(target);

            // UseShellExecute открывает папку системным файловым менеджером на всех трёх ОС.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            var hint = this.FindControl<TextBlock>("InstallPathHint")!;
            hint.Text = $"Не удалось открыть папку: {exception.Message}";
            hint.IsVisible = true;
        }
    }

    /// <summary>
    /// Открывает лог последнего запуска игры. Его первым делом просит поддержка,
    /// поэтому кнопка есть и в WPF-версии.
    /// </summary>
    private void OpenLatestLog_Click(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(ResolveInstallRootFromFields(), "logs", "latest.log");
        if (!File.Exists(path))
        {
            ShowHint($"Файл latest.log пока не найден: {path}");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ShowHint($"Не удалось открыть latest.log: {exception.Message}");
        }
    }

    /// <summary>
    /// Ярлык на рабочем столе. Сервис в ядре сам выбирает формат: .lnk на Windows,
    /// .desktop на Linux, симлинк на macOS.
    /// </summary>
    private void CreateShortcut_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = DesktopShortcutService.Create("BL-modern TFGM");
            ShowHint($"Ярлык создан: {path}");
        }
        catch (Exception exception)
        {
            ShowHint($"Не удалось создать ярлык: {exception.Message}");
        }
    }

    private string ResolveInstallRootFromFields()
    {
        var path = (this.FindControl<TextBox>("InstallPathTextBox")!.Text ?? string.Empty).Trim();
        return LauncherPaths.ExpandFull(string.IsNullOrWhiteSpace(path) ? _defaultInstallRoot : path);
    }

    /// <summary>
    /// Сообщения кнопок показываем в той же подсказке, что и проблемы с путём: отдельного
    /// окна с «ОК» в этом интерфейсе нет, а в WPF на его месте был MessageBox.
    /// </summary>
    private void ShowHint(string message)
    {
        var hint = this.FindControl<TextBlock>("InstallPathHint")!;
        hint.Text = message;
        hint.IsVisible = true;
    }

    /// <summary>Некорректное разрешение не должно ломать запуск — падаем на значение по умолчанию.</summary>
    private static int ParseSize(string? value, int fallback)
        => int.TryParse((value ?? string.Empty).Trim(), out var parsed) && parsed >= 320 ? parsed : fallback;
}
