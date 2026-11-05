using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private readonly ObservableCollection<RankedSeriesItem> _ranking = [];
    private List<RankContentOption> _rankContents = [];
    private CancellationTokenSource? _rankRequest;
    private string _rankContent = "all";
    private string _rankBoard = "ranklist_hot_play_sc";
    private string _rankCursor = "";
    private bool _rankHasMore;
    private bool _rankBusy;
    private bool _updatingRankOptions;
    private int _rankVersion;

    private async Task LoadRankingAsync(bool append)
    {
        if (_backend == null || _closing) return;
        if (await TryLoadRankGenreAsync(append)) return;
        if (append && (_rankBusy || !_rankHasMore || string.IsNullOrWhiteSpace(_rankCursor))) return;
        _rankRequest?.Cancel();
        _rankRequest?.Dispose();
        _rankRequest = new CancellationTokenSource();
        CancellationToken token = _rankRequest.Token;
        int version = ++_rankVersion;
        _rankBusy = true;
        RankMoreButton.IsEnabled = false;
        ClearError();
        RankStatus.Text = append ? "正在加载后续榜单…" : "正在更新榜单…";
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
            RankingPage page = ModelJson.Parse<RankingPage>(await _backend.CallAsync("rank", new
            {
                content = _rankContent, board = _rankBoard, cursor = append ? _rankCursor : ""
            }, token));
            if (token.IsCancellationRequested || version != _rankVersion || _closing) return;
            // 名次、平台推荐和热度沿用接口，不把个性化推荐重新包装成自行计算的热度榜。
            foreach (RankedSeriesItem item in page.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Id)) _ranking.Add(item);
            }
            _rankContent = page.Content;
            _rankBoard = page.Board;
            _rankCursor = page.Cursor;
            _rankHasMore = page.HasMore && !string.IsNullOrWhiteSpace(page.Cursor);
            UpdateRankOptions(page.Contents);
            _ = RefreshRankGenreOptionsAsync();
            string boardName = _rankContents.FirstOrDefault(item => item.Id == _rankContent)?.Boards.FirstOrDefault(item => item.Id == _rankBoard)?.Name ?? "";
            RankExplanation.Text = boardName.Contains("推荐")
                ? "推荐榜由平台按当前匿名设备返回，内容可能因人而异；想看热门作品可选择热播榜。标注「荐」的是平台推荐位。"
                : "榜单顺序、名次与热度来自平台；标注「荐」的推荐位单独展示，不占用数字名次。";
            RankStatus.Text = _ranking.Count == 0 ? "这个榜单暂未返回剧集，试试其他榜单。" : $"已加载 {_ranking.Count} 部 · 榜单顺序与热度由平台提供";
            RankMoreButton.Visibility = _rankHasMore ? Visibility.Visible : Visibility.Collapsed;
            if (!append)
            {
                MarkRankingUpdated();
                await Dispatcher.InvokeAsync(RankScroll.ScrollToTop, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version != _rankVersion || _closing) return;
            RankStatus.Text = "本次榜单没有加载完成";
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
    }

    private void UpdateRankOptions(List<RankContentOption> contents)
    {
        _updatingRankOptions = true;
        try
        {
            // 演员榜的行是人物，本页只提供能直接点播的作品榜。
            if (contents.Count > 0) _rankContents = contents.Where(item => item.Id != "ranklist_celebrity").ToList();
            RankContentCombo.ItemsSource = _rankContents;
            RankContentCombo.SelectedValue = _rankContent;
            RankBoardCombo.ItemsSource = _rankContents.FirstOrDefault(item => item.Id == _rankContent)?.Boards;
            RankBoardCombo.SelectedValue = _rankBoard;
        }
        finally { _updatingRankOptions = false; }
        UpdateRankTabs();
    }

    private async void Ranking_Click(object sender, RoutedEventArgs e)
    {
        Task stopped = StopCurrentAsync();
        if (_fullscreen) ToggleFullscreen();
        _feedMode = false;
        PageTitle.Text = "排行榜";
        ShowScreen(RankingScreen);
        await stopped;
        if (RankingScreen.Visibility == Visibility.Visible) await LoadRankingAsync(false);
    }

    private async void RankContent_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingRankOptions || RankContentCombo.SelectedItem is not RankContentOption selected) return;
        _rankContent = selected.Id;
        ResetRankGenreSelection();
        _ = RefreshRankGenreOptionsAsync();
        // 内容类型切换时，交给服务端选择该类型的默认榜，避免沿用另一类型的榜 ID。
        _rankBoard = "";
        _updatingRankOptions = true;
        RankBoardCombo.ItemsSource = selected.Boards;
        RankBoardCombo.SelectedIndex = -1;
        _updatingRankOptions = false;
        UpdateRankTabs();
        await LoadRankingAsync(false);
    }

    private async void RankBoard_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingRankOptions || IsRankGenreMode || RankBoardCombo.SelectedItem is not RankBoardOption selected) return;
        _rankBoard = selected.Id;
        UpdateRankTabs();
        await LoadRankingAsync(false);
    }

    private async void RankRefresh_Click(object sender, RoutedEventArgs e) => await LoadRankingAsync(false);
    private async void RankMore_Click(object sender, RoutedEventArgs e) => await LoadRankingAsync(true);

    private async void RankSeries_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RankedSeriesItem item) return;
        _feedMode = false;
        await OpenSeriesAsync(item.Id, true);
    }
}
