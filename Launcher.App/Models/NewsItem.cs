using System.Text.Json.Serialization;

namespace Launcher.App.Models;

public sealed class NewsItem
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("image_url")]
    public string ImageUrl { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
}
