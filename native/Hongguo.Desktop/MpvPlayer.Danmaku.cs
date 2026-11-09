using System.Globalization;
using System.Runtime.InteropServices;

namespace Hongguo.Desktop;

public sealed partial class MpvPlayer
{
    private long _danmakuTrackId = -1;
    private string _danmakuTrackTitle = "";
    private string _danmakuAss = "";
    private Dictionary<string, string>? _danmakuPreviousProperties;

    public void SetDanmaku(IReadOnlyList<DiscussionEntry> entries, DanmakuSettings settings)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ThrowIfDisposed();
            var normalized = new DanmakuSettings
            {
                Enabled = settings.Enabled, Area = settings.Area, Opacity = settings.Opacity,
                FontSize = settings.FontSize, Speed = settings.Speed, Density = settings.Density
            };
            normalized.Normalize();
            if (!normalized.Enabled || entries.Count == 0)
            {
                ClearDanmakuLocked();
                return;
            }
            if (!_loaded || !_frameReady) return;
            int width = Math.Max(1, (int)ReadLong("dwidth", _lastStatus.VideoWidth));
            int height = Math.Max(1, (int)ReadLong("dheight", _lastStatus.VideoHeight));
            if (_fillWindow)
            {
                // 拉伸铺满的实际宽高比跟随窗口，弹幕仍保留正常字形和可用行道。
                width = Math.Max(1, (int)ReadDouble("osd-dimensions/w", width));
                height = Math.Max(1, (int)ReadDouble("osd-dimensions/h", height));
            }
            double outputHeight = ReadDouble("osd-dimensions/h", height);
            double videoHeight = outputHeight - ReadDouble("osd-dimensions/mt", 0) - ReadDouble("osd-dimensions/mb", 0);
            int displayHeight = (int)Math.Max(1, Math.Round(videoHeight));
            string ass = DanmakuAssRenderer.Render(entries, normalized, width, height, displayHeight);
            if (_danmakuTrackId >= 0 && string.Equals(ass, _danmakuAss, StringComparison.Ordinal)
                && FindDanmakuTrack(_danmakuTrackTitle) == _danmakuTrackId && ReadLong("sid", -1) == _danmakuTrackId) return;

            bool first = _danmakuPreviousProperties == null;
            if (first)
            {
                _danmakuPreviousProperties = new Dictionary<string, string>
                {
                    ["sid"] = ReadDanmakuProperty("sid", "no"),
                    ["sub-visibility"] = ReadDanmakuProperty("sub-visibility", "yes"),
                    ["sub-ass-override"] = ReadDanmakuProperty("sub-ass-override", "scale"),
                    ["sub-delay"] = ReadDanmakuProperty("sub-delay", "0"),
                    ["sub-speed"] = ReadDanmakuProperty("sub-speed", "1")
                };
            }
            long previousTrack = _danmakuTrackId;
            string previousTitle = _danmakuTrackTitle;
            string addedTitle = "openhongguo-danmaku-" + Guid.NewGuid().ToString("N");
            try
            {
                // 这条轨的时间和位置由 ASS 自身定义，用户原有字幕设置在关闭时恢复。
                SetProperty("sub-ass-override", "no");
                SetProperty("sub-delay", "0");
                SetProperty("sub-speed", "1");
                SetProperty("sub-visibility", "yes");
                using var nodes = new CommandNodes();
                var command = nodes.Array([
                    nodes.String("sub-add"), nodes.String("memory://" + ass), nodes.String("select"), nodes.String(addedTitle)
                ]);
                Check(_api.Command(_handle, ref command, IntPtr.Zero), "加载原生弹幕");
                long added = FindDanmakuTrack(addedTitle);
                if (added < 0) throw new InvalidOperationException("原生弹幕轨道未就绪。");
                SetProperty("sid", added.ToString(CultureInfo.InvariantCulture));
                if (ReadLong("sid", -1) != added) throw new InvalidOperationException("原生弹幕轨道选择失败。");
                _danmakuTrackId = added;
                _danmakuTrackTitle = addedTitle;
                _danmakuAss = ass;
            }
            catch
            {
                long failed = FindDanmakuTrack(addedTitle);
                if (failed >= 0) RemoveDanmakuTrack(failed, addedTitle);
                if (first) ClearDanmakuLocked();
                else if (FindDanmakuTrack(previousTitle) == previousTrack)
                    _api.SetProperty(_handle, "sid", previousTrack.ToString(CultureInfo.InvariantCulture));
                throw;
            }
            // 先切到新轨再卸载旧轨，避免更新下一时间窗时出现空白和轨道累积。
            if (previousTrack >= 0 && previousTrack != _danmakuTrackId) RemoveDanmakuTrack(previousTrack, previousTitle);
        }
    }

    public void ClearDanmaku()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ClearDanmakuLocked();
        }
    }

    private void ClearDanmakuLocked()
    {
        if (_danmakuTrackId >= 0) RemoveDanmakuTrack(_danmakuTrackId, _danmakuTrackTitle);
        _danmakuTrackId = -1;
        _danmakuTrackTitle = "";
        _danmakuAss = "";
        if (_danmakuPreviousProperties == null) return;
        foreach (var property in _danmakuPreviousProperties)
            _api.SetProperty(_handle, property.Key, property.Value);
        _danmakuPreviousProperties = null;
    }

    private long FindDanmakuTrack(string title)
    {
        if (title.Length == 0) return -1;
        int count = (int)Math.Clamp(ReadLong("track-list/count", 0), 0, 1000);
        for (int index = 0; index < count; index++)
        {
            string prefix = "track-list/" + index.ToString(CultureInfo.InvariantCulture) + "/";
            if (ReadDanmakuProperty(prefix + "type", "") != "sub" || !ReadFlag(prefix + "external", false)) continue;
            if (ReadDanmakuProperty(prefix + "title", "") == title) return ReadLong(prefix + "id", -1);
        }
        return -1;
    }

    private void RemoveDanmakuTrack(long id, string title)
    {
        // 轨道编号可能随新文件重用，只有唯一标题与外部字幕身份都匹配时才卸载。
        if (FindDanmakuTrack(title) != id) return;
        using var nodes = new CommandNodes();
        var command = nodes.Array([nodes.String("sub-remove"), nodes.String(id.ToString(CultureInfo.InvariantCulture))]);
        _api.Command(_handle, ref command, IntPtr.Zero);
    }

    private string ReadDanmakuProperty(string name, string fallback)
    {
        if (_api.GetNode(_handle, name, 6, out MpvNode node) < 0) return fallback;
        try
        {
            return node.Format switch
            {
                1 => Marshal.PtrToStringUTF8(new IntPtr(node.Value)) ?? fallback,
                3 => node.Value == 0 ? "no" : "yes",
                4 => node.Value.ToString(CultureInfo.InvariantCulture),
                5 => BitConverter.Int64BitsToDouble(node.Value).ToString("R", CultureInfo.InvariantCulture),
                _ => fallback
            };
        }
        finally
        {
            _api.FreeNode(ref node);
        }
    }
}
