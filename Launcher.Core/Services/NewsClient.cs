using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
using Launcher.App.Models;

namespace Launcher.App.Services;

public sealed class NewsClient(HttpClient httpClient)
{
    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(string newsUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newsUrl))
        {
            return [];
        }

        var content = await ReadContentAsync(newsUrl, cancellationToken);
        var news = IsXml(content)
            ? ParseRss(content)
            : ParseJson(content);

        return news
            .Where(item => !string.IsNullOrWhiteSpace(item.Title) || !string.IsNullOrWhiteSpace(item.Description))
            .OrderByDescending(item => item.CreatedAt)
            .ToList();
    }

    private async Task<string> ReadContentAsync(string newsUrl, CancellationToken cancellationToken)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(newsUrl);
        if (File.Exists(expandedPath))
        {
            return await File.ReadAllTextAsync(Path.GetFullPath(expandedPath), cancellationToken);
        }

        var uri = new Uri(newsUrl, UriKind.Absolute);
        if (uri.IsFile && File.Exists(uri.LocalPath))
        {
            return await File.ReadAllTextAsync(uri.LocalPath, cancellationToken);
        }

        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static bool IsXml(string content)
    {
        return content.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal);
    }

    private static IReadOnlyList<NewsItem> ParseJson(string content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<NewsItem>();
        var index = 0;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            index++;
            result.Add(new NewsItem
            {
                Id = GetInt32(item, "id", index),
                Title = GetString(item, "title"),
                Description = GetString(item, "description"),
                Url = GetString(item, "url"),
                ImageUrl = GetString(item, "image_url"),
                CreatedAt = ParseRssDate(GetString(item, "createdAt"))
            });
        }

        return result;
    }

    private static IReadOnlyList<NewsItem> ParseRss(string content)
    {
        var document = XDocument.Parse(content);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase))
            .Select((item, index) => new NewsItem
            {
                Id = index + 1,
                Title = GetElementValue(item, "title"),
                Description = GetElementValue(item, "description"),
                Url = GetElementValue(item, "link"),
                CreatedAt = ParseRssDate(GetElementValue(item, "pubDate")),
                ImageUrl = item.Elements().FirstOrDefault(element => element.Name.LocalName.Equals("enclosure", StringComparison.OrdinalIgnoreCase))
                    ?.Attribute("url")?.Value ?? string.Empty
            })
            .ToList();
    }

    private static string GetElementValue(XElement parent, string name)
    {
        return parent.Elements()
            .FirstOrDefault(element => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value?.Trim() ?? string.Empty;
    }

    private static string GetString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.ToString();
    }

    private static int GetInt32(JsonElement parent, string name, int fallback)
    {
        if (!parent.TryGetProperty(name, out var property))
        {
            return fallback;
        }

        return property.TryGetInt32(out var value) ? value : fallback;
    }

    private static DateTimeOffset ParseRssDate(string value)
    {
        return DateTimeOffset.TryParse(value, out var date)
            ? date
            : DateTimeOffset.MinValue;
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }
}
