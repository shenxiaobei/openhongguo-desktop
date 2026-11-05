using System.Text.Json.Serialization;

namespace Hongguo.Desktop;

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
