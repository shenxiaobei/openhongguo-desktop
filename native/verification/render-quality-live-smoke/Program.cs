using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Hongguo.Desktop;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;
using Window = System.Windows.Window;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Cases = [];
    private static readonly Dictionary<string, object?> Report = [];
    private static string Output = "";
    private static string Stage = "initialize";
    private static string CandidateScaler = "ewa_lanczossharp";
    private static string CandidateAntiring = "1";
    private static int Failures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 4 || args.Length > 6 || !Regex.IsMatch(args[0], "^[0-9]+$") || !int.TryParse(args[1], out int episode)
            || !double.TryParse(args[2], CultureInfo.InvariantCulture, out double seconds)) return 2;
        if (args.Length >= 5) CandidateScaler = args[4];
        if (args.Length >= 6) CandidateAntiring = args[5];
        Output = Path.GetFullPath(args[3]);
        Directory.CreateDirectory(Output);
        Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadStyles(app);
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(args[0], episode, seconds); }
            catch (Exception error)
            {
                if (error is TargetInvocationException { InnerException: not null } wrapped) error = wrapped.InnerException!;
                Report["error"] = new { stage = Stage, type = error.GetType().Name };
                Failures++;
                Console.WriteLine($"FAIL stage={Stage} type={error.GetType().Name}");
            }
            finally
            {
                Report["at"] = DateTimeOffset.Now;
                Report["failures"] = Failures;
                Report["cases"] = Cases;
                Report["scope"] = "隔离配置、生产桥/播放模型和MpvPlayer；实际HEVC暂停同帧原生输出，不含桌面合成或手机实际码流";
                File.WriteAllText(Path.Combine(Output, "live-render-report.json"), JsonSerializer.Serialize(Report,
                    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync(string seriesId, int episodeNumber, double seconds)
    {
        DirectoryInfo state = Directory.CreateTempSubdirectory("hongguo-live-render-");
        string original = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openhongguo", "anonymous.json");
        if (File.Exists(original)) File.Copy(original, Path.Combine(state.FullName, "anonymous.json"));
        // 禁用测试弹幕，保证输出只来自同一片源画面。
        File.WriteAllText(Path.Combine(state.FullName, "danmaku-settings.json"), "{\"Enabled\":false}");
        MainWindow? window = null;
        Window? renderWindow = null;
        MpvPlayer? player = null;
        bool closed = false;
        try
        {
            Stage = "backend_start";
            window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = -12000 };
            Field("_configDir").SetValue(window, state.FullName);
            Application.Current.MainWindow = window;
            window.Show();
            ((Slider)window.FindName("VolumeSlider")).Value = 0;
            ((CheckBox)window.FindName("AutoNextCheck")).IsChecked = false;
            await WaitAsync(() => Field("_backend").GetValue(window) != null && !(bool)Field("_catalogBusy").GetValue(window)!, 45000);
            Stage = "open_series";
            bool opened = await ((Task<bool>)Invoke(window, "OpenSeriesAsync", seriesId, false, 0)!).WaitAsync(TimeSpan.FromSeconds(75));
            if (!opened) throw new InvalidOperationException();
            SeriesItem series = (SeriesItem)Field("_currentSeries").GetValue(window)!;
            EpisodeItem episode = ((IEnumerable<EpisodeItem>)Field("_episodes").GetValue(window)!).Single(value => value.Number == episodeNumber);
            if (series.Id != seriesId) throw new InvalidOperationException();
            Stage = "playback_metadata";
            await ((Task)Invoke(window, "PlayEpisodeAsync", episode, false)!).WaitAsync(TimeSpan.FromSeconds(75));
            if (Field("_lastPlaybackFailure").GetValue(window) is Exception failed) throw failed;
            PlaybackInfo playback = (PlaybackInfo)Field("_currentPlaybackInfo").GetValue(window)!;
            ((MpvPlayer)Field("_player").GetValue(window)!).Stop();
            Report["target"] = new { seriesId = series.Id, title = series.Title, episode = episode.Number, episodeId = episode.Id,
                definition = playback.Definition, width = playback.Width, height = playback.Height, bitrate = playback.Bitrate,
                hasMediaKey = !string.IsNullOrWhiteSpace(playback.Key), requestedSeconds = seconds };
            Console.WriteLine($"TARGET series={series.Id} episode={episode.Number} decoded_source={playback.Width}x{playback.Height} bitrate={playback.Bitrate}");

            var host = new NativeVideoHost();
            renderWindow = new Window { ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = -12000,
                Width = 1920, Height = 1080, Content = host };
            renderWindow.Show();
            await Task.Delay(150);
            player = new MpvPlayer(host.VideoHandle);
            player.SetMuted(true);
            Report["constructorOptions"] = new { dscale = Read(player, "options/dscale"), antiring = Read(player, "options/dscale-antiring") };
            if (Read(player, "options/dscale") != CandidateScaler ||
                !double.TryParse(Read(player, "options/dscale-antiring"), CultureInfo.InvariantCulture, out double currentAntiring) ||
                currentAntiring != double.Parse(CandidateAntiring, CultureInfo.InvariantCulture))
                throw new InvalidOperationException();
            foreach (string decoder in new[] { "auto-safe", "no" })
            {
                Stage = "decode_" + decoder;
                Set(player, "hwdec", decoder);
                Set(player, "sub-visibility", "no");
                Set(player, "screenshot-format", "png");
                Set(player, "screenshot-high-bit-depth", "no");
                player.Load(playback.Url, playback.AudioUrl, playback.Key, seconds, paused: true);
                await WaitAsync(() => player.GetStatus().FrameReady && Math.Abs(player.GetStatus().Position - seconds) < 0.15, 40000);
                await Task.Delay(300);
                foreach ((int width, int height) in new[] { (1920, 1080), (1658, 933) })
                {
                    await ResizeAsync(renderWindow, host, player, width, height);
                    foreach (string profile in new[] { "default", "candidate" })
                    {
                        Stage = "capture_" + decoder + "_" + width + "_" + profile;
                        Set(player, "dscale", profile == "default" ? "hermite" : CandidateScaler);
                        Set(player, "dscale-antiring", profile == "default" ? "0" : CandidateAntiring);
                        await Task.Delay(250);
                        string prefix = decoder + "-" + width + "x" + height + "-" + profile;
                        string videoPath = Path.Combine(Output, prefix + "-video.png");
                        string windowPath = Path.Combine(Output, prefix + "-window.png");
                        string? beforePts = Read(player, "time-pos");
                        if (!double.TryParse(beforePts, CultureInfo.InvariantCulture, out double capturedSeconds) ||
                            Math.Abs(capturedSeconds - seconds) > 0.05 || Read(player, "pause") != "yes")
                            throw new InvalidOperationException();
                        Capture(player, videoPath, "video");
                        Capture(player, windowPath, "window");
                        string? afterPts = Read(player, "time-pos");
                        if (beforePts != afterPts || Read(player, "pause") != "yes") throw new InvalidOperationException();
                        using FileStream stream = File.OpenRead(windowPath);
                        BitmapSource image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        if (image.PixelWidth != width || image.PixelHeight != height) throw new InvalidOperationException();
                        var metadata = new Dictionary<string, string?>();
                        foreach (string property in new[] { "video-pts", "time-pos", "hwdec-current", "video-codec", "video-format",
                            "video-params", "video-out-params", "osd-dimensions", "options/scale", "options/dscale", "options/dscale-antiring", "options/cscale" })
                            metadata[property] = Read(player, property);
                        Cases.Add(new { name = prefix, passed = true, positionStable = true, pausedBeforeAndAfter = true,
                            timeBefore = beforePts, timeAfter = afterPts, video = videoPath, output = windowPath, metadata });
                        Console.WriteLine($"PASS {prefix} paused_time={beforePts} hwdec={metadata["hwdec-current"]}");
                    }
                }
            }
        }
        finally
        {
            player?.Dispose();
            renderWindow?.Close();
            if (window != null)
            {
                TaskCompletionSource<bool> close = new(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => close.TrySetResult(true);
                window.Close();
                await close.Task.WaitAsync(TimeSpan.FromSeconds(15));
                closed = true;
            }
            Report["naturallyClosed"] = closed;
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (closed && state.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)) state.Delete(true);
            Report["isolatedStateRemoved"] = !Directory.Exists(state.FullName);
        }
    }

    private static async Task ResizeAsync(Window window, NativeVideoHost host, MpvPlayer player, int width, int height)
    {
        Stage = "resize";
        for (int index = 0; index < 5; index++)
        {
            GetClientRect(host.VideoHandle, out NativeRect bounds);
            if (bounds.Right == width && bounds.Bottom == height) break;
            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            window.Width += (width - bounds.Right) / dpi.DpiScaleX;
            window.Height += (height - bounds.Bottom) / dpi.DpiScaleY;
            window.UpdateLayout();
            await Task.Delay(150);
        }
        await WaitAsync(() => Read(player, "osd-width") == width.ToString() && Read(player, "osd-height") == height.ToString(), 10000);
    }

    private static void Capture(MpvPlayer player, string path, string mode)
    {
        WithNative(player, (module, handle) =>
        {
            var command = Marshal.GetDelegateForFunctionPointer<CommandString>(NativeLibrary.GetExport(module, "mpv_command_string"));
            if (command(handle, "screenshot-to-file \"" + path.Replace('\\', '/') + "\" " + mode) < 0) throw new InvalidOperationException();
            return 0;
        });
    }

    private static void Set(MpvPlayer player, string name, string value) => WithNative(player, (module, handle) =>
    {
        var set = Marshal.GetDelegateForFunctionPointer<SetPropertyString>(NativeLibrary.GetExport(module, "mpv_set_property_string"));
        if (set(handle, name, value) < 0) throw new InvalidOperationException();
        return 0;
    });

    private static string? Read(MpvPlayer player, string name) => WithNative(player, (module, handle) =>
    {
        var get = Marshal.GetDelegateForFunctionPointer<GetPropertyString>(NativeLibrary.GetExport(module, "mpv_get_property_string"));
        var free = Marshal.GetDelegateForFunctionPointer<Free>(NativeLibrary.GetExport(module, "mpv_free"));
        IntPtr result = get(handle, name);
        if (result == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(result); }
        finally { free(result); }
    });

    private static T WithNative<T>(MpvPlayer player, Func<IntPtr, IntPtr, T> action)
    {
        // 与生产事件循环共用锁，不在截图或属性读取期间释放原生对象。
        object gate = typeof(MpvPlayer).GetField("_gate", Private)!.GetValue(player)!;
        lock (gate)
        {
            IntPtr handle = (IntPtr)typeof(MpvPlayer).GetField("_handle", Private)!.GetValue(player)!;
            return action(GetModuleHandle("libmpv-2.dll"), handle);
        }
    }

    private static async Task WaitAsync(Func<bool> predicate, int timeout)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException();
            await Task.Delay(80);
        }
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, Private)!;
    private static object? Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);

    private static void LoadStyles(Application app)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"));
        XElement resources = source.Root!.Element(presentation + "Application.Resources")!;
        XElement dictionary = new(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), resources.Elements());
        using System.Xml.XmlReader reader = dictionary.CreateReader();
        app.Resources = (ResourceDictionary)XamlReader.Load(reader);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out NativeRect rect);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandString(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string command);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetPropertyString(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetPropertyString(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr pointer);
}
