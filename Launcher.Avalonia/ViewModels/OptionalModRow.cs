using System.ComponentModel;
using Launcher.App.Models;

namespace Launcher.Avalonia.ViewModels;

/// <summary>
/// Строка списка дополнительных модов. Галочка меняется пользователем, поэтому нужен
/// <see cref="INotifyPropertyChanged"/> — остальные поля неизменны.
/// </summary>
public sealed class OptionalModRow : INotifyPropertyChanged
{
    private bool _selected;

    public OptionalModRow(OptionalModEntry mod, bool selected)
    {
        _selected = selected;
        Id = mod.Id;
        Name = string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name;
        Description = mod.Description ?? string.Empty;
        Category = mod.Category ?? string.Empty;
        Meta = BuildMeta(mod);
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public string Category { get; }

    /// <summary>Имя файла, размер и автор — одной строкой под названием.</summary>
    public string Meta { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasCategory => !string.IsNullOrWhiteSpace(Category);

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
