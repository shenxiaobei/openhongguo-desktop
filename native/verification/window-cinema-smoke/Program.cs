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

    [STAThread]
    private static int Main()
    {
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
                File.WriteAllText("results/window-cinema-smoke.json", JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, failures = Failures, tests = Tests, dispatcherErrors = DispatcherErrors,
                    scope = "真实媒体在非激活自有屏幕外窗口；浮层定位专项使用Opacity=0的工作区内自有窗口，同时隐藏原生视频与独立Popup控件。未运行实际3秒活动窗口闲置计时"
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

    private static async Task RunAsync()
    {
        Window = new MainWindow
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -3000, Top = 20, Width = 640, Height = 360, Opacity = 0
        };
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window, typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        InputMethod.SetIsInputMethodEnabled(Window, false);
        InputMethod.SetIsInputMethodSuspended(Window, true);
        Window.Closed += (_, _) => Closed = true;
        System.Windows.Application.Current.MainWindow = Window;
        Control<TextBlock>("PlayingTitle").Text = "本地竖屏样片 · 窗口内全屏验证";
        ObservableCollection<EpisodeItem> episodes = Field<ObservableCollection<EpisodeItem>>("_episodes");
        for (int number = 1; number <= 36; number++) episodes.Add(new EpisodeItem { Id = number.ToString(), Number = number, Playable = true });
        Window.Show();
        Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
        await Settle();
        Require(Field<object?>("_backend") == null, "夹具意外启动了后端");
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
        // Popup 不继承主窗口透明度，定位夹具始终单独隐藏其中的控制层。
        Control<FrameworkElement>("PlayerControls").Opacity = 0;
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
                Require(Control<FrameworkElement>("VideoBorder").ActualHeight > normalVideoHeight, "视频没有使用释放的标题与底栏空间");
                object native = await VerifyNativeVideo();
                object overlay = VerifyOverlayState();
                Capture(Control<FrameworkElement>("RootLayout"), $"results/window-cinema-{width}x{height}.png");
                Capture(Control<FrameworkElement>("PlayerControls"), $"results/window-cinema-controls-{width}x{height}.png");
                Click("FullscreenButton");
                await Settle();
                Require(!Field<bool>("_windowCinema") && !Field<bool>("_fullscreen"), "再次点击没有退出窗口内模式");
                RequireSame(before, Snapshot(), "退出窗口内模式改变了外部窗口");
                VerifyNormalLayout();
                return new { before, after = Snapshot(), native, overlay, normalVideoHeight };
            });
        }

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
            Window.Left = -3000;
            Window.Top = 20;
            Control<FrameworkElement>("VideoHost").Visibility = Visibility.Visible;
            await Settle();
            object restoredNative = await VerifyNativeVideo();
            return new { initial, moved, resizedOverlay, maximizedOverlay, nativeCaptionPreserved = true, invisiblePositioningFixture = true, restoredNative };
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

        await Check("transparent_overlay_visibility_does_not_resize_video", async () =>
        {
            await ResetNormal(640, 360);
            Click("FullscreenButton");
            await Settle();
            Popup popup = Field<Popup>("_fullscreenControls");
            var brush = Control<Border>("PlayerControls").Background as LinearGradientBrush;
            Require(brush != null && brush.GradientStops.Any(stop => stop.Color.A == 0) && brush.GradientStops.All(stop => stop.Color.A < 255), "窗口内控制栏底板不是透明渐变");
            Rect before = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
            // 验证显隐布局与局部鼠标恢复；不把非激活夹具称作实际三秒闲置测试。
            popup.IsOpen = false;
            Invoke("SetFullscreenCursorHidden", true);
            await Settle();
            Require(Window.Cursor == Cursors.None, "局部隐藏状态未应用");
            RequireNear(before, ScreenBounds(Control<FrameworkElement>("VideoBorder")), "隐藏控制栏挤压了视频");
            Invoke("RevealFullscreenControls");
            await Settle();
            Require(popup.IsOpen && Window.Cursor != Cursors.None, "恢复控制栏没有恢复鼠标");
            VerifyOverlayState();
            RaiseKey(Window, Key.Escape);
            await Settle();
            Require(Window.Cursor != Cursors.None && Field<Popup?>("_fullscreenControls") == null, "退出后留下鼠标或控制浮层状态");
            return new { gradientAlpha = brush!.GradientStops.Select(stop => stop.Color.A).ToArray(), layoutStable = true, cursorRestored = true, actualIdleTimerTested = false };
        });

        await Check("options_shortcuts_and_navigation_restore_parents", async () =>
        {
            await ResetNormal(640, 360);
            Click("FullscreenButton");
            await Settle();
            Click("PlaybackOptionsButton");
            await Settle();
            Popup options = Field<Popup>("_playbackOptionsPopup");
            Require(options.IsOpen, "影院播放设置未打开");
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
            return new { f11FromOptions = true, previousCinemaRestored = true, navigationResetModes = true, parentRestored = true, backendStarted = Field<object?>("_backend") != null };
        });

        await Check("single_close_from_window_cinema_releases_player_and_popups", async () =>
        {
            await ResetNormal(640, 360);
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

    private static async Task ResetNormal(double width, double height)
    {
        if (Field<bool>("_fullscreen")) Invoke("ToggleFullscreen");
        if (Field<bool>("_windowCinema")) Click("FullscreenButton");
        Window.WindowState = WindowState.Normal;
        Window.Width = width;
        Window.Height = height;
        Window.Left = -3000;
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
        RequireNear(viewport, video, "视频未填满当前窗口客户区");
        ScrollViewer shell = Control<ScrollViewer>("ShellViewport");
        Require(shell.ScrollableWidth < 1 && shell.ScrollableHeight < 1, "影院保留整窗滚动");
        Require(Field<Popup?>("_fullscreenControls")?.Child == Control<FrameworkElement>("PlayerControls"), "影院控制未移到浮层");
    }

    private static object VerifyOverlay()
    {
        Popup popup = Field<Popup>("_fullscreenControls");
        Require(popup.IsOpen, "影院浮层没有显示");
        Rect root = ScreenBounds(Control<FrameworkElement>("RootLayout"));
        Rect controls = ScreenBounds(Control<FrameworkElement>("PlayerControls"));
        Require(Math.Abs(root.Left - controls.Left) <= 3 && Math.Abs(root.Right - controls.Right) <= 3 && Math.Abs(root.Bottom - controls.Bottom) <= 3,
            "控制浮层没有跟随窗口底部：" + root + " / " + controls);
        return new { root = root.ToString(CultureInfo.InvariantCulture), controls = controls.ToString(CultureInfo.InvariantCulture), aligned = true };
    }

    private static object VerifyOverlayState()
    {
        Popup popup = Field<Popup>("_fullscreenControls");
        Require(popup.IsOpen && popup.Child == Control<FrameworkElement>("PlayerControls"), "影院浮层未承载播放控制");
        Require(Math.Abs(popup.Width - Control<FrameworkElement>("RootLayout").ActualWidth) <= 1, "影院浮层宽度与当前客户区不符");
        return new { visible = true, widthFollowsWindow = true, coordinateCheck = "独立工作区内透明定位专项严格检查，离屏Popup由系统钳到显示器" };
    }

    private static async Task<object> VerifyNativeVideo()
    {
        await Settle();
        NativeVideoHost host = Control<NativeVideoHost>("VideoHost");
        Rect native = NativeClientBounds(host.VideoHandle);
        Rect video = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
        Rect shell = ScreenBounds(Control<FrameworkElement>("ShellViewport"));
        Require(native.Width > 1 && native.Height > 1, "原生视频客户区为空");
        RequireContains(video, native, "原生画面越过播放区");
        RequireContains(shell, native, "原生画面越过窗口客户区");
        await Wait(() => Math.Abs(ReadNumber("osd-dimensions/w") - native.Width) <= 2 && Math.Abs(ReadNumber("osd-dimensions/h") - native.Height) <= 2, 3000);
        double w = ReadNumber("osd-dimensions/w"), h = ReadNumber("osd-dimensions/h");
        double ml = ReadNumber("osd-dimensions/ml"), mr = ReadNumber("osd-dimensions/mr"), mt = ReadNumber("osd-dimensions/mt"), mb = ReadNumber("osd-dimensions/mb");
        double dw = ReadNumber("dwidth"), dh = ReadNumber("dheight");
        Require(ReadString("keepaspect") == "yes" && ReadNumber("panscan") == 0, "视频不再保持原始比例");
        Require(Math.Abs((w - ml - mr) - (h - mt - mb) * dw / dh) <= 2, "实际显示画面比例与源视频不符");
        Require(Player.GetStatus().FrameReady && dw == 360 && dh == 640, "真实媒体不再解码就绪");
        return new { client = native.ToString(CultureInfo.InvariantCulture), osd = new[] { w, h }, blackBars = new[] { ml, mr, mt, mb }, decodedDisplay = new[] { dw, dh }, sourceAspectPreserved = true };
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
    private static void Capture(FrameworkElement element, string path)
    {
        Popup? overlay = Field<Popup?>("_fullscreenControls");
        bool opened = overlay?.IsOpen == true;
        if (opened) overlay!.IsOpen = false;
        double opacity = element.Opacity;
        element.Opacity = 1;
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
        element.Opacity = opacity;
        if (opened) overlay!.IsOpen = true;
    }
    private static async Task Check(string name, Func<Task<object>> test)
    {
        try { Record(name, true, await test()); }
        catch (Exception error) { Record(name, false, new { type = error.GetType().Name, error = error.Message }); }
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
