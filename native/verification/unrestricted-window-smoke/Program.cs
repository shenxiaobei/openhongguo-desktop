using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
    private static MainWindow Window = null!;
    private static readonly List<object> Tests = new();
    private static int Failures;

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Element(presentation + "Application.Resources")!;
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), source.Elements());
        using (var reader = resources.CreateReader()) app.Resources = (ResourceDictionary)XamlReader.Load(reader);
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { error = error.Message, type = error.GetType().Name }); }
            finally
            {
                if (Window != null)
                {
                    Set("_closing", true);
                    Invoke("CloseEpisodePicker");
                    Invoke("RestoreWindowControls");
                    Invoke("CancelNextPlayback");
                    Field<MpvPlayer?>("_player")?.Dispose();
                    Field<MpvPlayer?>("_standbyPlayer")?.Dispose();
                    foreach (FieldInfo field in typeof(MainWindow).GetFields(Private))
                        if (field.GetValue(Window) is DispatcherTimer timer) timer.Stop();
                    Window.Close();
                }
                File.WriteAllText("results/unrestricted-window-smoke.json", JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures = Failures, tests = Tests }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync()
    {
        Window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = 20, Opacity = 0 };
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window, typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        InputMethod.SetIsInputMethodEnabled(Window, false);
        InputMethod.SetIsInputMethodSuspended(Window, true);
        System.Windows.Application.Current.MainWindow = Window;
        Control<TextBlock>("PageTitle").Text = "窗口缩放验证";
        Control<TextBlock>("PlayingTitle").Text = "本地竖屏样片 · 缩小与恢复";
        Control<TextBlock>("PlayingSubtitle").Text = "本地测试数据，不访问远端接口";
        ObservableCollection<EpisodeItem> episodes = Field<ObservableCollection<EpisodeItem>>("_episodes");
        for (int index = 1; index <= 36; index++) episodes.Add(new EpisodeItem { Id = index.ToString(), Number = index, Playable = true });
        Window.Show();
        await Settle();

        foreach ((int width, int height) in new[] { (320, 240), (480, 320), (640, 420) })
        {
            await Check($"window_accepts_{width}x{height}", async () =>
            {
                Window.Width = width;
                Window.Height = height;
                Invoke("ShowScreen", Control<Grid>("CatalogScreen"));
                await Settle();
                Require(Window.MinWidth == 0 && Window.MinHeight == 0, "应用仍设置了窗口最小尺寸");
                Require(Math.Abs(Window.ActualWidth - width) < 1 && Math.Abs(Window.ActualHeight - height) < 1, "窗口尺寸仍被旧的640×420钳制");
                ScrollViewer viewport = Control<ScrollViewer>("ShellViewport");
                Require(viewport.ActualWidth > 0 && viewport.ActualHeight > 0, "可见视口为空");
                if (width < 640) Require(viewport.ScrollableWidth > 0 && viewport.ScrollableHeight > 0, "小窗口没有可滚动的内容");
                viewport.ScrollToHorizontalOffset(0);
                Control<Button>("HistoryNav").BringIntoView();
                await Settle();
                Require(InViewport(Control<Button>("HistoryNav"), viewport), "滚动后仍访问不到观看记录入口");
                Require(Field<object?>("_backend") == null, "纯布局验证意外启动了后端");
                return new { requested = new[] { width, height }, actual = new[] { Window.ActualWidth, Window.ActualHeight }, windowMinimum = new[] { Window.MinWidth, Window.MinHeight }, viewport = new[] { viewport.ViewportWidth, viewport.ViewportHeight }, scrollable = new[] { viewport.ScrollableWidth, viewport.ScrollableHeight }, historyReachable = true };
            });
        }

        await Check("small_player_controls_and_picker_remain_reachable", async () =>
        {
            Window.Width = 320;
            Window.Height = 240;
            Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
            await Settle();
            ScrollViewer viewport = Control<ScrollViewer>("ShellViewport");
            Control<Button>("FullscreenButton").BringIntoView();
            await Settle();
            Require(InViewport(Control<Button>("FullscreenButton"), viewport), "滚动后仍访问不到全屏按钮");
            Capture(viewport, "results/320x240-scrolled-player.png");
            Control<Button>("EpisodePickerButton").BringIntoView();
            await Settle();
            Require(InViewport(Control<Button>("EpisodePickerButton"), viewport), "滚动后仍访问不到选集按钮");
            Control<Button>("EpisodePickerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Popup? popup = Field<Popup?>("_episodePickerPopup");
            Require(popup?.IsOpen == true && popup.Width > 0 && popup.Height > 0 && double.IsFinite(popup.Width) && double.IsFinite(popup.Height), "极小窗口选集浮层尺寸不合法");
            Require(Control<ItemsControl>("EpisodeItems").Items.Count == 36, "选集项在浮层中丢失");
            double popupWidth = popup!.Width, popupHeight = popup.Height;
            popup.IsOpen = false;
            return new { controlsReachable = true, pickerReachable = true, popupWidth, popupHeight, choices = 36 };
        });

        await Check("media_uses_memory_cache_and_survives_tiny_resize", async () =>
        {
            Invoke("EnsurePlayer");
            MpvPlayer player = Field<MpvPlayer>("_player");
            string sample = Path.GetFullPath(Path.Combine("..", "mpv-smoke", "sample.mp4"));
            Require(File.Exists(sample), "缺少既有本地视频样片");
            player.Load(sample, null, null, paused: true);
            Control<NativeVideoHost>("VideoHost").Visibility = Visibility.Visible;
            await Wait(() => player.GetStatus().FrameReady && player.GetStatus().CacheBytes > 0, 10_000);
            string disk = ReadMpvString(player, "options/cache-on-disk");
            Require(disk == "no", "视频播放器启用了磁盘缓存");
            MpvStatus prepared = player.GetStatus();
            Window.Width = 80;
            Window.Height = 80;
            await Settle();
            Require(Window.MinWidth == 0 && Window.MinHeight == 0 && Window.ActualWidth < 320 && Window.ActualHeight < 240, "极小窗口仍有应用层限制");
            Require(player.GetStatus().Loaded, "极小缩放中断了已加载媒体");
            Window.Width = 480;
            Window.Height = 320;
            await Settle();
            Require(player.GetStatus().FrameReady && player.GetStatus().VideoWidth == 360 && player.GetStatus().VideoHeight == 640, "恢复窗口后视频未保持就绪");
            ScrollViewer viewport = Control<ScrollViewer>("ShellViewport");
            viewport.ScrollToHome();
            await Settle();
            object clip = VerifyVideoClip(viewport);
            viewport.ScrollToRightEnd();
            viewport.ScrollToBottom();
            await Settle();
            object scrolledClip = VerifyVideoClip(viewport, false);
            player.Stop();
            await Wait(() => !player.GetStatus().Loaded && player.GetStatus().CacheBytes == 0, 5000);
            MpvStatus stopped = player.GetStatus();
            return new { cacheOnDisk = disk, cachedBytesBeforeStop = prepared.CacheBytes, cachedSecondsBeforeStop = prepared.CacheSeconds, extremeSizeAccepted = true, decodedDimensions = new[] { prepared.VideoWidth, prepared.VideoHeight }, cachedBytesAfterStop = stopped.CacheBytes, loadedAfterStop = stopped.Loaded, nativeClip = clip, scrolledNativeClip = scrolledClip };
        });

        await Check("fullscreen_and_wide_window_restore_clear_outer_scrolling", async () =>
        {
            Window.Width = 320;
            Window.Height = 240;
            await Settle();
            Invoke("ToggleFullscreen");
            await Settle();
            Require(Field<bool>("_fullscreen"), "没有进入全屏");
            ScrollViewer viewport = Control<ScrollViewer>("ShellViewport");
            Require(viewport.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled && viewport.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled, "全屏保留了外层滚动条");
            Invoke("ToggleFullscreen");
            await Settle();
            Require(!Field<bool>("_fullscreen") && Math.Abs(Window.ActualWidth - 320) < 1 && Math.Abs(Window.ActualHeight - 240) < 1, "退出全屏没有恢复原极小尺寸");
            Require(viewport.ScrollableWidth > 0 && viewport.ScrollableHeight > 0, "退出全屏后滚动内容未恢复");
            Window.Width = 1380;
            Window.Height = 900;
            await Settle();
            Require(viewport.ScrollableWidth < 1 && viewport.ScrollableHeight < 1, "放大后仍保留多余外层滚动");
            Require(Control<ColumnDefinition>("EpisodeColumn").ActualWidth > 0 && Control<Border>("EpisodeSidebar").Visibility == Visibility.Visible, "放大后选集侧栏没有恢复");
            Require(Control<Border>("PlayerControls").Parent == Control<Grid>("CinemaLayout"), "控制栏没有回到原布局");
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            int nativeRegion;
            try { nativeRegion = GetWindowRgn(Control<NativeVideoHost>("VideoHost").Handle, region); }
            finally { DeleteObject(region); }
            Require(nativeRegion == 0, "放大后仍留下旧的局部视频裁剪");
            Capture(viewport, "results/restored-wide-player.png");
            return new { tinySizeRestored = true, fullscreenNoOuterScroll = true, wideSize = new[] { Window.ActualWidth, Window.ActualHeight }, wideScrolling = new[] { viewport.ScrollableWidth, viewport.ScrollableHeight }, sidebarRestored = true, nativeRegionCleared = nativeRegion == 0 };
        });
    }

    private static bool InViewport(FrameworkElement item, ScrollViewer viewport)
    {
        Rect bounds = item.TransformToAncestor(viewport).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
        return item.IsVisible && item.ActualWidth > 0 && item.ActualHeight > 0 && bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1;
    }

    private static async Task Settle()
    {
        await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Window.UpdateLayout();
        await Task.Delay(120);
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

    private static string ReadMpvString(MpvPlayer player, string name)
    {
        IntPtr module = GetModuleHandle("libmpv-2.dll");
        Require(module != IntPtr.Zero, "已加载的播放器模块未找到");
        var get = Marshal.GetDelegateForFunctionPointer<GetPropertyString>(NativeLibrary.GetExport(module, "mpv_get_property_string"));
        var free = Marshal.GetDelegateForFunctionPointer<Free>(NativeLibrary.GetExport(module, "mpv_free"));
        IntPtr handle = (IntPtr)typeof(MpvPlayer).GetField("_handle", Private)!.GetValue(player)!;
        IntPtr value = get(handle, name);
        try { return Marshal.PtrToStringUTF8(value) ?? ""; }
        finally { if (value != IntPtr.Zero) free(value); }
    }

    private static object VerifyVideoClip(ScrollViewer viewport, bool expectVisible = true)
    {
        // 检查本窗口原生视频的有效裁剪区域，不能用不含 HWND 像素的 WPF 截图代替。
        Window.UpdateLayout();
        viewport.ApplyTemplate();
        var presenter = (ScrollContentPresenter)viewport.Template.FindName("PART_ScrollContentPresenter", viewport);
        Point topLeft = presenter.PointToScreen(new Point());
        Point bottomRight = presenter.PointToScreen(new Point(presenter.ActualWidth, presenter.ActualHeight));
        Rect allowed = new(topLeft, bottomRight);
        IntPtr native = Control<NativeVideoHost>("VideoHost").VideoHandle;
        Rect? visible = null;
        var regions = new List<object>();
        while (native != IntPtr.Zero)
        {
            Require(GetWindowRect(native, out NativeRect window), "读取视频窗口边界失败");
            Require(GetClientRect(native, out NativeRect client), "读取视频客户区失败");
            var origin = new NativePoint();
            Require(ClientToScreen(native, ref origin), "转换视频客户区坐标失败");
            Rect bounds = new(origin.X, origin.Y, Math.Max(0, client.Right - client.Left), Math.Max(0, client.Bottom - client.Top));
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            int kind;
            try
            {
                kind = GetWindowRgn(native, region);
                if (kind != 0)
                {
                    GetRgnBox(region, out NativeRect clip);
                    bounds.Intersect(new Rect(window.Left + clip.Left, window.Top + clip.Top, Math.Max(0, clip.Right - clip.Left), Math.Max(0, clip.Bottom - clip.Top)));
                }
            }
            finally { DeleteObject(region); }
            if (visible == null) visible = bounds;
            else { Rect clipped = visible.Value; clipped.Intersect(bounds); visible = clipped; }
            regions.Add(new { kind, bounds = bounds.ToString() });
            native = GetParent(native);
        }
        Rect effective = visible ?? Rect.Empty;
        if (expectVisible) Require(!effective.IsEmpty && effective.Width > 10 && effective.Height > 10, "视频裁剪错误地隐藏了本应可见的画面");
        Require(effective.IsEmpty || effective.Left >= allowed.Left - 2 && effective.Top >= allowed.Top - 2 && effective.Right <= allowed.Right + 2 && effective.Bottom <= allowed.Bottom + 2,
            "视频 HWND 超出滚动视口，可能覆盖滚动条：" + effective + " / " + allowed + " " + JsonSerializer.Serialize(regions));
        return new { effective = effective.ToString(), viewport = allowed.ToString(), contained = true, regions };
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect bounds);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetPropertyString(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr value);
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
}
