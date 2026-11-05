namespace Hongguo.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? _nextPlaybackRequest;
    private Task<PlaybackInfo?>? _nextPlaybackTask;
    private string _nextPlaybackEpisodeId = "";
    private string _nextPlaybackQuality = "";
    private DateTime _nextPlaybackCreatedAt;
    private MpvPlayer? _standbyPlayer;
    private PlaybackInfo? _bufferedPlayback;

    private void PrepareNextPlayback(bool initial = false)
    {
        if (_closing || _backend == null || _currentEpisode == null || AutoNextCheck.IsChecked != true)
        {
            CancelNextPlayback();
            return;
        }
        EpisodeItem? next = _episodes.FirstOrDefault(item => item.Playable && item.Number > _currentEpisode.Number);
        if (next == null)
        {
            CancelNextPlayback();
            return;
        }

        // 与正常切集保持一致：新一集默认最高画质，不继承本集的临时画质选择。
        const string nextQuality = "best";
        bool same = _nextPlaybackEpisodeId == next.Id && _nextPlaybackQuality == nextQuality;
        if (same && _nextPlaybackTask != null)
        {
            if (!_nextPlaybackTask.IsCompleted) return;
            double age = (DateTime.UtcNow - _nextPlaybackCreatedAt).TotalSeconds;
            PlaybackInfo? prepared = _nextPlaybackTask.GetAwaiter().GetResult();
            if (prepared != null && IsPreparedPlaybackFresh(prepared, age)) return;
            // 失败时节流，长视频只在接近片尾时更新过期的准备结果。
            if (age < 15 || (!initial && _duration - _position > 45)) return;
        }

        CancelNextPlayback();
        _nextPlaybackRequest = new CancellationTokenSource();
        _nextPlaybackEpisodeId = next.Id;
        _nextPlaybackQuality = nextQuality;
        _nextPlaybackCreatedAt = DateTime.UtcNow;
        _nextPlaybackTask = RequestNextPlaybackAsync(next.Id, nextQuality, _nextPlaybackRequest.Token);
    }

    private async Task<PlaybackInfo?> RequestNextPlaybackAsync(string episodeId, string quality, CancellationToken token)
    {
        try
        {
            PlaybackInfo playback = ModelJson.Parse<PlaybackInfo>(await _backend!.CallAsync("playback", new { episode_id = episodeId, quality }, token));
            if (string.IsNullOrWhiteSpace(playback.Url)) return null;
            token.ThrowIfCancellationRequested();
            try
            {
                _standbyPlayer ??= CreatePlayer(VideoHost.StandbyVideoHandle);
                _standbyPlayer.SetMuted(true);
                _standbyPlayer.Load(playback.Url, playback.AudioUrl, playback.Key, paused: true);
                _bufferedPlayback = playback;
            }
            catch
            {
                // 第二个解码器启动失败时仍保留地址，正式切集沿用单播放器流程。
                _bufferedPlayback = null;
            }
            return playback;
        }
        catch
        {
            // 预准备只是优化，接口失败或取消都交给正式切集时的正常请求处理。
            return null;
        }
    }

    private async Task<PlaybackInfo?> TakePreparedPlaybackAsync(string episodeId, string quality, CancellationToken token)
    {
        Task<PlaybackInfo?>? preparedTask = _nextPlaybackTask;
        DateTime preparedAt = _nextPlaybackCreatedAt;
        if (preparedTask == null || _nextPlaybackEpisodeId != episodeId || _nextPlaybackQuality != quality)
        {
            CancelNextPlayback();
            return null;
        }
        try
        {
            PlaybackInfo? playback = await preparedTask.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            double age = (DateTime.UtcNow - preparedAt).TotalSeconds;
            return playback != null && IsPreparedPlaybackFresh(playback, age) ? playback : null;
        }
        finally
        {
            if (ReferenceEquals(preparedTask, _nextPlaybackTask)) CancelNextPlayback();
        }
    }

    private static bool IsPreparedPlaybackFresh(PlaybackInfo playback, double age)
    {
        // 未返回有效期时只短暂复用；有有效期也留出至少 30 秒给后续连接与解码。
        if (playback.ExpiresAt == 0) return age < 20;
        return age < 75 && playback.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30;
    }

    private PlaybackInfo? ActivateBufferedPlayback(string episodeId, string quality)
    {
        if (_standbyPlayer == null || _bufferedPlayback == null || _player == null ||
            _nextPlaybackEpisodeId != episodeId || _nextPlaybackQuality != quality ||
            !IsPreparedPlaybackFresh(_bufferedPlayback, (DateTime.UtcNow - _nextPlaybackCreatedAt).TotalSeconds)) return null;
        MpvStatus status = _standbyPlayer.GetStatus();
        // 文件打开事件并不代表媒体已准备好；还需要首帧和至少两秒的真实媒体缓存。
        bool buffered = status.CacheSeconds >= 2 || status.CacheReachedEnd || status.CacheBytes >= 16 * 1024 * 1024;
        if (!status.Loaded || !status.FrameReady || status.VideoWidth <= 0 || status.VideoHeight <= 0 || !buffered) return null;

        PlaybackInfo playback = _bufferedPlayback;
        (_player, _standbyPlayer) = (_standbyPlayer, _player);
        VideoHost.SwapVideoSurfaces();
        // 旧播放器现在成为备用实例；取消准备只会停止它，不影响刚提升的播放器。
        CancelNextPlayback();
        _player.SetMuted(false);
        return playback;
    }

    private void CancelNextPlayback()
    {
        _nextPlaybackRequest?.Cancel();
        _nextPlaybackRequest?.Dispose();
        _nextPlaybackRequest = null;
        _nextPlaybackTask = null;
        _nextPlaybackEpisodeId = "";
        _nextPlaybackQuality = "";
        _nextPlaybackCreatedAt = default;
        _bufferedPlayback = null;
        _standbyPlayer?.Stop();
    }
}
