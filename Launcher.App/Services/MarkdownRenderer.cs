using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
// UseWindowsForms делает bare FontFamily неоднозначным (System.Drawing vs System.Windows.Media),
// как и Color/Point в LauncherThemeCatalog. Фиксируем WPF-тип.
using FontFamily = System.Windows.Media.FontFamily;

namespace Launcher.App.Services;

/// <summary>
/// Рисует разобранный Markdown элементами WPF для блока новостей. Разбор общий с Avalonia
/// (<see cref="MarkdownParser"/>) — здесь только оформление.
///
/// Безопасность: строим только текстовые элементы и ссылки. HTML не исполняется, картинки из
/// разметки не грузятся автоматически — ссылка на первую вынимается отдельно и показывается
/// штатным блоком новости.
/// </summary>
public static class MarkdownRenderer
{
    private const string CodeFont = "Consolas, Courier New, monospace";

    /// <summary>
    /// Вынимает URL первой картинки из разметки и отдаёт текст уже без неё — картинку показывает
    /// штатный блок изображения новости, дублировать её в тексте не нужно.
    /// </summary>
    public static string ExtractFirstImage(string markdown, out string imageUrl)
        => MarkdownParser.ExtractFirstImage(markdown, out imageUrl);

    /// <summary>
    /// Превращает Markdown в список блоков для вертикального контейнера.
    /// <paramref name="openLink"/> вызывается при клике по ссылке (лаунчер открывает её в браузере).
    /// </summary>
    public static IReadOnlyList<UIElement> Render(string markdown, Action<string> openLink)
    {
        var elements = new List<UIElement>();
        foreach (var block in MarkdownParser.Parse(markdown))
        {
            elements.Add(block.Kind switch
            {
                MarkdownBlockKind.Heading => CreateHeading(block, openLink),
                MarkdownBlockKind.ListItem => CreateListItem(block, openLink),
                MarkdownBlockKind.Quote => CreateQuote(block, openLink),
                MarkdownBlockKind.CodeBlock => CreateCodeBlock(block.RawText),
                MarkdownBlockKind.Rule => CreateRule(),
                _ => CreateParagraph(block, openLink)
            });
        }

        return elements;
    }

    private static TextBlock CreateParagraph(MarkdownBlock block, Action<string> openLink)
    {
        var text = BaseTextBlock("MutedBrush", 12);
        text.Margin = new Thickness(0, 0, 0, 8);
        AppendInlines(text, block.Inlines, openLink);
        return text;
    }

    private static TextBlock CreateHeading(MarkdownBlock block, Action<string> openLink)
    {
        var text = BaseTextBlock("TextBrush", block.HeadingLevel switch { 1 => 15, 2 => 14, _ => 13 });
        text.FontWeight = FontWeights.Bold;
        text.Margin = new Thickness(0, block.HeadingLevel == 1 ? 2 : 8, 0, 6);
        AppendInlines(text, block.Inlines, openLink);
        return text;
    }

    private static FrameworkElement CreateListItem(MarkdownBlock block, Action<string> openLink)
    {
        var grid = new Grid { Margin = new Thickness(2, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var bullet = BaseTextBlock("AccentBrush", 12);
        bullet.Text = block.ListMarker;
        bullet.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(bullet, 0);

        var content = BaseTextBlock("MutedBrush", 12);
        AppendInlines(content, block.Inlines, openLink);
        Grid.SetColumn(content, 2);

        grid.Children.Add(bullet);
        grid.Children.Add(content);
        return grid;
    }

    private static FrameworkElement CreateQuote(MarkdownBlock block, Action<string> openLink)
    {
        var content = BaseTextBlock("MutedBrush", 12);
        content.FontStyle = FontStyles.Italic;
        AppendInlines(content, block.Inlines, openLink);

        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 4, 0, 4),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Child = content
        };
        border.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        return border;
    }

    private static FrameworkElement CreateCodeBlock(string code)
    {
        var content = BaseTextBlock("TextBrush", 11);
        content.Text = code;
        content.FontFamily = new FontFamily(CodeFont);

        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            Child = content
        };
        border.SetResourceReference(Border.BackgroundProperty, "CardAltBrush");
        return border;
    }

    private static FrameworkElement CreateRule()
    {
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 10)
        };
        rule.SetResourceReference(Border.BackgroundProperty, "StrokeBrush");
        return rule;
    }

    private static TextBlock BaseTextBlock(string brushKey, double fontSize)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize
        };

        // Через ResourceReference, а не готовой кистью: при смене темы цвета обновятся сами.
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary>Раскладывает разобранные фрагменты в Inlines; ссылка — кликабельный Hyperlink.</summary>
    private static void AppendInlines(TextBlock block, IReadOnlyList<MarkdownInline> inlines, Action<string> openLink)
    {
        foreach (var inline in inlines)
        {
            var run = CreateRun(inline);
            if (inline.LinkUrl is null)
            {
                block.Inlines.Add(run);
                continue;
            }

            var link = new Hyperlink(run) { Cursor = System.Windows.Input.Cursors.Hand };
            // Тоже через ResourceReference: иначе ссылки не перекрасятся при смене темы.
            link.SetResourceReference(Hyperlink.ForegroundProperty, "AccentBrush");
            var url = inline.LinkUrl;
            link.Click += (_, _) => openLink(url);
            block.Inlines.Add(link);
        }
    }

    private static Run CreateRun(MarkdownInline inline)
    {
        var run = new Run(inline.Text);

        if (inline.Bold)
        {
            run.FontWeight = FontWeights.Bold;
        }

        if (inline.Italic)
        {
            run.FontStyle = FontStyles.Italic;
        }

        if (inline.Strikethrough)
        {
            run.TextDecorations = TextDecorations.Strikethrough;
        }

        if (inline.Code)
        {
            run.FontFamily = new FontFamily(CodeFont);
        }

        return run;
    }
}
