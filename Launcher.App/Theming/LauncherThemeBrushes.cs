using System.Windows;
using System.Windows.Media;
// UseWindowsForms подтягивает System.Drawing.* → bare Color/Point/ColorConverter становятся
// неоднозначными. Фиксируем их как WPF-типы (тут всё про WPF-кисти).
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Launcher.App.Theming;

/// <summary>
/// Раскладывает тему по ресурсам WPF. Сами темы описаны в ядре (<see cref="LauncherThemeCatalog"/>)
/// и не зависят от UI-фреймворка — здесь только превращение цветов в кисти.
/// </summary>
public static class LauncherThemeBrushes
{
    public static void ApplyTheme(ResourceDictionary resources, string? themeId)
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
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop(top, 0));
        brush.GradientStops.Add(new GradientStop(bottom, 1));
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(string hex, byte alpha)
    {
        var color = (Color)ColorConverter.ConvertFromString(NormalizeHex(hex))!;
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        resources[key] = CreateBrush(hex);
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var normalized = NormalizeHex(hex);
        var color = (Color)ColorConverter.ConvertFromString(normalized)!;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
