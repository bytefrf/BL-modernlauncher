using System.Diagnostics;
using System.Windows;
using Launcher.App.Configuration;
using Launcher.App.Models;
using Launcher.App.Services;
using Launcher.App.Theming;
using WinForms = System.Windows.Forms;

namespace Launcher.App;

public partial class SettingsWindow : Window
{
    private const double MouseWheelScrollStep = 18d;
    private readonly UserSettings _initialSettings;
    private readonly string _defaultInstallRoot;
    // id выбранной сборки в каталог-режиме (null = одиночный режим). Если задан — поле «папка
    // установки» относится к этой сборке (хранится в UserSettings.ModpackInstallRoots), а не к глобальному пути.
    private readonly string? _modpackId;
    private readonly int _memoryDefault;
    private readonly int _memoryMin;
    private readonly int _memoryMax;
    private bool _themeSelectionReady;

    public UserSettings Settings { get; private set; }
    public bool RequestedIntegrityCheck { get; private set; }

    public SettingsWindow(
        UserSettings settings,
        string defaultInstallRoot,
        int memoryDefault,
        int memoryMin,
        int memoryMax,
        string? modpackId = null)
    {
        InitializeComponent();
        _initialSettings = Clone(settings);
        _defaultInstallRoot = defaultInstallRoot;
        _modpackId = modpackId;
        _memoryDefault = memoryDefault;
        _memoryMin = memoryMin;
        _memoryMax = memoryMax;
        Settings = Clone(settings);
        ThemeListBox.ItemsSource = LauncherThemeCatalog.All;
        LauncherThemeBrushes.ApplyTheme(Resources, settings.ThemeId);
        DefaultInstallPathTextBlock.Text = defaultInstallRoot;
        MemorySlider.Minimum = memoryMin;
        MemorySlider.Maximum = memoryMax;
        MemorySlider.TickFrequency = 512;
        MemorySlider.SmallChange = 512;
        MemorySlider.LargeChange = 1024;
        PopulateFields(Settings);
        _themeSelectionReady = true;
        ApplyResponsiveWindowSize();
    }

    /// <summary>
    /// Окно настроек было жёстко 820×660 при ResizeMode=NoResize. На ноутбуках 1366×768 и при
    /// масштабе Windows 125–150% рабочая область в DIP меньше этого, и низ окна с кнопками
    /// «Сохранить»/«Отмена» уезжал за пределы экрана без возможности до него добраться.
    /// Теперь размер зажимается по рабочей области; содержимое и так лежит в ScrollViewer.
    /// </summary>
    private void ApplyResponsiveWindowSize()
    {
        const double desiredWidth = 820;
        const double desiredHeight = 660;
        const double screenMargin = 60;
        // Ниже этого настройки нечитаемы; если экран ещё меньше — окно просто займёт его целиком.
        const double floorWidth = 620;
        const double floorHeight = 420;

        var workArea = SystemParameters.WorkArea;
        var width = Math.Max(floorWidth, Math.Min(desiredWidth, workArea.Width - screenMargin));
        var height = Math.Max(floorHeight, Math.Min(desiredHeight, workArea.Height - screenMargin));

        // Порядок важен: Width нельзя опустить ниже действующего MinWidth, поэтому его снимаем первым.
        MinWidth = Math.Min(MinWidth, width);
        MinHeight = Math.Min(MinHeight, height);
        Width = width;
        Height = height;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInstallPathOrWarn())
        {
            return;
        }

        var candidate = ReadFields();
        if (!ConfirmMemoryOrFix(candidate))
        {
            return;
        }

        Settings = candidate;
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Объясняет игроку, чем плохо выбранное количество памяти, и предлагает подходящее значение.
    /// Возвращает false, если игрок решил вернуться к настройке.
    /// </summary>
    /// <remarks>
    /// Обе крайности выглядят одинаково — «игра вылетает, лаунчер сломался». Мало памяти роняет
    /// игру на загрузке мира; слишком много отбирает её у системы, и начинается своп. Поэтому
    /// говорим словами и подставляем значение сами, а не оставляем игрока наедине с ползунком.
    /// </remarks>
    private bool ConfirmMemoryOrFix(UserSettings candidate)
    {
        var verdict = MemoryAdvisor.Evaluate(candidate.MemoryMb, SystemInfoCollector.TryGetTotalRamMb());
        if (!verdict.NeedsAttention)
        {
            return true;
        }

        var recommended = SnapMemory(verdict.RecommendedMb);
        var answer = System.Windows.MessageBox.Show(
            this,
            verdict.Message + $"\n\nПоставить {recommended} МБ?",
            verdict.Title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            // «Нет» — игрок настаивает на своём значении. Настаивать в ответ не будем: настройка его.
            return true;
        }

        MemorySlider.Value = recommended;
        MemoryValueTextBlock.Text = recommended.ToString();
        candidate.MemoryMb = recommended;
        return true;
    }

    private bool ValidateInstallPathOrWarn()
    {
        var problem = InstallPathValidator.Validate(InstallPathTextBox.Text);
        if (problem is null)
        {
            return true;
        }

        System.Windows.MessageBox.Show(this, problem, "Папка установки", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
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

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        PopulateFields(new UserSettings
        {
            MemoryMb = _memoryDefault
        });
    }

    private void BrowseInstallPathButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Выберите папку установки сборки",
            UseDescriptionForTitle = true,
            SelectedPath = string.IsNullOrWhiteSpace(InstallPathTextBox.Text)
                ? _defaultInstallRoot
                : Environment.ExpandEnvironmentVariables(InstallPathTextBox.Text)
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            InstallPathTextBox.Text = dialog.SelectedPath;
        }
    }

    private void OpenInstallFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = GetEffectiveInstallRootFromFields();

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void CreateDesktopShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Launcher.App.Services.DesktopShortcutService.Create("BL-modern TFGM");
            System.Windows.MessageBox.Show(this, $"Ярлык создан на рабочем столе:\n{path}", "Ярлык", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(this, $"Не удалось создать ярлык: {exception.Message}", "Ярлык", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLatestLogButton_Click(object sender, RoutedEventArgs e)
    {
        var latestLogPath = Path.Combine(GetEffectiveInstallRootFromFields(), "logs", "latest.log");
        if (!File.Exists(latestLogPath))
        {
            System.Windows.MessageBox.Show(this, $"Файл latest.log пока не найден:\n{latestLogPath}", "latest.log", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = latestLogPath,
            UseShellExecute = true
        });
    }

    private void VerifyFilesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInstallPathOrWarn())
        {
            return;
        }

        Settings = ReadFields();
        RequestedIntegrityCheck = true;
        DialogResult = true;
        Close();
    }

    private string GetEffectiveInstallRootFromFields()
    {
        return string.IsNullOrWhiteSpace(InstallPathTextBox.Text)
            ? _defaultInstallRoot
            : Environment.ExpandEnvironmentVariables(InstallPathTextBox.Text.Trim());
    }

    private void BrowseJavaPathButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите javaw.exe или java.exe",
            Filter = "Java executable|javaw.exe;java.exe|Executable files|*.exe|All files|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            JavaPathTextBox.Text = NormalizeJavaPath(dialog.FileName);
        }
    }

    private void MemorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MemoryValueTextBlock is null)
        {
            return;
        }

        MemoryValueTextBlock.Text = SnapMemory((int)Math.Round(e.NewValue)).ToString();
    }

    private void SettingsScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        var deltaSteps = e.Delta / 120d;
        var offset = SettingsScrollViewer.VerticalOffset - deltaSteps * MouseWheelScrollStep;
        SettingsScrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, SettingsScrollViewer.ScrollableHeight));
    }

    private void ThemeListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_themeSelectionReady || ThemeListBox.SelectedItem is not LauncherTheme theme)
        {
            return;
        }

        LauncherThemeBrushes.ApplyTheme(Resources, theme.Id);
    }

    private void PopulateFields(UserSettings settings)
    {
        MemorySlider.Value = SnapMemory(settings.MemoryMb);
        MemoryValueTextBlock.Text = SnapMemory(settings.MemoryMb).ToString();
        // В каталог-режиме поле = папка выбранной сборки (пусто → стоит в папке по умолчанию из подсказки).
        InstallPathTextBox.Text = _modpackId is null
            ? settings.InstallRoot
            : (settings.ModpackInstallRoots != null && settings.ModpackInstallRoots.TryGetValue(_modpackId, out var perPack) ? perPack : string.Empty);
        JavaPathTextBox.Text = settings.JavaExecutable;
        JvmArgsTextBox.Text = settings.JvmArguments;
        GameArgsTextBox.Text = settings.GameArguments;
        CustomResolutionCheckBox.IsChecked = settings.UseCustomResolution;
        ResolutionWidthTextBox.Text = settings.ResolutionWidth.ToString();
        ResolutionHeightTextBox.Text = settings.ResolutionHeight.ToString();
        CloseOnGameStartCheckBox.IsChecked = settings.CloseOnGameStart;
        TelemetryEnabledCheckBox.IsChecked = settings.TelemetryEnabled;
        SoundEnabledCheckBox.IsChecked = settings.SoundEnabled;
        ThemeListBox.SelectedItem = LauncherThemeCatalog.Get(settings.ThemeId);
    }

    private UserSettings ReadFields()
    {
        var fieldPath = InstallPathTextBox.Text.Trim();

        // Папка установки. В одиночном режиме это глобальный InstallRoot; в каталог-режиме — папка
        // конкретной сборки (хранится в ModpackInstallRoots, пустое значение = вернуть к папке по умолчанию).
        var installRoot = _modpackId is null ? fieldPath : _initialSettings.InstallRoot;
        var modpackRoots = new Dictionary<string, string>(
            _initialSettings.ModpackInstallRoots ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
        if (_modpackId is not null)
        {
            if (string.IsNullOrWhiteSpace(fieldPath))
            {
                modpackRoots.Remove(_modpackId);
            }
            else
            {
                modpackRoots[_modpackId] = fieldPath;
            }
        }

        return new UserSettings
        {
            Username = _initialSettings.Username,
            MemoryMb = SnapMemory((int)Math.Round(MemorySlider.Value)),
            InstallRoot = installRoot,
            ModpackInstallRoots = modpackRoots,
            JavaExecutable = NormalizeJavaPath(JavaPathTextBox.Text),
            JvmArguments = JvmArgsTextBox.Text.Trim(),
            GameArguments = GameArgsTextBox.Text.Trim(),
            UseCustomResolution = CustomResolutionCheckBox.IsChecked == true,
            CloseOnGameStart = CloseOnGameStartCheckBox.IsChecked == true,
            TelemetryEnabled = TelemetryEnabledCheckBox.IsChecked == true,
            SoundEnabled = SoundEnabledCheckBox.IsChecked == true,
            ThemeId = (ThemeListBox.SelectedItem as LauncherTheme)?.Id ?? LauncherThemeCatalog.DefaultThemeId,
            ClientId = string.IsNullOrWhiteSpace(_initialSettings.ClientId) ? Guid.NewGuid().ToString() : _initialSettings.ClientId,
            ResolutionWidth = ParseInt(ResolutionWidthTextBox.Text, _initialSettings.ResolutionWidth, 320, 7680),
            ResolutionHeight = ParseInt(ResolutionHeightTextBox.Text, _initialSettings.ResolutionHeight, 240, 4320),
            // Поля, которых нет в окне настроек, переносим как есть, иначе они затрутся при сохранении.
            SelectedModpackId = _initialSettings.SelectedModpackId,
            SupportEmail = _initialSettings.SupportEmail
        };
    }

    private static UserSettings Clone(UserSettings settings)
    {
        return new UserSettings
        {
            Username = settings.Username,
            MemoryMb = settings.MemoryMb,
            InstallRoot = settings.InstallRoot,
            ModpackInstallRoots = new Dictionary<string, string>(
                settings.ModpackInstallRoots ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase),
            JavaExecutable = settings.JavaExecutable,
            JvmArguments = settings.JvmArguments,
            GameArguments = settings.GameArguments,
            UseCustomResolution = settings.UseCustomResolution,
            CloseOnGameStart = settings.CloseOnGameStart,
            TelemetryEnabled = settings.TelemetryEnabled,
            ThemeId = settings.ThemeId,
            ClientId = string.IsNullOrWhiteSpace(settings.ClientId) ? Guid.NewGuid().ToString() : settings.ClientId,
            ResolutionWidth = settings.ResolutionWidth,
            ResolutionHeight = settings.ResolutionHeight,
            SelectedModpackId = settings.SelectedModpackId,
            SupportEmail = settings.SupportEmail
        };
    }

    private static int ParseInt(string value, int fallback, int min, int max)
    {
        return int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;
    }

    private static string NormalizeJavaPath(string value)
    {
        var normalized = (value ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return Environment.ExpandEnvironmentVariables(normalized);
    }

    private int SnapMemory(int value)
    {
        var clamped = Math.Clamp(value, _memoryMin, _memoryMax);
        return Math.Clamp((int)Math.Round(clamped / 512d) * 512, _memoryMin, _memoryMax);
    }
}
