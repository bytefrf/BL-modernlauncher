using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Launcher.App.Models;
using Launcher.App.Platform;
using Launcher.App.Services;
using Launcher.Avalonia.Theming;
using Launcher.Avalonia.ViewModels;

namespace Launcher.Avalonia;

/// <summary>
/// Выбор дополнительных модов из каталога, одобренного администрацией.
/// </summary>
public partial class OptionalModsWindow : Window
{
    private readonly OptionalModsService _service;
    private readonly OptionalModsCatalog _catalog;
    private readonly string _installRoot;
    private readonly List<OptionalModRow> _rows = [];

    /// <summary>Выбранные моды после «Применить»; <c>null</c>, если игрок отменил.</summary>
    public IReadOnlyList<string>? SelectedIds { get; private set; }

    public OptionalModsWindow() : this(
        new OptionalModsService(new System.Net.Http.HttpClient()),
        new OptionalModsCatalog(), [], "-", "Сборка", null)
    {
    }

    public OptionalModsWindow(
        OptionalModsService service,
        OptionalModsCatalog catalog,
        IReadOnlyCollection<string> selectedIds,
        string installRoot,
        string modpackName,
        string? themeId)
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, themeId);

        _service = service;
        _catalog = catalog;
        _installRoot = installRoot;

        ConfigureWindowChrome();

        this.FindControl<TextBlock>("SubtitleTextBlock")!.Text =
            $"Сборка «{modpackName}». Отмеченные моды переустановятся автоматически после каждого обновления сборки.";

        // Порядок как в WPF: сначала по категории, внутри — по названию.
        foreach (var mod in catalog.Mods.OrderBy(mod => mod.Category).ThenBy(mod => mod.Name))
        {
            _rows.Add(new OptionalModRow(mod, selectedIds.Contains(mod.Id, StringComparer.OrdinalIgnoreCase)));
        }

        this.FindControl<ItemsControl>("ModsPanel")!.ItemsSource = _rows;
        this.FindControl<TextBlock>("EmptyTextBlock")!.IsVisible = _rows.Count == 0;
        this.FindControl<Button>("ApplyButton")!.IsEnabled = _rows.Count > 0;
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

    private async void ApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        var status = this.FindControl<TextBlock>("StatusTextBlock")!;
        var apply = this.FindControl<Button>("ApplyButton")!;

        var selected = _rows.Where(row => row.Selected).Select(row => row.Id).ToList();
        apply.IsEnabled = false;
        status.Text = "Устанавливаем моды…";

        try
        {
            var progress = new Progress<FileSyncProgress>(report => status.Text = report.Message);
            var result = await _service.SyncAsync(_installRoot, _catalog, selected, progress);
            SelectedIds = selected;
            status.Text = $"Готово: включено {result.Installed}, убрано {result.Removed}.";
            Close();
        }
        catch (Exception exception)
        {
            status.Text = exception.Message;
            apply.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
