using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using Button = System.Windows.Controls.Button;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    public static readonly DependencyProperty CompactLayoutProperty = DependencyProperty.Register(
        nameof(CompactLayout), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public bool CompactLayout
    {
        get => (bool)GetValue(CompactLayoutProperty);
        private set => SetValue(CompactLayoutProperty, value);
    }

    private bool _updatingResponsiveLayout;
    private bool? _genresExpanded;
    private Popup? _episodePickerPopup;

    private void ApplyResponsiveLayout()
    {
        if (_updatingResponsiveLayout || PlayerControls == null || _closing) return;
        _updatingResponsiveLayout = true;
        try
        {
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double height = ActualHeight > 0 ? ActualHeight : Height;
            bool compact = width < 1000;
            bool shortWindow = height < 650;
            bool cinema = PlayerScreen.Visibility == Visibility.Visible;
            bool compactPlayer = width < 1100 || height < 500;
            CompactLayout = compact;

            // 根画布直接服从可见尺寸，播放器不再借助整窗滚动维持较大的固定画布。
            ShellViewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ShellViewport.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            RootLayout.MinWidth = 0;
            RootLayout.MinHeight = 0;
            RootLayout.Width = double.NaN;
            RootLayout.Height = double.NaN;
            ShellViewport.ScrollToHome();

            NavigationContent.Margin = compact ? new Thickness(8, 18, 8, 14) : new Thickness(16, 30, 16, 20);
            BrandPanel.Margin = compact ? new Thickness(9, 0, 0, 20) : new Thickness(10, 0, 0, 34);
            BrandGlyph.Margin = compact ? new Thickness(0, 2, 0, 0) : new Thickness(0, 2, 9, 0);
            NavigationFooter.Margin = compact ? new Thickness(13, 0, 0, 0) : new Thickness(12, 0, 0, 0);
            AboutCompactGlyph.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            foreach (Button button in new[] { DiscoverNav, FeedNav, RankingNav, HistoryNav })
            {
                button.Padding = compact ? new Thickness(0) : new Thickness(14, 0, 14, 0);
                button.HorizontalContentAlignment = compact ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left;
                button.ToolTip = AutomationProperties.GetName(button);
            }
            NavColumn.Width = cinema ? new GridLength(0) : compact ? new GridLength(64) : _navigationWidth;
            HeaderRow.Height = cinema ? new GridLength(0) : new GridLength(shortWindow ? 64 : compact ? 76 : _headerHeight.Value);
            HeaderPanel.Padding = compact || shortWindow ? new Thickness(12, 8, 12, 8) : new Thickness(28, 22, 28, 22);
            PageEyebrow.Visibility = compact || shortWindow ? Visibility.Collapsed : Visibility.Visible;
            PageTitle.FontSize = compact ? 18 : 24;
            SearchColumn.Width = new GridLength(compact ? Math.Clamp((width - 64) * 0.44, 200, 270) : 330);
            SearchButton.Padding = compact ? new Thickness(12, 0, 12, 0) : new Thickness(18, 0, 18, 0);
            SearchButton.Height = compact || shortWindow ? 38 : 44;
            NoticeBar.Padding = shortWindow ? new Thickness(12, 6, 12, 6) : new Thickness(24, 12, 24, 12);

            CatalogScreen.Margin = compact ? new Thickness(12, 12, 8, 0) : new Thickness(26, 22, 18, 0);
            RankingScreen.Margin = compact ? new Thickness(12, 12, 12, 0) : new Thickness(28, 24, 28, 0);
            HistoryScreen.Margin = compact ? new Thickness(12) : new Thickness(28, 24, 28, 24);
            foreach (Button button in ChannelTabs.Children.OfType<Button>())
                button.Padding = compact ? new Thickness(10, 7, 10, 7) : new Thickness(17, 9, 17, 9);
            GenreFilterPanel.Padding = shortWindow ? new Thickness(10, 5, 10, 5) : new Thickness(14, 11, 14, 11);
            GenreFilterPanel.Margin = shortWindow ? new Thickness(2, 0, 4, 10) : new Thickness(2, 0, 10, 18);
            bool showGenres = _genresExpanded ?? !shortWindow;
            GenreOptionsScroll.Visibility = showGenres ? Visibility.Visible : Visibility.Collapsed;
            GenreOptionsScroll.MaxHeight = shortWindow ? 64 : 116;
            GenreHint.Visibility = showGenres ? Visibility.Visible : Visibility.Collapsed;
            GenrePanelToggleButton.Content = "按题材找剧 " + (showGenres ? "▴" : "▾");
            CatalogFooter.Margin = shortWindow ? new Thickness(0, 5, 0, 8) : new Thickness(0, 5, 0, 18);
            RankingHeading.Margin = shortWindow ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 0, 18);
            RankExplanation.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
            RankingFreshnessText.FontSize = compact ? 9 : 10;
            RankTabsPanel.Margin = shortWindow ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 0, 16);
            RankGenreScroll.MaxHeight = shortWindow ? 64 : 116;
            RankGenrePanel.Margin = shortWindow ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, 0, 18);
            RankingFooter.Margin = shortWindow ? new Thickness(0, 5, 0, 8) : new Thickness(0, 10, 0, 20);

            if (IsImmersivePlayback || !cinema || !compactPlayer) CloseEpisodePicker();
            EpisodePickerButton.Visibility = compactPlayer && !IsImmersivePlayback ? Visibility.Visible : Visibility.Collapsed;
            bool smallPlayer = width < 680 || height < 440;
            PlayerHeading.Margin = smallPlayer ? new Thickness(6, 3, 6, 3) : shortWindow ? new Thickness(12, 8, 12, 8) : new Thickness(24, 20, 24, 18);
            PlayerBackButton.Width = smallPlayer ? 30 : 40;
            PlayerBackButton.Height = smallPlayer ? 30 : 40;
            PlayerBackButton.Margin = smallPlayer ? new Thickness(0, 0, 6, 0) : new Thickness(0, 0, 16, 0);
            PlayingTitleGroup.Margin = smallPlayer ? new Thickness(0, 0, 6, 0) : new Thickness(0, 0, 18, 0);
            PlayingSubtitle.Visibility = smallPlayer ? Visibility.Collapsed : Visibility.Visible;
            NextRecommendationButton.Visibility = width < 480 || height < 300 ? Visibility.Collapsed : Visibility.Visible;
            PlayingTitle.FontSize = shortWindow ? 14 : compact ? 16 : 18;
            NextRecommendationButton.Padding = compact ? new Thickness(10, 8, 10, 8) : new Thickness(15, 10, 15, 10);
            EpisodePickerButton.Padding = smallPlayer ? new Thickness(7, 5, 7, 5) : new Thickness(13, 10, 13, 10);
            EpisodePickerButton.Margin = smallPlayer ? new Thickness(0) : new Thickness(0, 0, 6, 0);
            PlayerHeading.Visibility = IsImmersivePlayback || height < 160 ? Visibility.Collapsed : Visibility.Visible;
            EpisodeColumn.Width = IsImmersivePlayback || compactPlayer ? new GridLength(0) : _episodeWidth;
            if (_episodePickerPopup?.IsOpen != true)
            {
                EpisodeSidebar.Visibility = !IsImmersivePlayback && !compactPlayer && DiscussionPanel.Visibility != Visibility.Visible
                    ? Visibility.Visible : Visibility.Collapsed;
                if (compactPlayer || IsImmersivePlayback) DiscussionPanel.Visibility = Visibility.Collapsed;
            }
            DetailDescription.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
            VideoBorder.Margin = IsImmersivePlayback ? new Thickness(0) : smallPlayer ? new Thickness(4, 0, 4, 0) : compactPlayer ? new Thickness(12, 0, 12, 0) : _videoMargin;
            if (!IsImmersivePlayback)
                PlayerControls.Margin = smallPlayer ? new Thickness(4, 0, 4, 4) : compactPlayer ? new Thickness(12, 0, 12, 12) : _controlsMargin;
            if (_episodePickerPopup != null)
            {
                _episodePickerPopup.Width = Math.Min(340, Math.Max(1, RootLayout.ActualWidth - 24));
                _episodePickerPopup.Height = Math.Min(560, Math.Max(1, RootLayout.ActualHeight - 36));
            }
            UpdateControlsLayout();
            UpdateVideoViewportClip();
        }
        finally { _updatingResponsiveLayout = false; }
    }

    private void UpdateVideoViewportClip()
    {
        if (VideoHost == null) return;
        if (_fullscreen || PlayerScreen.Visibility != Visibility.Visible || _closing)
        {
            VideoHost.SetViewportClip(null);
            return;
        }
        // 原生子窗口按实际内容视口裁剪，缩放过渡中也不能越过应用客户区。
        if (ShellViewport.Template?.FindName("PART_ScrollContentPresenter", ShellViewport) is not FrameworkElement viewport
            || !viewport.IsLoaded || PresentationSource.FromVisual(viewport) == null) return;
        System.Windows.Point topLeft = viewport.PointToScreen(new System.Windows.Point());
        System.Windows.Point bottomRight = viewport.PointToScreen(new System.Windows.Point(viewport.ActualWidth, viewport.ActualHeight));
        VideoHost.SetViewportClip(new System.Windows.Rect(topLeft, bottomRight));
    }

    private void GenrePanelToggle_Click(object sender, RoutedEventArgs e)
    {
        bool shortWindow = (ActualHeight > 0 ? ActualHeight : Height) < 650;
        _genresExpanded = !(_genresExpanded ?? !shortWindow);
        ApplyResponsiveLayout();
    }

    private void EpisodePicker_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || IsImmersivePlayback || PlayerScreen.Visibility != Visibility.Visible) return;
        if (_episodePickerPopup?.IsOpen == true)
        {
            CloseEpisodePicker();
            return;
        }
        ClosePlaybackOptions();
        // Popup 自带原生窗口，选集与讨论区能完整覆盖视频 HWND，并复用现有播放事件。
        _updatingResponsiveLayout = true;
        try
        {
            CinemaLayout.Children.Remove(EpisodeSidebar);
            CinemaLayout.Children.Remove(DiscussionPanel);
            var panel = new Grid();
            panel.Children.Add(EpisodeSidebar);
            panel.Children.Add(DiscussionPanel);
            EpisodeSidebar.Margin = new Thickness(0);
            DiscussionPanel.Margin = new Thickness(0);
            EpisodeSidebar.Visibility = Visibility.Visible;
            DiscussionPanel.Visibility = Visibility.Collapsed;
            _episodePickerPopup = new Popup
            {
                Child = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                PlacementTarget = RootLayout, Placement = PlacementMode.Center,
                AllowsTransparency = true, StaysOpen = false,
                Width = Math.Min(340, Math.Max(1, RootLayout.ActualWidth - 24)),
                Height = Math.Min(560, Math.Max(1, RootLayout.ActualHeight - 36))
            };
            _episodePickerPopup.Closed += (_, _) => RestoreEpisodePickerPanels();
            _episodePickerPopup.IsOpen = true;
        }
        finally { _updatingResponsiveLayout = false; }
    }

    private void CloseEpisodePicker()
    {
        if (_episodePickerPopup != null) _episodePickerPopup.IsOpen = false;
    }

    private void RestoreEpisodePickerPanels()
    {
        if (_episodePickerPopup?.Child is not ScrollViewer scroll || scroll.Content is not Grid panel) return;
        panel.Children.Clear();
        _episodePickerPopup.Child = null;
        _episodePickerPopup = null;
        EpisodeSidebar.Margin = new Thickness(18, 0, 24, 24);
        DiscussionPanel.Margin = new Thickness(18, 0, 24, 24);
        DiscussionPanel.Visibility = Visibility.Collapsed;
        CinemaLayout.Children.Add(EpisodeSidebar);
        CinemaLayout.Children.Add(DiscussionPanel);
        ApplyResponsiveLayout();
    }
}
