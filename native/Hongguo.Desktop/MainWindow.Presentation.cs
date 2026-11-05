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
using Orientation = System.Windows.Controls.Orientation;

namespace Hongguo.Desktop;

public partial class MainWindow
{
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
    private DateTime _lastControlActivity;

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
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        RankingItems.ItemsSource = _ranking;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
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
        PreviewMouseMove += (_, _) => RevealFullscreenControls();
        PlayerControls.PreviewMouseMove += (_, _) => RevealFullscreenControls();
        PlayerControls.PreviewKeyDown += (_, args) =>
        {
            if (IsImmersivePlayback && !args.Handled) Window_KeyDown(this, args);
        };
        VideoHost.UserActivity += (_, _) => RevealFullscreenControls();
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreenControls();
        Activated += (_, _) => RevealFullscreenControls();
        Deactivated += (_, _) => HideFullscreenControls();
        StateChanged += (_, _) =>
        {
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
        bool compact = width < 680 || (!_fullscreen && ActualHeight < 440);
        bool wide = width >= 950;
        if (!compact) ClosePlaybackOptions();
        PlaybackOptionsButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        PlayerControls.Padding = compact ? new Thickness(8, 0, 8, 6) : new Thickness(18, 7, 18, 14);
        SeekSlider.Height = compact ? 18 : 22;
        SeekSlider.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 0, 4);
        PreviousButton.Visibility = width < 400 ? Visibility.Collapsed : Visibility.Visible;
        TimeText.Visibility = width < 410 ? Visibility.Collapsed : Visibility.Visible;
        bool iconNext = width < 480;
        NextEpisodeLabel.Visibility = iconNext ? Visibility.Collapsed : Visibility.Visible;
        NextEpisodeGlyph.Margin = iconNext ? new Thickness(0) : new Thickness(0, 0, 7, 0);
        NextButton.Width = iconNext ? 32 : double.NaN;
        NextButton.Padding = iconNext ? new Thickness(0) : new Thickness(10, 0, 10, 0);
        NextButton.Margin = compact ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 14, 0);
        PauseButton.Width = compact ? 32 : 44;
        PauseButton.Height = compact ? 32 : 44;
        PauseButton.Margin = compact ? new Thickness(0, 0, 4, 0) : new Thickness(0, 0, 9, 0);
        if (_playbackOptionsPopup == null)
        {
            SettingsGroup.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetRow(SettingsGroup, wide ? 0 : 1);
            Grid.SetColumn(SettingsGroup, wide ? 1 : 0);
            Grid.SetColumnSpan(SettingsGroup, wide ? 1 : 3);
            SettingsGroup.Margin = wide ? new Thickness(12, 0, 12, 0) : new Thickness(0, 12, 0, 0);
        }
        if (_playbackOptionsPopup != null) SizePlaybackOptions();
        RepositionImmersiveControls();
    }

    private void PlaybackOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_playbackOptionsPopup != null)
        {
            ClosePlaybackOptions();
            return;
        }
        CloseEpisodePicker();
        // 复用原来的设置控件和事件，弹层只改变摆放位置，不创建另一份播放状态。
        ControlGroups.Children.Remove(SettingsGroup);
        SettingsGroup.Visibility = Visibility.Visible;
        SettingsGroup.Orientation = Orientation.Vertical;
        SettingsGroup.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        SettingsGroup.Margin = new Thickness(0);
        AutoNextCheck.Margin = new Thickness(0, 0, 0, 12);
        SpeedCombo.Width = double.NaN;
        SpeedCombo.Margin = new Thickness(0, 0, 0, 10);
        QualityCombo.Width = double.NaN;
        QualityCombo.Margin = new Thickness(0, 0, 0, 10);
        VolumeGroup.Margin = new Thickness(0, 0, 0, 10);
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = "播放设置", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        content.Children.Add(SettingsGroup);
        var recommendation = new Button { Content = "换个故事", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 7, 8, 7) };
        recommendation.Click += (button, args) =>
        {
            ClosePlaybackOptions();
            NextRecommendation_Click(button, args);
        };
        content.Children.Add(recommendation);
        var border = new Border
        {
            Background = (Brush)FindResource("PanelBrush"), BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14),
            Child = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        border.PreviewKeyDown += (_, args) =>
        {
            if (args.Handled || (args.Key != Key.F11 && args.Key != Key.Escape)) return;
            ClosePlaybackOptions();
            if (IsImmersivePlayback || args.Key == Key.F11) Window_KeyDown(this, args);
            else args.Handled = true;
        };
        _playbackOptionsPopup = new Popup
        {
            Child = border, PlacementTarget = RootLayout, Placement = PlacementMode.Center,
            AllowsTransparency = true, StaysOpen = false
        };
        _playbackOptionsPopup.Closed += (_, _) => RestorePlaybackOptions();
        SizePlaybackOptions();
        _playbackOptionsPopup.IsOpen = true;
        RevealFullscreenControls();
    }

    private void SizePlaybackOptions()
    {
        if (_playbackOptionsPopup == null) return;
        _playbackOptionsPopup.Width = Math.Min(280, Math.Max(1, RootLayout.ActualWidth - 16));
        _playbackOptionsPopup.Height = Math.Min(300, Math.Max(1, RootLayout.ActualHeight - 16));
    }

    private void ClosePlaybackOptions()
    {
        if (_playbackOptionsPopup != null) _playbackOptionsPopup.IsOpen = false;
    }

    private void RestorePlaybackOptions()
    {
        if (_playbackOptionsPopup?.Child is not Border border || border.Child is not ScrollViewer scroll || scroll.Content is not StackPanel content) return;
        content.Children.Remove(SettingsGroup);
        _playbackOptionsPopup.Child = null;
        _playbackOptionsPopup = null;
        SettingsGroup.Orientation = Orientation.Horizontal;
        SettingsGroup.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        AutoNextCheck.Margin = new Thickness(0, 0, 14, 0);
        SpeedCombo.Width = 84;
        SpeedCombo.Margin = new Thickness(0, 0, 8, 0);
        QualityCombo.Width = 142;
        QualityCombo.Margin = new Thickness(0, 0, 14, 0);
        VolumeGroup.Margin = new Thickness(0, 0, 12, 0);
        ControlGroups.Children.Add(SettingsGroup);
        UpdateControlsLayout();
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
        // Popup 有自己的原生窗口，能覆盖视频 HWND；显隐时不挤占或缩放视频布局。
        CinemaLayout.Children.Remove(PlayerControls);
        PlayerControls.Margin = new Thickness(0);
        PlayerControls.CornerRadius = new CornerRadius(0);
        // 底板完全透明；只给控件轮廓加阴影，浅色视频上也能辨认文字。
        PlayerControls.Background = Brushes.Transparent;
        PlayerControls.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Opacity = 0.85
        };
        PlayerControls.BorderBrush = Brushes.Transparent;
        SeekSlider.Background = new SolidColorBrush(Color.FromArgb(95, 221, 229, 242));
        TimeText.Foreground = Brushes.White;
        _fullscreenControls = new Popup
        {
            PlacementTarget = RootLayout,
            Placement = PlacementMode.Custom,
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            Width = RootLayout.ActualWidth,
            Child = PlayerControls,
            CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            [new CustomPopupPlacement(new Point(0, targetSize.Height - popupSize.Height), PopupPrimaryAxis.Horizontal)]
        };
        RevealFullscreenControls();
        _fullscreenTimer.Start();
    }

    private void RepositionImmersiveControls()
    {
        if (_fullscreenControls == null) return;
        _fullscreenControls.Width = RootLayout.ActualWidth;
        // Popup 是独立窗口，宿主拖动时要主动刷新定位，确保控制栏仍贴住当前客户区底部。
        _fullscreenControls.HorizontalOffset = 0.1;
        _fullscreenControls.HorizontalOffset = 0;
    }

    private void RestoreWindowControls()
    {
        ClosePlaybackOptions();
        _fullscreenTimer.Stop();
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

    private void RevealFullscreenControls()
    {
        if (!IsImmersivePlayback || _closing || _fullscreenControls == null) return;
        // 鼠标经过后台窗口也会产生活动事件，禁止据此重开置顶浮层。
        if (!IsActive || !IsVisible || WindowState == WindowState.Minimized)
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
        if (!IsActive || !IsVisible || WindowState == WindowState.Minimized)
        {
            HideFullscreenControls();
            return;
        }
        if ((DateTime.UtcNow - _lastControlActivity).TotalSeconds < 3) return;
        if (PlayerControls.IsMouseOver || _seeking || SpeedCombo.IsDropDownOpen || QualityCombo.IsDropDownOpen || _playbackOptionsPopup?.IsOpen == true) return;
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
