using System.Windows;
using System.Windows.Media.Imaging;
using Launcher.App.Services;
using Launcher.App.Theming;
using WinForms = System.Windows.Forms;

namespace Launcher.App;

/// <summary>
/// Просмотр и смена скина игрока. Работает с тем же API, что кабинет на сайте (`/api/skin.php`).
/// Пароль запрашивается только в момент отправки, живёт в PasswordBox и нигде не сохраняется.
/// </summary>
public partial class SkinWindow : Window
{
    private readonly SkinClient _skinClient;
    private readonly string _nickname;
    private byte[]? _selectedSkin;
    private string _selectedFileName = string.Empty;
    private bool _busy;

    /// <summary>Скин изменился — главному окну нужно обновить превью.</summary>
    public bool SkinChanged { get; private set; }

    public SkinWindow(SkinClient skinClient, string nickname, byte[]? currentSkin, string model, string themeId)
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, themeId);

        _skinClient = skinClient;
        _nickname = nickname;

        if (model == "slim")
        {
            SlimRadio.IsChecked = true;
        }

        ShowPreview(currentSkin, isCurrent: true);
    }

    private void ShowPreview(byte[]? skinBytes, bool isCurrent)
    {
        var slim = SlimRadio.IsChecked == true;
        var rendered = skinBytes is null ? null : SkinRenderer.RenderBody(skinBytes, slim);

        SkinPreviewImage.Source = rendered;
        PreviewCaptionTextBlock.Text = isCurrent ? "Текущий скин" : "Предпросмотр нового скина";
        PreviewEmptyTextBlock.Visibility = rendered is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new WinForms.OpenFileDialog
        {
            Title = "Выберите файл скина",
            Filter = "Скин Minecraft (*.png)|*.png",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            return;
        }

        LoadSkinFile(dialog.FileName);
    }

    // Проверяем файл до отправки теми же правилами, что и сервер: PNG, 64×64 или 64×32, до 250 КБ.
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
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (!SkinClient.IsValidSkinSize(frame.PixelWidth, frame.PixelHeight))
            {
                SetStatus($"Размер {frame.PixelWidth}×{frame.PixelHeight} не подходит: нужен 64×64 или 64×32.");
                return;
            }

            _selectedSkin = bytes;
            _selectedFileName = Path.GetFileName(path);
            FileNameTextBlock.Text = _selectedFileName;
            SetStatus(string.Empty);
            ShowPreview(bytes, isCurrent: false);
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось прочитать файл: {exception.Message}");
        }
    }

    private void Model_Changed(object sender, RoutedEventArgs e)
    {
        // Модель меняет ширину рук — перерисовываем превью.
        if (IsLoaded && _selectedSkin is not null)
        {
            ShowPreview(_selectedSkin, isCurrent: false);
        }
    }

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
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

        var password = PasswordBox.Password;
        if (string.IsNullOrEmpty(password))
        {
            SetStatus("Введи пароль от аккаунта на сайте.");
            PasswordBox.Focus();
            return;
        }

        SetBusy(true, "Загружаем скин…");
        try
        {
            var model = SlimRadio.IsChecked == true ? "slim" : "classic";
            var result = await _skinClient.UploadAsync(_nickname, password, model, _selectedSkin, _selectedFileName);
            if (result.Success)
            {
                SkinChanged = true;
                PasswordBox.Clear();
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
            SetStatus($"Не удалось загрузить скин: {exception.Message}");
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var password = PasswordBox.Password;
        if (string.IsNullOrEmpty(password))
        {
            SetStatus("Чтобы сбросить скин, введи пароль от аккаунта.");
            PasswordBox.Focus();
            return;
        }

        // MessageBox неоднозначен из-за UseWindowsForms — указываем WPF-версию явно.
        var confirm = System.Windows.MessageBox.Show(
            this,
            "Удалить свой скин? В игре вернётся стандартный.",
            "Сброс скина",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
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
                _selectedFileName = string.Empty;
                FileNameTextBlock.Text = "Файл не выбран";
                PasswordBox.Clear();
                ShowPreview(null, isCurrent: true);
                SetStatus("Скин сброшен.");
            }
            else
            {
                SetStatus(result.Message);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Не удалось сбросить скин: {exception.Message}");
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        UploadButton.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
        if (status is not null)
        {
            SetStatus(status);
        }
    }

    private void SetStatus(string message) => StatusTextBlock.Text = message;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = SkinChanged;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
