using System.Text.RegularExpressions;

namespace Launcher.App.Services;

/// <summary>Тип блока разметки.</summary>
public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    ListItem,
    Quote,
    CodeBlock,
    Rule
}

/// <param name="Kind">Что это за блок.</param>
/// <param name="Inlines">Содержимое с разметкой; для кода и линейки не используется.</param>
/// <param name="HeadingLevel">Уровень заголовка 1–6, иначе 0.</param>
/// <param name="ListMarker">Маркер пункта списка: «•» или «1.».</param>
/// <param name="RawText">Текст как есть — нужен блокам кода.</param>
public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    IReadOnlyList<MarkdownInline> Inlines,
    int HeadingLevel = 0,
    string ListMarker = "",
    string RawText = "");

/// <param name="Text">Видимый текст.</param>
/// <param name="Bold">Полужирный.</param>
/// <param name="Italic">Курсив.</param>
/// <param name="Code">Моноширинный фрагмент в обратных кавычках.</param>
/// <param name="Strikethrough">Зачёркнутый.</param>
/// <param name="LinkUrl">Адрес ссылки или <c>null</c>, если это обычный текст.</param>
public sealed record MarkdownInline(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Code = false,
    bool Strikethrough = false,
    string? LinkUrl = null);

/// <summary>
/// Разбирает Markdown новостей в модель блоков. Отрисовку делает каждый интерфейс сам
/// (WPF и Avalonia), а правила разбора обязаны быть общими — иначе новости будут выглядеть
/// по-разному на разных системах.
/// </summary>
public static class MarkdownParser
{
    private static readonly Regex ImageRegex = new(@"!\[(?<alt>[^\]]*)\]\((?<url>[^)\s]+)[^)]*\)", RegexOptions.Compiled);
    private static readonly Regex LinkRegex = new(@"\[(?<text>[^\]]+)\]\((?<url>[^)\s]+)[^)]*\)", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^\s{0,3}([*+\-•])\s+(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex OrderedRegex = new(@"^\s{0,3}(?<number>\d{1,2})[.)]\s+(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(@"^\s{0,3}(?<hashes>#{1,6})\s*(?<text>.+?)\s*#*$", RegexOptions.Compiled);
    private static readonly Regex RuleRegex = new(@"^\s{0,3}([-*_])\s*\1\s*\1[\s\-*_]*$", RegexOptions.Compiled);

    /// <summary>
    /// Достаёт первую картинку из текста и убирает её разметку из результата: картинка
    /// показывается отдельным блоком над новостью.
    /// </summary>
    public static string ExtractFirstImage(string markdown, out string imageUrl)
    {
        imageUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown ?? string.Empty;
        }

        var match = ImageRegex.Match(markdown);
        if (!match.Success)
        {
            return markdown;
        }

        imageUrl = match.Groups["url"].Value;
        return markdown.Remove(match.Index, match.Length);
    }

    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return blocks;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();
        var inCodeFence = false;
        var codeLines = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            // Мягкие переносы внутри абзаца склеиваем, как это делает Markdown.
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, ParseInlines(string.Join(" ", paragraph))));
            paragraph.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeFence)
                {
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.CodeBlock, [], RawText: string.Join(Environment.NewLine, codeLines)));
                    codeLines.Clear();
                    inCodeFence = false;
                }
                else
                {
                    FlushParagraph();
                    inCodeFence = true;
                }

                continue;
            }

            if (inCodeFence)
            {
                codeLines.Add(rawLine);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            if (RuleRegex.IsMatch(line))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, []));
                continue;
            }

            var heading = HeadingRegex.Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Heading,
                    ParseInlines(heading.Groups["text"].Value),
                    HeadingLevel: heading.Groups["hashes"].Value.Length));
                continue;
            }

            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, ParseInlines(line.TrimStart().TrimStart('>').Trim())));
                continue;
            }

            var bullet = BulletRegex.Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.ListItem, ParseInlines(bullet.Groups["text"].Value), ListMarker: "•"));
                continue;
            }

            var ordered = OrderedRegex.Match(line);
            if (ordered.Success)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.ListItem,
                    ParseInlines(ordered.Groups["text"].Value),
                    ListMarker: ordered.Groups["number"].Value + "."));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        if (inCodeFence && codeLines.Count > 0)
        {
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.CodeBlock, [], RawText: string.Join(Environment.NewLine, codeLines)));
        }

        FlushParagraph();
        return blocks;
    }

    /// <summary>
    /// Разбирает строку на куски с оформлением. Сначала выделяются ссылки, потом внутри
    /// оставшегося текста — жирный, курсив и код.
    /// </summary>
    public static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        var result = new List<MarkdownInline>();
        if (string.IsNullOrEmpty(text))
        {
            return result;
        }

        var position = 0;
        foreach (Match match in LinkRegex.Matches(text))
        {
            if (match.Index > position)
            {
                result.AddRange(ParseEmphasis(text[position..match.Index]));
            }

            result.Add(new MarkdownInline(match.Groups["text"].Value, LinkUrl: match.Groups["url"].Value));
            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            result.AddRange(ParseEmphasis(text[position..]));
        }

        return result;
    }

    /// <summary>
    /// Строчная разметка: <c>**жирный**</c> (и <c>__</c>), <c>*курсив*</c> (и <c>_</c>),
    /// <c>~~зачёркнутый~~</c>, <c>`код`</c>, экранирование <c>\*</c>.
    /// </summary>
    /// <remarks>
    /// Маркеры именно ПЕРЕКЛЮЧАЮТ состояние, а не выделяют вложенный кусок: так текст
    /// вида <c>**a *b* c**</c> даёт «a» жирным, «b» жирным курсивом и «c» жирным.
    /// Алгоритм перенесён из WPF-версии дословно — иначе новости выглядели бы по-разному
    /// на Windows и на Linux. Маркер без закрывающей пары остаётся обычным символом,
    /// иначе одиночная звёздочка съедала бы остаток строки.
    /// </remarks>
    private static IEnumerable<MarkdownInline> ParseEmphasis(string text)
    {
        var buffer = new System.Text.StringBuilder();
        var result = new List<MarkdownInline>();
        var index = 0;
        var bold = false;
        var italic = false;
        var strike = false;

        void FlushBuffer()
        {
            if (buffer.Length > 0)
            {
                result.Add(new MarkdownInline(buffer.ToString(), bold, italic, false, strike));
                buffer.Clear();
            }
        }

        while (index < text.Length)
        {
            var rest = text.AsSpan(index);

            if (rest.StartsWith("\\") && rest.Length > 1)
            {
                // Экранированный символ (\* \_) выводим как есть.
                buffer.Append(text[index + 1]);
                index += 2;
                continue;
            }

            if ((rest.StartsWith("**") || rest.StartsWith("__")) && HasClosing(text, index, text.Substring(index, 2)))
            {
                FlushBuffer();
                bold = !bold;
                index += 2;
                continue;
            }

            if (rest.StartsWith("~~") && HasClosing(text, index, "~~"))
            {
                FlushBuffer();
                strike = !strike;
                index += 2;
                continue;
            }

            if ((rest[0] == '*' || rest[0] == '_') && HasClosing(text, index, text[index].ToString()))
            {
                FlushBuffer();
                italic = !italic;
                index++;
                continue;
            }

            if (rest[0] == '`')
            {
                var close = text.IndexOf('`', index + 1);
                if (close > index)
                {
                    FlushBuffer();
                    result.Add(new MarkdownInline(text[(index + 1)..close], Code: true));
                    index = close + 1;
                    continue;
                }
            }

            buffer.Append(text[index]);
            index++;
        }

        FlushBuffer();
        return result;
    }

    private static bool HasClosing(string text, int index, string marker) =>
        text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal) >= 0;
}
