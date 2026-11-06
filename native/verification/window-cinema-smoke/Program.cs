using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Tests = new();
    private static readonly List<object> DispatcherErrors = new();
    private static MainWindow Window = null!;
    private static MpvPlayer Player = null!;
    private static int Failures;
    private static bool Closed;
    private static bool PositioningFixture;
    private static bool OverlayOnly;
    private static bool OptionsOnly;
    private static bool OptionsAfterOverlay;
    private static bool VisibleForeground;

    [STAThread]
    private static int Main(string[] args)
    {
        OverlayOnly = args.Contains("--overlay-only");
        OptionsAfterOverlay = args.Contains("--options-after-overlay");
        OptionsOnly = args.Contains("--options-only") || OptionsAfterOverlay;
        bool visualPreview = args.Contains("--visual-preview");
        VisibleForeground = args.Contains("--visible-foreground");
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);
        app.DispatcherUnhandledException += (_, args) =>
        {
            // 只记录类型与本地调用栈，不记录任何播放参数或远端地址。
            DispatcherErrors.Add(new { type = args.Exception.GetType().Name, stack = args.Exception.StackTrace });
            args.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            if (visualPreview)
            {
                try { await RunVisualPreviewAsync(app); }
                catch (Exception error)
                {
                    Console.WriteLine("本地预览启动失败：" + error.Message);
                    app.Shutdown(1);
                }
                return;
            }
            try { await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { type = error.GetType().Name, error = error.Message }); }
            finally
            {
                if (!Closed && Window != null)
                {
                    // 失败后的兜底仅用于回收本夹具，不记作正常关闭通过。
                    Set("_closing", true);
                    Set("_closeReady", true);
                    Invoke("RestoreWindowControls");
                    Field<MpvPlayer?>("_player")?.Dispose();
                    foreach (FieldInfo field in typeof(MainWindow).GetFields(Private))
                        if (field.GetValue(Window) is DispatcherTimer timer) timer.Stop();
                    Window.Close();
                }
                if (DispatcherErrors.Count > 0) Record("dispatcher_errors", false, DispatcherErrors);
                string report = VisibleForeground ? "results/visible-foreground-smoke.json" : OptionsOnly ? "results/options-only-smoke.json" : OverlayOnly ? "results/overlay-only-smoke.json" : "results/window-cinema-smoke.json";
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, failures = Failures, tests = Tests, dispatcherErrors = DispatcherErrors, visibleForeground = VisibleForeground,
                    scope = VisibleForeground ? "生产MainWindow和对照窗口在工作区可见，Opacity=1，ShowInTaskbar=true；仅本地媒体、自有窗口，严格系统前台/三秒/后台/资源断言。控制层测量仍临时隐藏独立Popup，PNG不含原生视频或桌面合成。" : OptionsOnly ? "生产MainWindow及Popup的快捷键/父级恢复定向验证；可先运行真实同进程激活浮层检查；无SDK后端或媒体。记录真实前台及Popup关闭生命周期，不代表视频桌面合成验证。" : OverlayOnly
                        ? "生产MainWindow与Popup；双自有透明离屏窗口通过Activate切换真实激活状态；实际DispatcherTimer三秒显隐；不启动SDK后端或播放器。控制层PNG与alpha检查来自RenderTargetBitmap，不包含视频或其他窗口像素"
                        : "真实媒体在自有屏幕外窗口；定位专项显式暂开Opacity=0浮层并隐藏原生视频。另使用双自有透明离屏窗口验证真实激活、后台活动、最小化及实际三秒闲置；PNG只含WPF控制层，不包含原生视频像素"
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static void LoadResources(System.Windows.Application app)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Element(presentation + "Application.Resources")!;
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), source.Elements());
        using var reader = resources.CreateReader();
        app.Resources = (ResourceDictionary)XamlReader.Load(reader);
    }

    private static async Task RunVisualPreviewAsync(System.Windows.Application app)
    {
        // 使用真实窗口与解码器，只替换内容来源；预览不启动后端或保存观看记录。
        Window = new MainWindow
        {
            Title = "红果桌面版 1.0 · 本地视觉预览", Width = 1120, Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true, ShowActivated = true
        };
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window,
            typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        app.MainWindow = Window;
        Window.Closed += (_, _) => { Closed = true; app.Shutdown(); };
        var episode = new EpisodeItem { Id = "local-preview", Number = 1, Playable = true, Title = "字幕保护样片" };
        Field<ObservableCollection<EpisodeItem>>("_episodes").Add(episode);
        Set("_currentEpisode", episode);
        Set("_currentSeries", new SeriesItem { Id = "local-preview", Title = "字幕保护样片", EpisodeCount = 1 });
        Control<TextBlock>("PlayingTitle").Text = "字幕保护 · 本地样片";
        Control<TextBlock>("PlayingSubtitle").Text = "底部两行文字已烧录进视频；沉浸播放不再保留底部空带";
        Control<TextBlock>("DetailTitle").Text = "字幕保护样片";
        Control<TextBlock>("DetailCount").Text = "仅本地视觉验证";
        Control<CheckBox>("AutoNextCheck").IsChecked = false;
        Control<FrameworkElement>("PlayerControls").Opacity = 1;
        Window.Show();
        Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
        await Settle();
        Invoke("EnsurePlayer");
        Player = Field<MpvPlayer>("_player");
        string sample = Path.GetFullPath("../mpv-smoke/sample.mp4");
        Require(File.Exists(sample), "缺少本地字幕样片");
        Player.Load(sample, null, null, paused: true);
        Control<FrameworkElement>("VideoHost").Visibility = Visibility.Visible;
        Control<FrameworkElement>("VideoPlaceholder").Visibility = Visibility.Collapsed;
        await Wait(() => Player.GetStatus().FrameReady, 10_000);
        Control<Slider>("SeekSlider").IsEnabled = true;
        Control<Button>("PauseButton").IsEnabled = true;
        Control<Button>("PauseButton").Content = "播放";
        Field<DispatcherTimer>("_playerTimer").Start();
        Require(Field<object?>("_backend") == null, "视觉预览意外启动后端");
        Window.Activate();
    }

    private static async Task RunAsync()
    {
        Window = new MainWindow
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -3000, Top = 20, Width = 640, Height = 360, Opacity = 0
        };
        if (VisibleForeground)
        {
            Window.Title = "透明浮条可见前台验证";
            Window.Left = 40;
            Window.Opacity = 1;
            Window.ShowInTaskbar = true;
            Window.ShowActivated = true;
        }
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window, typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        InputMethod.SetIsInputMethodEnabled(Window, false);
        InputMethod.SetIsInputMethodSuspended(Window, true);
        Window.Closed += (_, _) => Closed = true;
        System.Windows.Application.Current.MainWindow = Window;
        Control<TextBlock>("PlayingTitle").Text = "本地竖屏样片 · 窗口内全屏验证";
        ObservableCollection<EpisodeItem> episodes = Field<ObservableCollection<EpisodeItem>>("_episodes");
        for (int number = 1; number <= 36; number++) episodes.Add(new EpisodeItem { Id = number.ToString(), Number = number, Playable = true });
        // 独立 Popup 不继承主窗口透明度，夹具始终另行隐藏控制层并关闭命中测试。
        Control<FrameworkElement>("PlayerControls").Opacity = 0;
        Window.Show();
        Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
        await Settle();
        Require(Field<object?>("_backend") == null, "夹具意外启动了后端");
        if (OptionsOnly)
        {
            if (OptionsAfterOverlay) await RunOverlayRegressionAsync();
            await RunOptionsShortcutsAsync();
            return;
        }
        if (OverlayOnly)
        {
            await RunOverlayRegressionAsync();
            Require(Field<object?>("_backend") == null && Field<object?>("_player") == null, "独立浮层检查意外启动后端或解码器");
            return;
        }
        Invoke("EnsurePlayer");
        Player = Field<MpvPlayer>("_player");
        string sample = Path.GetFullPath("../mpv-smoke/sample.mp4");
        Require(File.Exists(sample), "缺少既有本地视频样片");
        Player.Load(sample, null, null, paused: true);
        Control<FrameworkElement>("VideoHost").Visibility = Visibility.Visible;
        Control<FrameworkElement>("VideoPlaceholder").Visibility = Visibility.Collapsed;
        Control<Slider>("SeekSlider").IsEnabled = true;
        Control<Button>("PauseButton").IsEnabled = true;
        Control<Button>("NextButton").IsEnabled = true;
        await Wait(() => Player.GetStatus().FrameReady, 10_000);

        foreach ((int width, int height) in new[] { (640, 360), (950, 640) })
        {
            await Check($"button_fills_current_window_{width}x{height}", async () =>
            {
                await ResetNormal(width, height);
                WindowSnapshot before = Snapshot();
                double normalVideoHeight = Control<FrameworkElement>("VideoBorder").ActualHeight;
                Click("FullscreenButton");
                await Settle();
                Require(Field<bool>("_windowCinema") && !Field<bool>("_fullscreen"), "右下角按钮没有只进入窗口内模式");
                RequireSame(before, Snapshot(), "窗口内模式改变了外部窗口");
                VerifyImmersiveLayout();
                Require(Control<FrameworkElement>("VideoBorder").ActualHeight > normalVideoHeight, "视频没有使用释放的标题空间");
                object native = await VerifyNativeVideo();
                object overlay = VerifyOverlayState();
                Capture(Control<FrameworkElement>("RootLayout"), $"results/window-cinema-{width}x{height}.png");
                Click("FullscreenButton");
                await Settle();
                Require(!Field<bool>("_windowCinema") && !Field<bool>("_fullscreen"), "再次点击没有退出窗口内模式");
                RequireSame(before, Snapshot(), "退出窗口内模式改变了外部窗口");
                VerifyNormalLayout();
                return new { before, after = Snapshot(), native, overlay, normalVideoHeight };
            });
        }

        await Check("compact_toolbar_settings_and_shared_volume_state", async () =>
        {
            await ResetNormal(1280, 800);
            Require(Control<FrameworkElement>("SettingsGroup").Visibility == Visibility.Collapsed, "大窗口仍常驻倍速与画质设置");
            Require(Control<FrameworkElement>("PlaybackOptionsButton").IsVisible, "大窗口没有设置入口");
            Require(!Control<FrameworkElement>("SpeedCombo").IsVisible && !Control<FrameworkElement>("QualityCombo").IsVisible,
                "倍速或画质仍占主栏空间");
            Slider toolbarVolume = Control<Slider>("ToolbarVolumeSlider");
            Slider settingsVolume = Control<Slider>("VolumeSlider");
            ComboBox speed = Control<ComboBox>("SpeedCombo");
            CheckBox autoNext = Control<CheckBox>("AutoNextCheck");
            double originalVolume = settingsVolume.Value;
            int originalSpeed = speed.SelectedIndex;
            bool? originalAutoNext = autoNext.IsChecked;
            try
            {
                toolbarVolume.Value = 37;
                await Settle();
                Require(Math.Abs(settingsVolume.Value - 37) < 0.01 && Math.Abs(Player.GetStatus().Volume - 37) < 0.01,
                    "主栏音量没有更新设置控件及真实播放器");
                await ActivateOwnWindow(Window);
                Click("PlaybackOptionsButton");
                await Settle();
                Require(Field<Popup?>("_playbackOptionsPopup")?.IsOpen == true && !speed.IsVisible,
                    "紧凑设置未显示或仍展示大下拉框");
                MenuControl<Slider>("PlaybackMenuVolume").Value = 73;
                MenuControl<CheckBox>("PlaybackMenuAutoNext").IsChecked = false;
                ClickMenu("PlaybackSpeedMenuButton");
                await Settle();
                ClickMenu("PlaybackSpeedOption4");
                await Wait(() => Field<Popup?>("_playbackOptionsPopup") == null, 3000);
                await Settle();
                Require(Math.Abs(toolbarVolume.Value - 73) < 0.01 && Math.Abs(Player.GetStatus().Volume - 73) < 0.01,
                    "设置音量没有同步主栏及真实播放器");
                Require(Math.Abs(Player.GetStatus().Speed - 1.5) < 0.01, "设置中的倍速没有作用于真实播放器");
                Require(speed.SelectedIndex == 4 && autoNext.IsChecked == false, "关闭设置丢失播放状态");
                Require(Control<FrameworkElement>("SettingsGroup").Visibility == Visibility.Collapsed
                    && Control<FrameworkElement>("SettingsGroup").Parent == Control<Grid>("ControlGroups"),
                    "关闭设置没有恢复隐藏的原父级");
                return new { settingsOnly = true, toolbarVolume = 37, settingsVolume = 73, actualSpeed = 1.5,
                    autoNextPreserved = true, menuClickUpdatesOriginalState = true, largeCombosStayHidden = true };
            }
            finally
            {
                Invoke("ClosePlaybackOptions");
                await Wait(() => Field<Popup?>("_playbackOptionsPopup") == null, 3000);
                settingsVolume.Value = originalVolume;
                speed.SelectedIndex = originalSpeed;
                autoNext.IsChecked = originalAutoNext;
            }
        });

        await Check("compact_menu_pages_render_and_quality_click_updates_source", async () =>
        {
            await ResetNormal(1120, 760);
            await ActivateOwnWindow(Window);
            ComboBox quality = Control<ComboBox>("QualityCombo");
            System.Collections.IEnumerable? originalSource = quality.ItemsSource;
            object? originalSelection = quality.SelectedItem;
            bool originalEnabled = quality.IsEnabled;
            try
            {
                quality.ItemsSource = new[] { new PlaybackQuality { Definition = "best" }, new PlaybackQuality { Definition = "worst" } };
                quality.SelectedIndex = 0;
                quality.IsEnabled = true;
                Click("FullscreenButton");
                await Settle();
                Click("PlaybackOptionsButton");
                await Settle();
                Popup menu = Field<Popup>("_playbackOptionsPopup");
                FrameworkElement rootPage = (FrameworkElement)menu.Child;
                Require(rootPage.ActualHeight < 280, "设置根页仍保留过多固定留白");
                double rootHeight = rootPage.ActualHeight;
                CaptureMenu("results/settings-menu-root.png");
                ClickMenu("PlaybackSpeedMenuButton");
                await Settle();
                Require(ReferenceEquals(menu, Field<Popup>("_playbackOptionsPopup")), "倍速页新建了另一层菜单");
                Require(MenuControl<Button>("PlaybackSpeedOption4").Tag == Control<ComboBox>("SpeedCombo").Items[4],
                    "倍速选项没有绑定原控件状态");
                CaptureMenu("results/settings-menu-speed.png");
                ClickMenu("PlaybackMenuBack");
                await Settle();
                ClickMenu("PlaybackQualityMenuButton");
                await Settle();
                Require(ReferenceEquals(menu, Field<Popup>("_playbackOptionsPopup")), "画质页新建了另一层菜单");
                Require(MenuControl<Button>("PlaybackQualityOption1").Tag == quality.Items[1], "画质选项没有绑定原始档位");
                CaptureMenu("results/settings-menu-quality.png");
                ClickMenu("PlaybackQualityOption1");
                await Wait(() => Field<Popup?>("_playbackOptionsPopup") == null, 3000);
                Require(quality.SelectedItem is PlaybackQuality selected && selected.Definition == "worst",
                    "实际画质菜单点击没有更新原选项");
                Require(!Control<FrameworkElement>("SettingsGroup").IsVisible, "菜单切页展示了隐藏状态源");
                return new { samePopupPages = true, naturalRootHeight = rootHeight, qualityMenuSelectsOriginalOption = true,
                    artifacts = new[] { "results/settings-menu-root.png", "results/settings-menu-speed.png", "results/settings-menu-quality.png" },
                    nativeVideoPixelsIncluded = false, onlineQualityReloadTested = false };
            }
            finally
            {
                Invoke("ClosePlaybackOptions");
                await Wait(() => Field<Popup?>("_playbackOptionsPopup") == null, 3000);
                quality.ItemsSource = originalSource;
                quality.SelectedItem = originalSelection;
                quality.IsEnabled = originalEnabled;
            }
        });

        await Check("menu_stale_quality_and_episode_entry_preserve_valid_state", async () =>
        {
            await ResetNormal(1120, 760);
            await ActivateOwnWindow(Window);
            ComboBox quality = Control<ComboBox>("QualityCombo");
            System.Collections.IEnumerable? originalSource = quality.ItemsSource;
            object? originalSelection = quality.SelectedItem;
            bool originalEnabled = quality.IsEnabled;
            string originalQuality = Field<string>("_quality");
            string originalSuccessfulQuality = Field<string>("_lastSuccessfulQuality");
            PlaybackQuality[] originalQualities = Field<ObservableCollection<PlaybackQuality>>("_qualities").ToArray();
            try
            {
                quality.ItemsSource = new[] { new PlaybackQuality { Definition = "best" }, new PlaybackQuality { Definition = "worst" } };
                quality.SelectedIndex = 0;
                quality.IsEnabled = true;
                Click("FullscreenButton");
                await Settle();
                Click("PlaybackOptionsButton");
                await Settle();
                ClickMenu("PlaybackQualityMenuButton");
                await Settle();
                Button staleOption = MenuControl<Button>("PlaybackQualityOption1");
                var replacement = new PlaybackQuality { Definition = "720p" };
                quality.ItemsSource = new[] { replacement };
                quality.SelectedItem = replacement;
                staleOption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(ReferenceEquals(quality.SelectedItem, replacement), "已失效的画质按钮覆盖了当前有效档位");
                Require(Field<string>("_quality") == originalQuality, "失效画质按钮触发了播放档位变更");
                Invoke("ClosePlaybackOptions");
                await Wait(() => Field<Popup?>("_playbackOptionsPopup") == null, 3000);

                Click("PlaybackOptionsButton");
                await Settle();
                Popup menu = Field<Popup>("_playbackOptionsPopup");
                Require(menu.IsOpen, "分集入口回归的设置未打开");
                // 调用生产分集入口；无后端时仍应先回收旧菜单，不产生在线请求。
                await (Task)Invoke("PlayEpisodeAsync", new EpisodeItem { Id = "next-menu-regression", Number = 2, Playable = true }, false)!;
                await Wait(() => !menu.IsOpen && Field<Popup?>("_playbackOptionsPopup") == null, 3000);
                Require(Field<object?>("_backend") == null, "菜单状态回归意外启动后端");
                return new { staleQualityIgnored = true, episodeEntryClosesMenu = true, backendStarted = false,
                    onlineEpisodeTransitionTested = false };
            }
            finally
            {
                Invoke("ClosePlaybackOptions");
                await Wait(() => Field<Popup?>("_playbackOptionsPopup") == null, 3000);
                ObservableCollection<PlaybackQuality> qualities = Field<ObservableCollection<PlaybackQuality>>("_qualities");
                qualities.Clear();
                foreach (PlaybackQuality item in originalQualities) qualities.Add(item);
                quality.ItemsSource = originalSource;
                quality.SelectedItem = originalSelection;
                quality.IsEnabled = originalEnabled;
                Set("_quality", originalQuality);
                Set("_lastSuccessfulQuality", originalSuccessfulQuality);
            }
        });

        await Check("subtitle_safe_layout_remains_accessible_in_small_windows", async () =>
        {
            var sizes = new List<object>();
            foreach ((int width, int height) in new[] { (320, 240), (480, 320), (784, 517), (1280, 800) })
            {
                await ResetNormal(width, height);
                Rect controls = ScreenBounds(Control<FrameworkElement>("PlayerControls"));
                Rect video = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
                Require(video.Height > 1 && video.Bottom <= controls.Top + 2, "普通窗口控制栏覆盖视频");
                foreach (string name in new[] { "SeekSlider", "PauseButton", "NextButton", "PlaybackOptionsButton", "FullscreenButton" })
                {
                    FrameworkElement element = Control<FrameworkElement>(name);
                    Require(element.IsVisible, "小窗口主控不可见：" + name);
                    RequireContains(controls, ScreenBounds(element), "小窗口主控越过控制区：" + name);
                }
                object normalNative = await VerifyNativeVideo();
                Click("FullscreenButton");
                await Settle();
                VerifyImmersiveLayout();
                Rect immersiveVideo = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
                double controlHeight = Control<RowDefinition>("PlaybackControlsRow").ActualHeight;
                Invoke("HideFullscreenControls");
                await Settle();
                RequireNear(immersiveVideo, ScreenBounds(Control<FrameworkElement>("VideoBorder")), "收起控制栏改变视频尺寸");
                Require(controlHeight < 1 && Control<RowDefinition>("PlaybackControlsRow").ActualHeight < 1,
                    "沉浸播放仍保留底部空带");
                object immersiveNative = await VerifyNativeVideo();
                sizes.Add(new { width, height, controlHeight, normalNative, immersiveNative, stableAfterHide = true });
            }
            return new { sizes, normalControlsOutsideVideo = true, immersiveHasNoBottomBand = true, testedWithRealMedia = true };
        });

        await Check("landscape_media_has_no_reserved_bottom_band_or_caption_overlap", async () =>
        {
            string landscape = Path.GetFullPath("../mpv-smoke/sample-landscape.mp4");
            string portrait = Path.GetFullPath("../mpv-smoke/sample.mp4");
            Require(File.Exists(landscape), "缺少1280×720横屏字幕样片");
            try
            {
                await ResetNormal(1280, 800);
                Player.Load(landscape, null, null, paused: true);
                await Wait(() => Player.GetStatus().FrameReady && Player.GetStatus().VideoWidth == 1280, 10000);
                Click("FullscreenButton");
                await Settle();
                VerifyImmersiveLayout();
                object native = await VerifyNativeVideo(1280, 720);
                Rect before = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
                Invoke("HideFullscreenControls");
                await Settle();
                RequireNear(before, ScreenBounds(Control<FrameworkElement>("VideoBorder")), "横屏控件隐藏改变视频尺寸");
                PositioningFixture = true;
                Control<FrameworkElement>("VideoHost").Visibility = Visibility.Hidden;
                Window.Left = 50;
                Window.Top = 20;
                await Settle();
                object overlay = VerifyOverlay();
                Popup popup = Field<Popup>("_fullscreenControls");
                bool opened = popup.IsOpen;
                DispatcherTimer timer = Field<DispatcherTimer>("_fullscreenTimer");
                bool timerEnabled = timer.IsEnabled;
                timer.Stop();
                popup.IsOpen = true;
                await Settle();
                try
                {
                    Rect client = NativeClientBounds(Control<NativeVideoHost>("VideoHost").VideoHandle);
                    double top = ReadNumber("osd-dimensions/mt"), bottom = ReadNumber("osd-dimensions/mb");
                    double imageHeight = ReadNumber("osd-dimensions/h") - top - bottom;
                    Rect captions = new(client.Left, client.Top + top + imageHeight * 0.82, client.Width, imageHeight * 0.18);
                    Require(!ScreenBounds(Control<FrameworkElement>("PlayerControls")).IntersectsWith(captions),
                        "顶部浮条进入横屏实际画面的底部字幕区域");
                }
                finally
                {
                    popup.IsOpen = opened;
                    if (timerEnabled) timer.Start();
                }
                return new { native, overlay, decodedLandscape = new[] { 1280, 720 }, fixedBottomBandAbsent = true,
                    stableAfterHide = true, actualImageCaptionRegionUncovered = true, nativeVideoPixelsIncluded = false };
            }
            finally
            {
                await ResetNormal(640, 360);
                Player.Load(portrait, null, null, paused: true);
                await Wait(() => Player.GetStatus().FrameReady && Player.GetStatus().VideoWidth == 360, 10000);
            }
        });

        await Check("window_cinema_moves_resizes_and_maximizes_with_native_caption", async () =>
        {
            await ResetNormal(640, 360);
            PositioningFixture = true;
            Control<FrameworkElement>("VideoHost").Visibility = Visibility.Hidden;
            Window.Left = 50;
            Window.Top = 20;
            Click("FullscreenButton");
            await Settle();
            object initial = VerifyOverlay();
            Window.Left += 80;
            Window.Top += 55;
            await Settle();
            object moved = VerifyOverlay();
            Window.Width = 780;
            Window.Height = 460;
            await Settle();
            VerifyImmersiveLayout();
            object resizedOverlay = VerifyOverlay();
            WindowSnapshot resizedWindow = Snapshot();
            // 通过自有窗口的 WindowState 走系统最大化状态变化，不发送系统输入。
            Window.WindowState = WindowState.Maximized;
            await Settle();
            Require(Field<bool>("_windowCinema") && !Field<bool>("_fullscreen"), "标题栏最大化误进入屏幕全屏");
            Require(Window.WindowStyle == WindowStyle.SingleBorderWindow && Window.ResizeMode == ResizeMode.CanResize, "窗口内模式移除了原生标题栏");
            VerifyImmersiveLayout();
            object maximizedOverlay = VerifyOverlay();
            Window.WindowState = WindowState.Normal;
            await Settle();
            RequireSame(resizedWindow, Snapshot(), "系统还原没有回到影院窗口原尺寸");
            RaiseKey(Window, Key.Escape);
            await Settle();
            VerifyNormalLayout();
            PositioningFixture = false;
            Window.Left = VisibleForeground ? 40 : -3000;
            Window.Top = 20;
            Control<FrameworkElement>("VideoHost").Visibility = Visibility.Visible;
            await Settle();
            object restoredNative = await VerifyNativeVideo();
            return new { initial, moved, resizedOverlay, maximizedOverlay, nativeCaptionPreserved = true, invisiblePositioningFixture = true, restoredNative };
        });

        await Check("compact_settings_fit_window_and_open_away_from_toolbar", async () =>
        {
            var layouts = new List<object>();
            try
            {
                foreach ((int width, int height) in new[] { (320, 240), (640, 360), (950, 640) })
                {
                    await ResetNormal(width, height);
                    PositioningFixture = true;
                    Control<FrameworkElement>("VideoHost").Visibility = Visibility.Hidden;
                    Window.Left = 50;
                    Window.Top = 20;
                    await Settle();
                    await ActivateOwnWindow(Window);
                    foreach (bool immersive in new[] { false, true })
                    {
                        if (immersive)
                        {
                            Click("FullscreenButton");
                            await Settle();
                        }
                        Click("PlaybackOptionsButton");
                        await Settle();
                        Popup options = Field<Popup>("_playbackOptionsPopup");
                        Require(options.IsOpen, "设置定位检查未打开菜单");
                        Rect panel = ScreenBounds((FrameworkElement)options.Child);
                        Rect controls = ScreenBounds(Control<FrameworkElement>("PlayerControls"));
                        Rect root = ScreenBounds(Control<FrameworkElement>("RootLayout"));
                        if (immersive)
                            Require(panel.Top >= controls.Bottom - 2, "顶部菜单没有向下展开：" + panel + " / " + controls);
                        else
                            Require(panel.Bottom <= controls.Top + 2, "普通窗口菜单遮住主栏：" + panel + " / " + controls);
                        RequireContains(root, panel, "设置面板越过当前窗口客户区");
                        Require(((FrameworkElement)options.Child).ActualWidth <= 260 && ((FrameworkElement)options.Child).ActualHeight <= 280,
                            "根菜单仍有过大的固定尺寸：" + panel);
                        layouts.Add(new { width, height, immersive, panel = panel.ToString(CultureInfo.InvariantCulture),
                            controls = controls.ToString(CultureInfo.InvariantCulture), toolbarUncovered = true, compactRootMenu = true });
                        Invoke("ClosePlaybackOptions");
                        await Wait(() => Field<Popup?>("_playbackOptionsPopup") == null, 3000);
                    }
                }
                return new { layouts, actualPopupBoundsMeasured = true, invisiblePositioningFixture = true };
            }
            finally
            {
                Invoke("ClosePlaybackOptions");
                await Wait(() => Field<Popup?>("_playbackOptionsPopup") == null, 3000);
                await ResetNormal(640, 360);
            }
        });

        await Check("f11_screen_fullscreen_preserves_normal_window", async () =>
        {
            await ResetNormal(950, 640);
            WindowSnapshot before = Snapshot();
            RaiseKey(Window, Key.F11);
            await Settle();
            Require(Field<bool>("_fullscreen") && Window.WindowStyle == WindowStyle.None && Window.WindowState == WindowState.Maximized, "F11没有进入真正屏幕全屏");
            VerifyImmersiveLayout();
            Require(Player.GetStatus().FrameReady, "屏幕模式切换丢失已准备媒体");
            RaiseKey(Window, Key.Escape);
            await Settle();
            Require(!Field<bool>("_fullscreen") && !Field<bool>("_windowCinema"), "Esc没有恢复普通模式");
            RequireSame(before, Snapshot(), "F11退出没有恢复原外窗");
            VerifyNormalLayout();
            return new { before, restored = Snapshot(), nativeHostHiddenDuringScreenMode = true };
        });

        await Check("f11_and_escape_restore_previous_window_cinema", async () =>
        {
            await ResetNormal(640, 360);
            Click("FullscreenButton");
            await Settle();
            WindowSnapshot before = Snapshot();
            RaiseKey(Window, Key.F11);
            await Settle();
            Require(Field<bool>("_fullscreen"), "窗口内模式F11没有进入屏幕全屏");
            RaiseKey(Window, Key.Escape);
            await Settle();
            Require(!Field<bool>("_fullscreen") && Field<bool>("_windowCinema"), "首个Esc没有恢复先前窗口内模式");
            RequireSame(before, Snapshot(), "退出屏幕全屏没有恢复影院外窗");
            VerifyImmersiveLayout();
            object overlay = VerifyOverlayState();
            RaiseKey(Window, Key.Escape);
            await Settle();
            Require(!Field<bool>("_windowCinema"), "第二个Esc没有退出窗口内模式");
            VerifyNormalLayout();
            return new { previousModeRestored = true, secondEscapeRestoresNormal = true, overlay };
        });

        await Check("native_doubleclick_route_and_player_exit_button", async () =>
        {
            await ResetNormal(640, 360);
            NativeVideoHost host = Control<NativeVideoHost>("VideoHost");
            var toggle = (EventHandler)typeof(NativeVideoHost).GetField("ToggleFullscreenRequested", Private)!.GetValue(host)!;
            // 直接调用原生宿主事件的生产订阅链，不模拟鼠标或系统键盘输入。
            host.Visibility = Visibility.Hidden;
            toggle(host, EventArgs.Empty);
            await Settle();
            Require(Field<bool>("_fullscreen"), "原生双击/F11共用事件未进入屏幕全屏");
            Click("FullscreenButton");
            await Settle();
            Require(!Field<bool>("_fullscreen") && !Field<bool>("_windowCinema"), "屏幕全屏中的右下角按钮未恢复普通模式");
            Click("FullscreenButton");
            await Settle();
            var exit = (EventHandler)typeof(NativeVideoHost).GetField("ExitFullscreenRequested", Private)!.GetValue(host)!;
            exit(host, EventArgs.Empty);
            await Settle();
            Require(!Field<bool>("_windowCinema") && !Field<bool>("_fullscreen"), "原生Esc事件未退出窗口内模式");
            VerifyNormalLayout();
            return new { nativeToggleSubscriptionVerified = true, screenExitButtonRestoredNormal = true, nativeEscapeRestoredNormal = true, actualSystemInput = false };
        });

        await RunOverlayRegressionAsync();

        await RunOptionsShortcutsAsync();

        await Check("single_close_from_window_cinema_releases_player_and_popups", async () =>
        {
            await ResetNormal(640, 360);
            await ActivateOwnWindow(Window);
            Click("FullscreenButton");
            await Settle();
            Popup overlay = Field<Popup>("_fullscreenControls");
            Click("PlaybackOptionsButton");
            await Settle();
            Popup? options = Field<Popup?>("_playbackOptionsPopup");
            var elapsed = Stopwatch.StartNew();
            Window.Close();
            await Wait(() => Closed, 7000);
            Require((bool)typeof(MpvPlayer).GetField("_disposed", Private)!.GetValue(Player)!, "真实播放器关闭后仍未释放");
            Require(!overlay.IsOpen && (options == null || !options.IsOpen), "关闭后播放浮层仍然开启");
            foreach (FieldInfo field in typeof(MainWindow).GetFields(Private))
                if (field.GetValue(Window) is DispatcherTimer timer) Require(!timer.IsEnabled, "关闭后计时器仍在运行");
            Require(DispatcherErrors.Count == 0, "首次关闭产生Dispatcher异常");
            return new { singleClose = true, closed = Closed, elapsedMs = elapsed.Elapsed.TotalMilliseconds, decoderDisposed = true, popupsClosed = true };
        });
    }

    private static async Task RunOptionsShortcutsAsync()
    {
        await Check("options_shortcuts_and_navigation_restore_parents", async () =>
        {
            await ResetNormal(640, 360);
            await ActivateOwnWindow(Window);
            Click("FullscreenButton");
            await Settle();
            Click("PlaybackOptionsButton");
            await Settle();
            Popup options = Field<Popup>("_playbackOptionsPopup");
            Require(options.IsOpen, "影院播放设置未打开");
            bool optionsClosed = false;
            options.Closed += (_, _) => optionsClosed = true;
            object beforeEscape = ForegroundDiagnostic();
            RaiseKey(Control<FrameworkElement>("PlaybackOptionsButton"), Key.Escape);
            object immediatelyAfterEscape = ForegroundDiagnostic();
            // 淡出结束后才触发Closed，等待真实生命周期，不用固定150ms代替菜单归还完成。
            await Wait(() => optionsClosed && Field<Popup?>("_playbackOptionsPopup") == null, 3000);
            await Settle();
            Require(Field<Popup?>("_playbackOptionsPopup") == null && Field<bool>("_windowCinema") && !Field<bool>("_fullscreen"),
                "主栏齿轮上的Esc没有只收起设置 / " + JsonSerializer.Serialize(new { beforeEscape, immediatelyAfterEscape, afterSettle = ForegroundDiagnostic() }));
            Click("PlaybackOptionsButton");
            await Settle();
            options = Field<Popup>("_playbackOptionsPopup");
            RaiseKey((FrameworkElement)options.Child, Key.F11);
            await Settle();
            Require(Field<bool>("_fullscreen") && Field<Popup?>("_playbackOptionsPopup") == null, "设置里的F11未切换或未收起弹层");
            Require(Control<FrameworkElement>("SettingsGroup").Parent == Control<Grid>("ControlGroups"), "切全屏后设置父级未恢复");
            RaiseKey(Window, Key.Escape);
            await Settle();
            Require(Field<bool>("_windowCinema") && !Field<bool>("_fullscreen"), "设置F11返回丢失原影院模式");
            Invoke("ShowScreen", Control<Grid>("CatalogScreen"));
            await Settle();
            Require(!Field<bool>("_windowCinema") && !Field<bool>("_fullscreen"), "离开播放页没有退出影院状态");
            Require(Field<Popup?>("_fullscreenControls") == null && Field<Popup?>("_playbackOptionsPopup") == null, "导航后仍有播放浮层");
            Require(Control<FrameworkElement>("PlayerControls").Parent == Control<Grid>("CinemaLayout"), "导航后播放控制父级未恢复");
            Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
            await Settle();
            VerifyNormalLayout();
            return new { escapeFromToolbarClosesOnlyOptions = true, beforeEscape, immediatelyAfterEscape, f11FromOptions = true, previousCinemaRestored = true,
                navigationResetModes = true, parentRestored = true, backendStarted = Field<object?>("_backend") != null };
        });

    }

    private static async Task RunOverlayRegressionAsync()
    {
        var other = new System.Windows.Window
        {
            Title = "本地浮层回归对照窗口", ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -3800, Top = 20,
            Width = 300, Height = 200, Opacity = 0
        };
        if (VisibleForeground)
        {
            other.Left = 1100;
            other.Top = 120;
            other.Opacity = 1;
            other.ShowInTaskbar = true;
        }
        FrameworkElement controls = Control<FrameworkElement>("PlayerControls");
        bool hitTestVisible = controls.IsHitTestVisible;
        Visibility videoVisibility = Control<FrameworkElement>("VideoHost").Visibility;
        controls.IsHitTestVisible = false;
        other.Show();
        try
        {
            // 激活只在本进程创建的两个窗口之间切换，不读取或操作用户的其他窗口。
            await ActivateOwnWindow(other, Window);

            await Check("overlay_is_compact_transparent_without_bottom_band", async () =>
            {
                await ResetNormal(1100, 650);
                Control<FrameworkElement>("VideoHost").Visibility = Visibility.Hidden;
                object? normalEffect = Control<Border>("PlayerControls").Effect;
                Click("FullscreenButton");
                Control<FrameworkElement>("VideoHost").Visibility = Visibility.Hidden;
                await Settle();
                Popup popup = Field<Popup>("_fullscreenControls");
                Require(!Window.IsActive && !popup.IsOpen, "非激活窗口进入影院时重开了控制浮层");
                Require(Control<Border>("PlayerControls").Background is SolidColorBrush brush && brush.Color.A == 0,
                    "悬浮控制层仍有可见底板背景");
                Require(Control<Border>("PlayerControls").BorderBrush is SolidColorBrush outline && outline.Color.A == 0,
                    "悬浮控制层仍有可见外框");
                Require(Control<Border>("PlayerControls").Effect is System.Windows.Media.Effects.DropShadowEffect,
                    "透明控制层缺少用于辨认控件轮廓的阴影");

                DispatcherTimer timer = Field<DispatcherTimer>("_fullscreenTimer");
                bool timerEnabled = timer.IsEnabled;
                timer.Stop();
                RenderTargetBitmap bitmap;
                try
                {
                    // 暂开不可见浮层仅用于真实控件测量；此行为不计入生产显隐验证。
                    popup.IsOpen = true;
                    await Settle();
                    bitmap = Capture(controls, "results/overlay-compact-controls.png");
                }
                finally
                {
                    popup.IsOpen = false;
                    if (timerEnabled) timer.Start();
                }
                int stride = bitmap.PixelWidth * 4;
                byte[] pixels = new byte[stride * bitmap.PixelHeight];
                bitmap.CopyPixels(pixels, stride, 0);
                int[,] points = { { 0, 0 }, { bitmap.PixelWidth - 1, 0 }, { 0, bitmap.PixelHeight - 1 }, { bitmap.PixelWidth - 1, bitmap.PixelHeight - 1 } };
                var cornerSamples = new List<object>();
                for (int index = 0; index < points.GetLength(0); index++)
                {
                    int x = points[index, 0], y = points[index, 1];
                    byte alpha = pixels[y * stride + x * 4 + 3];
                    cornerSamples.Add(new { x, y, alpha });
                    Require(alpha == 0, "控制层空白角仍有底板像素：" + x + "," + y + " alpha=" + alpha);
                }
                int paintedPixels = 0;
                int opaquePixels = 0;
                for (int offset = 3; offset < pixels.Length; offset += 4)
                {
                    if (pixels[offset] > 0) paintedPixels++;
                    if (pixels[offset] == 255) opaquePixels++;
                }
                byte panelAlpha = pixels[5 * stride + (bitmap.PixelWidth / 2) * 4 + 3];
                Require(panelAlpha == 0, "悬浮条中部空白仍有底板像素");
                int transparentPixels = bitmap.PixelWidth * bitmap.PixelHeight - paintedPixels;
                Require(transparentPixels > bitmap.PixelWidth * bitmap.PixelHeight / 2, "控制层仍被大面积底板或阴影覆盖");
                Require(paintedPixels > 100 && opaquePixels > 10, "透明控制层未渲染清晰控件，可能是空图");
                var visibleControls = new List<object>();
                foreach (string name in new[] { "PauseButton", "TimeText", "SeekSlider" })
                {
                    FrameworkElement control = Control<FrameworkElement>(name);
                    Rect bounds = control.TransformToAncestor(controls).TransformBounds(new Rect(control.RenderSize));
                    int visiblePixels = 0;
                    int crispPixels = 0;
                    for (int y = Math.Max(0, (int)Math.Floor(bounds.Top)); y < Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(bounds.Bottom)); y++)
                    for (int x = Math.Max(0, (int)Math.Floor(bounds.Left)); x < Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(bounds.Right)); x++)
                    {
                        byte alpha = pixels[y * stride + x * 4 + 3];
                        if (alpha > 0) visiblePixels++;
                        if (alpha == 255) crispPixels++;
                    }
                    Require(control.IsVisible && visiblePixels > 5 && crispPixels > 0, "透明后控件内容消失：" + name);
                    visibleControls.Add(new { name, bounds = bounds.ToString(CultureInfo.InvariantCulture), visiblePixels, crispPixels });
                }
                Require(Control<RowDefinition>("PlaybackControlsRow").ActualHeight < 1, "悬浮条仍占底部布局行");
                Require(controls.ActualWidth <= 861 && controls.ActualHeight <= 45, "悬浮条没有保持紧凑尺寸");
                Click("FullscreenButton");
                await Settle();
                Require(ReferenceEquals(Control<Border>("PlayerControls").Effect, normalEffect), "退出影院后未恢复原有Effect");
                return new { width = bitmap.PixelWidth, height = bitmap.PixelHeight, cornerSamples, panelAlpha, transparentPixels, paintedPixels, opaquePixels, visibleControls, restoredEffect = true,
                    artifact = "results/overlay-compact-controls.png", nativeVideoPixelsIncluded = false, bottomBandAbsent = true };
            });

            await Check("inactive_activity_cannot_reopen_floating_controls", async () =>
            {
                await ResetNormal(640, 360);
                Control<FrameworkElement>("VideoHost").Visibility = Visibility.Hidden;
                await ActivateOwnWindow(other, Window);
                Click("FullscreenButton");
                Control<FrameworkElement>("VideoHost").Visibility = Visibility.Hidden;
                await Settle();
                Popup popup = Field<Popup>("_fullscreenControls");
                Require(!popup.IsOpen, "非激活窗口打开了浮层");
                // 模拟旧版本遗留的开启状态，生产定时器必须主动回收而非直接返回。
                popup.IsOpen = true;
                Invoke("SetFullscreenCursorHidden", true);
                Invoke("UpdateFullscreenControls");
                Require(!popup.IsOpen && Window.Cursor != Cursors.None, "后台更新未收起遗留浮层或恢复鼠标");
                RaiseVideoActivity();
                Invoke("RevealFullscreenControls");
                await Settle();
                Require(other.IsActive && !Window.IsActive && !popup.IsOpen, "后台活动重开浮层或抢走激活状态");
                return new { otherWindowActive = other.IsActive, mainWindowActive = Window.IsActive,
                    stalePopupClosed = true, backgroundActivityReopened = false, cursorRestored = true };
            });

            await Check("active_window_real_three_second_timer_and_activity", async () =>
            {
                await ActivateOwnWindow(Window, other);
                Popup popup = Field<Popup>("_fullscreenControls");
                Require(popup.IsOpen, "真实Activated事件未恢复控制浮层");
                Rect before = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
                Require(Field<DispatcherTimer>("_fullscreenTimer").IsEnabled, "生产控制栏计时器没有运行");
                Invoke("RevealFullscreenControls");
                var elapsed = Stopwatch.StartNew();
                // 不修改_lastControlActivity，等待真实生产DispatcherTimer跨过三秒阈值。
                await Wait(() => !popup.IsOpen, 5000);
                double idleMs = elapsed.Elapsed.TotalMilliseconds;
                Require(idleMs >= 2800 && idleMs <= 5000, "实际闲置隐藏时间不在三秒附近");
                Require(Window.Cursor == Cursors.None, "实际闲置后未隐藏本窗口鼠标");
                RequireNear(before, ScreenBounds(Control<FrameworkElement>("VideoBorder")), "控制栏隐藏改变了视频布局");
                RaiseVideoActivity();
                await Settle();
                Require(popup.IsOpen && Window.Cursor != Cursors.None, "原生视频活动未恢复浮层与鼠标");
                RequireNear(before, ScreenBounds(Control<FrameworkElement>("VideoBorder")), "恢复控制栏改变了视频布局");
                Click("PlaybackOptionsButton");
                Popup options = Field<Popup>("_playbackOptionsPopup");
                ((FrameworkElement)options.Child).IsHitTestVisible = false;
                Require(options.IsOpen, "播放设置未打开");
                await ActivateOwnWindow(other, Window);
                Require(!popup.IsOpen && Window.Cursor != Cursors.None, "真实Deactivated事件未收起控制浮层");
                // Popup.Closed由Dispatcher异步送达，等待真实归还完成，不以激活状态同步变化代替它。
                try
                {
                    await Wait(() => !options.IsOpen && Field<Popup?>("_playbackOptionsPopup") == null
                        && Control<FrameworkElement>("SettingsGroup").Parent == Control<Grid>("ControlGroups"), 3000);
                }
                catch (TimeoutException error)
                {
                    throw new InvalidOperationException("后台设置收起等待超时：" + JsonSerializer.Serialize(new
                    {
                        mainActive = Window.IsActive, otherActive = other.IsActive, optionsOpen = options.IsOpen,
                        registeredOptions = Field<Popup?>("_playbackOptionsPopup") != null,
                        settingsParent = Control<FrameworkElement>("SettingsGroup").Parent?.GetType().Name
                    }), error);
                }
                Require(!options.IsOpen && Field<Popup?>("_playbackOptionsPopup") == null,
                    "真实Deactivated事件未关闭独立设置浮层");
                Require(Control<FrameworkElement>("SettingsGroup").Parent == Control<Grid>("ControlGroups"),
                    "后台关闭设置时未归还控件父级");
                return new { actualIdleTimerTested = true, idleMs, activationAndDeactivationEvents = true,
                    activityRestoresControlsAndCursor = true, settingsClosedAndParentRestored = true,
                    layoutStable = true, controlsHitTestDisabledForInvisibleFixture = true };
            });

            await Check("deactivation_closes_actual_speed_and_quality_pages", async () =>
            {
                Window.Width = 1100;
                await Settle();
                ComboBox quality = Control<ComboBox>("QualityCombo");
                bool qualityEnabled = quality.IsEnabled;
                var qualitySource = quality.ItemsSource;
                quality.ItemsSource = new[] { new PlaybackQuality { Definition = "best" } };
                quality.IsEnabled = true;
                try
                {
                    foreach (string name in new[] { "PlaybackSpeedMenuButton", "PlaybackQualityMenuButton" })
                    {
                        await ActivateOwnWindow(Window, other);
                        Click("PlaybackOptionsButton");
                        await Settle();
                        Popup menu = Field<Popup>("_playbackOptionsPopup");
                        ClickMenu(name);
                        await Settle();
                        Require(menu.IsOpen && ReferenceEquals(menu, Field<Popup>("_playbackOptionsPopup"))
                            && MenuControl<Button>("PlaybackMenuBack").IsVisible, "实际设置选项页未打开：" + name);
                        ((FrameworkElement)menu.Child).IsHitTestVisible = false;
                        await ActivateOwnWindow(other, Window);
                        await Wait(() => !menu.IsOpen && Field<Popup?>("_playbackOptionsPopup") == null, 3000);
                        Require(!Field<Popup>("_fullscreenControls").IsOpen, "关闭设置子页时重开了后台控制层");
                        Require(!Control<ComboBox>("SpeedCombo").IsDropDownOpen && !quality.IsDropDownOpen,
                            "新设置菜单仍打开了旧下拉浮层");
                    }
                }
                finally
                {
                    quality.ItemsSource = qualitySource;
                    quality.IsEnabled = qualityEnabled;
                }
                return new { actualSpeedPageClosed = true, actualQualityPageClosed = true, originalDropdownsUnused = true };
            });

            await Check("pointer_proximity_and_keyboard_keep_controls_reachable", async () =>
            {
                await ActivateOwnWindow(Window, other);
                Popup popup = Field<Popup>("_fullscreenControls");
                FrameworkElement root = Control<FrameworkElement>("RootLayout");
                Rect initialVideo = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
                Invoke("HideFullscreenControls");
                Invoke("SetFullscreenCursorHidden", true);
                Point center = new(root.ActualWidth / 2, root.ActualHeight / 2);
                Invoke("HandlePlayerPointerActivity", center);
                await Settle();
                Require(!popup.IsOpen && Window.Cursor != Cursors.None, "中央移动弹出整栏或未恢复鼠标");
                Point bottom = new(root.ActualWidth / 2, root.ActualHeight - 10);
                RaiseVideoPointerActivity(root.PointToScreen(bottom));
                await Settle();
                Require(popup.IsOpen, "原生指针靠近底部没有唤醒控制栏");
                Invoke("HideFullscreenControls");
                RaiseVideoPointerActivity(root.PointToScreen(new Point(root.ActualWidth / 2, 10)));
                await Settle();
                Require(popup.IsOpen, "原生指针靠近顶部没有唤醒控制栏");
                Invoke("HideFullscreenControls");
                RaiseVideoActivity();
                await Settle();
                Require(popup.IsOpen, "原生键盘活动未唤醒控制栏");
                // 此处只提前过期活动时间验证交互保护；真实三秒由另一项完整等待。
                Set("_seeking", true);
                Set("_lastControlActivity", DateTime.UtcNow.AddSeconds(-4));
                Invoke("UpdateFullscreenControls");
                Require(popup.IsOpen, "拖动进度时自动隐藏控制栏");
                Set("_seeking", false);
                Invoke("HandlePlayerPointerActivity", center);
                Require(!Field<bool>("_controlsKeyboardInteraction"), "指针交互没有解除键盘保护状态");
                RaiseKey(Window, Key.Tab);
                Require(Field<bool>("_controlsKeyboardInteraction"), "从窗口首次按Tab未记录键盘交互");
                FrameworkElement focusTarget = Control<FrameworkElement>("FullscreenButton");
                Keyboard.Focus(focusTarget);
                await Settle();
                Require(focusTarget.IsKeyboardFocusWithin, "键盘焦点未进入主控制栏");
                Set("_lastControlActivity", DateTime.UtcNow.AddSeconds(-4));
                Invoke("UpdateFullscreenControls");
                Require(popup.IsOpen, "从窗口首次Tab进入主栏后自动隐藏控件");
                Keyboard.ClearFocus();
                Invoke("HandlePlayerPointerActivity", center);
                RaiseVideoActivity();
                Require(Field<bool>("_controlsKeyboardInteraction"), "原生键盘活动未记录键盘交互");
                Keyboard.Focus(focusTarget);
                await Settle();
                Require(focusTarget.IsKeyboardFocusWithin, "原生键盘活动后的焦点未进入主栏");
                Set("_lastControlActivity", DateTime.UtcNow.AddSeconds(-4));
                Invoke("UpdateFullscreenControls");
                Require(popup.IsOpen, "原生键盘首次进入主栏后自动隐藏控件");
                Keyboard.ClearFocus();
                Click("PlaybackOptionsButton");
                await Settle();
                Require(Field<Popup?>("_playbackOptionsPopup")?.IsOpen == true, "交互保护检查未打开设置");
                Set("_lastControlActivity", DateTime.UtcNow.AddSeconds(-4));
                Invoke("UpdateFullscreenControls");
                Require(popup.IsOpen, "设置打开时自动隐藏控制栏");
                Invoke("ClosePlaybackOptions");
                await Wait(() => Field<Popup?>("_playbackOptionsPopup") == null, 3000);
                Keyboard.ClearFocus();
                Invoke("HandlePlayerPointerActivity", center);
                RequireNear(initialVideo, ScreenBounds(Control<FrameworkElement>("VideoBorder")), "局部唤醒或设置改变视频尺寸");
                return new { centerMovementRevealsCursorOnly = true, bottomMovementOpensControls = true, topMovementOpensControls = true,
                    nativeKeyboardOpensControls = true, seekingProtected = true, firstWindowTabProtected = true,
                    firstNativeKeyboardFocusProtected = true, keyboardFocusProtected = true,
                    settingsProtected = true, stableVideoRect = true, actualSystemInput = false };
            });

            await Check("minimized_hidden_and_restored_windows_keep_overlay_scoped", async () =>
            {
                await ActivateOwnWindow(Window, other);
                Popup popup = Field<Popup>("_fullscreenControls");
                // 上一项可能保持窗口激活但收起浮层，显式建立本项的已打开前置状态。
                Invoke("RevealFullscreenControls");
                Require(popup.IsOpen, "最小化检查前未唤醒控制浮层");
                Window.WindowState = WindowState.Minimized;
                await Settle();
                Require(!popup.IsOpen && Window.Cursor != Cursors.None, "最小化后仍保留控制浮层或隐藏鼠标");
                RaiseVideoActivity();
                Invoke("RevealFullscreenControls");
                Require(!popup.IsOpen, "最小化窗口活动重开控制浮层");
                Window.WindowState = WindowState.Normal;
                await ActivateOwnWindow(Window);
                await WaitForRestoredOverlay(popup, "minimized_to_normal");
                Window.Hide();
                await Settle();
                RaiseVideoActivity();
                Invoke("RevealFullscreenControls");
                Require(!Window.IsVisible && !popup.IsOpen && Window.Cursor != Cursors.None, "隐藏窗口仍显示浮层或隐藏鼠标");
                Window.Show();
                await ActivateOwnWindow(Window);
                await WaitForRestoredOverlay(popup, "hidden_to_visible");
                Click("FullscreenButton");
                await Settle();
                VerifyNormalLayout();
                return new { minimizedClosesPopup = true, backgroundActivityBlocked = true,
                    hiddenClosesPopup = true, restoredActivationReopens = true, exitsRestoreLayout = true };
            });
        }
        finally
        {
            if (Field<bool>("_fullscreen")) Invoke("ToggleFullscreen");
            if (Field<bool>("_windowCinema")) Click("FullscreenButton");
            // 结束时先隐藏自有主窗口，避免关闭对照窗口时把不可见夹具留为活动窗口。
            Window.Hide();
            other.Close();
            Window.WindowState = WindowState.Normal;
            Window.Left = VisibleForeground ? 40 : -3000;
            Window.Top = 20;
            Window.Show();
            controls.IsHitTestVisible = hitTestVisible;
            Control<FrameworkElement>("VideoHost").Visibility = videoVisibility;
            await Settle();
        }
    }

    private static object ForegroundDiagnostic()
    {
        IntPtr main = new WindowInteropHelper(Window).Handle;
        IntPtr foreground = GetForegroundWindow();
        return new { main = main.ToInt64(), foreground = foreground.ToInt64(), rootOwner = GetAncestor(foreground, 3).ToInt64(),
            active = Window.IsActive, visible = Window.IsVisible, state = Window.WindowState.ToString(),
            productionForeground = Invoke("HasPlaybackForeground"), optionsRegistered = Field<Popup?>("_playbackOptionsPopup") != null,
            optionsOpen = Field<Popup?>("_playbackOptionsPopup")?.IsOpen,
            cinema = Field<bool>("_windowCinema"), fullscreen = Field<bool>("_fullscreen"), controlsOpen = Field<Popup?>("_fullscreenControls")?.IsOpen };
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private static async Task WaitForRestoredOverlay(Popup popup, string phase)
    {
        try { await Wait(() => Window.IsActive && popup.IsOpen, 3000); }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException("浮层恢复超时：" + JsonSerializer.Serialize(new
            {
                phase, active = Window.IsActive, visible = Window.IsVisible, state = Window.WindowState.ToString(),
                popupOpen = popup.IsOpen, currentPopupMatches = ReferenceEquals(popup, Field<Popup?>("_fullscreenControls")),
                cinema = Field<bool>("_windowCinema"), fullscreen = Field<bool>("_fullscreen"),
                controlsVisible = Control<FrameworkElement>("PlayerControls").IsVisible,
                timerEnabled = Field<DispatcherTimer>("_fullscreenTimer").IsEnabled
            }), error);
        }
    }

    private static void RaiseVideoActivity()
    {
        NativeVideoHost host = Control<NativeVideoHost>("VideoHost");
        var activity = (EventHandler)typeof(NativeVideoHost).GetField("UserActivity", Private)!.GetValue(host)!;
        // 调用真实生产订阅链，不向操作系统注入键鼠，也不修改主窗口的IsActive。
        activity(host, EventArgs.Empty);
    }

    private static void RaiseVideoPointerActivity(Point screenPosition)
    {
        NativeVideoHost host = Control<NativeVideoHost>("VideoHost");
        var activity = (EventHandler<Point>)typeof(NativeVideoHost).GetField("PointerActivity", Private)!.GetValue(host)!;
        // 坐标经过真实生产订阅链换算，覆盖原生窗口与 WPF 的 DPI 坐标边界。
        activity(host, screenPosition);
    }

    private static async Task ResetNormal(double width, double height)
    {
        if (Field<bool>("_fullscreen")) Invoke("ToggleFullscreen");
        if (Field<bool>("_windowCinema")) Click("FullscreenButton");
        Window.WindowState = WindowState.Normal;
        Window.Width = width;
        Window.Height = height;
        Window.Left = VisibleForeground ? 40 : -3000;
        Window.Top = 20;
        PositioningFixture = false;
        Control<FrameworkElement>("VideoHost").Visibility = Visibility.Visible;
        Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
        await Settle();
    }

    private static void VerifyNormalLayout()
    {
        Require(!Field<bool>("_fullscreen") && !Field<bool>("_windowCinema"), "没有恢复普通播放状态");
        Require(Control<FrameworkElement>("PlayerControls").Parent == Control<Grid>("CinemaLayout"), "普通控制栏未归还布局");
        Require(Control<FrameworkElement>("PlayerHeading").Visibility == Visibility.Visible, "退出后标题未恢复");
        Require(Window.WindowStyle == WindowStyle.SingleBorderWindow && Window.ResizeMode == ResizeMode.CanResize, "退出后系统标题栏未恢复");
        Require(Window.Cursor != Cursors.None, "退出后鼠标仍隐藏");
    }

    private static void VerifyImmersiveLayout()
    {
        Require(Control<FrameworkElement>("PlayerHeading").Visibility == Visibility.Collapsed, "影院标题仍占空间");
        Require(Control<ColumnDefinition>("EpisodeColumn").ActualWidth < 1 && !Control<FrameworkElement>("EpisodeSidebar").IsVisible, "影院侧栏仍占空间");
        Rect viewport = ScreenBounds(Control<FrameworkElement>("ShellViewport"));
        Rect video = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
        double reservedHeight = Control<RowDefinition>("PlaybackControlsRow").ActualHeight;
        Require(reservedHeight < 1, "沉浸播放仍预留底部空带");
        RequireNear(viewport, video, "沉浸视频没有填满当前客户区");
        ScrollViewer shell = Control<ScrollViewer>("ShellViewport");
        Require(shell.ScrollableWidth < 1 && shell.ScrollableHeight < 1, "影院保留整窗滚动");
        Require(Field<Popup?>("_fullscreenControls")?.Child == Control<FrameworkElement>("PlayerControls"), "影院控制未移到浮层");
    }

    private static object VerifyOverlay()
    {
        Popup popup = Field<Popup>("_fullscreenControls");
        Require(Control<FrameworkElement>("PlayerControls").Opacity == 0, "定位夹具未隐藏独立控制层");
        DispatcherTimer timer = Field<DispatcherTimer>("_fullscreenTimer");
        bool timerEnabled = timer.IsEnabled;
        bool opened = popup.IsOpen;
        timer.Stop();
        try
        {
            // 非激活状态正确保持关闭；仅为坐标检查暂开不可见Popup，不冒充生产显示行为。
            popup.IsOpen = true;
            Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Control<FrameworkElement>("PlayerControls").UpdateLayout();
            Rect root = ScreenBounds(Control<FrameworkElement>("RootLayout"));
            Rect controls = ScreenBounds(Control<FrameworkElement>("PlayerControls"));
            Rect video = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
            double dpiY = VisualTreeHelper.GetDpi(Control<FrameworkElement>("RootLayout")).DpiScaleY;
            Require(Math.Abs((root.Left + root.Right) / 2 - (controls.Left + controls.Right) / 2) <= 3
                && Math.Abs(controls.Top - root.Top - 12 * dpiY) <= 3,
                "顶部浮条没有随窗口居中定位：" + root + " / " + controls);
            RequireContains(root, controls, "顶部浮条越过窗口客户区");
            Rect captionRegion = new(video.Left, video.Top + video.Height * 0.7, video.Width, video.Height * 0.3);
            Require(!controls.IntersectsWith(captionRegion), "主控制浮条进入底部字幕区域：" + video + " / " + controls);
            return new { root = root.ToString(CultureInfo.InvariantCulture), controls = controls.ToString(CultureInfo.InvariantCulture), video = video.ToString(CultureInfo.InvariantCulture), aligned = true,
                centeredAtTop = true, bottomCaptionRegionUncovered = true,
                explicitlyOpenedInvisiblePositioningFixture = true };
        }
        finally
        {
            popup.IsOpen = opened;
            if (timerEnabled) timer.Start();
        }
    }

    private static object VerifyOverlayState()
    {
        Popup popup = Field<Popup>("_fullscreenControls");
        Require(popup.Child == Control<FrameworkElement>("PlayerControls"), "影院浮层未承载播放控制");
        if (!Window.IsActive || !Window.IsVisible || Window.WindowState == WindowState.Minimized)
            Require(!popup.IsOpen, "后台或不可见夹具仍打开生产浮层");
        double expectedWidth = Math.Min(860, Math.Max(1, Control<FrameworkElement>("RootLayout").ActualWidth - 32));
        Require(Math.Abs(popup.Width - expectedWidth) <= 1, "紧凑浮条宽度与当前客户区不符");
        return new { visible = popup.IsOpen, active = Window.IsActive, widthFollowsWindow = true,
            coordinateCheck = "工作区内定位专项显式暂开不可见Popup，后台生产浮层保持关闭" };
    }

    private static async Task<object> VerifyNativeVideo(int expectedWidth = 360, int expectedHeight = 640)
    {
        await Settle();
        NativeVideoHost host = Control<NativeVideoHost>("VideoHost");
        Rect native = NativeClientBounds(host.VideoHandle);
        Rect video = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
        Rect shell = ScreenBounds(Control<FrameworkElement>("ShellViewport"));
        Require(native.Width > 1 && native.Height > 1, "原生视频客户区为空");
        RequireContains(video, native, "原生画面越过播放区");
        RequireContains(shell, native, "原生画面越过窗口客户区");
        double reservedHeight = Control<RowDefinition>("PlaybackControlsRow").ActualHeight;
        Point controlOrigin = Control<Grid>("CinemaLayout").PointToScreen(new Point(0,
            Control<Grid>("CinemaLayout").ActualHeight - reservedHeight));
        Require(native.Bottom <= controlOrigin.Y + 2, "真实原生视频越过布局底边");
        bool immersive = Field<bool>("_fullscreen") || Field<bool>("_windowCinema");
        if (immersive)
            Require(Math.Abs(native.Bottom - video.Bottom) <= 2 && reservedHeight < 1, "真实原生视频底部仍有额外预留空带");
        await Wait(() => Math.Abs(ReadNumber("osd-dimensions/w") - native.Width) <= 2 && Math.Abs(ReadNumber("osd-dimensions/h") - native.Height) <= 2, 3000);
        double w = ReadNumber("osd-dimensions/w"), h = ReadNumber("osd-dimensions/h");
        double ml = ReadNumber("osd-dimensions/ml"), mr = ReadNumber("osd-dimensions/mr"), mt = ReadNumber("osd-dimensions/mt"), mb = ReadNumber("osd-dimensions/mb");
        double dw = ReadNumber("dwidth"), dh = ReadNumber("dheight");
        Require(ReadString("keepaspect") == "yes" && ReadNumber("panscan") == 0, "视频不再保持原始比例");
        Require(Math.Abs((w - ml - mr) - (h - mt - mb) * dw / dh) <= 2, "实际显示画面比例与源视频不符");
        Require(Player.GetStatus().FrameReady && dw == expectedWidth && dh == expectedHeight, "真实媒体不再解码就绪");
        return new { client = native.ToString(CultureInfo.InvariantCulture), osd = new[] { w, h }, blackBars = new[] { ml, mr, mt, mb }, decodedDisplay = new[] { dw, dh },
            layoutBottom = controlOrigin.Y, reservedControlHeight = reservedHeight, sourceAspectPreserved = true,
            immersiveWithoutBottomBand = immersive };
    }

    private sealed record WindowSnapshot(double Left, double Top, double Width, double Height, double ActualWidth, double ActualHeight, string State, string Style, string ResizeMode, string Client, string Outer);
    private static WindowSnapshot Snapshot()
    {
        IntPtr handle = new WindowInteropHelper(Window).Handle;
        Require(GetWindowRect(handle, out NativeRect outer), "读取本窗口外部尺寸失败");
        return new(Window.Left, Window.Top, Window.Width, Window.Height, Window.ActualWidth, Window.ActualHeight,
            Window.WindowState.ToString(), Window.WindowStyle.ToString(), Window.ResizeMode.ToString(), NativeClientBounds(handle).ToString(CultureInfo.InvariantCulture),
            new Rect(outer.Left, outer.Top, outer.Right - outer.Left, outer.Bottom - outer.Top).ToString(CultureInfo.InvariantCulture));
    }
    private static void RequireSame(WindowSnapshot expected, WindowSnapshot actual, string message) => Require(expected == actual, message + " / " + JsonSerializer.Serialize(new { expected, actual }));
    private static void RequireNear(Rect expected, Rect actual, string message) => Require(Math.Abs(expected.Left - actual.Left) <= 2 && Math.Abs(expected.Top - actual.Top) <= 2 && Math.Abs(expected.Width - actual.Width) <= 2 && Math.Abs(expected.Height - actual.Height) <= 2, message + " / " + expected + " / " + actual);
    private static void RequireContains(Rect outer, Rect inner, string message) => Require(inner.Left >= outer.Left - 2 && inner.Top >= outer.Top - 2 && inner.Right <= outer.Right + 2 && inner.Bottom <= outer.Bottom + 2, message);
    private static Rect ScreenBounds(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    private static Rect NativeClientBounds(IntPtr handle)
    {
        Require(GetClientRect(handle, out NativeRect client), "读取本窗口客户区失败");
        var origin = new NativePoint();
        Require(ClientToScreen(handle, ref origin), "转换本窗口坐标失败");
        return new Rect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }
    private static void Click(string name)
    {
        Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (name == "PlaybackOptionsButton" && Field<Popup?>("_playbackOptionsPopup")?.Child is FrameworkElement panel) panel.Opacity = 0;
        if (!PositioningFixture && !Field<bool>("_fullscreen")) Control<FrameworkElement>("VideoHost").Visibility = Visibility.Visible;
    }
    private static void ClickMenu(string name)
    {
        MenuControl<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (Field<Popup?>("_playbackOptionsPopup")?.Child is FrameworkElement panel) panel.Opacity = 0;
    }
    private static T MenuControl<T>(string name) where T : FrameworkElement
    {
        DependencyObject root = Field<Popup>("_playbackOptionsPopup").Child;
        return FindNamedDescendant<T>(root, name) ?? throw new InvalidOperationException("菜单控件未找到：" + name);
    }
    private static T? FindNamedDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && (element.Name == name || System.Windows.Automation.AutomationProperties.GetAutomationId(element) == name)) return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            T? found = FindNamedDescendant<T>(VisualTreeHelper.GetChild(root, index), name);
            if (found != null) return found;
        }
        return null;
    }
    private static void RaiseKey(FrameworkElement element, Key key)
    {
        if (key == Key.F11 && !Field<bool>("_fullscreen")) Control<FrameworkElement>("VideoHost").Visibility = Visibility.Hidden;
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        if (!PositioningFixture && !Field<bool>("_fullscreen")) Control<FrameworkElement>("VideoHost").Visibility = Visibility.Visible;
    }
    private static double ReadNumber(string name) => double.TryParse(ReadString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : double.NaN;
    private static string ReadString(string name)
    {
        IntPtr module = GetModuleHandle("libmpv-2.dll");
        Require(module != IntPtr.Zero, "播放器模块未加载");
        var get = Marshal.GetDelegateForFunctionPointer<GetPropertyString>(NativeLibrary.GetExport(module, "mpv_get_property_string"));
        var free = Marshal.GetDelegateForFunctionPointer<Free>(NativeLibrary.GetExport(module, "mpv_free"));
        IntPtr handle = (IntPtr)typeof(MpvPlayer).GetField("_handle", Private)!.GetValue(Player)!;
        IntPtr value = get(handle, name);
        try { return Marshal.PtrToStringUTF8(value) ?? ""; }
        finally { if (value != IntPtr.Zero) free(value); }
    }
    private static async Task Settle()
    {
        await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Window.UpdateLayout();
        await Task.Delay(150);
    }
    private static async Task Wait(Func<bool> condition, int timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > timeout) throw new TimeoutException("本地状态等待超时");
            await Task.Delay(20);
        }
    }
    private static async Task ActivateOwnWindow(System.Windows.Window target, System.Windows.Window? inactive = null)
    {
        bool requestAccepted = target.Activate();
        IntPtr handle = new WindowInteropHelper(target).Handle;
        bool foregroundAccepted = SetForegroundWindow(handle);
        try
        {
            // WPF 激活不代表系统前台已交接，必须同时匹配真实根 owner。
            await Wait(() => target.IsActive && GetAncestor(GetForegroundWindow(), 3) == handle
                && (inactive == null || !inactive.IsActive), 3000);
        }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException("自有窗口激活状态等待超时：" + JsonSerializer.Serialize(new
            {
                requestAccepted, foregroundAccepted, expected = handle.ToInt64(),
                foreground = GetForegroundWindow().ToInt64(), rootOwner = GetAncestor(GetForegroundWindow(), 3).ToInt64(),
                targetActive = target.IsActive, targetVisible = target.IsVisible,
                targetState = target.WindowState.ToString(), inactiveActive = inactive?.IsActive,
                mainActive = Window.IsActive, mainVisible = Window.IsVisible,
                controlsOpen = Field<Popup?>("_fullscreenControls")?.IsOpen,
                optionsOpen = Field<Popup?>("_playbackOptionsPopup")?.IsOpen
            }), error);
        }
    }
    private static RenderTargetBitmap Capture(FrameworkElement element, string path)
    {
        double opacity = element.Opacity;
        try
        {
            // 同步临时恢复透明度只用于离屏绘制，返回Dispatcher前恢复夹具状态。
            // 保持Popup开启可保留真实布局与绑定，避免关闭后导出空白PNG。
            element.Opacity = 1;
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream output = File.Create(path);
            encoder.Save(output);
            return bitmap;
        }
        finally
        {
            element.Opacity = opacity;
        }
    }
    private static void CaptureMenu(string path)
    {
        RenderTargetBitmap bitmap = Capture((FrameworkElement)Field<Popup>("_playbackOptionsPopup").Child, path);
        byte[] pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        int painted = 0;
        for (int offset = 3; offset < pixels.Length; offset += 4)
            if (pixels[offset] > 0) painted++;
        Require(painted > 100, "菜单PNG未实际渲染：" + path);
    }
    private static async Task Check(string name, Func<Task<object>> test)
    {
        try { Record(name, true, await test()); }
        catch (Exception error) { Record(name, false, new { type = error.GetType().Name, error = error.Message, foreground = ForegroundDiagnostic() }); }
    }
    private static void Record(string name, bool passed, object detail) { if (!passed) Failures++; Tests.Add(new { name, passed, detail }); Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Control<T>(string name) where T : class => Window.FindName(name) as T ?? throw new InvalidOperationException("控件未找到：" + name);
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, Private)!.GetValue(Window) ?? default(T)!);
    private static void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(Window, value);
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(Window, args);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetPropertyString(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr value);
}
