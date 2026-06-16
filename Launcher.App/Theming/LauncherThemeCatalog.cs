using System.Windows;
using System.Windows.Media;

namespace Launcher.App.Theming;

public sealed record LauncherTheme(
    string Id,
    string DisplayName,
    string WindowBackground,
    string TitleBarBackground,
    string TitleBarBorder,
    string WindowIconBackground,
    string WindowIconBorder,
    string Panel,
    string PanelAlt,
    string Card,
    string CardAlt,
    string Stroke,
    string Muted,
    string Text,
    string Accent,
    string AccentDark,
    string TextBoxBackground,
    string TextBoxBorder,
    string SoftButtonBackground,
    string SoftButtonBorder,
    string SoftButtonHover,
    string SoftButtonPressed,
    string SocialButtonBackground,
    string SocialButtonBorder,
    string SocialButtonHover,
    string IconButtonBackground,
    string IconButtonBorder,
    string IconButtonHover,
    string TitleBarButtonHover,
    string TitleBarButtonPressed,
    string PrimaryButtonBorder,
    string PrimaryButtonHover);

public static class LauncherThemeCatalog
{
    public const string DefaultThemeId = "verdant";

    public static IReadOnlyList<LauncherTheme> All { get; } =
    [
        new(
            "verdant",
            "Лесная",
            "#0E1612",
            "#F3131C18",
            "#325345",
            "#294036",
            "#6F9F86",
            "#18261F",
            "#21342A",
            "#D01A2821",
            "#CC111C19",
            "#5E8A73",
            "#C9D6C8",
            "#FFF7EA",
            "#E0B24F",
            "#B9802A",
            "#E1141F1A",
            "#5B8A72",
            "#355545",
            "#6B9A7F",
            "#456D5A",
            "#294236",
            "#3E6452",
            "#7DA78E",
            "#507D67",
            "#355545",
            "#6B9A7F",
            "#456D5A",
            "#344E41",
            "#23372D",
            "#F5D07A",
            "#F0C96A"),
        new(
            "ocean",
            "Океан",
            "#0D141B",
            "#F1111A23",
            "#2F5B76",
            "#1A3342",
            "#74B6D8",
            "#152733",
            "#1C3848",
            "#D0192F3C",
            "#CC152733",
            "#6BA6C7",
            "#C4D9E5",
            "#F4FBFF",
            "#59C3FF",
            "#2A7DB9",
            "#E1111C26",
            "#5688A5",
            "#28475A",
            "#6BA6C7",
            "#35607A",
            "#213B4B",
            "#31576D",
            "#80BAD7",
            "#3D6D86",
            "#28475A",
            "#6BA6C7",
            "#35607A",
            "#2D556D",
            "#203D50",
            "#8AD8FF",
            "#74CBFF"),
        new(
            "ember",
            "Янтарь",
            "#18110D",
            "#F1321712",
            "#7A5338",
            "#4B2D1F",
            "#D59B74",
            "#2A1D16",
            "#3A281F",
            "#D033241B",
            "#CC2A1D16",
            "#A97452",
            "#E6D4C6",
            "#FFF8F1",
            "#F0A35A",
            "#BA6A2F",
            "#E1120F0B",
            "#8A6247",
            "#5A3B2A",
            "#A16A47",
            "#B7815C",
            "#764B33",
            "#593A2A",
            "#B98761",
            "#6A4731",
            "#5A3B2A",
            "#A16A47",
            "#B7815C",
            "#6D4831",
            "#543526",
            "#F4BE7D",
            "#F2B068"),
        new(
            "amethyst",
            "Аметист",
            "#120F18",
            "#F1181322",
            "#5E4B84",
            "#312547",
            "#A88FE0",
            "#221C30",
            "#2F2642",
            "#D0262038",
            "#CC221C30",
            "#7E6AAE",
            "#D5D0E7",
            "#FBF8FF",
            "#B28DFF",
            "#7F59D8",
            "#E1181822",
            "#695C8F",
            "#533F74",
            "#735A9C",
            "#876EAF",
            "#604B82",
            "#4C3B69",
            "#8A72B4",
            "#68528D",
            "#533F74",
            "#735A9C",
            "#876EAF",
            "#5D4B82",
            "#46365F",
            "#CCB3FF",
            "#BEA0FF"),
        new(
            "crimson",
            "Бордовая",
            "#190E12",
            "#F1221116",
            "#7E4656",
            "#481F2B",
            "#D996A7",
            "#2A161D",
            "#3B1F28",
            "#D0321C24",
            "#CC2A161D",
            "#A86477",
            "#E8D1D8",
            "#FFF7F9",
            "#F08AA4",
            "#B84B69",
            "#E1170D14",
            "#8A5A68",
            "#6A3442",
            "#8D4D5F",
            "#B16B80",
            "#733F4D",
            "#5A313D",
            "#A85E73",
            "#815063",
            "#6A3442",
            "#8D4D5F",
            "#B16B80",
            "#6E4050",
            "#542E39",
            "#F3A5BA",
            "#EF91AA")
    ];

    public static LauncherTheme Get(string? themeId)
    {
        return All.FirstOrDefault(theme => theme.Id.Equals(themeId, StringComparison.OrdinalIgnoreCase))
               ?? All[0];
    }

    public static void ApplyTheme(ResourceDictionary resources, string? themeId)
    {
        var theme = Get(themeId);
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
    }

    private static void Set(ResourceDictionary resources, string key, string hex)
    {
        resources[key] = CreateBrush(hex);
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var normalized = NormalizeHex(hex);
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(normalized)!;
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
