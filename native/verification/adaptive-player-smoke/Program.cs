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
    private static bool PopupOnly;

    [STAThread]
    private static int Main(string[] args)
    {
        PopupOnly = args.Contains("--popup-only");
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);
        app.DispatcherUnhandledException += (_, args) =>
        {
            // 夹具只包含本地路径，仍只保留类型和调用栈，避免引入异常正文。
            DispatcherErrors.Add(new { type = args.Exception.GetType().Name, stack = args.Exception.StackTrace });
            args.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { error = error.Message, type = error.GetType().Name }); }
            finally
            {
                Cleanup();
                if (DispatcherErrors.Count > 0) Record("dispatcher_errors", false, DispatcherErrors);
                string report = PopupOnly ? "results/popup-shortcuts.json" : "results/adaptive-player-smoke.json";
                File.WriteAllText(report, JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures = Failures, tests = Tests, dispatcherErrors = DispatcherErrors }, new JsonSerializerOptions { WriteIndented = true }));
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
            Left = -3000, Top = 20, Width = 640, Height = 420
        };
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window, typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        InputMethod.SetIsInputMethodEnabled(Window, false);
        InputMethod.SetIsInputMethodSuspended(Window, true);
        System.Windows.Application.Current.MainWindow = Window;
        Control<TextBlock>("PlayingTitle").Text = "和离后我带女儿横扫魔域 · 本地适配样片";
        Control<TextBlock>("PlayingSubtitle").Text = "第 1 集 · 自适应画面 · 1 倍速 · 1080p";
        ObservableCollection<EpisodeItem> episodes = Field<ObservableCollection<EpisodeItem>>("_episodes");
        for (int index = 1; index <= 36; index++) episodes.Add(new EpisodeItem { Id = index.ToString(), Number = index, Playable = true });
        Window.Show();
        Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
        await Settle();
        Require(Field<object?>("_backend") == null, "验收意外启动了联网后端");
        Invoke("EnsurePlayer");
        Player = Field<MpvPlayer>("_player");
        string sample = Path.GetFullPath(Path.Combine("..", "mpv-smoke", "sample.mp4"));
        Require(File.Exists(sample), "缺少既有本地视频样片");
        Player.Load(sample, null, null, paused: true);
        Control<NativeVideoHost>("VideoHost").Visibility = Visibility.Visible;
        Control<FrameworkElement>("VideoPlaceholder").Visibility = Visibility.Collapsed;
        Control<Slider>("SeekSlider").IsEnabled = true;
        Control<Slider>("SeekSlider").Maximum = 12;
        Control<Slider>("SeekSlider").Value = 3;
        Control<Button>("PauseButton").IsEnabled = true;
        Control<Button>("NextButton").IsEnabled = true;
        Set("_updatingQualities", true);
        ObservableCollection<PlaybackQuality> qualities = Field<ObservableCollection<PlaybackQuality>>("_qualities");
        qualities.Add(new PlaybackQuality { Definition = "1080p", Width = 1920, Height = 1080 });
        Control<ComboBox>("QualityCombo").SelectedIndex = 0;
        Control<ComboBox>("QualityCombo").IsEnabled = true;
        Set("_updatingQualities", false);
        await Wait(() => Player.GetStatus().FrameReady, 10_000);

        (int, int)[] sizes = PopupOnly ? [] : [(320, 240), (480, 240), (620, 250), (640, 260), (784, 400), (950, 640), (1380, 900)];
        foreach ((int width, int height) in sizes)
        {
            await Check($"direct_player_fit_{width}x{height}", async () =>
            {
                Window.Width = width;
                Window.Height = height;
                await Settle();
                ScrollViewer shell = Control<ScrollViewer>("ShellViewport");
                Capture(shell, $"results/player-{width}x{height}.png");
                Require(Window.MinWidth == 0 && Window.MinHeight == 0, "应用重新设置了窗口最小尺寸");
                double expectedWidth = Math.Min(width, SystemParameters.MaximumWindowTrackWidth);
                double expectedHeight = Math.Min(height, SystemParameters.MaximumWindowTrackHeight);
                Require(Math.Abs(Window.ActualWidth - expectedWidth) < 1 && Math.Abs(Window.ActualHeight - expectedHeight) < 1, "窗口尺寸被应用钳制");
                Require(shell.ScrollableWidth < 1 && shell.ScrollableHeight < 1, "播放器仍依赖整窗滚动：" + shell.ScrollableWidth + "," + shell.ScrollableHeight);
                var bounds = new Dictionary<string, string>();
                foreach (string name in new[] { "VideoBorder", "VideoHost", "PlayerControls", "SeekSlider", "PauseButton", "NextButton", "FullscreenButton" })
                {
                    FrameworkElement element = Control<FrameworkElement>(name);
                    Rect rectangle = Inside(element, shell, name);
                    bounds[name] = rectangle.ToString(CultureInfo.InvariantCulture);
                }
                Rect pause = RelativeBounds(Control<FrameworkElement>("PauseButton"), shell);
                Rect next = RelativeBounds(Control<FrameworkElement>("NextButton"), shell);
                Rect fullscreen = RelativeBounds(Control<FrameworkElement>("FullscreenButton"), shell);
                Require(!pause.IntersectsWith(next) && !next.IntersectsWith(fullscreen), "主要按钮互相覆盖");
                Require(Control<FrameworkElement>("VideoBorder").ActualHeight >= 20, "播放器布局没有给视频保留可见高度");
                MpvStatus status = Player.GetStatus();
                Require(status.FrameReady && status.VideoWidth == 360 && status.VideoHeight == 640, "缩放后真实竖屏媒体未保持解码");
                object native = await VerifyNativeVideo();
                return new
                {
                    requested = new[] { width, height }, actual = new[] { Window.ActualWidth, Window.ActualHeight },
                    viewport = new[] { shell.ViewportWidth, shell.ViewportHeight },
                    scrolling = new[] { shell.ScrollableWidth, shell.ScrollableHeight }, bounds, native
                };
            });
        }

        await Check("compact_settings_and_episode_popup", async () =>
        {
            Window.Width = 320;
            Window.Height = 240;
            await Settle();
            ScrollViewer shell = Control<ScrollViewer>("ShellViewport");
            Button options = Control<Button>("PlaybackOptionsButton");
            Inside(options, shell, "播放器设置入口");
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Popup? popup = Field<Popup?>("_playbackOptionsPopup");
            Require(popup?.IsOpen == true && popup.Child is FrameworkElement, "小窗播放器设置未打开");
            FrameworkElement panel = (FrameworkElement)popup!.Child;
            Require(panel.ActualWidth <= shell.ActualWidth + 1 && panel.ActualHeight <= shell.ActualHeight + 1, "小窗设置浮层超出可用视口尺寸");
            ScrollViewer optionsScroll = (ScrollViewer)((Border)panel).Child;
            var settingOffsets = new Dictionary<string, double>();
            foreach (string name in new[] { "SpeedCombo", "QualityCombo", "VolumeGroup", "AutoNextCheck" })
            {
                FrameworkElement setting = Control<FrameworkElement>(name);
                setting.BringIntoView();
                await Settle();
                Inside(setting, optionsScroll, name);
                settingOffsets[name] = optionsScroll.VerticalOffset;
            }
            optionsScroll.ScrollToTop();
            await Settle();
            Capture(panel, "results/compact-settings.png");
            optionsScroll.ScrollToBottom();
            await Settle();
            Capture(panel, "results/compact-settings-bottom.png");
            double popupWidth = panel.ActualWidth, popupHeight = panel.ActualHeight;
            popup.IsOpen = false;
            await Settle();
            Button picker = Control<Button>("EpisodePickerButton");
            Inside(picker, shell, "选集入口");
            picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Popup? episodesPopup = Field<Popup?>("_episodePickerPopup");
            Require(episodesPopup?.IsOpen == true && episodesPopup.Width > 0 && episodesPopup.Height > 0, "小窗选集未打开");
            Require(Control<ItemsControl>("EpisodeItems").Items.Count == 36, "选集内容在布局切换中丢失");
            episodesPopup!.IsOpen = false;
            return new { settingsSize = new[] { popupWidth, popupHeight }, settings = 4, settingOffsets, episodeChoices = 36 };
        });

        await Check("popup_escape_f11_and_recommendation_restore_settings_parent", async () =>
        {
            Button options = Control<Button>("PlaybackOptionsButton");
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Popup popup = Field<Popup>("_playbackOptionsPopup");
            FrameworkElement panel = (FrameworkElement)popup.Child;
            RaiseKey(panel, Key.Escape);
            await Settle();
            Require(Field<Popup?>("_playbackOptionsPopup") == null && !Field<bool>("_fullscreen"), "Esc没有只关闭普通窗口设置弹层");
            Require(Control<FrameworkElement>("SettingsGroup").Parent == Control<Grid>("ControlGroups"), "Esc关闭后设置未归还父级");
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            popup = Field<Popup>("_playbackOptionsPopup");
            RaiseKey((FrameworkElement)popup.Child, Key.F11);
            await Settle();
            Require(Field<bool>("_fullscreen") && Field<Popup?>("_playbackOptionsPopup") == null, "设置弹层F11没有进入全屏并关闭菜单");
            Invoke("ToggleFullscreen");
            await Settle();
            Require(!Field<bool>("_fullscreen") && Control<FrameworkElement>("SettingsGroup").Parent == Control<Grid>("ControlGroups"), "全屏来回后设置父级未恢复");
            options.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            popup = Field<Popup>("_playbackOptionsPopup");
            var scroll = (ScrollViewer)((Border)popup.Child).Child;
            var content = (StackPanel)scroll.Content;
            Button recommendation = content.Children.OfType<Button>().Single();
            recommendation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Require(Field<Popup?>("_playbackOptionsPopup") == null && Control<FrameworkElement>("SettingsGroup").Parent == Control<Grid>("ControlGroups"), "换个故事没有先关闭弹层并归还设置父级");
            Require(Field<object?>("_backend") == null, "菜单验证意外启动后端");
            return new { escapeClosedMenu = true, f11EnteredFullscreen = true, returnedToCompactWindow = true, recommendationClosedMenu = true, settingsParentRestored = true, backendStarted = false };
        });

        await Check("wide_short_player_uses_episode_popup", async () =>
        {
            Window.Width = 1380;
            Window.Height = 300;
            await Settle();
            ScrollViewer shell = Control<ScrollViewer>("ShellViewport");
            Require(shell.ScrollableWidth < 1 && shell.ScrollableHeight < 1, "宽矮窗口依赖整窗滚动");
            Require(Control<ColumnDefinition>("EpisodeColumn").ActualWidth == 0 && !Control<FrameworkElement>("EpisodeSidebar").IsVisible, "宽矮窗未收起选集侧栏");
            var bounds = new Dictionary<string, string>();
            foreach (string name in new[] { "VideoBorder", "SeekSlider", "PauseButton", "NextButton", "FullscreenButton", "EpisodePickerButton" })
                bounds[name] = Inside(Control<FrameworkElement>(name), shell, name).ToString(CultureInfo.InvariantCulture);
            Control<Button>("EpisodePickerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Popup? popup = Field<Popup?>("_episodePickerPopup");
            Require(popup?.IsOpen == true && popup.Width <= shell.ActualWidth && popup.Height <= shell.ActualHeight, "宽矮窗选集弹层尺寸不合适");
            Require(Control<ItemsControl>("EpisodeItems").Items.Count == 36, "宽矮窗选集内容丢失");
            double[] popupSize = [popup!.Width, popup.Height];
            popup.IsOpen = false;
            await Settle();
            Capture(shell, "results/player-1380x300.png");
            return new { actual = new[] { Window.ActualWidth, Window.ActualHeight }, bounds, popupSize, episodeChoices = 36, native = await VerifyNativeVideo() };
        });

        if (PopupOnly) return;

        await Check("extreme_resize_and_fullscreen_restore", async () =>
        {
            Window.Width = 80;
            Window.Height = 80;
            await Settle();
            double[] extremeActual = [Window.ActualWidth, Window.ActualHeight];
            Require(Window.MinWidth == 0 && Window.MinHeight == 0 && Window.ActualWidth < 320 && Window.ActualHeight < 240, "极小窗口仍受应用最小值限制");
            Window.Width = 480;
            Window.Height = 240;
            await Settle();
            Invoke("ToggleFullscreen");
            await Settle();
            Require(Field<bool>("_fullscreen"), "未进入全屏");
            ScrollViewer shell = Control<ScrollViewer>("ShellViewport");
            Require(shell.ScrollableWidth < 1 && shell.ScrollableHeight < 1, "全屏有多余外层滚动");
            Popup? fullscreenPopup = Field<Popup?>("_fullscreenControls");
            Require(fullscreenPopup?.Child == Control<FrameworkElement>("PlayerControls"), "全屏浮层未承载控制栏");
            object fullscreenNative = await VerifyNativeVideo();
            Invoke("ToggleFullscreen");
            await Settle();
            Require(!Field<bool>("_fullscreen") && Math.Abs(Window.ActualWidth - 480) < 1 && Math.Abs(Window.ActualHeight - 240) < 1, "退出全屏未恢复小窗尺寸");
            foreach (string name in new[] { "VideoBorder", "SeekSlider", "PauseButton", "NextButton", "FullscreenButton" }) Inside(Control<FrameworkElement>(name), shell, name);
            Require(shell.ScrollableWidth < 1 && shell.ScrollableHeight < 1, "全屏恢复后回到整窗滚动");
            Window.Width = 1380;
            Window.Height = 900;
            await Settle();
            Require(Control<FrameworkElement>("PlayerControls").Parent == Control<Grid>("CinemaLayout"), "普通控制栏未归还原布局");
            Require(Control<ColumnDefinition>("EpisodeColumn").ActualWidth > 0 && Control<FrameworkElement>("EpisodeSidebar").IsVisible, "大窗选集侧栏未恢复");
            Require(Player.GetStatus().FrameReady, "极小窗口与全屏切换中丢失真实解码");
            return new { extremeRequested = new[] { 80, 80 }, extremeActual, fullscreenNative, restoredSize = new[] { 480, 240 }, wideSidebarRestored = true };
        });
    }

    private static async Task<object> VerifyNativeVideo()
    {
        await Settle();
        NativeVideoHost host = Control<NativeVideoHost>("VideoHost");
        Require(GetClientRect(host.VideoHandle, out NativeRect client), "读取本窗口视频客户区失败");
        var origin = new NativePoint();
        Require(ClientToScreen(host.VideoHandle, ref origin), "转换本窗口视频坐标失败");
        Rect native = new(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
        Rect video = ScreenBounds(Control<FrameworkElement>("VideoBorder"));
        Rect shell = ScreenBounds(Control<FrameworkElement>("ShellViewport"));
        Require(native.Width > 1 && native.Height > 1, "原生视频客户区为空");
        RequireContains(video, native, "原生视频超出播放器容器");
        RequireContains(shell, native, "原生视频超出当前窗口视口");
        await Wait(() => Math.Abs(ReadNumber("osd-dimensions/w") - native.Width) <= 2 && Math.Abs(ReadNumber("osd-dimensions/h") - native.Height) <= 2, 3000);
        double osdWidth = ReadNumber("osd-dimensions/w"), osdHeight = ReadNumber("osd-dimensions/h");
        double left = ReadNumber("osd-dimensions/ml"), right = ReadNumber("osd-dimensions/mr");
        double top = ReadNumber("osd-dimensions/mt"), bottom = ReadNumber("osd-dimensions/mb");
        double displayWidth = ReadNumber("dwidth"), displayHeight = ReadNumber("dheight");
        string keepAspect = ReadString("keepaspect");
        Require(keepAspect == "yes" && ReadNumber("panscan") == 0, "播放器未保持完整画面比例");
        double drawnWidth = osdWidth - left - right, drawnHeight = osdHeight - top - bottom;
        Require(drawnWidth > 0 && drawnHeight > 0 && displayWidth > 0 && displayHeight > 0, "播放器显示参数为空");
        Require(Math.Abs(drawnWidth - drawnHeight * displayWidth / displayHeight) <= 2, "实际输出区域未保持原视频比例");
        return new
        {
            client = native.ToString(CultureInfo.InvariantCulture), video = video.ToString(CultureInfo.InvariantCulture),
            osd = new[] { osdWidth, osdHeight }, blackBars = new[] { left, right, top, bottom },
            displayedVideo = new[] { drawnWidth, drawnHeight }, decodedDisplay = new[] { displayWidth, displayHeight }, keepAspect, panscan = 0
        };
    }

    private static Rect Inside(FrameworkElement element, FrameworkElement viewport, string name)
    {
        Require(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0, name + "没有可见尺寸");
        Rect bounds = RelativeBounds(element, viewport);
        RequireContains(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight), bounds, name + "超出当前可见视口：" + bounds);
        return bounds;
    }

    private static Rect RelativeBounds(FrameworkElement element, FrameworkElement ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static void RaiseKey(FrameworkElement element, Key key) => element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static Rect ScreenBounds(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    private static void RequireContains(Rect outer, Rect inner, string message) => Require(inner.Left >= outer.Left - 2 && inner.Top >= outer.Top - 2 && inner.Right <= outer.Right + 2 && inner.Bottom <= outer.Bottom + 2, message + " / " + outer);

    private static double ReadNumber(string name)
    {
        string value = ReadString(name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : double.NaN;
    }

    private static string ReadString(string name)
    {
        IntPtr module = GetModuleHandle("libmpv-2.dll");
        Require(module != IntPtr.Zero, "已加载的播放器模块未找到");
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
            if (timer.ElapsedMilliseconds > timeout) throw new TimeoutException("本地媒体状态等待超时");
            await Task.Delay(20);
        }
    }

    private static void Capture(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }

    private static void Cleanup()
    {
        if (Window == null) return;
        Set("_closing", true);
        Invoke("CloseEpisodePicker");
        MethodInfo? closeOptions = typeof(MainWindow).GetMethod("ClosePlaybackOptions", Private);
        closeOptions?.Invoke(Window, null);
        Invoke("RestoreWindowControls");
        Invoke("CancelNextPlayback");
        Field<MpvPlayer?>("_player")?.Dispose();
        Field<MpvPlayer?>("_standbyPlayer")?.Dispose();
        foreach (FieldInfo field in typeof(MainWindow).GetFields(Private))
            if (field.GetValue(Window) is DispatcherTimer timer) timer.Stop();
        Window.Close();
    }

    private static async Task Check(string name, Func<Task<object>> test)
    {
        try { Record(name, true, await test()); }
        catch (Exception error) { Record(name, false, new { error = error.Message, type = error.GetType().Name }); }
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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetPropertyString(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr value);
}
