using System.Windows;
using System.Windows.Threading;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer _rankingUpdateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Dictionary<string, DateOnly> _rankingUpdatedDays = [];
    private readonly Dictionary<string, DateTime> _rankingRetryAfter = [];
    private bool _checkingDailyRanking;

    private string RankingUpdateKey => IsRankGenreMode
        ? "genre\n" + RankGenreKey
        : "platform\n" + _rankContent + "\n" + _rankBoard;

    private void InitializeRankingUpdates()
    {
        _rankingUpdateTimer.Tick += async (_, _) => await CheckDailyRankingUpdateAsync(DateTime.Now);
        _rankingUpdateTimer.Start();
    }

    private void MarkRankingUpdated()
    {
        DateTime now = DateTime.Now;
        string key = RankingUpdateKey;
        _rankingUpdatedDays[key] = DateOnly.FromDateTime(now);
        _rankingRetryAfter.Remove(key);
        RankingFreshnessText.Text = $"每日自动更新 · 更新于 {now:MM-dd HH:mm}";
    }

    private async Task CheckDailyRankingUpdateAsync(DateTime now)
    {
        if (_closing || _backend == null || _checkingDailyRanking || _rankBusy
            || RankingScreen.Visibility != Visibility.Visible || _rankContents.Count == 0) return;
        string key = RankingUpdateKey;
        DateOnly today = DateOnly.FromDateTime(now);
        // 初次进入由榜单导航正常加载；每日检查只更新已经展示过的数据。
        if (!_rankingUpdatedDays.TryGetValue(key, out DateOnly updated) || updated >= today) return;
        if (_rankingRetryAfter.TryGetValue(key, out DateTime retryAfter) && now < retryAfter) return;

        List<RankedSeriesItem> previous = _ranking.ToList();
        string previousCursor = _rankCursor;
        bool previousHasMore = _rankHasMore;
        double previousScroll = RankScroll.VerticalOffset;
        int expectedVersion = _rankVersion + 1;
        _checkingDailyRanking = true;
        _rankingRetryAfter[key] = now.AddMinutes(10);
        RankingFreshnessText.Text = "正在自动更新今天的榜单…";
        try
        {
            await LoadRankingAsync(false);
            if (_closing || key != RankingUpdateKey || expectedVersion != _rankVersion) return;
            if (_rankingUpdatedDays.GetValueOrDefault(key) >= today) return;

            // 网络失败保留上次成功的榜单，稍后自动重试；旧请求不覆盖用户的新选择。
            _ranking.Clear();
            foreach (RankedSeriesItem item in previous) _ranking.Add(item);
            _rankCursor = previousCursor;
            _rankHasMore = previousHasMore;
            RankMoreButton.Visibility = previousHasMore ? Visibility.Visible : Visibility.Collapsed;
            RankStatus.Text = $"保留上次更新的 {_ranking.Count} 部 · 正在等待自动重试";
            RankingFreshnessText.Text = $"上次更新 {updated:MM-dd} · 10 分钟后自动重试";
            await Dispatcher.InvokeAsync(() => RankScroll.ScrollToVerticalOffset(previousScroll), DispatcherPriority.Loaded);
        }
        finally { _checkingDailyRanking = false; }
    }
}
