using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Launcher.App.Platform;
using Launcher.App.Services;
using Launcher.Avalonia.Theming;
using Launcher.Avalonia.ViewModels;
using Path = System.IO.Path;

namespace Launcher.Avalonia;

/// <summary>
/// Галерея снимков из папки сборки. Паритет с WPF-версией: Minecraft кладёт скриншоты внутрь
/// папки установки, а она у каждого своя и часто спрятана — лаунчер путь знает.
/// </summary>
public partial class ScreenshotsWindow : Window
{
    private readonly string _installRoot;
    private readonly string? _themeId;

    public ScreenshotsWindow() : this(string.Empty, null)
    {
    }

    public ScreenshotsWindow(string installRoot, string? themeId)
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, themeId);
        ConfigureWindowChrome();

        _installRoot = installRoot;
        _themeId = themeId;
        Reload();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void Reload()
    {
        var items = ScreenshotGalleryService.List(_installRoot);
        this.FindControl<ItemsControl>("ScreenshotsPanel")!.ItemsSource = items.Select(ToViewModel).ToList();

        var folder = ScreenshotGalleryService.ResolveFolder(_installRoot);
        this.FindControl<TextBlock>("SummaryTextBlock")!.Text = items.Count switch
        {
            0 => $"Снимков пока нет. Нажми F2 в игре — они появятся здесь.\n{folder}",
            1 => $"1 снимок · {folder}",
            _ => $"{items.Count} снимков (показываем последние) · {folder}"
        };
    }

    /// <summary>
    /// Превью читаем через поток и сразу его закрываем: иначе файл остаётся занятым и снимок
    /// нельзя удалить, пока окно открыто.
    /// </summary>
    private static ScreenshotViewModel ToViewModel(GameScreenshot screenshot)
    {
        Bitmap? preview = null;
        try
        {
            using var stream = File.OpenRead(screenshot.Path);
            preview = Bitmap.DecodeToWidth(stream, 320);
        }
        catch
        {
            preview = null;
        }

        return new ScreenshotViewModel(screenshot.Path, screenshot.FileName, screenshot.Caption, preview);
    }

    private void OpenScreenshot_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && File.Exists(path))
        {
            OpenWithSystem(path);
        }
    }

    /// <summary>UseShellExecute — единственный способ, работающий и на Windows, и на Linux, и на macOS.</summary>
    private static void OpenWithSystem(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // Нет ассоциации или путь исчез — окно продолжает работать.
        }
    }

    private async void DeleteScreenshot_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
        {
            return;
        }

        var confirm = new ConfirmWindow(
            "Удаление снимка",
            "Удалить снимок?",
            $"{Path.GetFileName(path)} будет удалён с диска безвозвратно.",
            _themeId,
            "Удалить",
            "Отмена");

        await confirm.ShowDialog(this);
        if (!confirm.Accepted)
        {
            return;
        }

        ScreenshotGalleryService.TryDelete(path);
        Reload();
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = ScreenshotGalleryService.ResolveFolder(_installRoot);
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        OpenWithSystem(folder);
    }

    /// <summary>Своя рамка на Windows, системная на Linux и macOS — как и у остальных окон.</summary>
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

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
