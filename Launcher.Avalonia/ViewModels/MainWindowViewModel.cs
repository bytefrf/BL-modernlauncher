using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Launcher.Avalonia.ViewModels;

/// <summary>
/// Данные главного окна. Пока здесь только список сборок для выпадающего меню — остальное
/// окно ещё живёт на именованных элементах, как и в WPF-версии.
/// </summary>
public sealed class MainWindowViewModel
{
    public ObservableCollection<ModpackListItem> Modpacks { get; } = [];

    public ObservableCollection<ServerListItem> Servers { get; } = [];

    public ObservableCollection<StatTileItem> ProfileStats { get; } = [];

    public ObservableCollection<PlayTimelineItem> PlayTimeline { get; } = [];

    public ObservableCollection<ModpackTimeItem> ProfileModpacks { get; } = [];

    public ObservableCollection<AchievementItem> Achievements { get; } = [];
}

/// <summary>Плитка статистики в личном кабинете.</summary>
public sealed class StatTileItem
{
    public required string Value { get; init; }

    public required string Caption { get; init; }
}

/// <summary>Столбик графика «как играл за месяц».</summary>
public sealed class PlayTimelineItem
{
    public required string Tooltip { get; init; }

    public required double BarHeight { get; init; }

    public required double BarOpacity { get; init; }
}

/// <summary>Строка «время по сборкам».</summary>
public sealed class ModpackTimeItem
{
    public required string Name { get; init; }

    public required string TimeText { get; init; }

    public double Percent { get; init; }
}

/// <summary>Карточка достижения.</summary>
public sealed class AchievementItem
{
    public required string Icon { get; init; }

    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    public string ProgressText { get; init; } = string.Empty;

    public string Tooltip { get; init; } = string.Empty;

    public double Percent { get; init; }

    /// <summary>Неполученные достижения приглушены — так видно, что уже взято.</summary>
    public double CardOpacity { get; init; } = 1;

    public IBrush TitleBrush { get; init; } = Brushes.Gray;

    public IBrush BorderBrush { get; init; } = Brushes.DimGray;
}

/// <summary>Строка списка серверов сборки.</summary>
public sealed class ServerListItem
{
    public required string Name { get; init; }

    public required string Host { get; init; }

    public string Detail { get; init; } = string.Empty;

    public string StatusText { get; init; } = string.Empty;

    public string PlayersText { get; init; } = string.Empty;

    public string PlayersCaption { get; init; } = string.Empty;

    public bool IsOnline { get; init; }

    public IBrush StatusBrush { get; init; } = Brushes.Gray;
}

/// <summary>
/// Строка выпадающего списка сборок.
/// </summary>
/// <remarks>
/// В WPF шаблон привязывался к полям <c>*Visibility</c> типа <c>Visibility</c>. В Avalonia
/// видимость — это <c>bool IsVisible</c>, поэтому поля названы <c>Has*</c>: конвертер
/// не нужен вовсе.
/// </remarks>
public sealed class ModpackListItem
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public string StatusText { get; init; } = string.Empty;

    public string PlaytimeText { get; init; } = string.Empty;

    public Bitmap? IconSource { get; init; }

    /// <summary>Первая буква названия — заглушка, пока обложка не загружена.</summary>
    public string InitialLetter => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    public IBrush StatusBrush { get; init; } = Brushes.Gray;

    public bool HasIcon => IconSource is not null;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    public bool HasPlaytime => !string.IsNullOrWhiteSpace(PlaytimeText);
}
