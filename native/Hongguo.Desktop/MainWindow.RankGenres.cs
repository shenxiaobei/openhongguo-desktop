using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, string> _rankGenreSelections = [];
    private CancellationTokenSource? _rankGenreOptionsRequest;
    private GenrePanel? _rankGenrePanel;
    private string? _rankGenreLoadedFor;
    private string? _rankGenreLoadingContent;
    private int _rankGenreOptionsVersion;
    private bool _updatingRankGenres;
    private Task _rankGenreHydrationTask = Task.CompletedTask;

    private string? RankFilterGenre => _rankContent switch
    {
        "all" => "",
        "human" => "short_play",
        "comic_series_rank" => "comic_series",
        "ai_playlet" => "ai_series",
        _ => null
    };

    private bool IsRankGenreMode => RankFilterGenre != null && _rankGenreSelections.Count > 0;

    private string RankGenreKey => _rankContent + "\n" + string.Join("&", _rankGenreSelections
        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
        .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    private void UpdateRankGenreState()
    {
        RankBoardCombo.IsEnabled = !IsRankGenreMode;
        ClearRankGenresButton.IsEnabled = _rankGenreSelections.Count > 0;
        RankGenreControls.Visibility = RankFilterGenre == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateRankTabSelection();
        UpdateRankCategoryPresentation();
        if (RankFilterGenre == null)
            RankGenreHint.Text = "这个作品类型暂未提供题材分类，当前展示所选平台榜单。";
        else if (IsRankGenreMode)
            RankGenreHint.Text = "已按题材组合筛选，使用平台热度排序；点「全部题材」返回平台榜单。";
        else if (_rankGenreLoadedFor == _rankContent)
            RankGenreHint.Text = "选择主题、人物设定或故事背景，查看该题材的热门作品。";
    }

    private void ResetRankGenreSelection()
    {
        _rankGenreSelections.Clear();
        _rankGenreExpanded = false;
        CancelRankGenreRequests();
        _rankGenrePanel = null;
        _rankGenreLoadedFor = null;
        _updatingRankGenres = true;
        try
        {
            foreach (ComboBox combo in new[] { RankThemeCombo, RankSettingCombo, RankBackgroundCombo })
            {
                // 先移除旧作品类型的选项，输入队列中的旧选择也会被面板校验丢弃。
                combo.ItemsSource = null;
                combo.IsEnabled = false;
            }
        }
        finally { _updatingRankGenres = false; }
        RankGenreHint.Text = "正在读取这个作品类型的题材分类…";
        UpdateRankGenreState();
    }

    private async Task RefreshRankGenreOptionsAsync()
    {
        UpdateRankGenreState();
        if (_backend == null || _closing || RankFilterGenre is not string genre) return;
        if (_rankContents.Count == 0)
        {
            // 首次先取得作品类型目录，避免题材请求取消初始化后留下空的类型下拉框。
            RankGenreHint.Text = "正在读取平台榜单与作品类型…";
            return;
        }
        string content = _rankContent;
        if (_rankGenreLoadedFor == content && _rankGenrePanel != null) return;
        if (_genreOptions.TryGetValue(genre, out var cached) && (DateTime.Now - cached.At).TotalMinutes < 30)
        {
            RenderRankGenreOptions(cached.Panel, content);
            return;
        }
        if (_rankGenreLoadingContent == content && _rankGenreOptionsRequest?.IsCancellationRequested == false) return;
        CancelRankGenreRequests();
        _rankGenreOptionsRequest = new CancellationTokenSource();
        CancellationToken token = _rankGenreOptionsRequest.Token;
        int version = ++_rankGenreOptionsVersion;
        _rankGenreLoadingContent = content;
        RankGenreHint.Text = "正在读取平台题材分类，榜单可先浏览。";
        try
        {
            GenrePanel panel = ModelJson.Parse<GenrePanel>(await _backend.CallAsync("filter_options", new { genre }, token));
            if (token.IsCancellationRequested || version != _rankGenreOptionsVersion || _closing || content != _rankContent) return;
            // 只复用平台返回的选项缓存，排行榜的选择与发现页保持独立。
            _genreOptions[genre] = (panel, DateTime.Now);
            RenderRankGenreOptions(panel, content);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (version != _rankGenreOptionsVersion || _closing || content != _rankContent) return;
            RankGenreHint.Text = "这次题材分类没有加载完成，刷新榜单后可重试。";
        }
        finally
        {
            if (version == _rankGenreOptionsVersion) _rankGenreLoadingContent = null;
        }
    }

    private void RenderRankGenreOptions(GenrePanel panel, string content)
    {
        if (content != _rankContent || RankFilterGenre == null) return;
        _rankGenrePanel = panel;
        _rankGenreLoadedFor = content;
        _updatingRankGenres = true;
        try
        {
            foreach (ComboBox combo in new[] { RankThemeCombo, RankSettingCombo, RankBackgroundCombo })
            {
                string key = combo.Tag?.ToString() ?? "";
                GenreRow? row = panel.Rows.FirstOrDefault(item => item.Key == key);
                List<GenreOption> options = [new GenreOption { Name = "全部", Value = "" }];
                if (row != null)
                    options.AddRange(row.Options.Where(option => !string.IsNullOrWhiteSpace(option.Name) && !string.IsNullOrWhiteSpace(option.Value)));
                combo.ItemsSource = options;
                combo.SelectedValue = _rankGenreSelections.GetValueOrDefault(key, "");
                combo.IsEnabled = options.Count > 1;
            }
        }
        finally { _updatingRankGenres = false; }
        UpdateRankGenreState();
    }

    private async void RankGenre_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingRankGenres || _closing || sender is not ComboBox combo
            || combo.SelectedItem is not GenreOption option || _rankGenreLoadedFor != _rankContent) return;
        string key = combo.Tag?.ToString() ?? "";
        GenreRow? row = _rankGenrePanel?.Rows.FirstOrDefault(item => item.Key == key);
        if (row == null || (!string.IsNullOrWhiteSpace(option.Value) && !row.Options.Any(item => item.Value == option.Value))) return;
        if (_rankGenreSelections.GetValueOrDefault(key, "") == option.Value) return;
        if (string.IsNullOrWhiteSpace(option.Value)) _rankGenreSelections.Remove(key);
        else _rankGenreSelections[key] = option.Value;
        UpdateRankGenreState();
        await LoadRankingAsync(false);
    }

    private async void ClearRankGenres_Click(object sender, RoutedEventArgs e)
    {
        if (_rankGenreSelections.Count == 0) return;
        _rankGenreSelections.Clear();
        if (_rankGenrePanel != null) RenderRankGenreOptions(_rankGenrePanel, _rankContent);
        UpdateRankGenreState();
        await LoadRankingAsync(false);
    }

    private async Task<bool> TryLoadRankGenreAsync(bool append)
    {
        _ = RefreshRankGenreOptionsAsync();
        if (!IsRankGenreMode) return false;
        if (_backend == null || _closing) return true;
        if (append && (_rankBusy || !_rankHasMore || string.IsNullOrWhiteSpace(_rankCursor))) return true;
        _rankRequest?.Cancel();
        _rankRequest?.Dispose();
        _rankRequest = new CancellationTokenSource();
        CancellationToken token = _rankRequest.Token;
        int version = ++_rankVersion;
        string selectionKey = RankGenreKey;
        string cursor = append ? _rankCursor : "";
        var selections = _rankGenreSelections.ToDictionary(pair => pair.Key, pair => new[] { pair.Value });
        selections["sort"] = ["hot_score"];
        if (!string.IsNullOrWhiteSpace(RankFilterGenre)) selections["genre"] = [RankFilterGenre!];
        _rankBusy = true;
        RankMoreButton.IsEnabled = false;
        ClearError();
        UpdateRankGenreState();
        RankExplanation.Text = "按平台热度排序，数字为当前列表序号；这是题材热门列表，不代表原平台榜单名次，接口没有返回单项热度分。";
        RankStatus.Text = append ? "正在加载更多同类热门作品…" : "正在加载这个题材的热门作品…";
        if (!append)
        {
            _ranking.Clear();
            _rankCursor = "";
            _rankHasMore = false;
            RankMoreButton.Visibility = Visibility.Collapsed;
            RankScroll.ScrollToTop();
        }
        try
        {
            CatalogPage page = ModelJson.Parse<CatalogPage>(await _backend.CallAsync("filter", new { selections, cursor }, token));
            if (token.IsCancellationRequested || version != _rankVersion || selectionKey != RankGenreKey || _closing) return true;
            var known = new HashSet<string>(_ranking.Select(item => item.Id));
            foreach (SeriesItem item in page.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Id) || !known.Add(item.Id)) continue;
                ApplyCachedSummary(item);
                _ranking.Add(new RankedSeriesItem
                {
                    Id = item.Id, Title = item.Title, CoverUrl = item.CoverUrl, EpisodeCount = item.EpisodeCount,
                    Position = _ranking.Count + 1, IsCategoryResult = true
                });
            }
            _rankCursor = page.Cursor;
            _rankHasMore = page.HasMore && !string.IsNullOrWhiteSpace(page.Cursor) && (!append || page.Cursor != cursor);
            RankStatus.Text = _ranking.Count == 0 ? "这个题材组合暂未返回作品，试试其他分类。"
                : $"已加载 {_ranking.Count} 部 · 平台热度排序 · 数字为列表序号";
            RankMoreButton.Visibility = _rankHasMore ? Visibility.Visible : Visibility.Collapsed;
            _rankGenreHydrationTask = HydrateRankGenreCoversAsync(selectionKey, version, token);
            if (!append)
            {
                MarkRankingUpdated();
                await Dispatcher.InvokeAsync(RankScroll.ScrollToTop, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version != _rankVersion || selectionKey != RankGenreKey || _closing) return true;
            RankStatus.Text = "本次题材热门列表没有加载完成";
            ShowError(UserMessage(error), () => LoadRankingAsync(append));
        }
        finally
        {
            if (version == _rankVersion)
            {
                _rankBusy = false;
                RankMoreButton.IsEnabled = true;
            }
        }
        return true;
    }

    private async Task HydrateRankGenreCoversAsync(string selectionKey, int version, CancellationToken token)
    {
        // 每次续页也补齐此前未完成的海报；同一份摘要缓存避免重复请求详情。
        List<string> ids = _ranking.Select(item => item.Id).ToList();
        await HydrateSummariesAsync(ids, summary =>
        {
            if (token.IsCancellationRequested || version != _rankVersion || selectionKey != RankGenreKey
                || RankingScreen.Visibility != Visibility.Visible || _closing) return;
            foreach (RankedSeriesItem item in _ranking.Where(item => item.Id == summary.Id)) item.ApplySummary(summary);
        }, token);
    }

    private void ResumeRankGenreHydration()
    {
        if (_closing || !IsRankGenreMode || _rankBusy || _ranking.Count == 0) return;
        // 点播时会取消榜单请求，返回后只恢复补图，保留榜单、筛选、游标与滚动位置。
        _rankRequest?.Cancel();
        _rankRequest?.Dispose();
        _rankRequest = new CancellationTokenSource();
        int version = ++_rankVersion;
        _rankGenreHydrationTask = HydrateRankGenreCoversAsync(RankGenreKey, version, _rankRequest.Token);
    }

    private void CancelRankGenreRequests()
    {
        _rankGenreOptionsVersion++;
        _rankGenreOptionsRequest?.Cancel();
        _rankGenreOptionsRequest?.Dispose();
        _rankGenreOptionsRequest = null;
        _rankGenreLoadingContent = null;
    }
}
