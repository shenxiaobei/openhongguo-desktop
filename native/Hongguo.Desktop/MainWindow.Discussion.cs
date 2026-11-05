using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<DiscussionItem> _discussionItems = [];
    private CancellationTokenSource? _engagementRequest;
    private EngagementInfo? _engagement;
    private int _engagementVersion;
    private string _discussionTab = "comments";
    private string _engagementStatus = "";

    private sealed class DiscussionItem
    {
        public string Author { get; init; } = "";
        public string Text { get; init; } = "";
        public string Meta { get; init; } = "";
        public double? PositionSeconds { get; init; }
    }

    private async Task RefreshEngagementAsync()
    {
        _engagementRequest?.Cancel();
        _engagementRequest?.Dispose();
        _engagementRequest = new CancellationTokenSource();
        CancellationToken token = _engagementRequest.Token;
        int version = ++_engagementVersion;
        _engagement = null;
        _discussionItems.Clear();
        _engagementStatus = "正在读取本集互动信息…";
        EngagementText.Text = _engagementStatus;
        UpdateDiscussionStatus();
        if (_backend == null || _currentSeries == null || _closing) return;
        string seriesId = _currentSeries.Id;
        string episodeId = _currentEpisode?.Id ?? "";
        int detailVersion = _detailVersion;
        try
        {
            EngagementInfo info = ModelJson.Parse<EngagementInfo>(await _backend.CallAsync("engagement", new
            {
                series_id = seriesId, episode_id = episodeId
            }, token));
            // 连续换集时，旧请求和旧详情都不能更新当前集的统计。
            if (token.IsCancellationRequested || version != _engagementVersion || detailVersion != _detailVersion || _closing
                || _currentSeries?.Id != seriesId || (_currentEpisode?.Id ?? "") != episodeId) return;
            _engagement = info;
            _engagementStatus = "";
            EngagementText.Text = $"播放 {CountLabel(info.PlayCount)} · 追剧 {CountLabel(info.FollowCount)}\n"
                + $"本集赞 {CountLabel(info.EpisodeLikeCount)} · 评论 {CountLabel(info.CommentCount)} · 收藏 {CountLabel(info.FavoriteCount)}";
            UpdateDiscussionStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (version != _engagementVersion || detailVersion != _detailVersion || _closing
                || _currentSeries?.Id != seriesId || (_currentEpisode?.Id ?? "") != episodeId) return;
            _engagementStatus = "互动信息本次没有返回，可重新打开讨论区重试。";
            EngagementText.Text = "播放 — · 追剧 —\n本集赞 — · 评论 — · 收藏 —";
            UpdateDiscussionStatus();
        }
    }

    private static string CountLabel(long? count)
    {
        if (count is null or <= 0) return "—";
        if (count >= 100_000_000) return $"{count / 100_000_000d:0.#}亿";
        if (count >= 10_000) return $"{count / 10_000d:0.#}万";
        return count.Value.ToString("N0");
    }

    private void UpdateDiscussionTabs()
    {
        foreach (Button button in new[] { CommentsTabButton, DanmakuTabButton })
        {
            bool active = button == CommentsTabButton ? _discussionTab == "comments" : _discussionTab == "danmaku";
            button.Background = (Brush)FindResource(active ? "AccentSoftBrush" : "RaisedBrush");
            button.Foreground = (Brush)FindResource(active ? "AccentBrush" : "MutedBrush");
        }
    }

    private void UpdateDiscussionStatus()
    {
        UpdateDiscussionTabs();
        DiscussionMoreButton.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(_engagementStatus))
        {
            DiscussionStatus.Text = _engagementStatus;
            return;
        }
        if (_discussionTab == "comments")
        {
            string count = CountLabel(_engagement?.CommentCount);
            DiscussionStatus.Text = count == "—" ? "当前接口没有返回评论正文，评论量尚未确认。"
                : $"本集评论 {count} 条；当前接口没有返回评论正文。";
        }
        else
        {
            DiscussionStatus.Text = "当前接口没有返回弹幕正文与时间轴，暂未展示片内弹幕。";
        }
        DiscussionStatus.Text += "\n追剧数与收藏量是不同指标，收藏量未返回时显示 —。";
    }

    private async void Discussion_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSeries == null || _closing) return;
        DiscussionPanel.Visibility = Visibility.Visible;
        EpisodeSidebar.Visibility = Visibility.Collapsed;
        UpdateDiscussionStatus();
        if (_engagement == null) await RefreshEngagementAsync();
    }

    private void CloseDiscussionPanel()
    {
        DiscussionPanel.Visibility = Visibility.Collapsed;
        EpisodeSidebar.Visibility = IsImmersivePlayback ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseDiscussion_Click(object sender, RoutedEventArgs e) => CloseDiscussionPanel();

    private void CommentsTab_Click(object sender, RoutedEventArgs e)
    {
        _discussionTab = "comments";
        UpdateDiscussionStatus();
    }

    private void DanmakuTab_Click(object sender, RoutedEventArgs e)
    {
        _discussionTab = "danmaku";
        UpdateDiscussionStatus();
    }

    private void DiscussionItem_Click(object sender, RoutedEventArgs e)
    {
        if (_discussionTab != "danmaku" || (sender as Button)?.Tag is not DiscussionItem item
            || item.PositionSeconds is not double seconds || _player == null || !SeekSlider.IsEnabled) return;
        _player.Seek(Math.Clamp(seconds, 0, Math.Max(0, _duration)));
    }

    private void DiscussionMore_Click(object sender, RoutedEventArgs e) => UpdateDiscussionStatus();

    private void CancelDiscoveryRequests()
    {
        _rankingUpdateTimer.Stop();
        CancelRankGenreRequests();
        _genreOptionsVersion++;
        _engagementVersion++;
        _genreOptionsRequest?.Cancel();
        _genreOptionsRequest?.Dispose();
        _genreOptionsRequest = null;
        _engagementRequest?.Cancel();
        _engagementRequest?.Dispose();
        _engagementRequest = null;
    }
}
