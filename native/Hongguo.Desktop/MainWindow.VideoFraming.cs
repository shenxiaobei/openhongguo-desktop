namespace Hongguo.Desktop;

public partial class MainWindow
{
    private bool _fillVideoWindow;

    private void SetVideoFillMode(bool fillWindow)
    {
        // 同时更新预加载实例，交接首帧时沿用本窗口选择的画面模式。
        _player?.SetFillWindow(fillWindow);
        _standbyPlayer?.SetFillWindow(fillWindow);
        _fillVideoWindow = fillWindow;
        _rollingDanmakuDirty = true;
    }
}
