using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Launcher.App.Platform;
using Launcher.App.Services;
using Launcher.Avalonia.Rendering;
using Launcher.Avalonia.Theming;
using Path = System.IO.Path;

namespace Launcher.Avalonia;

/// <summary>
/// Просмотр и загрузка скина игрока.
/// </summary>
public partial class SkinWindow : Window
{
    private readonly SkinClient _skinClient;
    private readonly string _nickname;
    private byte[]? _selectedSkin;
    private string _selectedFileName = string.Empty;
    private bool _busy;

    /// <summary>Скин менялся — главному окну нужно перечитать превью.</summary>
    public bool SkinChanged { get; private set; }

    public SkinWindow() : this(new SkinClient(new System.Net.Http.HttpClient()), "Player", null, "classic", null)
    {
    }

    public SkinWindow(SkinClient skinClient, string nickname, byte[]? currentSkin, string model, string? themeId)
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, themeId);

        _skinClient = skinClient;
        _nickname = nickname;

        ConfigureWindowChrome();

        if (model == "slim")
        {
            this.FindControl<RadioButton>("SlimRadio")!.IsChecked = true;
        }

        // Модель меняет ширину рук — перерисовываем превью.
        this.FindControl<RadioButton>("ClassicRadio")!.IsCheckedChanged += (_, _) => RedrawSelectedPreview();
        this.FindControl<RadioButton>("SlimRadio")!.IsCheckedChanged += (_, _) => RedrawSelectedPreview();

        ShowPreview(currentSkin, isCurrent: true);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

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

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private bool IsSlim => this.FindControl<RadioButton>("SlimRadio")!.IsChecked == true;

    private void RedrawSelectedPreview()
    {
        if (_selectedSkin is not null)
        {
            ShowPreview(_selectedSkin, isCurrent: false);
        }
    }

    private void ShowPreview(byte[]? skinBytes, bool isCurrent)
    {
        var image = this.FindControl<Image>("SkinPreviewImage")!;
        var empty = this.FindControl<TextBlock>("PreviewEmptyTextBlock")!;
        var caption = this.FindControl<TextBlock>("PreviewCaptionTextBlock")!;

        var body = SkinImage.RenderBody(skinBytes, IsSlim);
        image.Source = body;
        empty.IsVisible = body is null;
        caption.Text = isCurrent ? "Текущий скин" : "Выбранный файл";
    }

    private async void BrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выбери файл скина",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Скин Minecraft") { Patterns = ["*.png"] }]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            LoadSkinFile(path);
        }
    }

    /// <summary>
    /// Проверяем файл до отправки теми же правилами, что и сервер: PNG, 64×64 или 64×32, до 250 КБ.
    /// </summary>
    private void LoadSkinFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > SkinClient.MaxSkinBytes)
            {
                SetStatus($"Файл слишком большой: {bytes.Length / 1024} КБ, максимум 250 КБ.");
                return;
            }

            using var stream = new MemoryStream(bytes, writable: false);
            using var decoded = new Bitmap(stream);
            var width = decoded.PixelSize.Width;
            var height = decoded.PixelSize.Height;
            if (!SkinClient.IsValidSkinSize(width, height))
            {
                SetStatus($"Размер {width}×{height} не подходит: нужен 64×64 или 64×32.");
                return;
            }

            _selectedSkin = bytes;
            _selectedFileName = Path.GetFileName(path);
            this.FindControl<TextBlock>("FileNameTextBlock")!.Text = _selectedFileName;
            SetStatus(string.Empty);
            ShowPreview(bytes, isCurrent: false);
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось прочитать файл: {exception.Message}");
        }
    }

    private async void UploadButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_selectedSkin is null)
        {
            SetStatus("Сначала выбери PNG-файл скина.");
            return;
        }

        var passwordBox = this.FindControl<TextBox>("PasswordBox")!;
        var password = passwordBox.Text ?? string.Empty;
        if (password.Length == 0)
        {
            SetStatus("Введи пароль от аккаунта на сайте.");
            passwordBox.Focus();
            return;
        }

        SetBusy(true, "Загружаем скин…");
        try
        {
            var result = await _skinClient.UploadAsync(
                _nickname, password, IsSlim ? "slim" : "classic", _selectedSkin, _selectedFileName);

            if (result.Success)
            {
                SkinChanged = true;
                // Пароль не держим в памяти дольше необходимого.
                passwordBox.Text = string.Empty;
                SetStatus("Скин загружен. В игре может понадобиться команда /skin refresh.");
                ShowPreview(_selectedSkin, isCurrent: true);
            }
            else
            {
                SetStatus(result.Message);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось загрузить: {exception.Message}");
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void ResetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var passwordBox = this.FindControl<TextBox>("PasswordBox")!;
        var password = passwordBox.Text ?? string.Empty;
        if (password.Length == 0)
        {
            SetStatus("Введи пароль от аккаунта на сайте.");
            passwordBox.Focus();
            return;
        }

        SetBusy(true, "Сбрасываем скин…");
        try
        {
            var result = await _skinClient.ResetAsync(_nickname, password);
            if (result.Success)
            {
                SkinChanged = true;
                _selectedSkin = null;
                passwordBox.Text = string.Empty;
                this.FindControl<TextBlock>("FileNameTextBlock")!.Text = "Файл не выбран";
                ShowPreview(null, isCurrent: true);
                SetStatus("Скин сброшен — в игре снова стандартный.");
            }
            else
            {
                SetStatus(result.Message);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось сбросить: {exception.Message}");
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        this.FindControl<Button>("UploadButton")!.IsEnabled = !busy;
        this.FindControl<Button>("ResetButton")!.IsEnabled = !busy;
        if (status is not null)
        {
            SetStatus(status);
        }
    }

    private void SetStatus(string text) => this.FindControl<TextBlock>("StatusTextBlock")!.Text = text;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
