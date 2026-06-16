using System.Diagnostics;
using System.Windows;
using Launcher.App.Models;
using Launcher.App.Theming;
using WinForms = System.Windows.Forms;

namespace Launcher.App;

public partial class SettingsWindow : Window
{
    private const double MouseWheelScrollStep = 18d;
    private readonly UserSettings _initialSettings;
    private readonly string _defaultInstallRoot;
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
        int memoryMax)
    {
        InitializeComponent();
        _initialSettings = Clone(settings);
        _defaultInstallRoot = defaultInstallRoot;
        _memoryDefault = memoryDefault;
        _memoryMin = memoryMin;
        _memoryMax = memoryMax;
        Settings = Clone(settings);
        ThemeListBox.ItemsSource = LauncherThemeCatalog.All;
        LauncherThemeCatalog.ApplyTheme(Resources, settings.ThemeId);
        DefaultInstallPathTextBlock.Text = defaultInstallRoot;
        MemorySlider.Minimum = memoryMin;
        MemorySlider.Maximum = memoryMax;
        MemorySlider.TickFrequency = 512;
        MemorySlider.SmallChange = 512;
        MemorySlider.LargeChange = 1024;
        PopulateFields(Settings);
        _themeSelectionReady = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Settings = ReadFields();
        DialogResult = true;
        Close();
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

        LauncherThemeCatalog.ApplyTheme(Resources, theme.Id);
    }

    private void PopulateFields(UserSettings settings)
    {
        MemorySlider.Value = SnapMemory(settings.MemoryMb);
        MemoryValueTextBlock.Text = SnapMemory(settings.MemoryMb).ToString();
        InstallPathTextBox.Text = settings.InstallRoot;
        JavaPathTextBox.Text = settings.JavaExecutable;
        JvmArgsTextBox.Text = settings.JvmArguments;
        GameArgsTextBox.Text = settings.GameArguments;
        CustomResolutionCheckBox.IsChecked = settings.UseCustomResolution;
        ResolutionWidthTextBox.Text = settings.ResolutionWidth.ToString();
        ResolutionHeightTextBox.Text = settings.ResolutionHeight.ToString();
        TelemetryEnabledCheckBox.IsChecked = settings.TelemetryEnabled;
        ThemeListBox.SelectedItem = LauncherThemeCatalog.Get(settings.ThemeId);
    }

    private UserSettings ReadFields()
    {
        return new UserSettings
        {
            Username = _initialSettings.Username,
            MemoryMb = SnapMemory((int)Math.Round(MemorySlider.Value)),
            InstallRoot = InstallPathTextBox.Text.Trim(),
            JavaExecutable = NormalizeJavaPath(JavaPathTextBox.Text),
            JvmArguments = JvmArgsTextBox.Text.Trim(),
            GameArguments = GameArgsTextBox.Text.Trim(),
            UseCustomResolution = CustomResolutionCheckBox.IsChecked == true,
            TelemetryEnabled = TelemetryEnabledCheckBox.IsChecked == true,
            ThemeId = (ThemeListBox.SelectedItem as LauncherTheme)?.Id ?? LauncherThemeCatalog.DefaultThemeId,
            ClientId = string.IsNullOrWhiteSpace(_initialSettings.ClientId) ? Guid.NewGuid().ToString() : _initialSettings.ClientId,
            ResolutionWidth = ParseInt(ResolutionWidthTextBox.Text, _initialSettings.ResolutionWidth, 320, 7680),
            ResolutionHeight = ParseInt(ResolutionHeightTextBox.Text, _initialSettings.ResolutionHeight, 240, 4320)
        };
    }

    private static UserSettings Clone(UserSettings settings)
    {
        return new UserSettings
        {
            Username = settings.Username,
            MemoryMb = settings.MemoryMb,
            InstallRoot = settings.InstallRoot,
            JavaExecutable = settings.JavaExecutable,
            JvmArguments = settings.JvmArguments,
            GameArguments = settings.GameArguments,
            UseCustomResolution = settings.UseCustomResolution,
            TelemetryEnabled = settings.TelemetryEnabled,
            ThemeId = settings.ThemeId,
            ClientId = string.IsNullOrWhiteSpace(settings.ClientId) ? Guid.NewGuid().ToString() : settings.ClientId,
            ResolutionWidth = settings.ResolutionWidth,
            ResolutionHeight = settings.ResolutionHeight
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
