using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<DiscussionEntry> _discussionItems = [];
    private CancellationTokenSource? _engagementRequest;
    private EngagementInfo? _engagement;
    private int _engagementVersion;
    private string _discussionTab = "comments";
    private string _engagementStatus = "";

    private CancellationTokenSource? _discussionRequest;
    private int _discussionVersion;
    private bool _discussionBusy;
    private bool _discussionLoaded;
    private bool _discussionHasMore;
    private bool _discussionLimited;
    private string _discussionCursor = "";
    private string _discussionError = "";
    private string _discussionNote = "";
    private long? _discussionTotal;

    private async Task RefreshEngagementAsync()
    {
        _engagementRequest?.Cancel();
        _engagementRequest?.Dispose();
        _engagementRequest = new CancellationTokenSource();
        CancellationToken token = _engagementRequest.Token;
        int version = ++_engagementVersion;
        _engagement = null;
        ResetDiscussion();
        _engagementStatus = "正在读取本集互动信息…";
        EngagementText.Text = _engagementStatus;
        UpdateDiscussionStatus();
        if (_backend == null || _currentSeries == null || _closing) return;
        string seriesId = _currentSeries.Id;
        string episodeId = _currentEpisode?.Id ?? "";
        int detailVersion = _detailVersion;
        if (DiscussionPanel.Visibility == Visibility.Visible) _ = LoadDiscussionAsync();
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
        if (count is null or < 0) return "—";
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
        DiscussionMoreButton.IsEnabled = !_discussionBusy;
        bool reloadLimited = _discussionTab == "danmaku" && _discussionLimited && !_discussionHasMore;
        DiscussionMoreButton.Content = _discussionError.Length > 0 ? "重试" : reloadLimited ? "重新读取弹幕" : "加载下一段弹幕";
        DiscussionMoreButton.Visibility = _discussionHasMore || _discussionError.Length > 0 || reloadLimited
            ? Visibility.Visible : Visibility.Collapsed;
        if (_discussionBusy)
        {
            DiscussionStatus.Text = _discussionItems.Count > 0 ? $"已加载 {_discussionItems.Count} 条，正在读取下一段…" : "正在读取讨论…";
            return;
        }
        if (_discussionError.Length > 0)
        {
            DiscussionStatus.Text = _discussionError;
            return;
        }
        if (!_discussionLoaded)
        {
            DiscussionStatus.Text = _currentEpisode == null ? "选择一集后查看评论与弹幕。" : "打开讨论区读取内容。";
            return;
        }
        if (_discussionTab == "comments")
        {
            string total = _discussionTotal is >= 0 ? $"，官网总计 {CountLabel(_discussionTotal)} 条" : "";
            DiscussionStatus.Text = $"官网评论预览 · 已显示 {_discussionItems.Count} 条{total}。\n仅展示官网公开预览，非完整评论列表。";
        }
        else
        {
            DiscussionStatus.Text = $"弹幕列表 · 已加载 {_discussionItems.Count} 条，点击可定位。\n片内滚动弹幕独立播放；点击主栏「弹」开关，齿轮中调整样式。";
            if (!_discussionHasMore && !_discussionLimited) DiscussionStatus.Text += "\n本集时间段已加载完毕。";
        }
        if (_discussionTab == "danmaku" && _discussionLimited && !string.IsNullOrWhiteSpace(_discussionNote))
            DiscussionStatus.Text += "\n" + _discussionNote;
    }

    private void CancelDiscussionRequest()
    {
        _discussionVersion++;
        _discussionRequest?.Cancel();
        _discussionRequest?.Dispose();
        _discussionRequest = null;
        _discussionBusy = false;
    }

    private void ResetDiscussion()
    {
        CancelDiscussionRequest();
        _discussionItems.Clear();
        _discussionCursor = "";
        _discussionLoaded = false;
        _discussionHasMore = false;
        _discussionLimited = false;
        _discussionTotal = null;
        _discussionError = "";
        _discussionNote = "";
        DiscussionScroll.ScrollToTop();
    }

    private async Task LoadDiscussionAsync(bool more = false)
    {
        if (_backend == null || _currentSeries == null || _currentEpisode == null || _closing
            || DiscussionPanel.Visibility != Visibility.Visible || _discussionBusy || (more && !_discussionHasMore)) return;
        string tab = _discussionTab;
        // 优先保留源时长的毫秒精度，防止整数秒截掉片尾弹幕；兼容旧后端与测试夹具。
        long durationMS = GetCurrentDanmakuDurationMS();
        if (tab == "danmaku" && durationMS == 0)
        {
            _discussionError = "播放时长就绪后可读取弹幕，请稍后重试。";
            UpdateDiscussionStatus();
            return;
        }
        CancelDiscussionRequest();
        _discussionRequest = new CancellationTokenSource();
        CancellationToken token = _discussionRequest.Token;
        int version = _discussionVersion;
        int detailVersion = _detailVersion;
        string seriesId = _currentSeries.Id;
        string episodeId = _currentEpisode.Id;
        string cursor = more ? _discussionCursor : "";
        _discussionBusy = true;
        _discussionError = "";
        UpdateDiscussionStatus();
        bool IsCurrent() => !token.IsCancellationRequested && !_closing && version == _discussionVersion
            && detailVersion == _detailVersion && _currentSeries?.Id == seriesId && _currentEpisode?.Id == episodeId
            && _discussionTab == tab && DiscussionPanel.Visibility == Visibility.Visible;
        try
        {
            object args = tab == "comments" ? new { series_id = seriesId, episode_id = episodeId } : (object)new
            {
                series_id = seriesId, episode_id = episodeId, cursor,
                duration_ms = durationMS
            };
            DiscussionPage page = ModelJson.Parse<DiscussionPage>(await _backend.CallAsync(tab, args, token));
            // 换集、换标签或关闭面板后，旧请求既不能填入正文，也不能覆盖新请求的状态。
            if (!IsCurrent()) return;
            if (!more) _discussionItems.Clear();
            HashSet<string> keys = new(_discussionItems.Select(DiscussionKey), StringComparer.Ordinal);
            foreach (DiscussionEntry item in page.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Text)) continue;
                if (tab == "danmaku" && (item.PositionMS == null || item.PositionMS < 0 || item.PositionMS > durationMS)) continue;
                if (!keys.Add(DiscussionKey(item))) continue;
                int index = _discussionItems.Count;
                if (tab == "danmaku")
                {
                    // 时间窗口可能重叠，追加页面后仍保持整份列表按播放时间排序。
                    while (index > 0 && _discussionItems[index - 1].PositionMS > item.PositionMS) index--;
                }
                _discussionItems.Insert(index, item);
            }
            _discussionLoaded = true;
            _discussionTotal = page.Total;
            _discussionLimited = page.SourceLimited || (more && _discussionLimited);
            if (!more || page.SourceLimited) _discussionNote = page.Note;
            _discussionHasMore = tab == "danmaku" && page.HasMore && !string.IsNullOrWhiteSpace(page.Cursor) && page.Cursor != cursor;
            _discussionCursor = _discussionHasMore ? page.Cursor : "";
            if (tab == "danmaku" && page.HasMore && !_discussionHasMore)
            {
                _discussionLimited = true;
                _discussionNote = "后续时间段游标未推进，已保留当前内容。";
            }
            if (!more) DiscussionScroll.ScrollToTop();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsCurrent()) return;
            _discussionError = _discussionItems.Count > 0
                ? "下一段读取失败，已保留当前内容，可点击重试。" : "本次讨论内容读取失败，请点击重试。";
        }
        finally
        {
            if (IsCurrent())
            {
                _discussionBusy = false;
                UpdateDiscussionStatus();
            }
        }
    }

    private static string DiscussionKey(DiscussionEntry item)
    {
        // 弹幕跨页 ID 可能变化，按时间与正文去重；普通评论优先使用稳定 ID。
        if (item.PositionMS != null) return $"{item.PositionMS}\n{item.Text}";
        return string.IsNullOrEmpty(item.Id) ? $"{item.Author}\n{item.CreatedAt}\n{item.Text}" : item.Id;
    }

    private async void Discussion_Click(object sender, RoutedEventArgs e)
    {
        if (_currentSeries == null || _closing) return;
        DiscussionPanel.Visibility = Visibility.Visible;
        EpisodeSidebar.Visibility = Visibility.Collapsed;
        UpdateDiscussionStatus();
        if (_engagement == null) _ = RefreshEngagementAsync();
        if (!_discussionLoaded) await LoadDiscussionAsync();
    }

    private void CloseDiscussionPanel()
    {
        DiscussionPanel.Visibility = Visibility.Collapsed;
        CancelDiscussionRequest();
        EpisodeSidebar.Visibility = IsImmersivePlayback ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CloseDiscussion_Click(object sender, RoutedEventArgs e) => CloseDiscussionPanel();

    private async void CommentsTab_Click(object sender, RoutedEventArgs e)
    {
        if (_discussionTab == "comments" && (_discussionLoaded || _discussionBusy)) return;
        _discussionTab = "comments";
        ResetDiscussion();
        await LoadDiscussionAsync();
    }

    private async void DanmakuTab_Click(object sender, RoutedEventArgs e)
    {
        if (_discussionTab == "danmaku" && (_discussionLoaded || _discussionBusy)) return;
        _discussionTab = "danmaku";
        ResetDiscussion();
        await LoadDiscussionAsync();
    }

    private void DiscussionItem_Click(object sender, RoutedEventArgs e)
    {
        if (_discussionTab != "danmaku" || (sender as Button)?.Tag is not DiscussionEntry item
            || item.PositionSeconds is not double seconds || _player == null || !SeekSlider.IsEnabled) return;
        _player.Seek(Math.Clamp(seconds, 0, Math.Max(0, _duration)));
    }

    private async void DiscussionMore_Click(object sender, RoutedEventArgs e) => await LoadDiscussionAsync(_discussionLoaded && _discussionHasMore);

    private void CancelDiscoveryRequests()
    {
        ResetRollingDanmaku();
        CancelDiscussionRequest();
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
