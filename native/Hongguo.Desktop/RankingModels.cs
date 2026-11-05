using System.Text.Json.Serialization;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hongguo.Desktop;

public class RankedSeriesItem : INotifyPropertyChanged
{
    private string _title = "";
    private string _coverUrl = "";
    private string _intro = "";
    private int _episodeCount;
    [JsonPropertyName("series_id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; Notify(); }
    }
    [JsonPropertyName("cover_url")] public string CoverUrl
    {
        get => _coverUrl;
        set { if (_coverUrl == value) return; _coverUrl = value; Notify(); }
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
            Notify(nameof(Subtitle));
        }
    }
    [JsonPropertyName("position")] public int Position { get; set; }
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("promoted")] public bool IsPromoted { get; set; }
    [JsonPropertyName("score")] public string Score { get; set; } = "";
    [JsonPropertyName("intro")] public string Intro
    {
        get => _intro;
        set
        {
            if (_intro == value) return;
            _intro = value;
            Notify();
            Notify(nameof(Tagline));
        }
    }
    [JsonIgnore] public bool IsCategoryResult { get; set; }
    public string PositionLabel => IsCategoryResult ? Position.ToString("00") : IsPromoted ? "荐" : Rank > 0 ? Rank.ToString("00") : "—";
    public string PositionColor => IsCategoryResult ? "#A8B5C9" : IsPromoted ? "#D5AA84" : Rank switch { 1 => "#FF8A65", 2 => "#D9B884", 3 => "#B7BFCE", _ => "#606A7E" };
    public string RankContextLabel => IsCategoryResult ? "列表序号" : IsPromoted ? "推荐位" : "平台名次";
    public string EpisodeLabel => EpisodeCount > 0 ? $"{EpisodeCount} 集" : "点击查看剧集";
    public string ScoreLabel => IsCategoryResult ? "平台热度排序" : IsPromoted ? "平台推荐" : Score;
    public string DisplayScoreLabel => string.IsNullOrWhiteSpace(ScoreLabel) ? "平台榜单" : ScoreLabel;
    public string Subtitle => EpisodeLabel;
    public string Tagline => Intro;

    public void ApplySummary(SeriesItem summary)
    {
        // 补齐真实海报和剧集信息，列表顺序与平台榜单字段保持原样。
        if (!string.IsNullOrWhiteSpace(summary.Title)) Title = summary.Title;
        if (!string.IsNullOrWhiteSpace(summary.CoverUrl)) CoverUrl = summary.CoverUrl;
        if (summary.EpisodeCount > 0) EpisodeCount = summary.EpisodeCount;
        if (string.IsNullOrWhiteSpace(Intro) && !string.IsNullOrWhiteSpace(summary.Description)) Intro = summary.Description;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class RankBoardOption
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("selected")] public bool Selected { get; set; }
}

public class RankContentOption : RankBoardOption
{
    [JsonPropertyName("boards")] public List<RankBoardOption> Boards { get; set; } = [];
}

public class RankingPage
{
    [JsonPropertyName("contents")] public List<RankContentOption> Contents { get; set; } = [];
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("board")] public string Board { get; set; } = "";
    [JsonPropertyName("items")] public List<RankedSeriesItem> Items { get; set; } = [];
    [JsonPropertyName("cursor")] public string Cursor { get; set; } = "";
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
}
