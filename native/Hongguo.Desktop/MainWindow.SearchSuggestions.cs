using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? _searchSuggestionRequest;
    private int _searchSuggestionVersion;
    private int _searchSuggestionPopupVersion;
    private bool _searchComposition;
    private readonly Dictionary<string, (List<string> Words, DateTime At)> _searchSuggestionCache = [];

    private void InitializeSearchSuggestions()
    {
        ConfigurePlaybackPopup(SearchSuggestionsPopup);
        SearchInput.TextChanged += (_, _) => ScheduleSearchSuggestions();
        SearchInput.GotKeyboardFocus += (_, _) => ScheduleSearchSuggestions();
        SearchInput.PreviewKeyDown += SearchSuggestions_KeyDown;
        SearchInput.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (SearchInput.IsKeyboardFocusWithin) return;
            _searchComposition = false;
            CloseSearchSuggestions();
        }, DispatcherPriority.Input);
        TextCompositionManager.AddPreviewTextInputStartHandler(SearchInput, (_, _) =>
        {
            _searchComposition = true;
            CloseSearchSuggestions();
        });
        TextCompositionManager.AddPreviewTextInputHandler(SearchInput, (_, _) =>
        {
            _searchComposition = false;
            Dispatcher.BeginInvoke(ScheduleSearchSuggestions, DispatcherPriority.Input);
        });
        SearchSuggestionsList.PreviewMouseLeftButtonDown += async (_, args) =>
        {
            if (ItemsControl.ContainerFromElement(SearchSuggestionsList, args.OriginalSource as DependencyObject) is not ListBoxItem row
                || row.Content is not string word) return;
            args.Handled = true;
            await SubmitSearchSuggestionAsync(word);
        };
        SearchSuggestionsPopup.Closed += (_, _) =>
        {
            // 旧浮层关闭可晚于重新聚焦；只取消它所属的请求，避免吞掉新一轮联想。
            if (_searchSuggestionPopupVersion == _searchSuggestionVersion)
                CancelSearchSuggestionRequest();
        };
        SearchInput.IsVisibleChanged += (_, _) => { if (!SearchInput.IsVisible) CloseSearchSuggestions(); };
        Deactivated += (_, _) => CloseSearchSuggestions();
        SizeChanged += (_, _) => CloseSearchSuggestions();
        LocationChanged += (_, _) => CloseSearchSuggestions();
        Closing += (_, _) => CloseSearchSuggestions();
        PreviewMouseDown += (_, args) =>
        {
            if (!SearchEntry.IsMouseOver && !SearchSuggestionsPopup.IsMouseOver) CloseSearchSuggestions();
        };
    }

    private void CancelSearchSuggestionRequest()
    {
        _searchSuggestionVersion++;
        _searchSuggestionRequest?.Cancel();
        _searchSuggestionRequest?.Dispose();
        _searchSuggestionRequest = null;
    }

    private void CloseSearchSuggestions()
    {
        CancelSearchSuggestionRequest();
        SearchSuggestionsPopup.IsOpen = false;
        SearchSuggestionsList.SelectedIndex = -1;
    }

    private bool CanShowSearchSuggestions(string query, int version)
    {
        return !_closing && !_searchComposition && HasPlaybackForeground() && SearchInput.IsVisible
            && SearchInput.IsKeyboardFocusWithin && version == _searchSuggestionVersion
            && string.Equals(SearchInput.Text.Trim(), query, StringComparison.Ordinal);
    }

    private async void ScheduleSearchSuggestions()
    {
        CancelSearchSuggestionRequest();
        string query = SearchInput.Text.Trim();
        int version = _searchSuggestionVersion;
        SearchSuggestionsList.ItemsSource = null;
        SearchSuggestionsList.SelectedIndex = -1;
        if (query.Length == 0 || _backend == null || !CanShowSearchSuggestions(query, version))
        {
            SearchSuggestionsPopup.IsOpen = false;
            return;
        }
        _searchSuggestionRequest = new CancellationTokenSource();
        CancellationToken token = _searchSuggestionRequest.Token;
        try
        {
            await Task.Delay(300, token);
            if (!CanShowSearchSuggestions(query, version)) return;
            if (_searchSuggestionCache.TryGetValue(query, out var cached) && (DateTime.UtcNow - cached.At).TotalMinutes < 2)
            {
                ShowSearchSuggestionWords(cached.Words);
                return;
            }
            ShowSearchSuggestionStatus("正在联想…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var result = await _backend.CallAsync("suggest", new { query }, timeout.Token);
            if (!CanShowSearchSuggestions(query, version) || token.IsCancellationRequested) return;
            List<string> words = ModelJson.Parse<List<string>>(result)
                .Select(word => word.Trim()).Where(word => word.Length > 0 && word.Length <= 100)
                .Distinct(StringComparer.Ordinal).Take(8).ToList();
            _searchSuggestionCache[query] = (words, DateTime.UtcNow);
            if (_searchSuggestionCache.Count > 30)
                _searchSuggestionCache.Remove(_searchSuggestionCache.MinBy(entry => entry.Value.At).Key);
            ShowSearchSuggestionWords(words);
        }
        catch (OperationCanceledException)
        {
            if (CanShowSearchSuggestions(query, version) && !token.IsCancellationRequested)
                ShowSearchSuggestionStatus("联想稍慢，按 Enter 直接搜索");
        }
        catch
        {
            if (CanShowSearchSuggestions(query, version))
                ShowSearchSuggestionStatus("暂未取得联想，按 Enter 直接搜索");
        }
    }

    private void ShowSearchSuggestionStatus(string text)
    {
        SearchSuggestionsList.ItemsSource = null;
        SearchSuggestionsStatus.Text = text;
        SearchSuggestionsStatus.Visibility = Visibility.Visible;
        OpenSearchSuggestions();
    }

    private void ShowSearchSuggestionWords(List<string> words)
    {
        SearchSuggestionsList.ItemsSource = words;
        SearchSuggestionsList.SelectedIndex = -1;
        SearchSuggestionsStatus.Text = "暂无联想，按 Enter 搜索当前关键词";
        SearchSuggestionsStatus.Visibility = words.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenSearchSuggestions();
    }

    private void OpenSearchSuggestions()
    {
        // 列表服从当前客户区，窗口很矮时直接保留输入框和回车搜索。
        double below = RootLayout.ActualHeight - SearchEntry.TranslatePoint(new Point(0, SearchEntry.ActualHeight), RootLayout).Y - 10;
        if (below < 90) { SearchSuggestionsPopup.IsOpen = false; return; }
        SearchSuggestionsPopup.Width = Math.Min(SearchEntry.ActualWidth, Math.Max(1, RootLayout.ActualWidth - 16));
        double entryLeft = SearchEntry.TranslatePoint(new Point(), RootLayout).X;
        double listLeft = Math.Clamp(entryLeft, 8, Math.Max(8, RootLayout.ActualWidth - SearchSuggestionsPopup.Width - 8));
        SearchSuggestionsPopup.HorizontalOffset = listLeft - entryLeft;
        SearchSuggestionsList.MaxHeight = Math.Min(264, Math.Max(24, below - 72));
        _searchSuggestionPopupVersion = _searchSuggestionVersion;
        SearchSuggestionsPopup.IsOpen = true;
    }

    private async void SearchSuggestions_KeyDown(object sender, KeyEventArgs e)
    {
        if (_searchComposition || e.Key == Key.ImeProcessed) return;
        if (e.Key == Key.Escape && SearchSuggestionsPopup.IsOpen)
        {
            e.Handled = true;
            CloseSearchSuggestions();
            return;
        }
        if (e.Key is Key.Down or Key.Up && SearchSuggestionsPopup.IsOpen && SearchSuggestionsList.Items.Count > 0)
        {
            e.Handled = true;
            int count = SearchSuggestionsList.Items.Count;
            int index = SearchSuggestionsList.SelectedIndex;
            SearchSuggestionsList.SelectedIndex = e.Key == Key.Down ? (index + 1) % count : (index <= 0 ? count - 1 : index - 1);
            SearchSuggestionsList.ScrollIntoView(SearchSuggestionsList.SelectedItem);
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (SearchSuggestionsPopup.IsOpen && SearchSuggestionsList.SelectedItem is string word)
            await SubmitSearchSuggestionAsync(word);
        else
            await SearchAsync();
    }

    private async Task SubmitSearchSuggestionAsync(string word)
    {
        CloseSearchSuggestions();
        // 填词会触发 TextChanged，随后 SearchAsync 再取消本轮请求，避免提交后重开列表。
        SearchInput.Text = word;
        SearchInput.CaretIndex = word.Length;
        await SearchAsync();
    }
}
