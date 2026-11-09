using System.Text.Json.Serialization;

namespace Hongguo.Desktop;

public sealed class DiscussionPage
{
    [JsonPropertyName("items")] public List<DiscussionEntry> Items { get; set; } = [];
    [JsonPropertyName("total")] public long? Total { get; set; }
    [JsonPropertyName("cursor")] public string Cursor { get; set; } = "";
    [JsonPropertyName("has_more")] public bool HasMore { get; set; }
    [JsonPropertyName("source_limited")] public bool SourceLimited { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "";
}

public sealed class DiscussionEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("like_count")] public long? LikeCount { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }
    [JsonPropertyName("position_ms")] public long? PositionMS { get; set; }
    public string AuthorLabel => string.IsNullOrWhiteSpace(Author) ? "观众" : Author;
    public double? PositionSeconds => PositionMS / 1000d;
    public string Meta
    {
        get
        {
            if (PositionMS is >= 0)
            {
                long seconds = PositionMS.Value / 1000;
                return $"{seconds / 60:00}:{seconds % 60:00} · 点击跳转";
            }
            string date = "";
            // 上游时间缺失或超出范围时仍展示正文，避免单条记录影响整页。
            if (CreatedAt > 0 && CreatedAt <= 253402300799)
                date = DateTimeOffset.FromUnixTimeSeconds(CreatedAt).ToLocalTime().ToString("yyyy-MM-dd");
            string likes = LikeCount is >= 0 ? $"赞 {LikeCount.Value:N0}" : "";
            return string.Join(" · ", new[] { date, likes }.Where(value => value.Length > 0));
        }
    }
}

public class EngagementInfo
{
    [JsonPropertyName("series_id")] public string SeriesId { get; set; } = "";
    [JsonPropertyName("episode_id")] public string EpisodeId { get; set; } = "";
    [JsonPropertyName("play_count")] public long? PlayCount { get; set; }
    [JsonPropertyName("follow_count")] public long? FollowCount { get; set; }
    [JsonPropertyName("series_like_count")] public long? SeriesLikeCount { get; set; }
    [JsonPropertyName("episode_like_count")] public long? EpisodeLikeCount { get; set; }
    [JsonPropertyName("comment_count")] public long? CommentCount { get; set; }
    [JsonPropertyName("favorite_count")] public long? FavoriteCount { get; set; }
    [JsonPropertyName("comments_available")] public bool CommentsAvailable { get; set; }
    [JsonPropertyName("danmaku_available")] public bool DanmakuAvailable { get; set; }
    [JsonPropertyName("note")] public string Note { get; set; } = "";

    public string PlayLabel => FormatCount(PlayCount);
    public string FollowLabel => FormatCount(FollowCount);
    public string LikeLabel => FormatCount(EpisodeLikeCount ?? SeriesLikeCount);
    public string CommentLabel => FormatCount(CommentCount);
    public string FavoriteLabel => FormatCount(FavoriteCount);

    private static string FormatCount(long? count)
    {
        if (count is null) return "—";
        if (count >= 100_000_000) return $"{count / 100_000_000d:0.#}亿";
        if (count >= 10_000) return $"{count / 10_000d:0.#}万";
        return count.Value.ToString("N0");
    }
}
