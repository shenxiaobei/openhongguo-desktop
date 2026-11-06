using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Point = System.Windows.Point;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private void PlaybackOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || !HasPlaybackForeground()) return;
        if (_playbackOptionsPopup != null)
        {
            ClosePlaybackOptions();
            return;
        }
        CloseEpisodePicker();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(247, 23, 27, 34)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(7), Child = scroll
        };
        KeyboardNavigation.SetTabNavigation(border, KeyboardNavigationMode.Cycle);
        border.PreviewKeyDown += (_, args) =>
        {
            if (args.Handled || args.Key is not (Key.Escape or Key.F11)) return;
            ClosePlaybackOptions();
            if (args.Key == Key.F11) Window_KeyDown(this, args);
            args.Handled = true;
        };
        _playbackOptionsPopup = new Popup
        {
            Child = border, PlacementTarget = RootLayout, Placement = PlacementMode.Custom,
            AllowsTransparency = true, StaysOpen = false, PopupAnimation = PopupAnimation.Fade,
            CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
            {
                // 回调尺寸采用设备像素，屏幕坐标也保持同单位，避免高 DPI 下菜单压住浮条。
                Point origin = RootLayout.PointToScreen(new Point());
                Point buttonRight = PlaybackOptionsButton.PointToScreen(new Point(PlaybackOptionsButton.ActualWidth, 0));
                Point controlsTop = PlayerControls.PointToScreen(new Point());
                Point controlsBottom = PlayerControls.PointToScreen(new Point(0, PlayerControls.ActualHeight));
                double gap = 8 * VisualTreeHelper.GetDpi(RootLayout).DpiScaleY;
                double left = Math.Clamp(buttonRight.X - origin.X - popupSize.Width,
                    0, Math.Max(0, targetSize.Width - popupSize.Width));
                double top = IsImmersivePlayback ? controlsBottom.Y - origin.Y + gap : controlsTop.Y - origin.Y - popupSize.Height - gap;
                top = Math.Clamp(top, 0, Math.Max(0, targetSize.Height - popupSize.Height));
                return [new CustomPopupPlacement(new Point(left, top), PopupPrimaryAxis.None)];
            }
        };
        ConfigurePlaybackPopup(_playbackOptionsPopup);
        _playbackOptionsPopup.Closed += (_, _) => RestorePlaybackOptions();
        SetPlaybackMenuPage("root");
        _playbackOptionsPopup.IsOpen = true;
        RevealFullscreenControls();
    }

    private void SetPlaybackMenuPage(string page)
    {
        if (_playbackOptionsPopup?.Child is not Border border || border.Child is not ScrollViewer scroll) return;
        var content = new StackPanel();
        if (page == "speed")
        {
            Button back = CreatePlaybackMenuRow("‹  播放倍速", "返回", () => SetPlaybackMenuPage("root"));
            back.Name = "PlaybackMenuBack";
            content.Children.Add(back);
            AddPlaybackMenuDivider(content);
            int index = 0;
            foreach (ComboBoxItem speed in SpeedCombo.Items.OfType<ComboBoxItem>())
            {
                Button option = CreatePlaybackMenuRow(speed.Content?.ToString() ?? "", "", () =>
                {
                    SpeedCombo.SelectedItem = speed;
                    ClosePlaybackOptions();
                }, selected: ReferenceEquals(speed, SpeedCombo.SelectedItem));
                option.Name = "PlaybackSpeedOption" + index++;
                option.Tag = speed;
                content.Children.Add(option);
            }
        }
        else if (page == "quality")
        {
            Button back = CreatePlaybackMenuRow("‹  画质", "返回", () => SetPlaybackMenuPage("root"));
            back.Name = "PlaybackMenuBack";
            content.Children.Add(back);
            AddPlaybackMenuDivider(content);
            int index = 0;
            foreach (PlaybackQuality quality in QualityCombo.Items.OfType<PlaybackQuality>())
            {
                Button option = CreatePlaybackMenuRow(quality.Label, "", () =>
                {
                    if (!QualityCombo.Items.Contains(quality)) return;
                    QualityCombo.SelectedItem = quality;
                    ClosePlaybackOptions();
                }, selected: ReferenceEquals(quality, QualityCombo.SelectedItem));
                option.Name = "PlaybackQualityOption" + index++;
                option.Tag = quality;
                option.IsEnabled = QualityCombo.IsEnabled;
                content.Children.Add(option);
            }
        }
        else if (page == "framing")
        {
            Button back = CreatePlaybackMenuRow("‹  画面模式", "返回", () => SetPlaybackMenuPage("root"));
            back.Name = "PlaybackMenuBack";
            content.Children.Add(back);
            AddPlaybackMenuDivider(content);
            Button fit = CreatePlaybackMenuRow("完整显示", "保留全画面", () =>
            {
                SetVideoFillMode(false);
                ClosePlaybackOptions();
            }, selected: !_fillVideoWindow);
            fit.Name = "PlaybackFramingFitOption";
            content.Children.Add(fit);
            Button fill = CreatePlaybackMenuRow("裁切铺满", "会裁掉边缘", () =>
            {
                SetVideoFillMode(true);
                ClosePlaybackOptions();
            }, selected: _fillVideoWindow);
            fill.Name = "PlaybackFramingFillOption";
            content.Children.Add(fill);
        }
        else
        {
            string speed = (SpeedCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "1.0 倍";
            string quality = (QualityCombo.SelectedItem as PlaybackQuality)?.Label ?? "暂无可选画质";
            Button speedRow = CreatePlaybackMenuRow("播放倍速", speed, () => SetPlaybackMenuPage("speed"), submenu: true,
                valueBinding: new Binding("SelectedItem.Content") { Source = SpeedCombo });
            speedRow.Name = "PlaybackSpeedMenuButton";
            content.Children.Add(speedRow);
            Button qualityRow = CreatePlaybackMenuRow("画质", quality, () => SetPlaybackMenuPage("quality"), submenu: true,
                valueBinding: new Binding("SelectedItem.Label") { Source = QualityCombo, TargetNullValue = "暂无可选画质" });
            qualityRow.Name = "PlaybackQualityMenuButton";
            qualityRow.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = QualityCombo });
            content.Children.Add(qualityRow);
            Button framingRow = CreatePlaybackMenuRow("画面模式", _fillVideoWindow ? "裁切铺满" : "完整显示",
                () => SetPlaybackMenuPage("framing"), submenu: true);
            framingRow.Name = "PlaybackFramingMenuButton";
            content.Children.Add(framingRow);
            AddPlaybackMenuDivider(content);

            Grid autoRow = CreatePlaybackSettingRow("自动连播");
            var autoNext = new CheckBox { Name = "PlaybackMenuAutoNext", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            autoNext.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(CheckBox.IsChecked)) { Source = AutoNextCheck, Mode = BindingMode.TwoWay });
            AutomationProperties.SetName(autoNext, "自动连播");
            AutomationProperties.SetAutomationId(autoNext, autoNext.Name);
            Grid.SetColumn(autoNext, 1);
            autoRow.Children.Add(autoNext);
            content.Children.Add(autoRow);

            Grid volumeRow = CreatePlaybackSettingRow("音量");
            var volume = new Slider
            {
                Name = "PlaybackMenuVolume", Style = (Style)FindResource("VolumeSlider"), Minimum = 0, Maximum = 100,
                Width = 104, Height = 26, IsMoveToPointEnabled = true, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            volume.SetBinding(Slider.ValueProperty, new Binding(nameof(Slider.Value)) { Source = VolumeSlider, Mode = BindingMode.TwoWay });
            AutomationProperties.SetName(volume, "音量");
            AutomationProperties.SetAutomationId(volume, volume.Name);
            Grid.SetColumn(volume, 1);
            volumeRow.Children.Add(volume);
            content.Children.Add(volumeRow);
        }
        // 原控件保留为唯一状态源；菜单换页只更换视图，沿用原来的播放事件。
        foreach (FrameworkElement element in content.Children)
            if (!string.IsNullOrEmpty(element.Name)) AutomationProperties.SetAutomationId(element, element.Name);
        scroll.Content = content;
        SizePlaybackOptions();
        if (InputManager.Current.MostRecentInputDevice is KeyboardDevice)
        {
            // 键盘打开或换页后进入新菜单；关闭后到达的回调不再争抢焦点。
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (_playbackOptionsPopup?.IsOpen == true && ReferenceEquals(scroll.Content, content))
                    content.Children.OfType<Button>().FirstOrDefault(button => button.IsEnabled)?.Focus();
            }));
        }
    }

    private Button CreatePlaybackMenuRow(string label, string value, Action clicked, bool selected = false, bool submenu = false, Binding? valueBinding = null)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        layout.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(233, 236, 241)) });
        var current = new TextBlock
        {
            Text = value, FontSize = 11, MaxWidth = 105, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.FromRgb(153, 164, 181)), VerticalAlignment = VerticalAlignment.Center
        };
        if (valueBinding != null) current.SetBinding(TextBlock.TextProperty, valueBinding);
        Grid.SetColumn(current, 1);
        layout.Children.Add(current);
        var marker = new TextBlock
        {
            Text = selected ? "✓" : submenu ? "›" : "", FontSize = selected ? 13 : 18,
            Foreground = selected ? (System.Windows.Media.Brush)FindResource("AccentBrush") : new SolidColorBrush(Color.FromRgb(134, 146, 164)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(marker, 2);
        layout.Children.Add(marker);
        var button = new Button { Style = (Style)FindResource("PlayerMenuRow"), Content = layout };
        if (selected) button.Background = new SolidColorBrush(Color.FromArgb(22, 255, 133, 104));
        AutomationProperties.SetName(button, label + (string.IsNullOrEmpty(value) ? "" : "，" + value));
        button.Click += (_, _) => clicked();
        return button;
    }

    private static Grid CreatePlaybackSettingRow(string label)
    {
        var row = new Grid { Height = 36, Margin = new Thickness(11, 0, 11, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(233, 236, 241)), VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static void AddPlaybackMenuDivider(StackPanel content)
    {
        content.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), Margin = new Thickness(10, 5, 10, 5) });
    }

    private void SizePlaybackOptions()
    {
        if (_playbackOptionsPopup?.Child is not Border border || border.Child is not ScrollViewer scroll
            || PresentationSource.FromVisual(PlayerControls) == null) return;
        Point controls = RootLayout.PointFromScreen(PlayerControls.PointToScreen(new Point()));
        double available = IsImmersivePlayback ? RootLayout.ActualHeight - controls.Y - PlayerControls.ActualHeight - 16 : controls.Y - 16;
        double height = Math.Max(1, available);
        _playbackOptionsPopup.Width = Math.Min(240, Math.Max(1, RootLayout.ActualWidth - 16));
        _playbackOptionsPopup.Height = double.NaN;
        _playbackOptionsPopup.MaxHeight = height;
        border.MaxHeight = height;
        scroll.MaxHeight = Math.Max(1, height - 16);
    }

    private void ClosePlaybackOptions()
    {
        if (_playbackOptionsPopup != null) _playbackOptionsPopup.IsOpen = false;
    }

    private void RestorePlaybackOptions()
    {
        if (_playbackOptionsPopup == null) return;
        _playbackOptionsPopup.Child = null;
        _playbackOptionsPopup = null;
    }
}
