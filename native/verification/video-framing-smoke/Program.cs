using System;
using System.Collections.Generic;
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
    private static readonly List<string> Errors = new();
    private static MainWindow Window = null!;
    private static int Failures;
    private static bool Closed;
    private static bool ResizeOnly;

    [STAThread]
    private static int Main(string[] args)
    {
        ResizeOnly = args.Contains("--resize-only");
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);
        app.DispatcherUnhandledException += (_, args) =>
        {
            Errors.Add(args.Exception.GetType().Name + ": " + args.Exception.Message);
            args.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            try { if (ResizeOnly) await RunResizeAsync(); else await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { error = error.ToString() }); }
            finally
            {
                if (Window != null && !Closed)
                {
                    // 失败后的回收只处理本夹具，不将强制释放记为正常关闭通过。
                    Set("_closing", true);
                    Set("_closeReady", true);
                    Invoke("RestoreWindowControls");
                    Field<MpvPlayer?>("_player")?.Dispose();
                    Field<MpvPlayer?>("_standbyPlayer")?.Dispose();
                    foreach (FieldInfo field in typeof(MainWindow).GetFields(Private))
                        if (field.GetValue(Window) is DispatcherTimer timer) timer.Stop();
                    Window.Close();
                }
                if (Errors.Count > 0) Record("dispatcher_errors", false, Errors);
                File.WriteAllText(ResizeOnly ? "results/resize-smoke.json" : "results/video-framing-smoke.json", JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, failures = Failures, tests = Tests, dispatcherErrors = Errors,
                    scope = "生产MainWindow菜单、双MpvPlayer与实际预加载交接；仅本地横竖屏样片；原生几何和播放器属性，不含桌面合成或线上内容验证"
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunResizeAsync()
    {
        string portrait = Path.GetFullPath("../mpv-smoke/sample.mp4");
        string landscape = Path.GetFullPath("../mpv-smoke/sample-landscape.mp4");
        Window = new MainWindow
        {
            Title = "1.0 原生缩放边界验证", Left = 50, Top = 40, Width = 1000, Height = 650,
            ShowInTaskbar = true, ShowActivated = true, WindowStartupLocation = WindowStartupLocation.Manual
        };
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window,
            typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        Window.Closed += (_, _) => Closed = true;
        System.Windows.Application.Current.MainWindow = Window;
        Window.Show();
        Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
        await Settle();
        Invoke("EnsurePlayer");
        MpvPlayer player = Field<MpvPlayer>("_player");
        MpvPlayer standby = (MpvPlayer)Invoke("CreatePlayer", Control<NativeVideoHost>("VideoHost").StandbyVideoHandle)!;
        Set("_standbyPlayer", standby);
        standby.SetMuted(true);
        Control<FrameworkElement>("VideoPlaceholder").Visibility = Visibility.Collapsed;
        Control<FrameworkElement>("VideoHost").Visibility = Visibility.Visible;
        Control<CheckBox>("AutoNextCheck").IsChecked = false;
        int[,] sizes = { { 1280, 800 }, { 800, 500 }, { 660, 400 }, { 642, 373 }, { 640, 360 },
            { 620, 340 }, { 480, 300 }, { 320, 240 }, { 642, 373 }, { 1000, 650 } };
        foreach (bool cinema in new[] { false, true })
        foreach (bool isPortrait in new[] { false, true })
        foreach (bool paused in new[] { true, false })
        {
            string id = (cinema ? "cinema" : "normal") + "-" + (isPortrait ? "portrait" : "landscape") + "-" + (paused ? "paused" : "playing");
            await Check("native_resize_fit_" + id, async () =>
            {
                await ResetNormal(1000, 650);
                Invoke("SetVideoFillMode", false);
                player.Load(isPortrait ? portrait : landscape, null, null, paused: paused);
                standby.Load(isPortrait ? landscape : portrait, null, null, paused: true);
                await Ready(player);
                await Ready(standby);
                if (cinema) Control<Button>("FullscreenButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Settle();
                var frames = new List<object>();
                for (int index = 0; index < sizes.GetLength(0); index++)
                {
                    int width = sizes[index, 0], height = sizes[index, 1];
                    Require(SetWindowPos(new WindowInteropHelper(Window).Handle, IntPtr.Zero, 0, 0, width, height, 0x0016), "原生缩放失败");
                    await Settle();
                    object native = NativeGeometry(player, standby);
                    string artifact = "resize-" + id + "-" + index + ".png";
                    object output = CaptureVideoOutput(player, artifact, false);
                    frames.Add(new { requestedPhysical = new[] { width, height }, native, output });
                    try
                    {
                        RequireNativeGeometry(player, standby);
                        await VerifyGeometry(player, false, isPortrait ? 360 : 1280, isPortrait ? 640 : 720);
                        CaptureVideoOutput(player, artifact, true, scaleCaptionThreshold: true);
                    }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException(error.Message + " / " + JsonSerializer.Serialize(new { id, frames }), error);
                    }
                }
                // 同一数值也按DIP请求，明确区分用户可能报告的物理与逻辑窗口尺寸。
                Window.Width = 642;
                Window.Height = 373;
                await Settle();
                object logical = NativeGeometry(player, standby);
                RequireNativeGeometry(player, standby);
                CaptureVideoOutput(player, "resize-" + id + "-642dip.png", true, scaleCaptionThreshold: true);
                return new { frames, logical642x373 = logical };
            });
        }
        foreach (bool cinema in new[] { false, true })
        {
            await Check("native_resize_after_explicit_crop_" + (cinema ? "cinema" : "normal"), async () =>
            {
                await ResetNormal(1000, 650);
                Invoke("SetVideoFillMode", false);
                player.Load(landscape, null, null, paused: true);
                standby.Load(portrait, null, null, paused: true);
                await Ready(player);
                await Ready(standby);
                if (cinema) Control<Button>("FullscreenButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Settle();
                await SelectFraming("PlaybackFramingFillOption");
                Require(SetWindowPos(new WindowInteropHelper(Window).Handle, IntPtr.Zero, 0, 0, 642, 373, 0x0016), "原生缩放失败");
                await Settle();
                object native = NativeGeometry(player, standby);
                object output = CaptureVideoOutput(player, "resize-after-crop-" + (cinema ? "cinema" : "normal") + ".png", false);
                RequireCompleteMode(player, standby, new { native, output });
                RequireNativeGeometry(player, standby);
                CaptureVideoOutput(player, "resize-after-crop-" + (cinema ? "cinema" : "normal") + ".png", true, scaleCaptionThreshold: true);
                return new { native, output };
            });
        }
        await Check("resize_single_close_releases_both_decoders", async () =>
        {
            Require(Field<object?>("_backend") == null, "缩放夹具启动了后端");
            Window.Close();
            await Wait(() => Closed, 5000);
            Require((bool)typeof(MpvPlayer).GetField("_disposed", Private)!.GetValue(player)!
                && (bool)typeof(MpvPlayer).GetField("_disposed", Private)!.GetValue(standby)!, "缩放后关闭未释放双实例");
            return new { naturallyClosed = true, bothDisposed = true };
        });
    }

    private sealed record PixelRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
    private static PixelRect Intersect(PixelRect a, PixelRect b) => new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
    private static PixelRect ClientBounds(IntPtr hwnd)
    {
        Require(GetClientRect(hwnd, out NativeRect rect), "读取原生客户区失败");
        var origin = new NativePoint();
        Require(ClientToScreen(hwnd, ref origin), "原生客户区坐标转换失败");
        return new(origin.X, origin.Y, origin.X + rect.Right - rect.Left, origin.Y + rect.Bottom - rect.Top);
    }
    private static object SurfaceGeometry(IntPtr hwnd, MpvPlayer player)
    {
        PixelRect surface = ClientBounds(hwnd), effective = surface;
        var ancestors = new List<object>();
        for (IntPtr current = hwnd; current != IntPtr.Zero; current = GetParent(current))
        {
            PixelRect client = ClientBounds(current);
            effective = Intersect(effective, client);
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            int regionKind;
            PixelRect? regionScreen = null;
            try
            {
                regionKind = GetWindowRgn(current, region);
                if (regionKind > 0)
                {
                    GetRgnBox(region, out NativeRect box);
                    GetWindowRect(current, out NativeRect window);
                    regionScreen = new(window.Left + box.Left, window.Top + box.Top, window.Left + box.Right, window.Top + box.Bottom);
                    effective = Intersect(effective, regionScreen);
                }
            }
            finally { DeleteObject(region); }
            ancestors.Add(new { hwnd = current.ToInt64(), client, regionKind, regionScreen });
        }
        double w = ReadNumber(player, "osd-dimensions/w"), h = ReadNumber(player, "osd-dimensions/h");
        double ml = ReadNumber(player, "osd-dimensions/ml"), mr = ReadNumber(player, "osd-dimensions/mr"), mt = ReadNumber(player, "osd-dimensions/mt"), mb = ReadNumber(player, "osd-dimensions/mb");
        var image = new PixelRect(surface.Left + (int)Math.Round(ml), surface.Top + (int)Math.Round(mt), surface.Left + (int)Math.Round(w - mr), surface.Top + (int)Math.Round(h - mb));
        bool imageVisible = image.Left >= effective.Left - 1 && image.Top >= effective.Top - 1 && image.Right <= effective.Right + 1 && image.Bottom <= effective.Bottom + 1;
        return new { surface, effective, image, imageVisible, osd = new[] { w, h }, margins = new[] { ml, mr, mt, mb }, panscan = ReadNumber(player, "panscan"), ancestors };
    }
    private static object NativeGeometry(MpvPlayer player, MpvPlayer standby)
    {
        NativeVideoHost host = Control<NativeVideoHost>("VideoHost");
        FrameworkElement root = Control<FrameworkElement>("RootLayout");
        FrameworkElement shell = Control<FrameworkElement>("ShellViewport");
        Point rp = root.PointToScreen(new Point()), sp = shell.PointToScreen(new Point());
        return new { outer = WindowBounds(), dpi = VisualTreeHelper.GetDpi(Window).DpiScaleX,
            root = new[] { rp.X, rp.Y, root.ActualWidth, root.ActualHeight }, shell = new[] { sp.X, sp.Y, shell.ActualWidth, shell.ActualHeight },
            active = SurfaceGeometry(host.VideoHandle, player), standby = SurfaceGeometry(host.StandbyVideoHandle, standby) };
    }
    private static void RequireNativeGeometry(MpvPlayer player, MpvPlayer standby)
    {
        NativeVideoHost host = Control<NativeVideoHost>("VideoHost");
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(NativeGeometry(player, standby)));
        foreach (string name in new[] { "active", "standby" })
        {
            JsonElement state = document.RootElement.GetProperty(name);
            JsonElement surface = state.GetProperty("surface"), osd = state.GetProperty("osd");
            Require(surface.GetProperty("Width").GetInt32() == osd[0].GetDouble() && surface.GetProperty("Height").GetInt32() == osd[1].GetDouble(),
                "原生视频输出与实际子窗口尺寸不符 / " + document.RootElement);
            Require(state.GetProperty("imageVisible").GetBoolean(), "视频完整输出被祖先客户区或Region裁掉 / " + document.RootElement);
        }
    }

    private static async Task RunAsync()
    {
        string portrait = Path.GetFullPath("../mpv-smoke/sample.mp4");
        string landscape = Path.GetFullPath("../mpv-smoke/sample-landscape.mp4");
        Require(File.Exists(portrait) && File.Exists(landscape), "缺少本地横竖屏样片");
        Window = new MainWindow
        {
            Title = "1.0 画面模式本地回归", Width = 1040, Height = 470, Left = -3000, Top = 20,
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Opacity = 0
        };
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window,
            typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        Window.Closed += (_, _) => Closed = true;
        System.Windows.Application.Current.MainWindow = Window;
        Control<FrameworkElement>("PlayerControls").Opacity = 0;
        Window.Show();
        Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
        await Settle();
        Invoke("EnsurePlayer");
        MpvPlayer player = Field<MpvPlayer>("_player");
        player.Load(portrait, null, null, paused: true);
        await Ready(player);

        await Check("default_mode_preserves_entire_portrait_frame", async () =>
        {
            Require(!Field<bool>("_fillVideoWindow"), "默认画面模式不是完整显示");
            return await VerifyGeometry(player, false, 360, 640);
        });

        MpvPlayer standby = (MpvPlayer)Invoke("CreatePlayer", Control<NativeVideoHost>("VideoHost").StandbyVideoHandle)!;
        Set("_standbyPlayer", standby);
        standby.SetMuted(true);
        standby.Load(landscape, null, null, paused: true);
        await Ready(standby);

        await Check("menu_fill_preserves_aspect_and_updates_both_decoders", async () =>
        {
            await SelectFraming("PlaybackFramingFillOption");
            Require(Field<bool>("_fillVideoWindow"), "铺满菜单未更新本窗口状态");
            return new { current = await VerifyGeometry(player, true, 360, 640), standby = await VerifyGeometry(standby, true, 1280, 720) };
        });

        await Check("replacement_load_retains_fill_mode", async () =>
        {
            player.Load(landscape, null, null, paused: true);
            standby.Load(portrait, null, null, paused: true);
            await Ready(player);
            await Ready(standby);
            return new { current = await VerifyGeometry(player, true, 1280, 720), standby = await VerifyGeometry(standby, true, 360, 640) };
        });

        await Check("new_preload_decoder_inherits_selected_mode", async () =>
        {
            standby.Dispose();
            Set("_standbyPlayer", null);
            standby = (MpvPlayer)Invoke("CreatePlayer", Control<NativeVideoHost>("VideoHost").StandbyVideoHandle)!;
            Set("_standbyPlayer", standby);
            standby.SetMuted(true);
            standby.Load(portrait, null, null, paused: true);
            await Ready(standby);
            return await VerifyGeometry(standby, true, 360, 640);
        });

        await Check("buffered_surface_handoff_retains_fill_mode", async () =>
        {
            Set("_nextPlaybackEpisodeId", "local-next");
            Set("_nextPlaybackQuality", "best");
            Set("_nextPlaybackCreatedAt", DateTime.UtcNow);
            Set("_bufferedPlayback", new PlaybackInfo { Url = portrait });
            await Wait(() => standby.GetStatus().CacheReachedEnd || standby.GetStatus().CacheSeconds >= 2, 5000);
            object? playback = Invoke("ActivateBufferedPlayback", "local-next", "best");
            Require(playback != null && ReferenceEquals(Field<MpvPlayer>("_player"), standby), "未实际交接预解码播放器");
            player = standby;
            standby = Field<MpvPlayer>("_standbyPlayer");
            Require(ReadNumber(standby, "panscan") == 1, "交接后旧实例丢失铺满设置");
            return new { current = await VerifyGeometry(player, true, 360, 640), oldDecoderStopped = !standby.GetStatus().Loaded };
        });

        await Check("menu_restores_fit_and_resize_preserves_full_frame", async () =>
        {
            await SelectFraming("PlaybackFramingFitOption");
            Require(!Field<bool>("_fillVideoWindow") && ReadNumber(standby, "panscan") == 0, "恢复完整显示未同步备用实例");
            Window.Width = 660;
            Window.Height = 600;
            await Settle();
            return await VerifyGeometry(player, false, 360, 640);
        });

        await Check("window_cinema_after_crop_restores_complete_frame_and_caption", async () =>
        {
            var cases = new List<object>();
            foreach (bool usePortrait in new[] { true, false })
            {
                await ResetNormal(524, 254);
                player.Load(usePortrait ? portrait : landscape, null, null, paused: true);
                standby.Load(usePortrait ? landscape : portrait, null, null, paused: true);
                await Ready(player);
                await Ready(standby);
                await SelectFraming("PlaybackFramingFillOption");
                WindowSnapshot before = WindowBounds();
                Control<Button>("FullscreenButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Settle();
                WindowSnapshot after = WindowBounds();
                object pixels = CaptureVideoOutput(player, "window-cinema-" + (usePortrait ? "portrait" : "landscape") + ".png", false);
                RequireSameWindowBounds(before, after, "窗口内全屏改变了外窗尺寸、位置或标题栏");
                Require(Field<bool>("_windowCinema") && !Field<bool>("_fullscreen"), "按钮未进入窗口内全屏");
                RequireCompleteMode(player, standby, new { before, after, pixels });
                object geometry = await VerifyGeometry(player, false, usePortrait ? 360 : 1280, usePortrait ? 640 : 720);
                object caption = CaptureVideoOutput(player, "window-cinema-" + (usePortrait ? "portrait" : "landscape") + ".png", true);
                cases.Add(new { before, after, geometry, caption });
            }
            return new { cases, systemInputInjected = false };
        });

        await Check("f11_after_crop_restores_complete_frame_and_return_bounds", async () =>
        {
            await ResetNormal(524, 254);
            player.Load(landscape, null, null, paused: true);
            await Ready(player);
            await SelectFraming("PlaybackFramingFillOption");
            WindowSnapshot before = WindowBounds();
            RaiseKey(Key.F11);
            await Settle();
            object pixels = CaptureVideoOutput(player, "f11-complete-landscape.png", false);
            Require(Field<bool>("_fullscreen") && Window.WindowStyle == WindowStyle.None, "F11未进入屏幕全屏");
            RequireCompleteMode(player, standby, pixels);
            object geometry = await VerifyGeometry(player, false, 1280, 720);
            object caption = CaptureVideoOutput(player, "f11-complete-landscape.png", true);
            RaiseKey(Key.Escape);
            await Settle();
            WindowSnapshot after = WindowBounds();
            RequireSameWindowBounds(before, after, "退出F11未恢复原外窗边界和标题栏");
            Require(!Field<bool>("_fullscreen") && !Field<bool>("_windowCinema"), "退出F11丢失原普通模式");
            return new { before, after, geometry, caption, systemInputInjected = false };
        });

        await Check("system_maximize_after_crop_restores_full_bottom_caption", async () =>
        {
            var cases = new List<object>();
            foreach (bool cinema in new[] { false, true })
            {
                await ResetNormal(524, 254);
                player.Load(landscape, null, null, paused: true);
                await Ready(player);
                if (cinema)
                {
                    Control<Button>("FullscreenButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Settle();
                }
                await SelectFraming("PlaybackFramingFillOption");
                WindowSnapshot before = WindowBounds();
                Window.WindowState = WindowState.Maximized;
                await Settle();
                object pixels = CaptureVideoOutput(player, "maximized-" + (cinema ? "cinema" : "normal") + ".png", false);
                Require(Window.WindowState == WindowState.Maximized && Window.WindowStyle == WindowStyle.SingleBorderWindow
                    && !Field<bool>("_fullscreen"), "系统最大化丢失标题栏或混入F11状态");
                RequireCompleteMode(player, standby, pixels);
                object geometry = await VerifyGeometry(player, false, 1280, 720);
                object caption = CaptureVideoOutput(player, "maximized-" + (cinema ? "cinema" : "normal") + ".png", true);
                Window.WindowState = WindowState.Normal;
                await Settle();
                RequireSameWindowBounds(before, WindowBounds(), "还原系统最大化丢失原窗口边界");
                object restored = await VerifyGeometry(player, false, 1280, 720);
                cases.Add(new { cinema, geometry, caption, restored });
            }
            return new { cases, systemTitleBarRetained = true };
        });

        Require(Field<object?>("_backend") == null, "夹具意外启动SDK后端");
        await Check("single_close_naturally_releases_both_decoders", async () =>
        {
            Window.Close();
            await Wait(() => Closed, 5000);
            Require((bool)typeof(MpvPlayer).GetField("_disposed", Private)!.GetValue(player)!
                && (bool)typeof(MpvPlayer).GetField("_disposed", Private)!.GetValue(standby)!, "关闭未释放双解码器");
            return new { naturallyClosed = Closed, bothDisposed = true };
        });
    }

    private static async Task ResetNormal(double width, double height)
    {
        if (Field<bool>("_fullscreen")) Invoke("ToggleFullscreen");
        if (Field<bool>("_windowCinema")) Invoke("ToggleWindowCinema");
        Window.WindowState = WindowState.Normal;
        Window.Width = width;
        Window.Height = height;
        await Settle();
    }

    private sealed record WindowSnapshot(double Left, double Top, double Width, double Height, WindowState State,
        WindowStyle Style, ResizeMode ResizeMode, string Outer);

    private static WindowSnapshot WindowBounds() => new(Window.Left, Window.Top, Window.Width, Window.Height,
        Window.WindowState, Window.WindowStyle, Window.ResizeMode, ReadWindowRect(new WindowInteropHelper(Window).Handle));

    private static void RequireSameWindowBounds(WindowSnapshot before, WindowSnapshot after, string message)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(Window);
        // 以原生像素外框为严格条件；WPF 请求值在样式切换后可能回写为同一像素对应的逻辑尺寸。
        Require(before.Outer == after.Outer && before.Left == after.Left && before.Top == after.Top
            && before.State == after.State && before.Style == after.Style && before.ResizeMode == after.ResizeMode
            && Math.Abs(before.Width - after.Width) * dpi.DpiScaleX <= 1
            && Math.Abs(before.Height - after.Height) * dpi.DpiScaleY <= 1,
            message + " / " + JsonSerializer.Serialize(new { before, after, dpi.DpiScaleX, dpi.DpiScaleY }));
    }

    private static string ReadWindowRect(IntPtr handle)
    {
        Require(GetWindowRect(handle, out NativeRect rect), "读取外窗边界失败");
        return $"{rect.Left},{rect.Top},{rect.Right},{rect.Bottom}";
    }

    private static void RequireCompleteMode(MpvPlayer player, MpvPlayer standby, object diagnostic)
    {
        Require(!Field<bool>("_fillVideoWindow") && ReadNumber(player, "panscan") == 0 && ReadNumber(standby, "panscan") == 0,
            "全屏或最大化沿用了裁切模式 / " + JsonSerializer.Serialize(new
            {
                selectedFill = Field<bool>("_fillVideoWindow"), activePanscan = ReadNumber(player, "panscan"),
                standbyPanscan = ReadNumber(standby, "panscan"), diagnostic
            }));
    }

    private static void RaiseKey(Key key) => Window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
        PresentationSource.FromVisual(Window), 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static object CaptureVideoOutput(MpvPlayer player, string name, bool assertCaption, bool scaleCaptionThreshold = false)
    {
        string path = Path.GetFullPath(Path.Combine("results", name));
        IntPtr module = GetModuleHandle("libmpv-2.dll");
        var command = Marshal.GetDelegateForFunctionPointer<CommandString>(NativeLibrary.GetExport(module, "mpv_command_string"));
        IntPtr handle = (IntPtr)typeof(MpvPlayer).GetField("_handle", Private)!.GetValue(player)!;
        int result = command(handle, "screenshot-to-file \"" + path.Replace('\\', '/') + "\" window");
        Require(result >= 0 && File.Exists(path), "原生视频输出截图失败，错误码 " + result);
        using FileStream input = File.OpenRead(path);
        BitmapSource bitmap = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int w = converted.PixelWidth, h = converted.PixelHeight;
        byte[] pixels = new byte[w * h * 4];
        converted.CopyPixels(pixels, w * 4, 0);
        int yellowMinX = w, yellowMaxX = -1, yellowMinY = h, yellowMaxY = -1;
        int captionWhite = 0, upperCaptionWhite = 0, lowerCaptionWhite = 0;
        int topEdgeYellow = 0, bottomEdgeYellow = 0, leftEdgeYellow = 0, rightEdgeYellow = 0;
        double ml = ReadNumber(player, "osd-dimensions/ml"), mr = ReadNumber(player, "osd-dimensions/mr");
        double mt = ReadNumber(player, "osd-dimensions/mt"), mb = ReadNumber(player, "osd-dimensions/mb");
        double captionTop = mt + (h - mt - mb) * 0.84;
        double imageWidth = w - ml - mr, imageHeight = h - mt - mb;
        double sourceHeight = ReadNumber(player, "dheight");
        double sourceCaptionHeight = sourceHeight == 640 ? 22 : 36;
        double projectedCaptionHeight = sourceCaptionHeight * imageHeight / sourceHeight;
        // 极小输出中的字笔画仅有几像素，缩放抗锯齿会降低亮度；保留分行取样和四边几何断言。
        bool tinyCaption = scaleCaptionThreshold && projectedCaptionHeight < 10;
        int captionBrightness = tinyCaption ? 110 : 190;
        int minimumCaptionPixels = tinyCaption ? 1 : 10;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            if (pixels[i] < 100 && pixels[i + 1] > 140 && pixels[i + 2] > 160)
            {
                yellowMinX = Math.Min(yellowMinX, x); yellowMaxX = Math.Max(yellowMaxX, x);
                yellowMinY = Math.Min(yellowMinY, y); yellowMaxY = Math.Max(yellowMaxY, y);
                if (x >= ml + imageWidth * 0.15 && x <= ml + imageWidth * 0.85)
                {
                    if (Math.Abs(y - mt) <= 5) topEdgeYellow++;
                    if (Math.Abs(y - (h - mb - 1)) <= 5) bottomEdgeYellow++;
                }
                if (y >= mt + imageHeight * 0.15 && y <= mt + imageHeight * 0.85)
                {
                    if (Math.Abs(x - ml) <= 5) leftEdgeYellow++;
                    if (Math.Abs(x - (w - mr - 1)) <= 5) rightEdgeYellow++;
                }
            }
            if (y >= captionTop && pixels[i] > captionBrightness && pixels[i + 1] > captionBrightness && pixels[i + 2] > captionBrightness)
            {
                captionWhite++;
                if (y < mt + imageHeight * 0.935) upperCaptionWhite++;
                else lowerCaptionWhite++;
            }
        }
        var evidence = new { artifact = path, outputSize = new[] { w, h }, margins = new[] { ml, mr, mt, mb },
            yellowBounds = new[] { yellowMinX, yellowMinY, yellowMaxX, yellowMaxY }, bottomCaptionWhitePixels = captionWhite,
            captionLineWhitePixels = new[] { upperCaptionWhite, lowerCaptionWhite },
            frameEdgeYellowPixels = new[] { topEdgeYellow, bottomEdgeYellow, leftEdgeYellow, rightEdgeYellow },
            projectedCaptionHeight, captionBrightness, minimumCaptionPixels,
            nativeVideoOutputScreenshot = true, desktopCompositeIncluded = false };
        if (assertCaption)
        {
            Require(w == ReadNumber(player, "osd-dimensions/w") && h == ReadNumber(player, "osd-dimensions/h"), "截图尺寸未匹配实际原生输出");
            Require(Math.Abs(yellowMinX - ml) <= 4 && Math.Abs(yellowMaxX - (w - mr - 1)) <= 4
                && Math.Abs(yellowMinY - mt) <= 4 && Math.Abs(yellowMaxY - (h - mb - 1)) <= 4,
                "原生画面四边标记未完整保留 / " + JsonSerializer.Serialize(evidence));
            Require(captionWhite >= minimumCaptionPixels * 2, "原生输出下边缘烧录字幕丢失 / " + JsonSerializer.Serialize(evidence));
            Require(upperCaptionWhite >= minimumCaptionPixels && lowerCaptionWhite >= minimumCaptionPixels, "原生输出两行字幕没有同时保留 / " + JsonSerializer.Serialize(evidence));
            Require(topEdgeYellow >= imageWidth * 0.4 && bottomEdgeYellow >= imageWidth * 0.4
                && leftEdgeYellow >= imageHeight * 0.4 && rightEdgeYellow >= imageHeight * 0.4,
                "原生输出四边标记未完整进入画面 / " + JsonSerializer.Serialize(evidence));
        }
        return evidence;
    }

    private static async Task SelectFraming(string optionName)
    {
        Window.Activate();
        await Wait(() => Window.IsActive, 3000);
        Control<Button>("PlaybackOptionsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        Popup popup = Field<Popup>("_playbackOptionsPopup");
        ((FrameworkElement)popup.Child).Opacity = 0;
        MenuControl<Button>("PlaybackFramingMenuButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Settle();
        MenuControl<Button>(optionName).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => Field<Popup?>("_playbackOptionsPopup") == null, 3000);
    }

    private static async Task<object> VerifyGeometry(MpvPlayer player, bool fill, int width, int height)
    {
        await Settle();
        Require(ReadString(player, "keepaspect") == "yes", "画面被非等比拉伸");
        Require(ReadString(player, "video-unscaled") == "no", "画面未随宿主缩放");
        Require(ReadNumber(player, "panscan") == (fill ? 1 : 0), "实际播放器模式不符");
        double w = ReadNumber(player, "osd-dimensions/w"), h = ReadNumber(player, "osd-dimensions/h");
        double ml = ReadNumber(player, "osd-dimensions/ml"), mr = ReadNumber(player, "osd-dimensions/mr");
        double mt = ReadNumber(player, "osd-dimensions/mt"), mb = ReadNumber(player, "osd-dimensions/mb");
        double dw = ReadNumber(player, "dwidth"), dh = ReadNumber(player, "dheight");
        Require(w > 0 && h > 0 && dw == width && dh == height, "真实媒体解码尺寸不符");
        // panscan 放大已按像素取整的适配矩形，边界误差也随放大倍数传播。
        double cropZoom = fill ? Math.Max(w / dw, h / dh) / Math.Min(w / dw, h / dh) : 1;
        double aspectTolerance = 2 * cropZoom;
        double aspectError = Math.Abs((w - ml - mr) - (h - mt - mb) * dw / dh);
        Require(aspectError <= aspectTolerance, "真实输出矩形没有保持片源比例：" + JsonSerializer.Serialize(new
        {
            fill, source = new[] { dw, dh }, osd = new[] { w, h }, margins = new[] { ml, mr, mt, mb },
            videoOut = ReadString(player, "video-out-params"), videoParams = ReadString(player, "video-params")
        }));
        if (fill)
            Require(Math.Max(ml, 0) + Math.Max(mr, 0) + Math.Max(mt, 0) + Math.Max(mb, 0) <= 2, "铺满仍留有由比例产生的黑边");
        else
            Require(ml >= -1 && mr >= -1 && mt >= -1 && mb >= -1, "完整显示裁切了画面");
        return new { fill, source = new[] { dw, dh }, osd = new[] { w, h }, margins = new[] { ml, mr, mt, mb },
            aspectError, aspectTolerance, cropZoom, aspectPreserved = true };
    }

    private static void LoadResources(System.Windows.Application app)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Element(presentation + "Application.Resources")!;
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), source.Elements());
        using var reader = resources.CreateReader();
        app.Resources = (ResourceDictionary)XamlReader.Load(reader);
    }

    private static async Task Ready(MpvPlayer player) => await Wait(() => player.GetStatus().FrameReady, 10000);
    private static async Task Settle()
    {
        await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Window.UpdateLayout();
        await Task.Delay(180);
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
    private static async Task Check(string name, Func<Task<object>> action)
    {
        try { Record(name, true, await action()); }
        catch (Exception error) { Record(name, false, new { error = error.ToString() }); }
    }
    private static void Record(string name, bool passed, object detail)
    {
        if (!passed) Failures++;
        Tests.Add(new { name, passed, detail });
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Control<T>(string name) where T : class => Window.FindName(name) as T ?? throw new InvalidOperationException("控件未找到：" + name);
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, Private)!.GetValue(Window) ?? default(T)!);
    private static void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(Window, value);
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(Window, args);
    private static T MenuControl<T>(string name) where T : FrameworkElement => FindControl<T>(Field<Popup>("_playbackOptionsPopup").Child, name)
        ?? throw new InvalidOperationException("菜单控件未找到：" + name);
    private static T? FindControl<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T element && element.Name == name) return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            T? found = FindControl<T>(VisualTreeHelper.GetChild(root, index), name);
            if (found != null) return found;
        }
        return null;
    }
    private static double ReadNumber(MpvPlayer player, string name) => double.TryParse(ReadString(player, name), NumberStyles.Float,
        CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
    private static string ReadString(MpvPlayer player, string name)
    {
        IntPtr module = GetModuleHandle("libmpv-2.dll");
        Require(module != IntPtr.Zero, "播放器模块未加载");
        var get = Marshal.GetDelegateForFunctionPointer<GetPropertyString>(NativeLibrary.GetExport(module, "mpv_get_property_string"));
        var free = Marshal.GetDelegateForFunctionPointer<Free>(NativeLibrary.GetExport(module, "mpv_free"));
        IntPtr handle = (IntPtr)typeof(MpvPlayer).GetField("_handle", Private)!.GetValue(player)!;
        IntPtr value = get(handle, name);
        try { return Marshal.PtrToStringUTF8(value) ?? ""; }
        finally { if (value != IntPtr.Zero) free(value); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(IntPtr region, out NativeRect rect);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandString(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string command);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetPropertyString(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr value);
}
