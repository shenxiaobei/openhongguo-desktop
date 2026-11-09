using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private sealed record DanmakuSegment(long StartMS, long EndMS, List<DiscussionEntry> Items);

    private readonly List<DanmakuSegment> _rollingDanmakuSegments = [];
    private readonly List<DiscussionEntry> _rollingDanmakuItems = [];
    private CancellationTokenSource? _rollingDanmakuRequest;
    private int _rollingDanmakuVersion;
    private bool _rollingDanmakuBusy;
    private bool _rollingDanmakuDirty = true;
    private string _rollingDanmakuSeriesId = "";
    private string _rollingDanmakuEpisodeId = "";
    private string _rollingDanmakuStatus = "";
    private DateTime _rollingDanmakuRetryAt = DateTime.MinValue;
    private int _rollingDanmakuFailures;
    private long _rollingDanmakuRequestedPositionMS;

    private void ToggleDanmaku_Click(object sender, RoutedEventArgs e)
    {
        _danmakuSettings.Enabled = !_danmakuSettings.Enabled;
        ApplyDanmakuSettings();
        SaveDanmakuSettings();
    }

    private void ApplyDanmakuSettings()
    {
        _danmakuSettings.Normalize();
        _rollingDanmakuDirty = true;
        _rollingDanmakuRetryAt = DateTime.MinValue;
        _rollingDanmakuFailures = 0;
        if (!_danmakuSettings.Enabled)
        {
            CancelRollingDanmakuRequest();
            ClearRollingDanmakuDisplay();
            _rollingDanmakuStatus = "";
        }
        else if (_currentPlaybackInfo != null && _currentEpisode != null && _currentSeries != null)
        {
            PrepareRollingDanmakuForEpisode();
            if (_player != null) RenderRollingDanmaku(_player.GetStatus());
        }
        UpdateDanmakuQuickButton();
    }

    private void UpdateDanmakuQuickButton()
    {
        if (DanmakuToggleButton == null) return;
        DanmakuToggleGlyph.Foreground = (Brush)FindResource(_danmakuSettings.Enabled ? "AccentBrush" : "MutedBrush");
        string state = _danmakuSettings.Enabled ? "弹幕已开启 · 点击关闭" : "弹幕已关闭 · 点击开启";
        DanmakuToggleButton.ToolTip = state + " · D\n齿轮 → 弹幕：范围、字号、透明度、速度与密度"
            + (_rollingDanmakuStatus.Length == 0 ? "" : "\n" + _rollingDanmakuStatus);
        AutomationProperties.SetName(DanmakuToggleButton, state);
        AutomationProperties.SetItemStatus(DanmakuToggleButton, _rollingDanmakuStatus);
    }

    private void CancelRollingDanmakuRequest()
    {
        _rollingDanmakuVersion++;
        _rollingDanmakuRequest?.Cancel();
        _rollingDanmakuRequest?.Dispose();
        _rollingDanmakuRequest = null;
        _rollingDanmakuBusy = false;
    }

    private void ClearRollingDanmakuDisplay()
    {
        try { _player?.ClearDanmaku(); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            // 切集或退出时原生播放器可能已经释放，弹幕清理不阻断媒体生命周期。
        }
    }

    private void ResetRollingDanmaku()
    {
        CancelRollingDanmakuRequest();
        ClearRollingDanmakuDisplay();
        _rollingDanmakuSegments.Clear();
        _rollingDanmakuItems.Clear();
        _rollingDanmakuSeriesId = "";
        _rollingDanmakuEpisodeId = "";
        _rollingDanmakuStatus = "";
        _rollingDanmakuDirty = true;
        _rollingDanmakuFailures = 0;
        _rollingDanmakuRetryAt = DateTime.MinValue;
        _rollingDanmakuRequestedPositionMS = 0;
        UpdateDanmakuQuickButton();
    }

    private void PrepareRollingDanmakuForEpisode()
    {
        if (_currentSeries == null || _currentEpisode == null || _closing) return;
        if (_rollingDanmakuSeriesId != _currentSeries.Id || _rollingDanmakuEpisodeId != _currentEpisode.Id)
        {
            ResetRollingDanmaku();
            _rollingDanmakuSeriesId = _currentSeries.Id;
            _rollingDanmakuEpisodeId = _currentEpisode.Id;
        }
        // 同一集切换画质或交换预加载播放器也会重新加载字幕轨，但复用当前集的正文。
        _rollingDanmakuDirty = true;
        _ = LoadRollingDanmakuAsync(_position);
    }

    private long GetCurrentDanmakuDurationMS()
    {
        if (_currentPlaybackInfo == null) return 0;
        long durationMS = _currentPlaybackInfo.DurationMS;
        if (durationMS <= 0)
        {
            double duration = _duration > 0 ? _duration : _currentPlaybackInfo.DurationSeconds;
            if (double.IsFinite(duration) && duration > 0 && duration <= 86400)
                durationMS = (long)Math.Ceiling(duration * 1000);
        }
        return durationMS is > 0 and <= 86400000 ? durationMS : 0;
    }

    private void UpdateRollingDanmaku(MpvStatus status)
    {
        if (!_danmakuSettings.Enabled || _closing || !status.Loaded) return;
        RenderRollingDanmaku(status);
        _ = LoadRollingDanmakuAsync(status.Position);
    }

    private void RenderRollingDanmaku(MpvStatus status)
    {
        if (!_rollingDanmakuDirty || !_danmakuSettings.Enabled || !status.FrameReady || _player == null || _closing) return;
        try
        {
            _player.SetDanmaku(_rollingDanmakuItems, _danmakuSettings);
            _rollingDanmakuDirty = false;
        }
        catch (Exception)
        {
            // 渲染失败只影响弹幕，不暂停视频；重新开启或修改设置时可再试。
            _rollingDanmakuDirty = false;
            _rollingDanmakuStatus = "弹幕显示暂未就绪，可关闭后重新开启。";
            UpdateDanmakuQuickButton();
        }
    }

    private async Task LoadRollingDanmakuAsync(double positionSeconds)
    {
        if (!_danmakuSettings.Enabled || _backend == null || _closing
            || _currentSeries == null || _currentEpisode == null
            || _rollingDanmakuSeriesId != _currentSeries.Id || _rollingDanmakuEpisodeId != _currentEpisode.Id) return;
        long durationMS = GetCurrentDanmakuDurationMS();
        if (durationMS == 0 || !double.IsFinite(positionSeconds)) return;
        long positionMS = (long)Math.Clamp(positionSeconds * 1000, 0, durationMS);
        if (Math.Abs(positionMS - _rollingDanmakuRequestedPositionMS) > 30000)
        {
            // 远距离拖动后，旧时间段的慢请求不应挡住当前位置的弹幕。
            if (_rollingDanmakuBusy) CancelRollingDanmakuRequest();
            _rollingDanmakuFailures = 0;
            _rollingDanmakuRetryAt = DateTime.MinValue;
        }
        if (_rollingDanmakuBusy || DateTime.UtcNow < _rollingDanmakuRetryAt) return;
        long neededEnd = Math.Min(durationMS, positionMS + 12000);
        // 慢速最多停留16秒，回看18秒覆盖拖动后仍在画面中的文字，只读取附近时间段。
        long startMS = Math.Max(0, positionMS - 18000) / 30000 * 30000;
        foreach (DanmakuSegment segment in _rollingDanmakuSegments.OrderBy(item => item.StartMS))
        {
            if (segment.StartMS > startMS) break;
            if (segment.EndMS > startMS) startMS = segment.EndMS;
        }
        if (startMS >= neededEnd) return;

        _rollingDanmakuRequest?.Dispose();
        _rollingDanmakuRequest = new CancellationTokenSource();
        CancellationToken token = _rollingDanmakuRequest.Token;
        int version = _rollingDanmakuVersion;
        string seriesId = _rollingDanmakuSeriesId;
        string episodeId = _rollingDanmakuEpisodeId;
        _rollingDanmakuBusy = true;
        _rollingDanmakuRequestedPositionMS = positionMS;
        bool IsCurrent() => !token.IsCancellationRequested && !_closing && _danmakuSettings.Enabled
            && version == _rollingDanmakuVersion && _currentSeries?.Id == seriesId && _currentEpisode?.Id == episodeId;
        try
        {
            DiscussionPage page = ModelJson.Parse<DiscussionPage>(await _backend.CallAsync("danmaku", new
            {
                series_id = seriesId, episode_id = episodeId,
                cursor = startMS == 0 ? "" : startMS.ToString(CultureInfo.InvariantCulture), duration_ms = durationMS
            }, token));
            if (!IsCurrent()) return;
            long endMS;
            bool limited = page.SourceLimited;
            if (!page.HasMore && !limited) endMS = durationMS;
            else if (page.HasMore && long.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out long next)
                && next > startMS && next <= durationMS) endMS = next;
            else
            {
                // 来源缺少续段信息时仅记下本次尝试，避免每个计时周期重复请求同一段。
                endMS = Math.Min(durationMS, startMS + 30000);
                limited = true;
            }
            List<DiscussionEntry> entries = [];
            foreach (DiscussionEntry item in page.Items ?? [])
            {
                if (string.IsNullOrWhiteSpace(item.Text) || item.PositionMS is not long time || time < startMS || time > durationMS) continue;
                entries.Add(item);
                if (entries.Count >= 500) break;
            }
            _rollingDanmakuSegments.Add(new DanmakuSegment(startMS, endMS, entries));
            // 只保存离当前播放位置最近的十二段，远距离回看时重新读取，内存不会随观看增长。
            if (_rollingDanmakuSegments.Count > 12)
            {
                DanmakuSegment[] nearest = _rollingDanmakuSegments.OrderBy(item => Math.Abs(item.StartMS - positionMS)).Take(12).ToArray();
                _rollingDanmakuSegments.Clear();
                _rollingDanmakuSegments.AddRange(nearest);
            }
            _rollingDanmakuItems.Clear();
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (DiscussionEntry item in _rollingDanmakuSegments.SelectMany(segment => segment.Items).OrderBy(item => item.PositionMS))
            {
                if (seen.Add(DiscussionKey(item))) _rollingDanmakuItems.Add(item);
            }
            _rollingDanmakuDirty = true;
            _rollingDanmakuFailures = 0;
            _rollingDanmakuRetryAt = DateTime.MinValue;
            _rollingDanmakuStatus = limited ? "当前弹幕来源信息不完整，已显示有效内容。" : entries.Count == 0 ? "当前时间段暂无弹幕。" : "";
            if (_player != null) RenderRollingDanmaku(_player.GetStatus());
            UpdateDanmakuQuickButton();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsCurrent()) return;
            _rollingDanmakuFailures++;
            int seconds = _rollingDanmakuFailures == 1 ? 5 : _rollingDanmakuFailures == 2 ? 15 : 60;
            _rollingDanmakuRetryAt = DateTime.UtcNow.AddSeconds(seconds);
            _rollingDanmakuStatus = "弹幕读取稍有延迟，正在重试；视频继续播放。";
            UpdateDanmakuQuickButton();
        }
        finally
        {
            if (version == _rollingDanmakuVersion) _rollingDanmakuBusy = false;
        }
    }
}
