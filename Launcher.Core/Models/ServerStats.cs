using System.Text.Json.Serialization;

namespace Launcher.App.Models;

public sealed class ServerStats
{
    public bool Success { get; set; }
    public bool Online { get; set; }
    public int Players { get; set; }
    public double Tps { get; set; }
    public string Ram { get; set; } = string.Empty;

    [JsonPropertyName("recorded_at")]
    public string RecordedAt { get; set; } = string.Empty;
}
