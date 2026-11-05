using System.Text.Json.Serialization;

namespace Hongguo.Desktop;

public class GenreOption
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

public class GenreRow
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("options")] public List<GenreOption> Options { get; set; } = [];
}

public class GenrePanel
{
    [JsonPropertyName("rows")] public List<GenreRow> Rows { get; set; } = [];
}
