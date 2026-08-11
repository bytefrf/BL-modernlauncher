using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Launcher.App.Theming;

namespace Launcher.Avalonia.Theming;

/// <summary>
/// Раскладывает тему по ресурсам Avalonia. Сами темы описаны в ядре
/// (<see cref="LauncherThemeCatalog"/>) и одинаковы для обоих интерфейсов — здесь только
/// превращение цветов в кисти.
/// </summary>
/// <remarks>
/// Отличия от WPF-версии, из-за которых код нельзя было переиспользовать: в Avalonia нет
/// <c>Freeze()</c> (кисти и так неизменяемы, если их не менять), а координаты градиента
/// задаются через <see cref="RelativePoint"/>, а не через <c>Point</c>.
/// </remarks>
public static class LauncherThemeBrushes
{
    public static void ApplyTheme(IResourceDictionary resources, string? themeId)
    {
        var theme = LauncherThemeCatalog.Get(themeId);
        Set(resources, "WindowBackgroundBrush", theme.WindowBackground);
        Set(resources, "TitleBarBackgroundBrush", theme.TitleBarBackground);
        Set(resources, "TitleBarBorderBrush", theme.TitleBarBorder);
        Set(resources, "WindowIconBackgroundBrush", theme.WindowIconBackground);
        Set(resources, "WindowIconBorderBrush", theme.WindowIconBorder);
        Set(resources, "PanelBrush", theme.Panel);
        Set(resources, "PanelAltBrush", theme.PanelAlt);
        Set(resources, "CardBrush", theme.Card);
        Set(resources, "CardAltBrush", theme.CardAlt);
        Set(resources, "StrokeBrush", theme.Stroke);
        Set(resources, "MutedBrush", theme.Muted);
        Set(resources, "TextBrush", theme.Text);
        Set(resources, "AccentBrush", theme.Accent);
        Set(resources, "AccentDarkBrush", theme.AccentDark);
        Set(resources, "TextBoxBackgroundBrush", theme.TextBoxBackground);
        Set(resources, "TextBoxBorderBrush", theme.TextBoxBorder);
        Set(resources, "SoftButtonBackgroundBrush", theme.SoftButtonBackground);
        Set(resources, "SoftButtonBorderBrush", theme.SoftButtonBorder);
        Set(resources, "SoftButtonHoverBrush", theme.SoftButtonHover);
        Set(resources, "SoftButtonPressedBrush", theme.SoftButtonPressed);
        Set(resources, "SocialButtonBackgroundBrush", theme.SocialButtonBackground);
        Set(resources, "SocialButtonBorderBrush", theme.SocialButtonBorder);
        Set(resources, "SocialButtonHoverBrush", theme.SocialButtonHover);
        Set(resources, "IconButtonBackgroundBrush", theme.IconButtonBackground);
        Set(resources, "IconButtonBorderBrush", theme.IconButtonBorder);
        Set(resources, "IconButtonHoverBrush", theme.IconButtonHover);
        Set(resources, "TitleBarButtonHoverBrush", theme.TitleBarButtonHover);
        Set(resources, "TitleBarButtonPressedBrush", theme.TitleBarButtonPressed);
        Set(resources, "PrimaryButtonBorderBrush", theme.PrimaryButtonBorder);
        Set(resources, "PrimaryButtonHoverBrush", theme.PrimaryButtonHover);

        // Ползунок Fluent рисуется СВОИМ синим акцентом — в WPF-версии такого не было,
        // потому что там шаблон писался руками. Перекрываем ключи темы Fluent под нашу палитру,
        // иначе синяя ручка выбивается из оформления на всех окнах сразу.
        Set(resources, "SliderThumbBackground", theme.Accent);
        Set(resources, "SliderThumbBackgroundPointerOver", theme.Accent);
        Set(resources, "SliderThumbBackgroundPressed", theme.AccentDark);
        Set(resources, "SliderThumbBackgroundDisabled", theme.Muted);
        Set(resources, "SliderTrackValueFill", theme.Accent);
        Set(resources, "SliderTrackValueFillPointerOver", theme.Accent);
        Set(resources, "SliderTrackValueFillPressed", theme.AccentDark);
        Set(resources, "SliderTrackValueFillDisabled", theme.Muted);
        Set(resources, "SliderTrackFill", theme.Stroke);
        Set(resources, "SliderTrackFillPointerOver", theme.Stroke);
        Set(resources, "SliderTrackFillPressed", theme.Stroke);
        Set(resources, "SliderTrackFillDisabled", theme.Stroke);

        // Стеклянные кисти выводим из цветов темы, чтобы glass перекрашивался вместе с темой:
        // заливка — затемнённые полупрозрачные тона панели/фона, грань — светлый приглушённый тон.
        resources["GlassFillBrush"] = CreateVerticalGradient(
            WithAlpha(theme.PanelAlt, 0x59),
            WithAlpha(theme.WindowBackground, 0x8F));
        resources["GlassStrokeBrush"] = CreateVerticalGradient(
            WithAlpha(theme.Muted, 0x5A),
            WithAlpha(theme.Muted, 0x14));
    }

    private static LinearGradientBrush CreateVerticalGradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative)
        };
        brush.GradientStops.Add(new GradientStop(top, 0));
        brush.GradientStops.Add(new GradientStop(bottom, 1));
        return brush;
    }

    private static Color WithAlpha(string hex, byte alpha)
    {
        var color = ParseColor(hex);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static void Set(IResourceDictionary resources, string key, string hex)
    {
        resources[key] = new SolidColorBrush(ParseColor(hex));
    }

    private static Color ParseColor(string hex)
    {
        // Формат тот же, что и в WPF: #RRGGBB или #AARRGGBB. Некорректное значение даёт
        // ядовито-розовый — заметно на глаз и не роняет запуск.
        return Color.TryParse(NormalizeHex(hex), out var color) ? color : Color.FromRgb(0xFF, 0x00, 0xFF);
    }

    private static string NormalizeHex(string hex)
    {
        var value = (hex ?? string.Empty).Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        var digits = value[1..];
        if (digits.Length is 6 or 8)
        {
            return value;
        }

        return "#FFFF00FF";
    }
}
