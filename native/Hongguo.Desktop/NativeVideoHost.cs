using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Runtime.InteropServices;

namespace Hongguo.Desktop;

// 使用原生子窗口承载视频；普通控制栏在画面外，沉浸控制栏使用归属主窗口的浮层。
public sealed class NativeVideoHost : WindowsFormsHost
{
    private readonly Panel _container;
    private Panel _activePanel;
    private Panel _standbyPanel;
    private readonly IntPtr _hiddenCursorHandle;
    private readonly Cursor _hiddenCursor;
    private bool _resourcesDisposed;
    private System.Windows.Rect? _viewportClipBounds;
    private IntPtr _viewportClipHandle;
    private (int Left, int Top, int Right, int Bottom)? _viewportClipRegion;
    private bool _updatingViewportClip;
    public IntPtr VideoHandle => _activePanel.Handle;
    public IntPtr StandbyVideoHandle => _standbyPanel.Handle;
    public event EventHandler? ToggleFullscreenRequested;
    public event EventHandler? ExitFullscreenRequested;
    public event EventHandler? TogglePauseRequested;
    public event EventHandler<int>? RecommendationScrollRequested;
    public event EventHandler<int>? SeekRequested;
    public event EventHandler? UserActivity;
    public event EventHandler<System.Windows.Point>? PointerActivity;

    public NativeVideoHost()
    {
        _container = new Panel { BackColor = System.Drawing.Color.Black, Dock = DockStyle.Fill };
        _activePanel = CreateVideoPanel();
        _standbyPanel = CreateVideoPanel();
        _container.Controls.Add(_standbyPanel);
        _container.Controls.Add(_activePanel);
        _activePanel.BringToFront();
        byte[] mask = Enumerable.Repeat((byte)255, 128).ToArray();
        _hiddenCursorHandle = CreateCursor(IntPtr.Zero, 0, 0, 32, 32, mask, new byte[128]);
        if (_hiddenCursorHandle == IntPtr.Zero) throw new InvalidOperationException("视频鼠标指针初始化失败。");
        _hiddenCursor = new Cursor(_hiddenCursorHandle);
        Child = _container;
    }

    public void SwapVideoSurfaces()
    {
        bool focused = _activePanel.ContainsFocus;
        (_activePanel, _standbyPanel) = (_standbyPanel, _activePanel);
        _activePanel.BringToFront();
        if (focused) _activePanel.Focus();
    }

    public void SetCursorHidden(bool hidden)
    {
        // 只改变本宿主的光标，不使用会影响其他窗口的全局 Hide/Show 计数。
        Cursor cursor = hidden ? _hiddenCursor : Cursors.Default;
        _container.Cursor = cursor;
        _activePanel.Cursor = cursor;
        _standbyPanel.Cursor = cursor;
    }

    public void SetViewportClip(System.Windows.Rect? screenBounds)
    {
        _viewportClipBounds = screenBounds;
        ApplyViewportClip();
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ApplyViewportClip();
    }

    private void ApplyViewportClip()
    {
        if (_resourcesDisposed || _updatingViewportClip || Handle == IntPtr.Zero) return;
        _updatingViewportClip = true;
        try
        {
            if (_viewportClipBounds == null)
            {
                if (_viewportClipHandle == Handle && _viewportClipRegion != null && SetWindowRgn(Handle, IntPtr.Zero, true) == 0) return;
                _viewportClipHandle = IntPtr.Zero;
                _viewportClipRegion = null;
                return;
            }
            if (!GetWindowRect(Handle, out NativeRect host)) return;
            System.Windows.Rect viewport = _viewportClipBounds.Value;
            int width = Math.Max(0, host.Right - host.Left);
            int height = Math.Max(0, host.Bottom - host.Top);
            int left = Math.Clamp((int)Math.Ceiling(viewport.Left - host.Left), 0, width);
            int top = Math.Clamp((int)Math.Ceiling(viewport.Top - host.Top), 0, height);
            int right = Math.Max(left, Math.Clamp((int)Math.Floor(viewport.Right - host.Left), 0, width));
            int bottom = Math.Max(top, Math.Clamp((int)Math.Floor(viewport.Bottom - host.Top), 0, height));
            var regionBounds = (Left: left, Top: top, Right: right, Bottom: bottom);
            if (_viewportClipHandle == Handle && _viewportClipRegion == regionBounds) return;
            IntPtr region = CreateRectRgn(left, top, right, bottom);
            if (region == IntPtr.Zero) return;
            // 成功后区域句柄归系统管理；失败时仍由调用方释放，避免 GDI 资源积累。
            if (SetWindowRgn(Handle, region, true) == 0)
            {
                _ = DeleteObject(region);
                return;
            }
            _viewportClipHandle = Handle;
            _viewportClipRegion = regionBounds;
        }
        finally { _updatingViewportClip = false; }
    }

    private Panel CreateVideoPanel()
    {
        var panel = new VideoPanel { BackColor = System.Drawing.Color.Black, Dock = DockStyle.Fill };
        panel.MouseDown += (_, args) =>
        {
            panel.Focus();
            System.Drawing.Point screen = panel.PointToScreen(args.Location);
            PointerActivity?.Invoke(this, new System.Windows.Point(screen.X, screen.Y));
        };
        System.Drawing.Point lastPointer = new(-1, -1);
        panel.MouseMove += (_, args) =>
        {
            if (lastPointer == args.Location) return;
            lastPointer = args.Location;
            System.Drawing.Point screen = panel.PointToScreen(args.Location);
            PointerActivity?.Invoke(this, new System.Windows.Point(screen.X, screen.Y));
        };
        panel.DoubleClick += (_, _) => ToggleFullscreenRequested?.Invoke(this, EventArgs.Empty);
        panel.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePauseRequested?.Invoke(this, EventArgs.Empty); };
        panel.MouseWheel += (_, e) => RecommendationScrollRequested?.Invoke(this, e.Delta < 0 ? 1 : -1);
        panel.PreviewKeyDown += (_, e) => { if (e.KeyCode is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Space or Keys.F11 or Keys.Escape) e.IsInputKey = true; };
        panel.KeyDown += (_, e) =>
        {
            UserActivity?.Invoke(this, EventArgs.Empty);
            if (e.KeyCode == Keys.F11)
            {
                ToggleFullscreenRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            if (e.KeyCode == Keys.Escape)
            {
                ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            if (e.KeyCode == Keys.Space)
            {
                TogglePauseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                RecommendationScrollRequested?.Invoke(this, e.KeyCode == Keys.Down ? 1 : -1);
                e.Handled = true;
            }
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
            {
                SeekRequested?.Invoke(this, e.KeyCode == Keys.Right ? 5 : -5);
                e.Handled = true;
            }
            if (e.Handled) e.SuppressKeyPress = true;
        };
        return panel;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            SetViewportClip(null);
            _resourcesDisposed = true;
            SetCursorHidden(false);
            _hiddenCursor.Dispose();
            if (_hiddenCursorHandle != IntPtr.Zero) DestroyCursor(_hiddenCursorHandle);
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateCursor(IntPtr instance, int x, int y, int width, int height, byte[] andMask, byte[] xorMask);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(IntPtr cursor);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    private sealed class VideoPanel : Panel
    {
        public VideoPanel()
        {
            // 普通 Panel 默认不接收焦点，这里让视频区也能直接使用播放快捷键。
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }
    }
}
