using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace Hongguo.Desktop;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SeriesItem> _catalog = [];
    private readonly ObservableCollection<EpisodeItem> _episodes = [];
    private readonly ObservableCollection<HistoryItem> _history = [];
    private readonly ObservableCollection<PlaybackQuality> _qualities = [];
    private readonly List<SeriesItem> _feedQueue = [];
    private readonly Dictionary<string, CatalogSnapshot> _catalogCache = [];
    private readonly Dictionary<string, (SeriesItem Item, DateTime At)> _summaryCache = [];
    private readonly DispatcherTimer _playerTimer;
    private readonly string _configDir;
    private BackendClient? _backend;
    private MpvPlayer? _player;
    private SeriesItem? _currentSeries;
    private EpisodeItem? _currentEpisode;
    private CancellationTokenSource? _catalogRequest;
    private CancellationTokenSource? _detailRequest;
    private CancellationTokenSource? _playRequest;
    private CancellationTokenSource? _feedRequest;
    private CancellationTokenSource? _catalogHydration;
    private CancellationTokenSource? _historyRequest;
    private Task _catalogHydrationTask = Task.CompletedTask;
    private Task _historyHydrationTask = Task.CompletedTask;
    private Func<Task>? _retryAction;
    private string _channel = "推荐";
    private string _query = "";
    private string _searchTab = "剧集";
    private string _quality = "best";
    private string _lastSuccessfulQuality = "best";
    private string _cursor = "";
    private string _feedCursor = "";
    private string _feedChannel = "";
    private string _feedSourceLabel = "";
    private readonly HashSet<string> _unavailableFeedSeries = [];
    private int _feedVersion;
    private Exception? _lastPlaybackFailure;
    private bool _hasMore;
    private bool _feedHasMore = true;
    private bool _catalogBusy;
    private bool _feedBusy;
    private bool _feedMode;
    private bool _seeking;
    private bool _updatingTimeline;
    private bool _playbackBusy;
    private bool _episodeCompleted;
    private bool _closing;
    private bool _closeReady;
    private bool _fullscreen;
    private bool _windowCinema;
    private bool _updatingQualities;
    private int _catalogVersion;
    private int _detailVersion;
    private int _playVersion;
    private int _feedIndex = -1;
    private int _catalogHydrating;
    private int _historyVersion;
    private double _position;
    private double _duration;
    private double _speed = 1;
    private DateTime _lastHistorySave = DateTime.MinValue;
    private Task _historySaveTask = Task.CompletedTask;
    private DateTime _lastFeedSwitch = DateTime.MinValue;
    private WindowState _previousWindowState;
    private bool IsImmersivePlayback => _fullscreen || _windowCinema;
    private sealed record CatalogSnapshot(List<SeriesItem> Items, string Cursor, bool HasMore, DateTime At);
    private sealed record DiscoverySnapshot(List<SeriesItem> Items, DateTime SavedAt);
    private string CatalogKey => _channel + "\n" + _searchTab + "\n" + _query + GenreCacheKeySuffix;
    private string FeedSourceSuffix => _feedMode && !string.IsNullOrWhiteSpace(_feedSourceLabel) ? " · " + _feedSourceLabel : "";

    public MainWindow()
    {
        InitializeComponent();
        double availableWidth = Math.Max(1, SystemParameters.WorkArea.Width - 40);
        double availableHeight = Math.Max(1, SystemParameters.WorkArea.Height - 40);
        Width = Math.Min(1380, availableWidth);
        Height = Math.Min(900, availableHeight);
        _configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openhongguo");
        CatalogItems.ItemsSource = _catalog;
        EpisodeItems.ItemsSource = _episodes;
        HistoryItems.ItemsSource = _history;
        QualityCombo.ItemsSource = _qualities;
        _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _playerTimer.Tick += PlayerTimer_Tick;
        VideoHost.ToggleFullscreenRequested += (_, _) => ToggleFullscreen();
        VideoHost.ExitFullscreenRequested += (_, _) => ExitImmersivePlayback();
        VideoHost.TogglePauseRequested += (_, _) => TogglePause();
        VideoHost.SeekRequested += (_, seconds) =>
        {
            if (_player != null && SeekSlider.IsEnabled)
                _player.Seek(Math.Clamp(_position + seconds, 0, Math.Max(0, _duration)));
        };
        VideoHost.RecommendationScrollRequested += async (_, direction) =>
        {
            if (_feedMode) await MoveRecommendationAsync(direction);
        };
        InitializePresentation();
        InitializeSearchSuggestions();
        AutoNextCheck.Unchecked += (_, _) => CancelNextPlayback();
        AutoNextCheck.Checked += (_, _) => PrepareNextPlayback(true);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_configDir);
            LoadSavedDiscoveryCache();
            _backend = new BackendClient(_configDir);
            await _backend.CallAsync("status");
            await LoadCatalogAsync(false);
        }
        catch (Exception error)
        {
            ShowError("应用初始化没有完成。" + UserMessage(error), InitializeBackendAsync);
        }
    }

    private async Task InitializeBackendAsync()
    {
        _backend?.Dispose();
        _backend = new BackendClient(_configDir);
        await _backend.CallAsync("status");
        await LoadCatalogAsync(false);
    }

    private void EnsurePlayer()
    {
        if (_player != null) return;
        _player = CreatePlayer(VideoHost.VideoHandle);
    }

    private MpvPlayer CreatePlayer(IntPtr handle)
    {
        var player = new MpvPlayer(handle);
        player.SetFillWindow(_fillVideoWindow);
        player.Ended += async (sender, _) =>
        {
            if (ReferenceEquals(sender, _player)) await PlaybackEndedAsync();
        };
        player.Error += (sender, message) =>
        {
            if (_closing || !ReferenceEquals(sender, _player)) return;
            _playbackBusy = false;
            ShowError(string.IsNullOrWhiteSpace(message) ? "视频播放没有完成，请重试或选择其他分集。" : message, RetryCurrentPlaybackAsync);
            PlayingSubtitle.Text = "播放遇到问题，可以重试或切换分集";
        };
        return player;
    }

    private async Task LoadCatalogAsync(bool append)
    {
        if (_backend == null || _closing) return;
        if (await TryLoadGenreCatalogAsync(append)) return;
        if (append && (_catalogBusy || !_hasMore || string.IsNullOrEmpty(_cursor))) return;
        _catalogRequest?.Cancel();
        _catalogRequest?.Dispose();
        _catalogRequest = new CancellationTokenSource();
        CancellationToken token = _catalogRequest.Token;
        int version = ++_catalogVersion;
        _catalogBusy = true;
        UpdateNavigation();
        ClearError();
        MoreButton.IsEnabled = false;
        CatalogStatus.Text = append ? "正在加载更多故事…" : "正在寻找好故事…";
        if (!append)
        {
            _catalogHydration?.Cancel();
            _catalogHydration?.Dispose();
            _catalogHydration = new CancellationTokenSource();
            _catalog.Clear();
            _cursor = "";
            _hasMore = false;
            MoreButton.Visibility = Visibility.Collapsed;
            if (_catalogCache.TryGetValue(CatalogKey, out CatalogSnapshot? cached) && (DateTime.Now - cached.At).TotalMinutes < 5)
            {
                foreach (SeriesItem item in cached.Items) _catalog.Add(item);
                _cursor = cached.Cursor;
                _hasMore = cached.HasMore;
                CatalogStatus.Text = $"已显示缓存的 {_catalog.Count} 部 · 正在更新…";
            }
            ScrollCatalogToTop();
        }
        string cacheKey = CatalogKey;
        string query = _query;
        string channel = _channel;
        string tab = _searchTab;
        try
        {
            JsonElement result;
            if (string.IsNullOrWhiteSpace(query))
            {
                result = await _backend.CallAsync("discover", new { channel, cursor = append ? _cursor : "" }, token);
            }
            else
            {
                result = await _backend.CallAsync("search", new { query, tab, cursor = append ? _cursor : "" }, token);
            }
            if (token.IsCancellationRequested || version != _catalogVersion || _closing) return;
            CatalogPage page = ModelJson.Parse<CatalogPage>(result);
            if (!append) _catalog.Clear();
            var known = new HashSet<string>(_catalog.Select(item => item.Id));
            foreach (SeriesItem item in page.Items)
            {
                ApplyCachedSummary(item);
                if (known.Add(item.Id)) _catalog.Add(item);
            }
            _cursor = page.Cursor;
            _hasMore = page.HasMore && !string.IsNullOrWhiteSpace(page.Cursor);
            CatalogStatus.Text = _catalog.Count == 0 ? "暂未找到内容，换个关键词试试。" : $"已加载 {_catalog.Count} 部" + (_hasMore ? " · 向下滚动，继续发现" : " · 还可以换个频道看看");
            if (!string.IsNullOrWhiteSpace(page.SourceLabel)) CatalogStatus.Text += " · " + page.SourceLabel;
            MoreButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;
            _catalogCache[cacheKey] = new CatalogSnapshot(_catalog.ToList(), _cursor, _hasMore, DateTime.Now);
            if (_catalogCache.Count > 12) _catalogCache.Remove(_catalogCache.MinBy(entry => entry.Value.At).Key);
            if (!append) ScrollCatalogToTop();
            _catalogHydration ??= new CancellationTokenSource();
            _catalogHydrationTask = HydrateCatalogAsync(page.Items, cacheKey, _catalogHydration.Token);
            if (!append && string.IsNullOrWhiteSpace(query) && channel == "推荐") _ = SaveDiscoveryCacheAsync(page.Items);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (version != _catalogVersion || _closing) return;
            CatalogStatus.Text = "本次加载没有完成";
            ShowError(UserMessage(error), () => LoadCatalogAsync(append));
        }
        finally
        {
            if (version == _catalogVersion)
            {
                _catalogBusy = false;
                MoreButton.IsEnabled = true;
            }
        }
    }

    private void LoadSavedDiscoveryCache()
    {
        try
        {
            string path = Path.Combine(_configDir, "catalog-cache.json");
            if (!File.Exists(path)) return;
            DiscoverySnapshot? saved = JsonSerializer.Deserialize<DiscoverySnapshot>(File.ReadAllText(path));
            if (saved == null || saved.Items.Count == 0 || (DateTime.Now - saved.SavedAt).TotalHours > 24) return;
            _catalog.Clear();
            foreach (SeriesItem item in saved.Items) _catalog.Add(item);
            _catalogCache[CatalogKey] = new CatalogSnapshot(saved.Items, "", false, DateTime.Now);
            CatalogStatus.Text = $"上次推荐 {_catalog.Count} 部 · 正在更新…";
        }
        catch
        {
            // 缓存损坏时直接请求新目录，不影响应用启动。
        }
    }

    private async Task SaveDiscoveryCacheAsync(List<SeriesItem> items)
    {
        string path = Path.Combine(_configDir, "catalog-cache.json");
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string content = JsonSerializer.Serialize(new DiscoverySnapshot(items, DateTime.Now));
            await File.WriteAllTextAsync(temporary, content);
            File.Move(temporary, path, true);
        }
        catch
        {
            // 只缓存推荐首屏的目录元数据，保存失败不打断浏览和播放。
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private void ScrollCatalogToTop()
    {
        CatalogScroll.ScrollToTop();
        // 新数据完成布局后再回顶，避免旧列表的滚动位置被布局过程恢复。
        Dispatcher.BeginInvoke(() => CatalogScroll.ScrollToTop(), DispatcherPriority.Loaded);
    }

    private void ApplyCachedSummary(SeriesItem item)
    {
        if (_summaryCache.TryGetValue(item.Id, out var cached) && (DateTime.Now - cached.At).TotalMinutes < 5)
            item.ApplySummary(cached.Item);
    }

    private async Task HydrateCatalogAsync(List<SeriesItem> items, string cacheKey, CancellationToken token)
    {
        _catalogHydrating++;
        try
        {
            await HydrateSummariesAsync(items.Select(item => item.Id), summary =>
            {
                if (cacheKey != CatalogKey || token.IsCancellationRequested || CatalogScreen.Visibility != Visibility.Visible) return;
                foreach (SeriesItem item in items.Where(item => item.Id == summary.Id)) item.ApplySummary(summary);
                foreach (SeriesItem item in _catalog.Where(item => item.Id == summary.Id)) item.ApplySummary(summary);
            }, token);
        }
        finally
        {
            _catalogHydrating--;
        }
    }

    private async Task HydrateSummariesAsync(IEnumerable<string> ids, Action<SeriesItem> apply, CancellationToken token)
    {
        if (_backend == null || _closing) return;
        List<string> missing = [];
        foreach (string id in ids.Distinct())
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (_summaryCache.TryGetValue(id, out var cached) && (DateTime.Now - cached.At).TotalMinutes < 5)
                apply(cached.Item);
            else
                missing.Add(id);
        }
        // 目录先展示，每批补齐少量封面，图片请求不延长列表的主加载状态。
        foreach (string[] batch in missing.Chunk(10))
        {
            if (token.IsCancellationRequested || _closing) return;
            try
            {
                CatalogPage response = ModelJson.Parse<CatalogPage>(await _backend.CallAsync("hydrate", new { ids = batch }, token));
                if (token.IsCancellationRequested || _closing) return;
                foreach (SeriesItem summary in response.Items)
                {
                    if (!string.IsNullOrWhiteSpace(summary.CoverUrl) || !string.IsNullOrWhiteSpace(summary.Title))
                        _summaryCache[summary.Id] = (summary, DateTime.Now);
                    apply(summary);
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // 单批补图失败时仍保留可点击目录，其余批次可以继续完成。
            }
        }
    }

    private Task<bool> OpenSeriesAsync(string id, bool autoPlay, int resumeEpisode = 0)
    {
        return OpenSeriesCoreAsync(id, autoPlay, resumeEpisode, false);
    }

    private async Task<bool> OpenSeriesCoreAsync(string id, bool autoPlay, int resumeEpisode, bool preserveFeedRequest)
    {
        if (_backend == null || _closing || string.IsNullOrWhiteSpace(id)) return false;
        Task stopped = StopCurrentCoreAsync(preserveFeedRequest);
        CloseDiscussionPanel();
        _ = RefreshEngagementAsync();
        _detailRequest?.Cancel();
        _detailRequest?.Dispose();
        _detailRequest = new CancellationTokenSource();
        CancellationToken token = _detailRequest.Token;
        int version = ++_detailVersion;
        ShowScreen(PlayerScreen);
        PageTitle.Text = _feedMode ? "连续推荐" : "正在观看";
        PlayingTitle.Text = "正在加载剧集…";
        PlayingSubtitle.Text = _feedMode ? "滚轮或 ↑ / ↓ 切换推荐，自动接着看下一集" + FeedSourceSuffix : "正在读取分集列表";
        VideoMessage.Text = "正在展开这个故事…";
        VideoHost.Visibility = Visibility.Hidden;
        VideoPlaceholder.Visibility = Visibility.Visible;
        _episodes.Clear();
        DetailTitle.Text = "正在读取…";
        DetailDescription.Text = "";
        CoverImages.SetUrl(CurrentCover, "");
        ClearError();
        try
        {
            await stopped;
            if (token.IsCancellationRequested || version != _detailVersion || _closing) return false;
            SeriesDetail detail = ModelJson.Parse<SeriesDetail>(await _backend.CallAsync("series", new { id }, token));
            if (token.IsCancellationRequested || version != _detailVersion || _closing) return false;
            if (detail.Series == null) throw new InvalidOperationException("这部剧暂未返回详情，请选择其他内容。");
            _currentSeries = detail.Series;
            PrepareSeasons();
            _summaryCache[id] = (detail.Series, DateTime.Now);
            foreach (EpisodeItem episode in detail.Episodes.OrderBy(item => item.Number)) _episodes.Add(episode);
            if (!autoPlay) _ = RefreshEngagementAsync();
            DetailTitle.Text = detail.Series.Title;
            DetailCount.Text = detail.Series.EpisodeLabel;
            DetailDescription.Text = detail.Series.Description;
            EpisodeCountText.Text = $"共 {_episodes.Count} 集";
            PlayingTitle.Text = detail.Series.Title;
            SetCover(CurrentCover, detail.Series.CoverUri);
            FeedHint.Visibility = _feedMode ? Visibility.Visible : Visibility.Collapsed;
            FeedHint.Text = "滚轮上下切换故事，也可以按 ↑ / ↓" + FeedSourceSuffix;
            EpisodeItem? first = _episodes.FirstOrDefault(item => item.Playable && item.Number >= resumeEpisode) ?? _episodes.FirstOrDefault(item => item.Playable);
            if (first == null)
            {
                VideoMessage.Text = "这部剧暂未返回可播放分集。";
                PlayingSubtitle.Text = "请选择其他剧集";
                return false;
            }
            if (autoPlay) await PlayEpisodeAsync(first, resumeEpisode > 0);
            if (token.IsCancellationRequested || version != _detailVersion || _closing) return false;
            // 推荐候选的播放信息错误交回推荐流程分类，其他播放入口仍保留原来的可见错误。
            if (preserveFeedRequest && _lastPlaybackFailure != null) throw _lastPlaybackFailure;
            return !autoPlay || VideoHost.Visibility == Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception error)
        {
            if (version != _detailVersion || _closing) return false;
            if (preserveFeedRequest) throw;
            PlayingSubtitle.Text = "剧集没有加载成功";
            VideoMessage.Text = "请重试，或选择其他剧集。";
            ShowError(UserMessage(error), () => OpenSeriesAsync(id, autoPlay, resumeEpisode));
            return false;
        }
    }

    private Task PlayEpisodeAsync(EpisodeItem episode, bool resume = false)
    {
        ClosePlaybackOptions();
        if (_currentEpisode?.Id != episode.Id)
        {
            _quality = "best";
            _lastSuccessfulQuality = "best";
            _qualities.Clear();
        }
        return PlayEpisodeCoreAsync(episode, resume, null, false);
    }

    private async Task PlayEpisodeCoreAsync(EpisodeItem episode, bool resume, double? startOverride, bool paused)
    {
        if (_backend == null || _currentSeries == null || !episode.Playable || _closing) return;
        ClosePlaybackOptions();
        _lastPlaybackFailure = null;
        string requestedQuality = _quality;
        if (_nextPlaybackEpisodeId != episode.Id || _nextPlaybackQuality != requestedQuality) CancelNextPlayback();
        Task saved = _episodeCompleted ? Task.CompletedTask : SaveHistoryAsync(false);
        PlaybackInfo? bufferedPlayback = !resume && startOverride == null ? ActivateBufferedPlayback(episode.Id, requestedQuality) : null;
        _playRequest?.Cancel();
        _playRequest?.Dispose();
        _playRequest = new CancellationTokenSource();
        CancellationToken token = _playRequest.Token;
        int version = ++_playVersion;
        if (bufferedPlayback == null) _player?.Stop();
        _playerTimer.Stop();
        _playbackBusy = true;
        _currentEpisode = episode;
        _ = RefreshEngagementAsync();
        _episodeCompleted = false;
        _position = 0;
        _duration = 0;
        VideoHost.Visibility = bufferedPlayback == null ? Visibility.Hidden : Visibility.Visible;
        VideoPlaceholder.Visibility = bufferedPlayback == null ? Visibility.Visible : Visibility.Collapsed;
        VideoMessage.Text = $"正在准备第 {episode.Number} 集…";
        PlayingSubtitle.Text = $"第 {episode.Number} 集 · 正在准备播放";
        PauseButton.IsEnabled = false;
        QualityCombo.IsEnabled = false;
        SeekSlider.IsEnabled = false;
        TimeText.Text = "00:00 / 00:00";
        HighlightEpisode();
        ClearError();
        try
        {
            // 先加载媒体，旧集历史在首帧交接后按顺序等候，不让历史写入拖慢手动切集。
            if (token.IsCancellationRequested || version != _playVersion || _closing) return;
            PlaybackInfo? prepared = bufferedPlayback ?? await TakePreparedPlaybackAsync(episode.Id, requestedQuality, token);
            PlaybackInfo playback = prepared ?? ModelJson.Parse<PlaybackInfo>(await _backend.CallAsync("playback", new { episode_id = episode.Id, quality = requestedQuality }, token));
            if (token.IsCancellationRequested || version != _playVersion || _closing) return;
            if (string.IsNullOrWhiteSpace(playback.Url)) throw new InvalidOperationException("本次没有取得播放地址，请重试。");
            double startSeconds = Math.Max(0, startOverride ?? 0);
            if (resume && startOverride == null)
            {
                List<HistoryItem> records = ModelJson.Parse<List<HistoryItem>>(await _backend.CallAsync("history", null, token));
                HistoryItem? record = records.FirstOrDefault(item => item.SeriesId == _currentSeries.Id && item.Episode == episode.Number && !item.Finished);
                if (record != null) startSeconds = Math.Max(0, record.Position);
            }
            if (token.IsCancellationRequested || version != _playVersion || _closing) return;
            EnsurePlayer();
            _duration = Math.Max(0, playback.DurationSeconds);
            _position = startSeconds;
            VideoHost.Visibility = Visibility.Visible;
            VideoPlaceholder.Visibility = Visibility.Collapsed;
            if (bufferedPlayback == null) _player!.Load(playback.Url, playback.AudioUrl, playback.Key, startSeconds, paused);
            _player!.SetSpeed(_speed);
            _player.SetVolume(VolumeSlider.Value);
            _player.SetPaused(paused);
            UpdateQualityOptions(playback);
            _lastSuccessfulQuality = _quality;
            PauseButton.IsEnabled = true;
            PauseButton.Content = paused ? "播放" : "暂停";
            _playbackBusy = false;
            PlayingSubtitle.Text = $"第 {episode.Number} 集 · 自适应画面 · {_speed:0.##} 倍速" + (string.IsNullOrWhiteSpace(playback.Definition) ? "" : " · " + playback.Definition) + FeedSourceSuffix;
            _lastHistorySave = DateTime.Now;
            _playerTimer.Start();
            PrepareNextPlayback(true);
            await saved;
            if (version == _playVersion && !_closing) await SaveHistoryAsync(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (version != _playVersion || _closing) return;
            _lastPlaybackFailure = error;
            _playbackBusy = false;
            _quality = _lastSuccessfulQuality;
            _updatingQualities = true;
            QualityCombo.SelectedItem = _qualities.FirstOrDefault(item => item.Definition == _quality);
            _updatingQualities = false;
            QualityCombo.IsEnabled = _qualities.Count > 0;
            VideoHost.Visibility = Visibility.Hidden;
            VideoPlaceholder.Visibility = Visibility.Visible;
            VideoMessage.Text = "播放没有成功，请重试或选择其他分集。";
            PlayingSubtitle.Text = "播放遇到问题";
            ShowError(UserMessage(error), RetryCurrentPlaybackAsync);
        }
    }

    private void UpdateQualityOptions(PlaybackInfo playback)
    {
        _updatingQualities = true;
        try
        {
            _qualities.Clear();
            _qualities.Add(new PlaybackQuality { Definition = "best" });
            _qualities.Add(new PlaybackQuality { Definition = "worst" });
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "best", "worst" };
            foreach (PlaybackQuality quality in playback.AvailableQualities)
            {
                if (!string.IsNullOrWhiteSpace(quality.Definition) && known.Add(quality.Definition)) _qualities.Add(quality);
            }
            QualityCombo.SelectedItem = _qualities.FirstOrDefault(item => item.Definition == _quality) ?? _qualities[0];
            QualityCombo.IsEnabled = true;
            QualityCombo.ToolTip = string.IsNullOrWhiteSpace(playback.Definition) ? "当前分集实际提供的画质" : "当前画质：" + playback.Definition;
        }
        finally
        {
            _updatingQualities = false;
        }
    }

    private async Task ChangeQualityAsync(string definition)
    {
        if (_player == null || _currentEpisode == null || _playbackBusy || definition == _quality) return;
        if (!_qualities.Any(item => item.Definition == definition)) return;
        MpvStatus status = _player.GetStatus();
        if (!status.Loaded) return;
        _quality = definition;
        await PlayEpisodeCoreAsync(_currentEpisode, false, status.Position, status.Paused);
    }

    private async void Quality_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingQualities || QualityCombo?.SelectedItem is not PlaybackQuality selected) return;
        await ChangeQualityAsync(selected.Definition);
    }

    private Task RetryCurrentPlaybackAsync()
    {
        return _currentEpisode == null ? Task.CompletedTask : PlayEpisodeAsync(_currentEpisode, true);
    }

    private Task StopCurrentAsync()
    {
        return StopCurrentCoreAsync(false);
    }

    private Task StopCurrentCoreAsync(bool preserveFeedRequest)
    {
        CancelSeasons();
        CancelNextPlayback();
        Task saved = SaveHistoryAsync(false);
        _detailVersion++;
        _detailRequest?.Cancel();
        _playVersion++;
        _playRequest?.Cancel();
        if (!preserveFeedRequest)
        {
            _feedVersion++;
            _feedRequest?.Cancel();
            _feedBusy = false;
        }
        _playbackBusy = false;
        _player?.Stop();
        _playerTimer.Stop();
        PauseButton.IsEnabled = false;
        QualityCombo.IsEnabled = false;
        SeekSlider.IsEnabled = false;
        _currentEpisode = null;
        _currentSeries = null;
        _episodeCompleted = false;
        return saved;
    }

    private Task SaveHistoryAsync(bool finished)
    {
        // 导航会先清空当前剧集，关闭时仍需等待此前排入的记录写完。
        if (_backend == null || _currentSeries == null || _currentEpisode == null) return _historySaveTask;
        SeriesItem series = _currentSeries;
        EpisodeItem episode = _currentEpisode;
        finished |= _episodeCompleted;
        double position = _position;
        try
        {
            if (_player != null)
            {
                MpvStatus status = _player.GetStatus();
                if (status.Loaded && status.Position >= 0) position = status.Position;
            }
            if (finished) position = Math.Max(position, _duration);
            // 在排队前捕获完整快照，后续切集不再改变这条记录。
            var record = new
            {
                series_id = series.Id, title = series.Title, episode = episode.Number,
                episodes = Math.Max(series.EpisodeCount, _episodes.Count), finished, position = Math.Max(0, position)
            };
            _historySaveTask = SaveHistoryInOrderAsync(_historySaveTask, _backend, record);
        }
        catch
        {
            // 记录写入失败不打断正在播放的视频，用户仍可继续观看。
        }
        return _historySaveTask;
    }

    private static async Task SaveHistoryInOrderAsync(Task previous, BackendClient backend, object record)
    {
        try
        {
            // 只串行化历史写入；媒体请求继续并行，避免旧响应晚到后覆盖新进度。
            await previous;
            await backend.CallAsync("history_record", record);
        }
        catch
        {
            // 单条记录失败后仍完成队列任务，让后续记录与关闭清理继续执行。
        }
    }

    private async Task PlaybackEndedAsync()
    {
        if (_closing || _currentEpisode == null || _playbackBusy || _episodeCompleted) return;
        int version = _playVersion;
        _episodeCompleted = true;
        // 先捕获旧集的完整记录，写入后台队列；媒体交接不等待磁盘或桥接响应。
        Task saved = SaveHistoryAsync(true);
        if (_closing || version != _playVersion || _currentEpisode == null) return;
        if (AutoNextCheck.IsChecked != true)
        {
            PauseButton.Content = "重播";
            PlayingSubtitle.Text = $"第 {_currentEpisode.Number} 集已结束，自动连播已关闭";
            await saved;
            return;
        }
        EpisodeItem? next = _episodes.FirstOrDefault(item => item.Playable && item.Number > _currentEpisode.Number);
        if (next != null)
        {
            await PlayEpisodeAsync(next);
            await saved;
            return;
        }
        if (_episodes.Any(item => item.Number > _currentEpisode.Number))
        {
            PauseButton.Content = "重播";
            PlayingSubtitle.Text = "本季后续分集暂未开放，已暂停自动连播。";
            return;
        }
        if (await TryPlayNextSeasonAsync(version)) return;
        if (_feedMode)
        {
            await MoveRecommendationAsync(1);
            return;
        }
        PauseButton.Content = "重播";
        PlayingSubtitle.Text = "本剧已播放完毕，可以换一个推荐继续看。";
    }

    private async Task MoveRecommendationAsync(int direction)
    {
        if (_backend == null || _feedBusy || _closing) return;
        if (_feedChannel != _channel) ResetFeedQueue(_channel);
        if ((DateTime.Now - _lastFeedSwitch).TotalMilliseconds < 650) return;
        _lastFeedSwitch = DateTime.Now;
        _feedBusy = true;
        _feedRequest?.Cancel();
        _feedRequest?.Dispose();
        CancellationTokenSource request = new();
        _feedRequest = request;
        CancellationToken token = request.Token;
        int version = ++_feedVersion;
        string channel = _feedChannel;
        bool previousFeedMode = _feedMode;
        bool committed = false;
        string previousTitle = PageTitle.Text;
        bool openedPlayerView = false;
        UIElement previousScreen = PlayerScreen.Visibility == Visibility.Visible ? PlayerScreen
            : RankingScreen.Visibility == Visibility.Visible ? RankingScreen
            : HistoryScreen.Visibility == Visibility.Visible ? HistoryScreen : CatalogScreen;
        bool IsCurrentRequest() => !_closing && !token.IsCancellationRequested && version == _feedVersion
            && channel == _channel && ReferenceEquals(request, _feedRequest);
        void RestorePreviousView()
        {
            _feedMode = previousFeedMode;
            if (openedPlayerView)
            {
                // 候选已替换播放器后不声称恢复旧剧，保留一致的失败占位供重试。
                CancelNextPlayback();
                _player?.Stop();
                _playerTimer.Stop();
                _playbackBusy = false;
                PauseButton.IsEnabled = false;
                QualityCombo.IsEnabled = false;
                SeekSlider.IsEnabled = false;
                VideoHost.Visibility = Visibility.Hidden;
                VideoPlaceholder.Visibility = Visibility.Visible;
                PlayingTitle.Text = _currentSeries?.Title ?? "推荐加载未完成";
                DetailTitle.Text = PlayingTitle.Text;
                PlayingSubtitle.Text = "本次推荐没有成功加载，可以重试或返回";
                VideoMessage.Text = "本次推荐没有成功加载，请重试或选择其他故事。";
            }
            if (previousScreen.Visibility != Visibility.Visible) ShowScreen(previousScreen);
            PageTitle.Text = previousTitle;
            FeedHint.Visibility = _feedMode ? Visibility.Visible : Visibility.Collapsed;
            UpdateNavigation();
        }
        ClearError();
        try
        {
            int step = direction < 0 ? -1 : 1;
            int target = Math.Max(0, _feedIndex + step);
            int attempts = 0;
            bool fetchedPage = false;
            // 一次操作最多尝试五个真实候选、读取一页推荐，不对限流或验证错误自动重试。
            while (attempts < 5 && IsCurrentRequest())
            {
                if (target < 0) break;
                if (target >= _feedQueue.Count)
                {
                    if (step < 0 || fetchedPage || !_feedHasMore) break;
                    fetchedPage = true;
                    string cursor = _feedCursor;
                    JsonElement response = await _backend.CallAsync("recommend", new { series_id = _currentSeries?.Id ?? "", cursor, channel }, token);
                    if (!IsCurrentRequest()) return;
                    CatalogPage page = ModelJson.Parse<CatalogPage>(response);
                    var known = new HashSet<string>(_feedQueue.Select(item => item.Id));
                    foreach (SeriesItem item in page.Items)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Id) && known.Add(item.Id)) _feedQueue.Add(item);
                    }
                    _feedCursor = page.Cursor;
                    _feedSourceLabel = page.SourceLabel;
                    _feedHasMore = page.HasMore && !string.IsNullOrWhiteSpace(page.Cursor) && page.Cursor != cursor;
                    if (target >= _feedQueue.Count) break;
                }
                SeriesItem candidate = _feedQueue[target];
                if (_unavailableFeedSeries.Contains(candidate.Id))
                {
                    target += step;
                    continue;
                }
                attempts++;
                _feedMode = true;
                PageTitle.Text = "连续推荐";
                bool opened;
                try
                {
                    openedPlayerView = true;
                    opened = await OpenSeriesCoreAsync(candidate.Id, true, 0, true);
                }
                catch (BackendRequestException error) when (error.Code is "CONTENT_UNAVAILABLE" or "ANONYMOUS_LIMITED")
                {
                    // 仅跳过平台明确标记不可用的单个候选，不跳过设备、账号、网络或限流错误。
                    opened = false;
                }
                if (!IsCurrentRequest()) return;
                if (opened)
                {
                    _feedIndex = target;
                    committed = true;
                    UpdateNavigation();
                    return;
                }
                _unavailableFeedSeries.Add(candidate.Id);
                target += step;
            }
            if (!IsCurrentRequest()) return;
            RestorePreviousView();
            string message = attempts > 0 ? "本次推荐候选暂未返回可播放内容，可以继续尝试下一批或换个频道。"
                : !_feedHasMore && _feedQueue.Count > 0 ? "这一轮推荐已经看完，可以重新加载推荐或换个频道。"
                : "暂未加载到新的推荐，请重试或换个频道。";
            bool hasQueuedCandidates = step > 0
                ? _feedQueue.Skip(Math.Max(0, _feedIndex + 1)).Any(item => !_unavailableFeedSeries.Contains(item.Id))
                : _feedQueue.Take(Math.Max(0, _feedIndex)).Any(item => !_unavailableFeedSeries.Contains(item.Id));
            ShowError(message, () => attempts > 0 && (_feedHasMore || hasQueuedCandidates) ? MoveRecommendationAsync(direction) : RestartFeedAsync());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (IsCurrentRequest())
            {
                RestorePreviousView();
                ShowError(UserMessage(error), () => MoveRecommendationAsync(direction));
            }
        }
        finally
        {
            if (version == _feedVersion && ReferenceEquals(request, _feedRequest))
            {
                _feedBusy = false;
                if (!committed) _lastFeedSwitch = DateTime.MinValue;
            }
        }
    }

    private Task RestartFeedAsync()
    {
        ResetFeedQueue(_channel);
        return MoveRecommendationAsync(1);
    }

    private void ResetFeedQueue(string channel)
    {
        _feedVersion++;
        _feedRequest?.Cancel();
        _feedRequest?.Dispose();
        _feedRequest = null;
        _feedBusy = false;
        _feedChannel = channel;
        _feedSourceLabel = "";
        _feedCursor = "";
        _feedHasMore = true;
        _feedIndex = -1;
        _feedQueue.Clear();
        _unavailableFeedSeries.Clear();
        _lastFeedSwitch = DateTime.MinValue;
    }

    private async Task LoadHistoryAsync()
    {
        if (_backend == null || _closing) return;
        _historyRequest?.Cancel();
        _historyRequest?.Dispose();
        _historyRequest = new CancellationTokenSource();
        CancellationToken token = _historyRequest.Token;
        int version = ++_historyVersion;
        ClearError();
        try
        {
            List<HistoryItem> records = ModelJson.Parse<List<HistoryItem>>(await _backend.CallAsync("history", null, token));
            if (_closing || token.IsCancellationRequested || version != _historyVersion) return;
            _history.Clear();
            foreach (HistoryItem entry in records)
            {
                if (_summaryCache.TryGetValue(entry.SeriesId, out var cached)) entry.CoverUrl = cached.Item.CoverUrl;
                _history.Add(entry);
            }
            if (_history.Count == 0) ShowError("还没有观看记录。去发现页选择一个故事吧。", null);
            _historyHydrationTask = HydrateSummariesAsync(records.Select(item => item.SeriesId), summary =>
            {
                if (token.IsCancellationRequested || version != _historyVersion || HistoryScreen.Visibility != Visibility.Visible) return;
                foreach (HistoryItem item in _history.Where(item => item.SeriesId == summary.Id)) item.CoverUrl = summary.CoverUrl;
            }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_closing && version == _historyVersion) ShowError(UserMessage(error), LoadHistoryAsync);
        }
    }

    private async void PlayerTimer_Tick(object? sender, EventArgs e)
    {
        if (_player == null || _closing || _playbackBusy) return;
        try
        {
            MpvStatus status = _player.GetStatus();
            if (status.Loaded)
            {
                _position = Math.Max(0, status.Position);
                if (status.Duration > 0) _duration = status.Duration;
                if (!_seeking)
                {
                    _updatingTimeline = true;
                    SeekSlider.Maximum = Math.Max(1, _duration);
                    SeekSlider.Value = Math.Min(_position, SeekSlider.Maximum);
                    _updatingTimeline = false;
                }
                SeekSlider.IsEnabled = _duration > 0;
                PauseButton.Content = status.Paused ? "播放" : "暂停";
                TimeText.Text = $"{FormatTime(_position)} / {FormatTime(_duration)}";
                if (!status.Paused) PrepareNextPlayback();
            }
            if ((DateTime.Now - _lastHistorySave).TotalSeconds >= 15)
            {
                _lastHistorySave = DateTime.Now;
                await SaveHistoryAsync(false);
            }
        }
        catch
        {
            // 播放器正在释放或切集时，跳过这一帧状态读取。
        }
    }

    private void HighlightEpisode()
    {
        PreviousButton.IsEnabled = _currentEpisode != null && _episodes.Any(item => item.Playable && item.Number < _currentEpisode.Number);
        NextButton.IsEnabled = _currentEpisode != null && _episodes.Any(item => item.Playable && item.Number > _currentEpisode.Number);
        Dispatcher.BeginInvoke(() =>
        {
            foreach (EpisodeItem episode in _episodes)
            {
                DependencyObject container = EpisodeItems.ItemContainerGenerator.ContainerFromItem(episode);
                Button? button = FindChild<Button>(container);
                if (button == null) continue;
                bool selected = episode.Id == _currentEpisode?.Id;
                button.Background = (Brush)FindResource(selected ? "AccentSoftBrush" : "RaisedBrush");
                button.Foreground = (Brush)FindResource(selected ? "AccentBrush" : "TextBrush");
                button.BorderBrush = (Brush)FindResource(selected ? "AccentBrush" : "LineBrush");
                System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "正在播放" : "");
            }
        }, DispatcherPriority.Loaded);
    }

    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        if (parent is T found) return found;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            T? child = FindChild<T>(VisualTreeHelper.GetChild(parent, index));
            if (child != null) return child;
        }
        return null;
    }

    private void ShowScreen(UIElement screen)
    {
        if (screen != PlayerScreen)
        {
            if (_fullscreen) ToggleFullscreen();
            if (_windowCinema) ToggleWindowCinema();
            CloseDiscussionPanel();
        }
        if (screen == PlayerScreen && PlayerScreen.Visibility != Visibility.Visible)
            _playerReturnScreen = RankingScreen.Visibility == Visibility.Visible ? RankingScreen : HistoryScreen.Visibility == Visibility.Visible ? HistoryScreen : CatalogScreen;
        if (screen != CatalogScreen)
        {
            _catalogRequest?.Cancel();
            _catalogHydration?.Cancel();
            _catalogVersion++;
            _catalogBusy = false;
        }
        if (screen != HistoryScreen) _historyRequest?.Cancel();
        if (screen != RankingScreen)
        {
            _rankRequest?.Cancel();
            _rankVersion++;
            _rankBusy = false;
        }
        CatalogScreen.Visibility = screen == CatalogScreen ? Visibility.Visible : Visibility.Collapsed;
        PlayerScreen.Visibility = screen == PlayerScreen ? Visibility.Visible : Visibility.Collapsed;
        HistoryScreen.Visibility = screen == HistoryScreen ? Visibility.Visible : Visibility.Collapsed;
        RankingScreen.Visibility = screen == RankingScreen ? Visibility.Visible : Visibility.Collapsed;
        ApplyShellLayout();
        UpdateNavigation();
    }

    private void ShowError(string message, Func<Task>? retry)
    {
        if (_closing) return;
        NoticeText.Text = message;
        _retryAction = retry;
        RetryButton.Visibility = retry == null ? Visibility.Collapsed : Visibility.Visible;
        NoticeBar.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        NoticeBar.Visibility = Visibility.Collapsed;
        _retryAction = null;
    }

    private static string UserMessage(Exception error)
    {
        if (error is OperationCanceledException) return "本次操作已取消，可以重新选择。";
        if (error is DllNotFoundException || error is BadImageFormatException) return "内嵌播放器初始化失败，请重新解压或下载完整版本。";
        return string.IsNullOrWhiteSpace(error.Message) ? "本次操作没有完成，请重试。" : error.Message;
    }

    private static void SetCover(System.Windows.Controls.Image image, Uri? uri)
    {
        CoverImages.SetUrl(image, uri?.AbsoluteUri ?? "");
    }

    private static string FormatTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        TimeSpan time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

    private void TogglePause()
    {
        RevealFullscreenControls();
        if (_player == null || _playbackBusy || _currentEpisode == null) return;
        if (_episodeCompleted)
        {
            _ = PlayEpisodeAsync(_currentEpisode);
            return;
        }
        MpvStatus status = _player.GetStatus();
        if (!status.Loaded)
        {
            _ = RetryCurrentPlaybackAsync();
            return;
        }
        _player.SetPaused(!status.Paused);
        PauseButton.Content = status.Paused ? "暂停" : "播放";
    }

    private void ToggleFullscreen()
    {
        if (_closing || PlayerScreen.Visibility != Visibility.Visible) return;
        CloseEpisodePicker();
        RestoreWindowControls();
        if (!_fullscreen)
        {
            SetVideoFillMode(false);
            // 屏幕全屏保留进入前的窗口内模式，退出后按原状态继续观看。
            _fullscreen = true;
            _previousWindowState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            UpdateCaptionIcon();
            ApplyShellLayout();
            RootLayout.UpdateLayout();
            MoveControlsToFullscreen();
        }
        else
        {
            _fullscreen = false;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _previousWindowState;
            UpdateCaptionIcon();
            ApplyShellLayout();
            if (_windowCinema)
            {
                RootLayout.UpdateLayout();
                MoveControlsToFullscreen();
            }
        }
    }

    private void ToggleWindowCinema()
    {
        if (_closing || PlayerScreen.Visibility != Visibility.Visible) return;
        if (_fullscreen)
        {
            ToggleFullscreen();
            return;
        }
        CloseEpisodePicker();
        RestoreWindowControls();
        // 只改变客户区的播放布局，窗口位置、尺寸、标题栏和最大化状态保持原值。
        if (!_windowCinema) SetVideoFillMode(false);
        _windowCinema = !_windowCinema;
        ApplyShellLayout();
        if (_windowCinema)
        {
            RootLayout.UpdateLayout();
            MoveControlsToFullscreen();
        }
    }

    private void ExitImmersivePlayback()
    {
        if (_fullscreen) ToggleFullscreen();
        else if (_windowCinema) ToggleWindowCinema();
    }

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        Task stopped = StopCurrentAsync();
        if (_fullscreen) ToggleFullscreen();
        _feedMode = false;
        _query = "";
        SearchInput.Text = "";
        PageTitle.Text = "发现好剧";
        ShowScreen(CatalogScreen);
        await stopped;
        if (CatalogScreen.Visibility == Visibility.Visible) await LoadCatalogAsync(false);
    }

    private async void Feed_Click(object sender, RoutedEventArgs e)
    {
        await MoveRecommendationAsync(1);
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        Task stopped = StopCurrentAsync();
        if (_fullscreen) ToggleFullscreen();
        _feedMode = false;
        PageTitle.Text = "观看记录";
        ShowScreen(HistoryScreen);
        await stopped;
        if (HistoryScreen.Visibility == Visibility.Visible) await LoadHistoryAsync();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void SearchTab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SearchTabCombo?.SelectedItem is not ComboBoxItem selected) return;
        _searchTab = selected.Tag?.ToString() ?? "剧集";
        if (_backend != null && !string.IsNullOrWhiteSpace(SearchInput?.Text)) await SearchAsync();
    }
    private async void SearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (_searchComposition || e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        CloseSearchSuggestions();
        Task stopped = StopCurrentAsync();
        _feedMode = false;
        _query = SearchInput.Text.Trim();
        PageTitle.Text = string.IsNullOrEmpty(_query) ? "发现好剧" : $"搜索：{_query}";
        ShowScreen(CatalogScreen);
        await stopped;
        if (_closing || CatalogScreen.Visibility != Visibility.Visible) return;
        await LoadCatalogAsync(false);
    }

    private async void Channel_Click(object sender, RoutedEventArgs e)
    {
        _channel = (sender as Button)?.Tag?.ToString() ?? "推荐";
        if (_feedChannel != _channel) ResetFeedQueue(_channel);
        _feedMode = false;
        UpdateNavigation();
        ResetGenreSelection();
        _ = RefreshGenreOptionsAsync();
        _query = "";
        SearchInput.Text = "";
        PageTitle.Text = _channel == "推荐" ? "发现好剧" : _channel;
        await LoadCatalogAsync(false);
    }

    private async void Series_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SeriesItem item)
        {
            _feedMode = false;
            await OpenSeriesAsync(item.Id, true);
        }
    }

    private async void Episode_Click(object sender, RoutedEventArgs e)
    {
        CloseEpisodePicker();
        if ((sender as Button)?.Tag is EpisodeItem episode) await PlayEpisodeAsync(episode);
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not HistoryItem item) return;
        _feedMode = false;
        int episode = item.Finished && item.Episode < item.Episodes ? item.Episode + 1 : item.Episode;
        await OpenSeriesAsync(item.SeriesId, true, episode);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadCatalogAsync(false);
    private async void More_Click(object sender, RoutedEventArgs e) => await LoadCatalogAsync(true);
    private async void CatalogScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange <= 0 || _catalogBusy || !_hasMore) return;
        if (CatalogScroll.ScrollableHeight - CatalogScroll.VerticalOffset < 450) await LoadCatalogAsync(true);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        Func<Task>? retry = _retryAction;
        if (retry == null) return;
        RetryButton.IsEnabled = false;
        try { await retry(); }
        catch (Exception error) { ShowError(UserMessage(error), retry); }
        finally { RetryButton.IsEnabled = true; }
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        Task stopped = StopCurrentAsync();
        if (_fullscreen) ToggleFullscreen();
        _feedMode = false;
        UIElement target = _playerReturnScreen ?? CatalogScreen;
        ShowScreen(target);
        if (target == RankingScreen)
        {
            ResumeRankGenreHydration();
            _ = CheckDailyRankingUpdateAsync(DateTime.Now);
        }
        PageTitle.Text = target == RankingScreen ? "排行榜" : target == HistoryScreen ? "观看记录" : string.IsNullOrWhiteSpace(_query) ? "发现好剧" : $"搜索：{_query}";
        ClearError();
        await stopped;
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => TogglePause();
    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEpisode == null) return;
        EpisodeItem? previous = _episodes.LastOrDefault(item => item.Playable && item.Number < _currentEpisode.Number);
        if (previous != null) await PlayEpisodeAsync(previous);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEpisode == null) return;
        EpisodeItem? next = _episodes.FirstOrDefault(item => item.Playable && item.Number > _currentEpisode.Number);
        if (next != null) await PlayEpisodeAsync(next);
    }

    private async void NextRecommendation_Click(object sender, RoutedEventArgs e) => await MoveRecommendationAsync(1);
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleWindowCinema();
    private void Seek_Begin(object sender, MouseButtonEventArgs e) => _seeking = true;
    private void Seek_End(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        if (_player != null && SeekSlider.IsEnabled) _player.Seek(SeekSlider.Value);
    }

    private void Seek_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTimeline || _player == null) return;
        SeekSlider.ToolTip = FormatTime(e.NewValue);
        if (!_seeking && SeekSlider.IsKeyboardFocusWithin && SeekSlider.IsEnabled) _player.Seek(e.NewValue);
    }

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedCombo?.SelectedItem is not ComboBoxItem selected) return;
        if (!double.TryParse(selected.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _speed)) _speed = 1;
        _player?.SetSpeed(_speed);
        if (_currentEpisode != null) PlayingSubtitle.Text = $"第 {_currentEpisode.Number} 集 · 自适应画面 · {_speed:0.##} 倍速" + FeedSourceSuffix;
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => _player?.SetVolume(e.NewValue);

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        _controlsKeyboardInteraction = true;
        RevealFullscreenControls();
        if (e.Key == Key.Escape && _playbackOptionsPopup?.IsOpen == true)
        {
            ClosePlaybackOptions();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && IsImmersivePlayback)
        {
            ExitImmersivePlayback();
            e.Handled = true;
            return;
        }
        if (e.OriginalSource is TextBoxBase || e.OriginalSource is ComboBoxItem || e.OriginalSource is Slider) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase && e.Key is Key.Space or Key.Enter) return;
        if (SpeedCombo.IsKeyboardFocusWithin || QualityCombo.IsKeyboardFocusWithin || SearchTabCombo.IsKeyboardFocusWithin || RankContentCombo.IsKeyboardFocusWithin || RankBoardCombo.IsKeyboardFocusWithin) return;
        if (PlayerScreen.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Space) { TogglePause(); e.Handled = true; }
        if (_feedMode && (e.Key == Key.Down || e.Key == Key.Up))
        {
            e.Handled = true;
            await MoveRecommendationAsync(e.Key == Key.Down ? 1 : -1);
        }
        if (_player != null && SeekSlider.IsEnabled && (e.Key == Key.Left || e.Key == Key.Right))
        {
            _player.Seek(Math.Clamp(_position + (e.Key == Key.Right ? 5 : -5), 0, Math.Max(0, _duration)));
            e.Handled = true;
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show("红果原生桌面版 1.0\n\n原生 Windows 窗口与内嵌视频播放器。\n提供逐行榜单、题材筛选、真实互动计数、画质切换、倍速、观看记录及连续推荐。\n下一集提前加载实际媒体并准备首帧，同系列支持跨季连播。\n\n右下角为窗口内全屏，保留当前窗口大小。\nF11 或双击视频进入屏幕全屏，Esc 逐级退出。\n快捷键：空格暂停，左右快进/后退 5 秒。\n沉浸播放使用顶部短浮条，静止约 3 秒隐藏控件与鼠标。\n鼠标靠近上下边缘展开操作栏；中央移动只恢复指针。\n倍速、画质、音量和自动连播放入齿轮设置。\n连续推荐模式：在视频区滚轮或 ↑ / ↓ 切换剧集。\n\n本地数据：" + _configDir, "应用信息", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            // 清理期间的再次关闭继续取消，只有资源释放完成后才真正销毁窗口。
            e.Cancel = !_closeReady;
            return;
        }
        e.Cancel = true;
        _closing = true;
        // 历史保存可能同步完成，先退出本轮 Closing，避免在关闭事件里重入 Close。
        await Dispatcher.Yield(DispatcherPriority.Background);
        CloseEpisodePicker();
        CancelSeasons();
        CancelDiscoveryRequests();
        _playerTimer.Stop();
        RestoreWindowControls();
        CancelNextPlayback();
        _catalogRequest?.Cancel();
        _detailRequest?.Cancel();
        _playRequest?.Cancel();
        _feedRequest?.Cancel();
        _catalogHydration?.Cancel();
        _historyRequest?.Cancel();
        _rankRequest?.Cancel();
        try { await SaveHistoryAsync(false); }
        finally
        {
            _player?.Dispose();
            _standbyPlayer?.Dispose();
            _backend?.Dispose();
            _catalogRequest?.Dispose();
            _detailRequest?.Dispose();
            _playRequest?.Dispose();
            _feedRequest?.Dispose();
            _catalogHydration?.Dispose();
            _historyRequest?.Dispose();
            _rankRequest?.Dispose();
            _closeReady = true;
            Close();
        }
    }
}
