namespace Hongguo.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? _seasonsRequest;
    private Task<SeasonPage?>? _seasonsTask;
    private string _seasonsSeriesId = "";
    private int _seasonsVersion;
    private Exception? _seasonsFailure;

    private void PrepareSeasons()
    {
        SeriesItem? series = _currentSeries;
        if (_backend == null || _closing || series == null) return;
        if (_seasonsSeriesId == series.Id && _seasonsTask != null) return;
        CancelSeasons();
        _seasonsSeriesId = series.Id;
        _seasonsRequest = new CancellationTokenSource();
        int version = ++_seasonsVersion;
        _seasonsTask = RequestSeasonsAsync(series.Id, version, _seasonsRequest.Token);
    }

    private async Task<SeasonPage?> RequestSeasonsAsync(string seriesId, int version, CancellationToken token)
    {
        try
        {
            SeasonPage page = ModelJson.Parse<SeasonPage>(await _backend!.CallAsync("seasons", new { id = seriesId }, token));
            if (token.IsCancellationRequested || version != _seasonsVersion || _currentSeries?.Id != seriesId || _closing) return null;
            return page;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && version == _seasonsVersion && _currentSeries?.Id == seriesId && !_closing)
                _seasonsFailure = error;
            return null;
        }
    }

    private async Task<bool> TryPlayNextSeasonAsync(int playVersion)
    {
        if (_currentSeries == null || _currentEpisode == null || AutoNextCheck.IsChecked != true || _closing) return false;
        string seriesId = _currentSeries.Id;
        PrepareSeasons();
        Task<SeasonPage?>? task = _seasonsTask;
        int version = _seasonsVersion;
        SeasonPage? page = task == null ? null : await task;
        if (_closing || version != _seasonsVersion || playVersion != _playVersion || _currentSeries?.Id != seriesId) return true;
        if (AutoNextCheck.IsChecked != true) return true;
        if (_seasonsFailure != null)
        {
            PauseButton.Content = "重播";
            PlayingSubtitle.Text = "本季已结束，下一季信息没有加载成功";
            Exception error = _seasonsFailure;
            ShowError(UserMessage(error), RetryNextSeasonAsync);
            return true;
        }
        if (page == null) return false;
        if (page.SeriesId != seriesId || page.LinkVerified && (string.IsNullOrWhiteSpace(page.AlbumId)
            || string.IsNullOrWhiteSpace(_currentSeries.RelatedAlbumId) || page.AlbumId != _currentSeries.RelatedAlbumId
            || _currentSeries.SeasonIndex > 0 && page.CurrentIndex != _currentSeries.SeasonIndex))
        {
            PauseButton.Content = "重播";
            PlayingSubtitle.Text = "本季已结束，下一季信息与当前剧集不匹配。";
            ShowError("下一季信息与当前剧集不匹配，请重新加载后重试。", RetryNextSeasonAsync);
            return true;
        }
        if (!page.LinkVerified || !page.HasNext || page.CurrentIndex <= 0 || page.NextIndex != page.CurrentIndex + 1
            || string.IsNullOrWhiteSpace(page.NextSeriesId) || page.NextSeriesId == seriesId)
        {
            if (!page.LinkVerified && page.CurrentIndex <= 0 && !_currentSeries.IsMultiSeason
                && string.IsNullOrWhiteSpace(_currentSeries.RelatedAlbumId)) return false;
            PauseButton.Content = "重播";
            PlayingSubtitle.Text = string.IsNullOrWhiteSpace(page.Note) ? "本季已结束，下一季暂未上线。" : page.Note;
            return true;
        }

        // 后端仅在真实相邻季已上线时提供 next；不按名称猜测，也不跳过中间未上线季。
        PlayingSubtitle.Text = $"本季已结束，正在加载第 {page.NextIndex} 季…";
        string nextId = page.NextSeriesId;
        try
        {
            bool opened = await OpenSeriesCoreAsync(nextId, true, 0, true);
            if (_closing || _currentSeries?.Id != nextId) return true;
            if (!opened)
            {
                PauseButton.Content = "重播";
                PlayingSubtitle.Text = "下一季暂未返回可播放分集，可以稍后重试。";
                ShowError("下一季暂未返回可播放分集。", () => OpenSeriesAsync(nextId, true));
            }
        }
        catch (Exception error)
        {
            if (_closing) return true;
            PauseButton.Content = "重播";
            PlayingTitle.Text = _currentSeries?.Title ?? "下一季加载未完成";
            DetailTitle.Text = PlayingTitle.Text;
            PlayingSubtitle.Text = "下一季没有加载成功，可以稍后重试。";
            ShowError(UserMessage(error), () => OpenSeriesAsync(nextId, true));
        }
        return true;
    }

    private async Task RetryNextSeasonAsync()
    {
        if (_currentSeries == null || _closing) return;
        CancelSeasons();
        PrepareSeasons();
        await TryPlayNextSeasonAsync(_playVersion);
    }

    private void CancelSeasons()
    {
        _seasonsVersion++;
        _seasonsRequest?.Cancel();
        _seasonsRequest?.Dispose();
        _seasonsRequest = null;
        _seasonsTask = null;
        _seasonsSeriesId = "";
        _seasonsFailure = null;
    }
}
