using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private const int WmActivateApp = 0x001C;
    private const uint GaRootOwner = 3;
    private const uint GwOwner = 4;
    private const int GwlpHwndParent = -8;
    private const uint SwpKeepPositionAndActivation = 0x0001 | 0x0002 | 0x0010;
    private UIElement? _playerReturnScreen;
    private GridLength _navigationWidth;
    private GridLength _headerHeight;
    private GridLength _episodeWidth;
    private Thickness _videoMargin;
    private Thickness _controlsMargin;
    private CornerRadius _controlsCorners;
    private CornerRadius _videoCorners;
    private Thickness _videoBorderThickness;
    private Brush? _controlsBackground;
    private Brush? _controlsBorderBrush;
    private Brush? _timelineBackground;
    private Brush? _timeForeground;
    private System.Windows.Media.Effects.Effect? _controlsEffect;
    private int _controlsRow;
    private int _controlsColumn;
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Popup? _fullscreenControls;
    private Popup? _playbackOptionsPopup;
    private HwndSource? _presentationSource;
    private DateTime _lastControlActivity;
    private bool _controlsKeyboardInteraction;

    private void InitializePresentation()
    {
        _playerReturnScreen = CatalogScreen;
        _navigationWidth = NavColumn.Width;
        _headerHeight = HeaderRow.Height;
        _episodeWidth = EpisodeColumn.Width;
        _videoMargin = VideoBorder.Margin;
        _controlsMargin = PlayerControls.Margin;
        _controlsCorners = PlayerControls.CornerRadius;
        _videoCorners = VideoBorder.CornerRadius;
        _videoBorderThickness = VideoBorder.BorderThickness;
        _controlsBackground = PlayerControls.Background;
        _controlsBorderBrush = PlayerControls.BorderBrush;
        _timelineBackground = SeekSlider.Background;
        _timeForeground = TimeText.Foreground;
        _controlsEffect = PlayerControls.Effect;
        _controlsRow = Grid.GetRow(PlayerControls);
        _controlsColumn = Grid.GetColumn(PlayerControls);
        SourceInitialized += (_, _) =>
        {
            ApplyDarkTitleBar();
            UpdateCaptionIcon();
            _presentationSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _presentationSource?.AddHook(PlaybackWindowMessage);
        };
        Closed += (_, _) =>
        {
            _presentationSource?.RemoveHook(PlaybackWindowMessage);
            _presentationSource = null;
        };
        RankingItems.ItemsSource = _ranking;
        SizeChanged += (_, args) =>
        {
            // 裁切仅作用于当前窗口尺寸；缩放后恢复完整画面，避免字幕随比例变化被裁掉。
            if ((args.WidthChanged || args.HeightChanged) && _fillVideoWindow)
                SetVideoFillMode(false);
            ApplyResponsiveLayout();
        };
        ShellViewport.ScrollChanged += (_, args) =>
        {
            if (args.ViewportWidthChange != 0 || args.ViewportHeightChange != 0) ApplyResponsiveLayout();
            else if (args.HorizontalChange != 0 || args.VerticalChange != 0) UpdateVideoViewportClip();
        };
        VideoHost.LayoutUpdated += (_, _) => UpdateVideoViewportClip();
        LocationChanged += (_, _) =>
        {
            UpdateVideoViewportClip();
            RepositionImmersiveControls();
        };
        PlayerControls.SizeChanged += (_, _) => UpdateControlsLayout();
        PreviewMouseMove += (_, args) => HandlePlayerPointerActivity(args.GetPosition(RootLayout));
        PlayerControls.PreviewMouseMove += (_, _) =>
        {
            _controlsKeyboardInteraction = false;
            RevealFullscreenControls();
        };
        PlayerControls.PreviewKeyDown += (_, args) =>
        {
            _controlsKeyboardInteraction = true;
            if (IsImmersivePlayback && !args.Handled) Window_KeyDown(this, args);
        };
        PlayerControls.PreviewMouseDown += (_, _) => _controlsKeyboardInteraction = false;
        PlayerControls.GotKeyboardFocus += (_, _) =>
        {
            if (InputManager.Current.MostRecentInputDevice is KeyboardDevice) _controlsKeyboardInteraction = true;
            RevealFullscreenControls();
        };
        VideoHost.PointerActivity += (_, screenPosition) =>
        {
            if (_closing || !IsImmersivePlayback || PresentationSource.FromVisual(RootLayout) == null) return;
            HandlePlayerPointerActivity(RootLayout.PointFromScreen(screenPosition));
        };
        VideoHost.UserActivity += (_, _) =>
        {
            _controlsKeyboardInteraction = true;
            RevealFullscreenControls();
        };
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreenControls();
        Activated += (_, _) => RevealFullscreenControls();
        Deactivated += (_, _) => HideFullscreenControls();
        StateChanged += (_, _) =>
        {
            // 最大化扩大的是播放区域，显式裁切模式不能随窗口入口继续裁掉字幕。
            if (!_closing && WindowState == WindowState.Maximized && PlayerScreen.Visibility == Visibility.Visible)
                SetVideoFillMode(false);
            if (WindowState == WindowState.Minimized) HideFullscreenControls();
            else RevealFullscreenControls();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideFullscreenControls(); };
        InitializeDiscoveryPresentation();
        InitializeRankingUpdates();
        EpisodeSidebar.IsVisibleChanged += (_, _) => ApplyResponsiveLayout();
        DiscussionPanel.IsVisibleChanged += (_, _) => ApplyResponsiveLayout();
        UpdateNavigation();
        ApplyResponsiveLayout();
    }

    private void ApplyDarkTitleBar()
    {
        // 使用系统原生标题栏的深色模式，旧版 Windows 忽略不支持的属性。
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        int captionColor = 0x00120D0B;
        _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
    }

    private void UpdateCaptionIcon()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        const int extendedStyleIndex = -20;
        const long dialogModalFrame = 0x00000001;
        long style = GetWindowLongPtr(handle, extendedStyleIndex).ToInt64();
        // 仅抑制标题栏小图标，保留系统菜单、窗口按钮及任务栏可用的大图标。
        long updatedStyle = _fullscreen ? style & ~dialogModalFrame : style | dialogModalFrame;
        if (style != updatedStyle) SetWindowLongPtr(handle, extendedStyleIndex, new IntPtr(updatedStyle));
        _ = SendMessage(handle, 0x0080, IntPtr.Zero, IntPtr.Zero); // WM_SETICON / ICON_SMALL。
        // 屏幕全屏不保留对话框边框；刷新非客户区不移动、缩放、激活或改变窗口层级。
        _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpKeepPositionAndActivation | 0x0004 | 0x0020);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private IntPtr PlaybackWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 跨进程失活先走原生通知，保留 WPF 自身的消息处理。
        if (message == WmActivateApp && wParam == IntPtr.Zero)
        {
            HideFullscreenControls();
            CloseEpisodePicker();
        }
        return IntPtr.Zero;
    }

    private bool HasPlaybackForeground()
    {
        if (_closing || !IsActive || !IsVisible || WindowState == WindowState.Minimized) return false;
        IntPtr owner = new WindowInteropHelper(this).Handle;
        IntPtr foreground = GetForegroundWindow();
        // WPF 激活状态可能晚于系统前台切换；只接受本窗口及其拥有的浮层。
        return owner != IntPtr.Zero && foreground != IntPtr.Zero && GetAncestor(foreground, GaRootOwner) == owner;
    }

    private void ConfigurePlaybackPopup(Popup popup)
    {
        popup.Opened += (_, _) =>
        {
            if (popup.Child == null || PresentationSource.FromVisual(popup.Child) is not HwndSource source) return;
            IntPtr owner = new WindowInteropHelper(this).Handle;
            // Popup 默认具有系统置顶属性；改为主窗口的普通拥有窗口，让系统立即约束层级。
            if (GetWindow(source.Handle, GwOwner) != owner) SetWindowLongPtr(source.Handle, GwlpHwndParent, owner);
            if (GetWindow(source.Handle, GwOwner) != owner
                || !SetWindowPos(source.Handle, new IntPtr(-2), 0, 0, 0, 0, SwpKeepPositionAndActivation))
                popup.IsOpen = false;
        };
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private void ApplyShellLayout()
    {
        bool cinema = PlayerScreen.Visibility == Visibility.Visible;
        if (!cinema) ClosePlaybackOptions();
        NavigationPanel.Visibility = cinema ? Visibility.Collapsed : Visibility.Visible;
        NavColumn.Width = cinema ? new GridLength(0) : _navigationWidth;
        HeaderPanel.Visibility = cinema ? Visibility.Collapsed : Visibility.Visible;
        HeaderRow.Height = cinema ? new GridLength(0) : _headerHeight;
        PlayerHeading.Visibility = IsImmersivePlayback ? Visibility.Collapsed : Visibility.Visible;
        EpisodeSidebar.Visibility = IsImmersivePlayback || DiscussionPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (IsImmersivePlayback) CloseDiscussionPanel();
        EpisodeColumn.Width = IsImmersivePlayback ? new GridLength(0) : _episodeWidth;
        VideoBorder.Margin = IsImmersivePlayback ? new Thickness(0) : _videoMargin;
        VideoBorder.CornerRadius = IsImmersivePlayback ? new CornerRadius(0) : _videoCorners;
        VideoBorder.BorderThickness = IsImmersivePlayback ? new Thickness(0) : _videoBorderThickness;
        string fullscreenAction = _fullscreen ? "退出屏幕全屏" : _windowCinema ? "退出窗口内全屏" : "窗口内全屏";
        FullscreenButton.Tag = IsImmersivePlayback ? "fullscreen" : "windowed";
        FullscreenButton.ToolTip = fullscreenAction + (_fullscreen ? " · Esc / F11" : " · F11 屏幕全屏");
        System.Windows.Automation.AutomationProperties.SetName(FullscreenButton, fullscreenAction);
        UpdateControlsLayout();
        ApplyResponsiveLayout();
    }

    private void UpdateControlsLayout()
    {
        if (PlayerControls == null || SettingsGroup == null) return;
        double width = PlayerControls.ActualWidth;
        PlaybackOptionsButton.Visibility = Visibility.Visible;
        PlayerControls.Padding = new Thickness(width < 400 ? 6 : 10, 0, width < 400 ? 6 : 10, 0);
        SeekSlider.Height = 20;
        SeekSlider.Margin = new Thickness(width < 400 ? 6 : 12, 0, width < 400 ? 8 : 16, 0);
        PreviousButton.Visibility = width < 460 ? Visibility.Collapsed : Visibility.Visible;
        PreviousButton.Height = 30;
        TimeText.Visibility = width < 620 ? Visibility.Collapsed : Visibility.Visible;
        NextEpisodeLabel.Visibility = Visibility.Collapsed;
        NextEpisodeGlyph.Margin = new Thickness(0);
        NextButton.Width = 30;
        NextButton.Height = 30;
        NextButton.Padding = new Thickness(0);
        NextButton.Margin = new Thickness(0, 0, 8, 0);
        PauseButton.Width = 28;
        PauseButton.Height = 28;
        PauseButton.Margin = new Thickness(0, 0, 6, 0);
        ToolbarVolumeGroup.Visibility = width < 720 ? Visibility.Collapsed : Visibility.Visible;
        if (_playbackOptionsPopup == null)
        {
            // 所有窗口尺寸都使用同一组设置，主栏不再常驻下拉框或另起一行。
            SettingsGroup.Visibility = Visibility.Collapsed;
        }
        if (_playbackOptionsPopup != null) SizePlaybackOptions();
        RepositionImmersiveControls();
    }

    private void UpdateNavigation()
    {
        DiscoverNav.Tag = CatalogScreen.Visibility == Visibility.Visible ? "active" : "";
        FeedNav.Tag = PlayerScreen.Visibility == Visibility.Visible && _feedMode ? "active" : "";
        RankingNav.Tag = RankingScreen.Visibility == Visibility.Visible ? "active" : "";
        HistoryNav.Tag = HistoryScreen.Visibility == Visibility.Visible ? "active" : "";
        foreach (Button channel in ChannelTabs.Children.OfType<Button>())
        {
            bool active = string.IsNullOrWhiteSpace(_query) && channel.Tag?.ToString() == _channel;
            channel.Background = (Brush)FindResource(active ? "AccentSoftBrush" : "RaisedBrush");
            channel.Foreground = (Brush)FindResource(active ? "AccentBrush" : "MutedBrush");
        }
    }

    private void MoveControlsToFullscreen()
    {
        ClosePlaybackOptions();
        // 沉浸模式不保留底部空带，短浮条放在上方，底部烧录字幕保持完整。
        PlaybackControlsRow.Height = new GridLength(0);
        CinemaLayout.Children.Remove(PlayerControls);
        PlayerControls.Margin = new Thickness(0);
        PlayerControls.CornerRadius = new CornerRadius(14);
        PlayerControls.Background = Brushes.Transparent;
        PlayerControls.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black, BlurRadius = 5, ShadowDepth = 1, Opacity = 0.8
        };
        PlayerControls.BorderBrush = Brushes.Transparent;
        SeekSlider.Background = new SolidColorBrush(Color.FromArgb(95, 221, 229, 242));
        TimeText.Foreground = Brushes.White;
        _fullscreenControls = new Popup
        {
            PlacementTarget = RootLayout,
            Placement = PlacementMode.Custom,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = true,
            Focusable = false,
            Width = Math.Min(860, Math.Max(1, RootLayout.ActualWidth - 32)),
            Child = PlayerControls,
            CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            [new CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2,
                Math.Min(12 * VisualTreeHelper.GetDpi(RootLayout).DpiScaleY,
                    Math.Max(0, targetSize.Height - popupSize.Height))), PopupPrimaryAxis.Horizontal)]
        };
        ConfigurePlaybackPopup(_fullscreenControls);
        RevealFullscreenControls();
        _fullscreenTimer.Start();
    }

    private void RepositionImmersiveControls()
    {
        if (_fullscreenControls == null) return;
        _fullscreenControls.Width = Math.Min(860, Math.Max(1, RootLayout.ActualWidth - 32));
        // Popup 是独立窗口，宿主拖动或缩放时要刷新顶部居中的定位。
        _fullscreenControls.HorizontalOffset = 0.1;
        _fullscreenControls.HorizontalOffset = 0;
    }

    private void RestoreWindowControls()
    {
        ClosePlaybackOptions();
        _fullscreenTimer.Stop();
        _controlsKeyboardInteraction = false;
        PlaybackControlsRow.Height = GridLength.Auto;
        SetFullscreenCursorHidden(false);
        SpeedCombo.IsDropDownOpen = false;
        QualityCombo.IsDropDownOpen = false;
        if (_fullscreenControls == null) return;
        _fullscreenControls.IsOpen = false;
        _fullscreenControls.Child = null;
        _fullscreenControls = null;
        PlayerControls.Margin = _controlsMargin;
        PlayerControls.CornerRadius = _controlsCorners;
        PlayerControls.Background = _controlsBackground;
        PlayerControls.BorderBrush = _controlsBorderBrush;
        PlayerControls.Effect = _controlsEffect;
        SeekSlider.Background = _timelineBackground;
        TimeText.Foreground = _timeForeground;
        Grid.SetRow(PlayerControls, _controlsRow);
        Grid.SetColumn(PlayerControls, _controlsColumn);
        CinemaLayout.Children.Add(PlayerControls);
    }

    private void HandlePlayerPointerActivity(Point position)
    {
        if (!IsImmersivePlayback || _closing || _fullscreenControls == null) return;
        if (!HasPlaybackForeground())
        {
            HideFullscreenControls();
            return;
        }
        _controlsKeyboardInteraction = false;
        _lastControlActivity = DateTime.UtcNow;
        SetFullscreenCursorHidden(false);
        // 中央活动只恢复指针；靠近上下边缘才展开顶部操作栏。
        if (position.X >= 0 && position.X <= RootLayout.ActualWidth
            && position.Y >= 0 && position.Y <= RootLayout.ActualHeight
            && (position.Y <= 76 || position.Y >= Math.Max(0, RootLayout.ActualHeight - 64)))
            _fullscreenControls.IsOpen = true;
    }

    private void RevealFullscreenControls()
    {
        if (!IsImmersivePlayback || _closing || _fullscreenControls == null) return;
        // 后台鼠标与延迟事件都不能重开浮层。
        if (!HasPlaybackForeground())
        {
            HideFullscreenControls();
            return;
        }
        _lastControlActivity = DateTime.UtcNow;
        SetFullscreenCursorHidden(false);
        _fullscreenControls.IsOpen = true;
    }

    private void UpdateFullscreenControls()
    {
        if (!IsImmersivePlayback || _closing || _fullscreenControls == null) return;
        if (!HasPlaybackForeground())
        {
            HideFullscreenControls();
            return;
        }
        if ((DateTime.UtcNow - _lastControlActivity).TotalSeconds < 3) return;
        if (PlayerControls.IsMouseOver || _seeking || SpeedCombo.IsDropDownOpen || QualityCombo.IsDropDownOpen
            || _playbackOptionsPopup?.IsOpen == true || (_controlsKeyboardInteraction && PlayerControls.IsKeyboardFocusWithin)) return;
        _fullscreenControls.IsOpen = false;
        SetFullscreenCursorHidden(true);
    }

    private void HideFullscreenControls()
    {
        if (_fullscreenControls != null) _fullscreenControls.IsOpen = false;
        SpeedCombo.IsDropDownOpen = false;
        QualityCombo.IsDropDownOpen = false;
        ClosePlaybackOptions();
        SetFullscreenCursorHidden(false);
    }

    private void SetFullscreenCursorHidden(bool hidden)
    {
        // 只改变本窗口和视频子窗口的指针，不使用影响全局计数的 Cursor.Hide。
        Cursor = hidden ? System.Windows.Input.Cursors.None : null;
        PlayerControls.Cursor = hidden ? System.Windows.Input.Cursors.None : null;
        VideoHost.SetCursorHidden(hidden);
    }
}
