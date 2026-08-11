using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Launcher.App.Services;

namespace Launcher.Avalonia.Rendering;

/// <summary>
/// Рисует разобранный Markdown контролами Avalonia. Разбор общий с WPF
/// (<see cref="MarkdownParser"/>) — здесь только оформление.
/// </summary>
/// <remarks>
/// Отличия от WPF-рендерера: в Avalonia нет инлайна <c>Hyperlink</c>, поэтому ссылка — это
/// <see cref="Run"/> с подчёркиванием, а клики ловит сам <see cref="TextBlock"/>: он определяет,
/// по какому фрагменту попали. Так ссылка остаётся частью потока текста, а не отдельной кнопкой,
/// которая ломала бы перенос строк.
/// </remarks>
public static class MarkdownView
{
    public static IReadOnlyList<Control> Render(
        string markdown,
        IResourceHost resources,
        Action<string> openLink)
    {
        var controls = new List<Control>();
        foreach (var block in MarkdownParser.Parse(markdown))
        {
            var control = block.Kind switch
            {
                MarkdownBlockKind.Heading => CreateHeading(block, resources, openLink),
                MarkdownBlockKind.ListItem => CreateListItem(block, resources, openLink),
                MarkdownBlockKind.Quote => CreateQuote(block, resources, openLink),
                MarkdownBlockKind.CodeBlock => CreateCodeBlock(block, resources),
                MarkdownBlockKind.Rule => CreateRule(resources),
                _ => CreateParagraph(block, resources, openLink)
            };

            controls.Add(control);
        }

        return controls;
    }

    private static TextBlock CreateParagraph(MarkdownBlock block, IResourceHost resources, Action<string> openLink)
    {
        var text = BaseText(resources, "MutedBrush", 12);
        text.Margin = new Thickness(0, 0, 0, 8);
        FillInlines(text, block.Inlines, resources, openLink);
        return text;
    }

    private static TextBlock CreateHeading(MarkdownBlock block, IResourceHost resources, Action<string> openLink)
    {
        // Размер убывает с уровнем, но не мельче обычного текста.
        var size = Math.Max(13, 19 - block.HeadingLevel * 2);
        var text = BaseText(resources, "TextBrush", size);
        text.FontWeight = FontWeight.SemiBold;
        text.Margin = new Thickness(0, block.HeadingLevel <= 2 ? 10 : 6, 0, 6);
        FillInlines(text, block.Inlines, resources, openLink);
        return text;
    }

    private static Control CreateListItem(MarkdownBlock block, IResourceHost resources, Action<string> openLink)
    {
        var marker = BaseText(resources, "AccentBrush", 12);
        marker.Text = block.ListMarker;
        marker.Margin = new Thickness(0, 0, 6, 0);
        marker.VerticalAlignment = VerticalAlignment.Top;

        var body = BaseText(resources, "MutedBrush", 12);
        FillInlines(body, block.Inlines, resources, openLink);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(6, 0, 0, 6)
        };
        Grid.SetColumn(marker, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(marker);
        grid.Children.Add(body);
        return grid;
    }

    private static Control CreateQuote(MarkdownBlock block, IResourceHost resources, Action<string> openLink)
    {
        var body = BaseText(resources, "MutedBrush", 12);
        body.FontStyle = FontStyle.Italic;
        FillInlines(body, block.Inlines, resources, openLink);

        return new Border
        {
            // Цветная полоса слева — привычный вид цитаты.
            BorderBrush = Brush(resources, "AccentBrush", Brushes.Goldenrod),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 2, 0, 2),
            Margin = new Thickness(0, 0, 0, 8),
            Child = body
        };
    }

    private static Control CreateCodeBlock(MarkdownBlock block, IResourceHost resources)
    {
        var text = new TextBlock
        {
            Text = block.RawText,
            FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace"),
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Brush(resources, "TextBrush", Brushes.White)
        };

        return new Border
        {
            Background = Brush(resources, "CardAltBrush", Brushes.Black),
            BorderBrush = Brush(resources, "StrokeBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            // Длинная строка кода не должна растягивать всю панель новостей.
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = text
            }
        };
    }

    private static Control CreateRule(IResourceHost resources) => new Rectangle
    {
        Height = 1,
        Margin = new Thickness(0, 6, 0, 10),
        Fill = Brush(resources, "StrokeBrush", Brushes.Gray)
    };

    /// <summary>
    /// Раскладывает фрагменты в TextBlock и вешает обработку кликов по ссылкам.
    /// </summary>
    private static void FillInlines(
        TextBlock target,
        IReadOnlyList<MarkdownInline> inlines,
        IResourceHost resources,
        Action<string> openLink)
    {
        target.Inlines ??= [];
        var linkBrush = Brush(resources, "AccentBrush", Brushes.Goldenrod);
        var codeBrush = Brush(resources, "TextBrush", Brushes.White);
        var links = new List<(Run Run, string Url)>();

        foreach (var inline in inlines)
        {
            var run = new Run(inline.Text);

            if (inline.Bold)
            {
                run.FontWeight = FontWeight.SemiBold;
            }

            if (inline.Italic)
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (inline.Code)
            {
                run.FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace");
                run.Foreground = codeBrush;
            }

            if (inline.Strikethrough)
            {
                run.TextDecorations = TextDecorations.Strikethrough;
            }

            if (inline.LinkUrl is not null)
            {
                run.Foreground = linkBrush;
                run.TextDecorations = TextDecorations.Underline;
                links.Add((run, inline.LinkUrl));
            }

            target.Inlines.Add(run);
        }

        if (links.Count == 0)
        {
            return;
        }

        target.Cursor = new Cursor(StandardCursorType.Hand);
        target.PointerPressed += (_, args) =>
        {
            // Определяем, по какому фрагменту попали: у Avalonia инлайны сами клики не ловят.
            var point = args.GetPosition(target);
            var hit = target.TextLayout.HitTestPoint(point);
            if (!hit.IsInside)
            {
                return;
            }

            var offset = 0;
            foreach (var inline in target.Inlines!)
            {
                var length = inline is Run run ? run.Text?.Length ?? 0 : 0;
                if (hit.TextPosition >= offset && hit.TextPosition < offset + length)
                {
                    var match = links.FirstOrDefault(l => ReferenceEquals(l.Run, inline));
                    if (match.Url is not null)
                    {
                        openLink(match.Url);
                    }

                    return;
                }

                offset += length;
            }
        };
    }

    private static TextBlock BaseText(IResourceHost resources, string brushKey, double fontSize) => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = fontSize,
        Foreground = Brush(resources, brushKey, Brushes.White)
    };

    private static IBrush Brush(IResourceHost resources, string key, IBrush fallback)
        => resources.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;
}
