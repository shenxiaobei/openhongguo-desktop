using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hongguo.Desktop;

public class SeriesItem : INotifyPropertyChanged
{
    private string _title = "";
    private string _description = "";
    private string _coverUrl = "";
    private int _episodeCount;
    private string _category = "";
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; Notify(); }
    }
    [JsonPropertyName("description")] public string Description
    {
        get => _description;
        set { if (_description == value) return; _description = value; Notify(); }
    }
    [JsonPropertyName("cover_url")] public string CoverUrl
    {
        get => _coverUrl;
        set
        {
            if (_coverUrl == value) return;
            _coverUrl = value;
            Notify();
            Notify(nameof(CoverUri));
        }
    }
    [JsonPropertyName("episode_count")] public int EpisodeCount
    {
        get => _episodeCount;
        set
        {
            if (_episodeCount == value) return;
            _episodeCount = value;
            Notify();
            Notify(nameof(EpisodeLabel));
        }
    }
    [JsonPropertyName("category")] public string Category
    {
        get => _category;
        set
        {
            if (_category == value) return;
            _category = value;
            Notify();
            Notify(nameof(CategoryLabel));
        }
    }
    [JsonPropertyName("right_text")] public string RightText { get; set; } = "";
    [JsonPropertyName("is_multi_season")] public bool IsMultiSeason { get; set; }
    [JsonPropertyName("season_index")] public int SeasonIndex { get; set; }
    [JsonPropertyName("related_album_id")] public string RelatedAlbumId { get; set; } = "";
    public string EpisodeLabel => EpisodeCount > 0 ? $"{EpisodeCount} 集" : "点击查看剧集";
    public string CategoryLabel => string.IsNullOrWhiteSpace(Category) ? "短剧推荐" : Category;
    public Uri? CoverUri
    {
        get
        {
            if (Uri.TryCreate(CoverUrl, UriKind.Absolute, out Uri? uri) && (uri.Scheme == "https" || uri.Scheme == "http")) return uri;
            return null;
        }
    }

    public void ApplySummary(SeriesItem summary)
    {
        // 补图只更新服务端实际返回的字段，不用空值覆盖已经显示的内容。
        if (!string.IsNullOrWhiteSpace(summary.Title)) Title = summary.Title;
        if (!string.IsNullOrWhiteSpace(summary.CoverUrl)) CoverUrl = summary.CoverUrl;
        if (!string.IsNullOrWhiteSpace(summary.Description)) Description = summary.Description;
        if (!string.IsNullOrWhiteSpace(summary.Category)) Category = summary.Category;
        if (summary.EpisodeCount > 0) EpisodeCount = summary.EpisodeCount;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class EpisodeItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("playable")] public bool Playable { get; set; }
    public string Label => Number.ToString();
    public string AccessibleLabel => $"播放第 {Number} 集 {Title}";
}

public class SeriesDetail
{
    [JsonPropertyName("series")] public SeriesItem? Series { get; set; }
    [JsonPropertyName("episodes")] public List<EpisodeItem> Episodes { get; set; } = [];
}

public class CatalogPage
{
    [JsonPropertyName("items")] public List<SeriesItem> Items { get; set; } = [];
    [JsonPropertyName("cursor")] public string Cursor { get; set; } = "";
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("source_label")] public string SourceLabel { get; set; } = "";
    [JsonPropertyName("fallback_reason")] public string FallbackReason { get; set; } = "";
}

public class SeasonItem
{
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("cover_url")] public string CoverUrl { get; set; } = "";
    [JsonPropertyName("episode_count")] public int EpisodeCount { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
}

public class SeasonPage
{
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("album_id")] public string AlbumId { get; set; } = "";
    [JsonPropertyName("link_verified")] public bool LinkVerified { get; set; }
    [JsonPropertyName("seasons")] public List<SeasonItem> Seasons { get; set; } = [];
    [JsonPropertyName("current_index")] public int CurrentIndex { get; set; }
    [JsonPropertyName("next_series_id")] public string NextSeriesId { get; set; } = "";
    [JsonPropertyName("next_index")] public int NextIndex { get; set; }
    [JsonPropertyName("has_next")] public bool HasNext { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

public class PlaybackInfo
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("audio_url")] public string AudioUrl { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("duration_seconds")] public double DurationSeconds { get; set; }
    [JsonPropertyName("duration_ms")] public long DurationMS { get; set; }
    [JsonPropertyName("expires_at")] public long ExpiresAt { get; set; }
    [JsonPropertyName("definition")] public string Definition { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("bitrate")] public long Bitrate { get; set; }
    [JsonPropertyName("available_qualities")] public List<PlaybackQuality> AvailableQualities { get; set; } = [];
}

public class PlaybackQuality
{
    [JsonPropertyName("definition")] public string Definition { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("bitrate")] public long Bitrate { get; set; }
    public string Label => Definition switch
    {
        "best" => "最高画质",
        "worst" => "流畅优先",
        _ => Width > 0 && Height > 0 ? $"{Definition} · {Width}×{Height}" : Definition
    };
}

public class HistoryItem : INotifyPropertyChanged
{
    private string _coverUrl = "";
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("episode")] public int Episode { get; set; }
    [JsonPropertyName("episodes")] public int Episodes { get; set; }
    [JsonPropertyName("finished")] public bool Finished { get; set; }
    [JsonPropertyName("position")] public double Position { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("cover_url")] public string CoverUrl
    {
        get => _coverUrl;
        set
        {
            if (_coverUrl == value) return;
            _coverUrl = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverUrl)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverUri)));
        }
    }
    public Uri? CoverUri => Uri.TryCreate(CoverUrl, UriKind.Absolute, out Uri? uri) && (uri.Scheme == "https" || uri.Scheme == "http") ? uri : null;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string ProgressLabel => Finished ? $"第 {Episode} 集 · 已看完" : $"第 {Episode} 集 · {TimeSpan.FromSeconds(Math.Max(0, Position)):mm\\:ss}";
    public string DateLabel => UpdatedAt.LocalDateTime.ToString("MM-dd HH:mm");
}

internal static class ModelJson
{
    internal static T Parse<T>(JsonElement value) where T : new()
    {
        return value.Deserialize<T>() ?? new T();
    }
}
