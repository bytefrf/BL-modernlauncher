using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Launcher.App.Services;
using Launcher.App.Theming;

namespace Launcher.App;

/// <summary>
/// Галерея снимков из папки сборки.
/// </summary>
/// <remarks>
/// Minecraft кладёт скриншоты внутрь папки установки, а она у каждого своя и часто спрятана
/// в AppData: игроки делают снимки и потом не могут их найти. Лаунчер путь знает.
/// </remarks>
public partial class ScreenshotsWindow : Window
{
    private readonly string _installRoot;

    public ScreenshotsWindow(string installRoot, string themeId)
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, themeId);
        _installRoot = installRoot;
        Reload();
    }

    private void Reload()
    {
        var items = ScreenshotGalleryService.List(_installRoot);
        ScreenshotsPanel.ItemsSource = items.Select(ToViewModel).ToList();

        var folder = ScreenshotGalleryService.ResolveFolder(_installRoot);
        SummaryTextBlock.Text = items.Count switch
        {
            0 => $"Снимков пока нет. Нажми F2 в игре — они появятся здесь.\n{folder}",
            1 => $"1 снимок · {folder}",
            _ => $"{items.Count} снимков (показываем последние) · {folder}"
        };
    }

    /// <summary>
    /// Превью грузим уменьшенным и с <c>OnLoad</c>: иначе WPF держит файл открытым, и снимок
    /// нельзя ни удалить, ни перезаписать, пока окно не закроется.
    /// </summary>
    private static ScreenshotViewModel ToViewModel(GameScreenshot screenshot)
    {
        BitmapImage? preview = null;
        try
        {
            preview = new BitmapImage();
            preview.BeginInit();
            preview.CacheOption = BitmapCacheOption.OnLoad;
            preview.DecodePixelWidth = 320;
            preview.UriSource = new Uri(screenshot.Path);
            preview.EndInit();
            preview.Freeze();
        }
        catch
        {
            // Битый или недочитанный файл: карточка покажется без превью, но не уронит окно.
            preview = null;
        }

        return new ScreenshotViewModel(screenshot.Path, screenshot.FileName, screenshot.Caption, preview);
    }

    private void OpenScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && File.Exists(path))
        {
            TryStart(path);
        }
    }

    private void DeleteScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path })
        {
            return;
        }

        var answer = System.Windows.MessageBox.Show(
            this,
            $"Удалить снимок {Path.GetFileName(path)}? Файл удалится с диска безвозвратно.",
            "Удаление снимка",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        if (!ScreenshotGalleryService.TryDelete(path))
        {
            System.Windows.MessageBox.Show(this, "Не удалось удалить файл — возможно, он открыт в другой программе.",
                "Удаление снимка", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Reload();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = ScreenshotGalleryService.ResolveFolder(_installRoot);
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        TryStart(folder);
    }

    private static void TryStart(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // Нет ассоциации для файла или папка исчезла — молчим, окно продолжает работать.
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record ScreenshotViewModel(string Path, string FileName, string Caption, BitmapImage? Preview);
}
