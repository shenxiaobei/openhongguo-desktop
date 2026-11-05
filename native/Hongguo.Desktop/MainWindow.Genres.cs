using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Binding = System.Windows.Data.Binding;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<string, string> _genreSelections = [];
    private readonly Dictionary<string, (GenrePanel Panel, DateTime At)> _genreOptions = [];
    private CancellationTokenSource? _genreOptionsRequest;
    private int _genreOptionsVersion;
    private string? _genreLoadingKey;
    private GenrePanel? _visibleGenrePanel;

    private string ChannelGenre => _channel switch
    {
        "真人剧" => "short_play",
        "漫剧" => "comic_series",
        _ => ""
    };

    private string SelectedGenre => _channel == "推荐" && _genreSelections.TryGetValue("genre", out string? genre)
        ? genre : ChannelGenre;

    private bool GenreFilteringAvailable => string.IsNullOrWhiteSpace(_query) && _channel != "电影";

    private string GenreCacheKeySuffix
    {
        get
        {
            if (!GenreFilteringAvailable || _genreSelections.Count == 0) return "";
            return "\nfilters:" + string.Join("&", _genreSelections.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        }
    }

    private void InitializeDiscoveryPresentation()
    {
        DiscussionItems.ItemsSource = _discussionItems;
        UpdateGenreVisibility();
        UpdateDiscussionTabs();
    }

    private void UpdateGenreVisibility()
    {
        // 电影与搜索没有相同的筛选协议，不把短剧题材带到其他内容中。
        GenreFilterPanel.Visibility = GenreFilteringAvailable ? Visibility.Visible : Visibility.Collapsed;
        ClearGenresButton.IsEnabled = _genreSelections.Count > 0;
    }

    private void ResetGenreSelection()
    {
        _genreSelections.Clear();
        _genreOptionsRequest?.Cancel();
        _genreOptionsVersion++;
        _genreLoadingKey = null;
        _visibleGenrePanel = null;
        GenreGroupsPanel.Children.Clear();
        GenreHint.Text = "正在读取平台题材…";
        UpdateGenreVisibility();
    }

    private async Task RefreshGenreOptionsAsync()
    {
        UpdateGenreVisibility();
        if (_backend == null || _closing || !GenreFilteringAvailable) return;
        string genre = SelectedGenre;
        if (_genreOptions.TryGetValue(genre, out var cached) && (DateTime.Now - cached.At).TotalMinutes < 30)
        {
            RenderGenreOptions(cached.Panel);
            return;
        }
        if (_genreLoadingKey == genre && _genreOptionsRequest?.IsCancellationRequested == false) return;
        _genreOptionsRequest?.Cancel();
        _genreOptionsRequest?.Dispose();
        _genreOptionsRequest = new CancellationTokenSource();
        CancellationToken token = _genreOptionsRequest.Token;
        int version = ++_genreOptionsVersion;
        _genreLoadingKey = genre;
        GenreHint.Text = "正在读取平台题材，推荐列表可先浏览。";
        try
        {
            GenrePanel panel = ModelJson.Parse<GenrePanel>(await _backend.CallAsync("filter_options", new { genre }, token));
            if (token.IsCancellationRequested || version != _genreOptionsVersion || _closing || genre != SelectedGenre) return;
            _genreOptions[genre] = (panel, DateTime.Now);
            RenderGenreOptions(panel);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (version != _genreOptionsVersion || _closing) return;
            GenreHint.Text = "本次题材没有加载完成，切换频道或刷新后可重试。";
        }
        finally
        {
            if (version == _genreOptionsVersion) _genreLoadingKey = null;
        }
    }

    private void RenderGenreOptions(GenrePanel panel)
    {
        _visibleGenrePanel = panel;
        GenreGroupsPanel.Children.Clear();
        foreach (GenreRow row in panel.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Key) || row.Options.Count == 0) continue;
            // 已选择的频道固定体裁，推荐频道才显示接口中的跨体裁选项。
            if (row.Key == "genre" && _channel != "推荐") continue;
            var line = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            line.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = GenreGroupsPanel });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.Children.Add(new TextBlock
            {
                Text = row.Name, FontSize = 10, Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(0, 7, 6, 0), TextWrapping = TextWrapping.Wrap
            });
            var choices = new WrapPanel();
            Grid.SetColumn(choices, 1);
            line.Children.Add(choices);
            AddGenreButton(choices, row.Key, "", "不限");
            foreach (GenreOption option in row.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Name) || string.IsNullOrWhiteSpace(option.Value)) continue;
                AddGenreButton(choices, row.Key, option.Value, option.Name);
            }
            GenreGroupsPanel.Children.Add(line);
        }
        GenreHint.Text = GenreGroupsPanel.Children.Count == 0
            ? "平台本次没有返回可选题材。" : "题材、排序与结果顺序均来自平台；可组合选择。";
        UpdateGenreVisibility();
    }

    private void AddGenreButton(WrapPanel panel, string key, string value, string name)
    {
        string selected = _genreSelections.GetValueOrDefault(key, "");
        bool active = selected == value;
        var button = new Button
        {
            Content = name, Tag = (key, value), Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 5, 3), FontSize = 10,
            Background = (Brush)FindResource(active ? "AccentSoftBrush" : "RaisedBrush"),
            Foreground = (Brush)FindResource(active ? "AccentBrush" : "MutedBrush")
        };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += GenreOption_Click;
        panel.Children.Add(button);
    }

    private async void GenreOption_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ValueTuple<string, string> selection || _closing) return;
        (string key, string value) = selection;
        // 体裁切换时旧按钮可能仍在输入队列，只接受当前面板实际提供的选项。
        GenreRow? row = _visibleGenrePanel?.Rows.FirstOrDefault(item => item.Key == key);
        if (row == null || (!string.IsNullOrWhiteSpace(value) && !row.Options.Any(item => item.Value == value))) return;
        if (_genreSelections.GetValueOrDefault(key, "") == value) return;
        if (key == "genre")
        {
            _genreSelections.Clear();
            _visibleGenrePanel = null;
            GenreGroupsPanel.Children.Clear();
            GenreHint.Text = "正在读取这个体裁的题材…";
        }
        if (string.IsNullOrWhiteSpace(value)) _genreSelections.Remove(key);
        else _genreSelections[key] = value;
        if (_visibleGenrePanel != null) RenderGenreOptions(_visibleGenrePanel);
        if (key == "genre") _ = RefreshGenreOptionsAsync();
        await LoadCatalogAsync(false);
    }

    private async void ClearGenres_Click(object sender, RoutedEventArgs e)
    {
        if (_genreSelections.Count == 0) return;
        ResetGenreSelection();
        _ = RefreshGenreOptionsAsync();
        await LoadCatalogAsync(false);
    }

    private async Task<bool> TryLoadGenreCatalogAsync(bool append)
    {
        UpdateGenreVisibility();
        _ = RefreshGenreOptionsAsync();
        if (!GenreFilteringAvailable || _genreSelections.Count == 0) return false;
        if (_backend == null || _closing) return true;
        if (append && (_catalogBusy || !_hasMore || string.IsNullOrWhiteSpace(_cursor))) return true;
        _catalogRequest?.Cancel();
        _catalogRequest?.Dispose();
        _catalogRequest = new CancellationTokenSource();
        CancellationToken token = _catalogRequest.Token;
        int version = ++_catalogVersion;
        string cacheKey = CatalogKey;
        string cursor = append ? _cursor : "";
        var selections = _genreSelections.ToDictionary(pair => pair.Key, pair => new[] { pair.Value });
        if (!string.IsNullOrWhiteSpace(ChannelGenre)) selections["genre"] = [ChannelGenre];
        _catalogBusy = true;
        MoreButton.IsEnabled = false;
        UpdateNavigation();
        ClearError();
        CatalogStatus.Text = append ? "正在加载更多同类故事…" : "正在按题材寻找故事…";
        if (!append)
        {
            _catalogHydration?.Cancel();
            _catalogHydration?.Dispose();
            _catalogHydration = new CancellationTokenSource();
            _catalog.Clear();
            _cursor = "";
            _hasMore = false;
            MoreButton.Visibility = Visibility.Collapsed;
            if (_catalogCache.TryGetValue(cacheKey, out CatalogSnapshot? cached) && (DateTime.Now - cached.At).TotalMinutes < 5)
            {
                foreach (SeriesItem item in cached.Items) _catalog.Add(item);
                _cursor = cached.Cursor;
                _hasMore = cached.HasMore;
                CatalogStatus.Text = $"已显示缓存的 {_catalog.Count} 部 · 正在更新…";
            }
            ScrollCatalogToTop();
        }
        try
        {
            CatalogPage page = ModelJson.Parse<CatalogPage>(await _backend.CallAsync("filter", new { selections, cursor }, token));
            if (token.IsCancellationRequested || version != _catalogVersion || cacheKey != CatalogKey || _closing) return true;
            if (!append) _catalog.Clear();
            var known = new HashSet<string>(_catalog.Select(item => item.Id));
            foreach (SeriesItem item in page.Items)
            {
                ApplyCachedSummary(item);
                if (!string.IsNullOrWhiteSpace(item.Id) && known.Add(item.Id)) _catalog.Add(item);
            }
            _cursor = page.Cursor;
            _hasMore = page.HasMore && !string.IsNullOrWhiteSpace(page.Cursor) && (!append || page.Cursor != cursor);
            CatalogStatus.Text = _catalog.Count == 0 ? "这个题材组合暂未返回内容，试试其他条件。"
                : $"已筛选 {_catalog.Count} 部" + (_hasMore ? " · 向下滚动，继续发现" : " · 已显示全部返回结果");
            MoreButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;
            _catalogCache[cacheKey] = new CatalogSnapshot(_catalog.ToList(), _cursor, _hasMore, DateTime.Now);
            if (_catalogCache.Count > 12) _catalogCache.Remove(_catalogCache.MinBy(entry => entry.Value.At).Key);
            if (!append) ScrollCatalogToTop();
            _catalogHydration ??= new CancellationTokenSource();
            _catalogHydrationTask = HydrateCatalogAsync(page.Items, cacheKey, _catalogHydration.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version != _catalogVersion || cacheKey != CatalogKey || _closing) return true;
            CatalogStatus.Text = "本次题材结果没有加载完成";
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
        return true;
    }
}
