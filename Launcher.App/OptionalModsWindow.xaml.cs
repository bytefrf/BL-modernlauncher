using System.ComponentModel;
using System.Windows;
using Launcher.App.Models;
using Launcher.App.Services;
using Launcher.App.Theming;

namespace Launcher.App;

/// <summary>
/// Выбор дополнительных модов из списка, одобренного администрацией. Произвольные jar-файлы
/// добавить нельзя: ставится только то, что есть в каталоге и совпало по SHA-256.
/// </summary>
public partial class OptionalModsWindow : Window
{
    private const double MouseWheelScrollStep = 24d;

    private readonly OptionalModsService _service;
    private readonly OptionalModsCatalog _catalog;
    private readonly string _installRoot;
    private readonly List<OptionalModRow> _rows = [];

    /// <summary>Итоговый выбор игрока (id модов), если окно закрыто кнопкой «Применить».</summary>
    public IReadOnlyList<string> SelectedIds { get; private set; } = [];

    public OptionalModsWindow(
        OptionalModsService service,
        OptionalModsCatalog catalog,
        IReadOnlyCollection<string> selectedIds,
        string installRoot,
        string modpackName,
        string themeId)
    {
        InitializeComponent();
        LauncherThemeBrushes.ApplyTheme(Resources, themeId);

        _service = service;
        _catalog = catalog;
        _installRoot = installRoot;

        SubtitleTextBlock.Text = $"Сборка «{modpackName}». Отмеченные моды переустановятся автоматически после каждого обновления сборки.";

        foreach (var mod in catalog.Mods.OrderBy(mod => mod.Category).ThenBy(mod => mod.Name))
        {
            _rows.Add(new OptionalModRow(mod, selectedIds.Contains(mod.Id, StringComparer.OrdinalIgnoreCase)));
        }

        ModsPanel.ItemsSource = _rows;
        EmptyTextBlock.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = _rows.Count > 0;
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(row => row.Selected).Select(row => row.Id).ToList();
        ApplyButton.IsEnabled = false;
        StatusTextBlock.Text = "Устанавливаем моды…";

        try
        {
            var progress = new Progress<FileSyncProgress>(report => StatusTextBlock.Text = report.Message);
            var result = await _service.SyncAsync(_installRoot, _catalog, selected, progress);
            SelectedIds = selected;
            StatusTextBlock.Text = $"Готово: включено {result.Installed}, убрано {result.Removed}.";
            DialogResult = true;
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = exception.Message;
            ApplyButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ModsScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        var offset = ModsScrollViewer.VerticalOffset - e.Delta / 120d * MouseWheelScrollStep;
        ModsScrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, ModsScrollViewer.ScrollableHeight));
    }

    /// <summary>Строка списка. INotifyPropertyChanged нужен для двусторонней привязки галочки.</summary>
    public sealed class OptionalModRow(OptionalModEntry mod, bool selected) : INotifyPropertyChanged
    {
        private bool _selected = selected;

        public string Id { get; } = mod.Id;
        public string Name { get; } = string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name;
        public string Description { get; } = mod.Description ?? string.Empty;
        public string Category { get; } = mod.Category ?? string.Empty;

        public Visibility DescriptionVisibility { get; } =
            string.IsNullOrWhiteSpace(mod.Description) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility CategoryVisibility { get; } =
            string.IsNullOrWhiteSpace(mod.Category) ? Visibility.Collapsed : Visibility.Visible;

        public string Meta { get; } = BuildMeta(mod);

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                {
                    return;
                }

                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private static string BuildMeta(OptionalModEntry mod)
        {
            var parts = new List<string> { mod.FileName };
            if (mod.Size > 0)
            {
                parts.Add(FormatSize(mod.Size));
            }

            if (!string.IsNullOrWhiteSpace(mod.Author))
            {
                parts.Add(mod.Author);
            }

            return string.Join("  ·  ", parts);
        }

        private static string FormatSize(long bytes) => bytes switch
        {
            >= 1024 * 1024 => $"{bytes / 1024d / 1024d:0.#} МБ",
            >= 1024 => $"{bytes / 1024d:0} КБ",
            _ => $"{bytes} Б"
        };
    }
}
