using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Results = new();
    private static readonly List<string> DispatcherErrors = new();
    private static MainWindow Window = null!;
    private static object Backend = null!;
    private static string StateDirectory = "";
    private static bool Closed;
    private static int Failures;
    private static MpvPlayer Player => Field<MpvPlayer>("_player");
    private static DanmakuSettings Settings => Field<DanmakuSettings>("_danmakuSettings");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fixture") return RunFixture(args[1], args[2]);
        if (args.Length != 2)
        {
            Console.Error.WriteLine("请依次传入竖屏与横屏本地样片绝对路径。");
            return 2;
        }
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);
        app.DispatcherUnhandledException += (_, item) =>
        {
            DispatcherErrors.Add(item.Exception.GetType().Name + ": " + item.Exception.Message);
            item.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1])); }
            catch (Exception error) { Record("harness", false, new { error = error.ToString() }); }
            finally
            {
                if (Window != null && !Closed)
                {
                    Window.Close();
                    try { await Wait(() => Closed, "测试窗口未自然关闭"); }
                    catch (Exception error) { Record("cleanup", false, new { error = error.Message }); }
                }
                if (DispatcherErrors.Count > 0) Record("dispatcher_errors", false, DispatcherErrors);
                bool removed = false;
                if (StateDirectory.Length > 0 && Directory.Exists(StateDirectory) && Closed)
                {
                    Directory.Delete(StateDirectory, true);
                    removed = true;
                }
                File.WriteAllText("results/rolling-playback-smoke.json", JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, failures = Failures, tests = Results, dispatcherErrors = DispatcherErrors,
                    naturallyClosed = Closed, isolatedStateRemoved = removed,
                    scope = "生产MainWindow播放生命周期与JSONL客户端、受控stdio后端、真实libmpv双实例及本地横竖屏样片；片内文字由libmpv内部ASS轨渲染，截图为libmpv输出，不含桌面/WPF/系统前台合成；不测线上弹幕接口、网络时延或鼠标操作"
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync(string portrait, string landscape)
    {
        Require(File.Exists(portrait) && File.Exists(landscape), "缺少横竖屏本地样片");
        CreateFixture(portrait, landscape);
        var first = new EpisodeItem { Id = "100101", SeriesId = "1001", Number = 1, Playable = true };
        var second = new EpisodeItem { Id = "100102", SeriesId = "1001", Number = 2, Playable = true };
        Set("_currentSeries", new SeriesItem { Id = "1001", Title = "原生弹幕生产集成", EpisodeCount = 2 });
        Field<ObservableCollection<EpisodeItem>>("_episodes").Add(first);
        Field<ObservableCollection<EpisodeItem>>("_episodes").Add(second);
        Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
        Settings.Opacity = 1;

        await Check("production_play_starts_native_danmaku_with_closed_panel_and_best_quality", async () =>
        {
            await Call("PlayEpisodeAsync", first, false);
            await Ready(1280, 720);
            Player.SetPaused(true);
            await Seek(3);
            await TrackReady("首集");
            Require(Control<Border>("DiscussionPanel").Visibility == Visibility.Collapsed, "后台弹幕依赖已打开讨论面板");
            JsonElement stats = await Stats();
            Require(stats.GetProperty("qualities")[0].GetString() == "best" && Field<string>("_quality") == "best",
                "首次播放未继续请求最高画质");
            Require(stats.GetProperty("danmaku").EnumerateArray().Any(item => item.GetProperty("episode").GetString() == first.Id),
                "生产播放路径未请求片内弹幕");
            PixelDifference pixels = CaptureDifference("01-first-panel-closed");
            Require(pixels.Changed > 50, "原生截图未出现可辨认的弹幕像素");
            Capture("01-first-panel-closed-window.png", "window");
            return new { panelClosed = true, quality = Field<string>("_quality"), track = TrackTitle(), pixels };
        });

        await Check("pause_freezes_and_seek_moves_native_text_on_media_timeline", async () =>
        {
            await Seek(3);
            var before = CaptureDifference("02-paused-before");
            await Task.Delay(300);
            var after = CaptureDifference("02-paused-after");
            Require(SamePixels(before.SubtitleImage, after.SubtitleImage), "暂停后原生弹幕仍在移动");
            await Seek(4);
            var moved = CaptureDifference("02-seek-forward");
            Require(moved.Changed > 50 && moved.CenterX < before.CenterX - 10, "向前跳转未带动弹幕向左移动");
            await Seek(3);
            var restored = CaptureDifference("02-seek-back");
            Require(Math.Abs(restored.CenterX - before.CenterX) < 2, "回到相同媒体时间后弹幕位置未恢复");
            return new { paused = Player.GetStatus().Paused, pausePixelsIdentical = true, before, moved, restored };
        });

        await Check("closing_discussion_keeps_independent_native_track_and_text", async () =>
        {
            string title = TrackTitle();
            Control<Border>("DiscussionPanel").Visibility = Visibility.Visible;
            Invoke("CloseDiscussionPanel");
            await Task.Delay(100);
            PixelDifference pixels = CaptureDifference("03-discussion-closed");
            Require(Control<Border>("DiscussionPanel").Visibility == Visibility.Collapsed
                && TrackTitle() == title && pixels.Changed > 50, "关闭讨论面板移除了原生弹幕");
            return new { trackPreserved = true, panelClosed = true, pixels };
        });

        await Check("quick_toggle_clears_pixels_and_d_key_restores_them", async () =>
        {
            Control<Button>("DanmakuToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !Settings.Enabled && TrackTitle().Length == 0, "快捷关闭未卸载弹幕轨");
            PixelDifference hidden = CaptureDifference("04-toggle-off");
            Require(hidden.Changed == 0, "关闭弹幕后原生视频截图仍有额外文字");
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Window), 0, Key.D)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Window.RaiseEvent(key);
            await TrackReady("首集");
            PixelDifference visible = CaptureDifference("04-d-key-on");
            Require(key.Handled && Settings.Enabled && visible.Changed > 50, "D键未通过生产快捷键恢复原生弹幕");
            return new { keyHandled = key.Handled, hidden, visible,
                preferencesSaved = File.Exists(Path.Combine(StateDirectory, "danmaku-settings.json")) };
        });

        await Check("stretch_and_resize_rebuild_native_aspect_without_accumulating_tracks", async () =>
        {
            Invoke("SetVideoFillMode", true);
            await Wait(() => AssWidth() == ExpectedStretchWidth() && !Field<bool>("_rollingDanmakuDirty"),
                "拉伸模式未按原生输出尺寸重建ASS宽高比");
            int initialWidth = AssWidth();
            Window.Width = 940;
            Window.Height = 680;
            Window.UpdateLayout();
            await Wait(() => ExpectedStretchWidth() != initialWidth && AssWidth() == ExpectedStretchWidth()
                && !Field<bool>("_rollingDanmakuDirty"), "缩窗后ASS宽高比未跟随新的视频输出");
            int resizedWidth = AssWidth();
            Require(DanmakuTrackCount() == 1, "拉伸或缩窗累积了旧弹幕轨");
            PixelDifference pixels = CaptureDifference("04-stretch-resized");
            Require(pixels.Changed > 50, "拉伸缩窗后原生弹幕消失");
            Window.Width = 1280;
            Window.Height = 820;
            Invoke("SetVideoFillMode", false);
            Window.UpdateLayout();
            await Wait(() => AssWidth() == 1280 && !Field<bool>("_rollingDanmakuDirty"),
                "切回完整显示未恢复片源宽高比");
            Require(DanmakuTrackCount() == 1, "恢复完整显示累积了旧弹幕轨");
            return new { initialWidth, resizedWidth, restoredWidth = AssWidth(), activeDanmakuTracks = DanmakuTrackCount(), pixels };
        });

        await Check("same_episode_quality_reload_reattaches_track_and_preserves_pause_position", async () =>
        {
            await Seek(3);
            string oldTrack = TrackTitle();
            int requestsBefore = (await Stats()).GetProperty("danmaku").GetArrayLength();
            await Call("ChangeQualityAsync", "worst");
            await Ready(360, 640);
            await TrackReady("首集");
            PixelDifference pixels = CaptureDifference("05-quality-reloaded");
            Require(TrackTitle() != oldTrack && Player.GetStatus().Paused
                && Math.Abs(Player.GetStatus().Position - 3) < 0.15 && pixels.Changed > 50,
                "同集换画质未重新挂轨或丢失播放位置");
            Require((await Stats()).GetProperty("danmaku").GetArrayLength() == requestsBefore,
                "同集换画质丢弃已读取弹幕并重复请求");
            return new { newTrack = TrackTitle(), previousTrack = oldTrack, reusedText = true, pixels };
        });

        MpvPlayer? prepared = null;
        await Check("preloaded_next_handoff_uses_best_and_contains_only_new_episode_text", async () =>
        {
            Control<CheckBox>("AutoNextCheck").IsChecked = true;
            await Wait(() => Field<MpvPlayer?>("_standbyPlayer")?.GetStatus().FrameReady == true
                && Field<PlaybackInfo?>("_bufferedPlayback") != null, "下一集预解码未就绪");
            prepared = Field<MpvPlayer>("_standbyPlayer");
            await Wait(() => prepared.GetStatus().CacheReachedEnd || prepared.GetStatus().CacheSeconds >= 2,
                "下一集缓存未达到交接条件");
            await Call("PlayEpisodeAsync", second, false);
            await Ready(360, 640);
            Player.SetPaused(true);
            await Seek(3);
            await TrackReady("次集");
            string ass = PlayerField<string>(Player, "_danmakuAss");
            PixelDifference pixels = CaptureDifference("06-next-preloaded");
            Require(ReferenceEquals(Player, prepared), "生产切集未交换真实备用解码器");
            Require(!ass.Contains("首集") && ass.Contains("次集") && pixels.Changed > 50,
                "新集弹幕含上一集内容或未显示文字");
            Require(Field<string>("_quality") == "best"
                && (await Stats()).GetProperty("qualities").EnumerateArray().Last().GetString() == "best",
                "预加载交接未继续使用最高画质");
            return new { usedPreparedDecoder = true, quality = Field<string>("_quality"), oldTextAbsent = true, pixels };
        });

        await Check("production_stop_clears_tracks_buffers_and_identity", async () =>
        {
            Control<CheckBox>("AutoNextCheck").IsChecked = false;
            MpvPlayer player = Player;
            await Call("StopCurrentAsync");
            Require(PlayerField<long>(player, "_danmakuTrackId") == -1
                && PlayerField<string>(player, "_danmakuAss").Length == 0
                && Field<List<DiscussionEntry>>("_rollingDanmakuItems").Count == 0
                && Field<string>("_rollingDanmakuEpisodeId").Length == 0
                && Field<EpisodeItem?>("_currentEpisode") == null
                && !player.GetStatus().Loaded, "正常停止保留了片内弹幕轨、正文或媒体身份");
            return new { trackCleared = true, bodyCleared = true, episodeCleared = true, mediaStopped = true };
        });
    }

    private static void CreateFixture(string portrait, string landscape)
    {
        StateDirectory = Path.Combine(Path.GetTempPath(), "openhongguo-rolling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StateDirectory);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = StateDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--fixture");
        start.ArgumentList.Add(portrait);
        start.ArgumentList.Add(landscape);
        Process process = Process.Start(start)!;
        _ = process.StandardError.ReadToEndAsync();
        Type type = typeof(MainWindow).Assembly.GetType("Hongguo.Desktop.BackendClient")!;
        // 仅将进程替换成本地夹具，生产JSONL解析、响应分派与取消逻辑保持原样。
        Backend = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("process", Private)!.SetValue(Backend, process);
        FieldInfo pending = type.GetField("pending", Private)!;
        pending.SetValue(Backend, Activator.CreateInstance(pending.FieldType));
        type.GetField("writeLock", Private)!.SetValue(Backend, new SemaphoreSlim(1, 1));
        _ = (Task)type.GetMethod("ReadResponsesAsync", Private)!.Invoke(Backend, null)!;
        Window = new MainWindow
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000, Top = 40, Width = 1280, Height = 820, Opacity = 0
        };
        InputMethod.SetIsInputMethodEnabled(Window, false);
        InputMethod.SetIsInputMethodSuspended(Window, true);
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window,
            typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        Set("_configDir", StateDirectory);
        Set("_backend", Backend);
        Control<CheckBox>("AutoNextCheck").IsChecked = false;
        Control<Slider>("VolumeSlider").Value = 0;
        System.Windows.Application.Current.MainWindow = Window;
        Window.Closed += (_, _) => Closed = true;
        Window.Show();
    }

    private static int RunFixture(string portrait, string landscape)
    {
        var qualities = new List<string>();
        var danmaku = new List<object>();
        while (Console.ReadLine() is string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement request = document.RootElement;
            string id = request.GetProperty("id").GetString()!;
            string method = request.GetProperty("method").GetString()!;
            JsonElement args = request.TryGetProperty("args", out JsonElement value) ? value : default;
            object result = new { };
            if (method == "playback")
            {
                string quality = args.GetProperty("quality").GetString()!;
                string episode = args.GetProperty("episode_id").GetString()!;
                qualities.Add(quality);
                bool low = quality == "worst" || episode == "100102";
                result = new
                {
                    url = low ? portrait : landscape, duration_seconds = 60, duration_ms = 60000,
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
                    definition = low ? "360p" : "720p", width = low ? 360 : 1280, height = low ? 640 : 720,
                    bitrate = low ? 500000 : 4000000,
                    available_qualities = new[]
                    {
                        new { definition = "360p", width = 360, height = 640, bitrate = 500000 },
                        new { definition = "720p", width = 1280, height = 720, bitrate = 4000000 }
                    }
                };
            }
            else if (method == "danmaku")
            {
                string episode = args.GetProperty("episode_id").GetString()!;
                danmaku.Add(new { episode, cursor = args.GetProperty("cursor").GetString(), duration = args.GetProperty("duration_ms").GetInt64() });
                string label = episode == "100101" ? "首集" : "次集";
                result = new
                {
                    items = new[]
                    {
                        new { id = episode + "-a", text = label + "原生滚动弹幕", position_ms = 500 },
                        new { id = episode + "-b", text = label + "关闭面板仍然显示", position_ms = 900 }
                    },
                    cursor = "", has_more = false, source_limited = false
                };
            }
            else if (method == "fixture_stats") result = new { qualities = qualities.ToArray(), danmaku = danmaku.ToArray() };
            else if (method == "history") result = Array.Empty<object>();
            Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
        }
        return 0;
    }

    private sealed record PixelDifference(string SubtitleImage, string VideoImage, int Width, int Height,
        int Changed, double CenterX, int Left, int Right, int Top, int Bottom);

    private static PixelDifference CaptureDifference(string name)
    {
        string plain = Capture(name + "-video.png", "video");
        string subtitled = Capture(name + "-subtitles.png", "subtitles");
        var first = ReadPixels(plain);
        var second = ReadPixels(subtitled);
        Require(first.Width == second.Width && first.Height == second.Height, "有无字幕截图的源尺寸不一致");
        int count = 0, minX = first.Width, maxX = -1, minY = first.Height, maxY = -1;
        long sumX = 0;
        for (int y = 0; y < first.Height; y++)
        for (int x = 0; x < first.Width; x++)
        {
            int index = (y * first.Width + x) * 4;
            int difference = Math.Abs(first.Pixels[index] - second.Pixels[index])
                + Math.Abs(first.Pixels[index + 1] - second.Pixels[index + 1])
                + Math.Abs(first.Pixels[index + 2] - second.Pixels[index + 2]);
            if (difference < 25) continue;
            count++;
            sumX += x;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return new PixelDifference(subtitled, plain, first.Width, first.Height, count,
            count == 0 ? -1 : (double)sumX / count, minX, maxX, minY, maxY);
    }

    private static string Capture(string name, string mode)
    {
        string path = Path.GetFullPath(Path.Combine("results", name));
        IntPtr module = GetModuleHandle("libmpv-2.dll");
        var command = Marshal.GetDelegateForFunctionPointer<CommandString>(NativeLibrary.GetExport(module, "mpv_command_string"));
        int result = command(PlayerField<IntPtr>(Player, "_handle"), "screenshot-to-file \"" + path.Replace('\\', '/') + "\" " + mode);
        Require(result >= 0 && File.Exists(path), "libmpv输出截图失败，错误码 " + result);
        return path;
    }

    private static (int Width, int Height, byte[] Pixels) ReadPixels(string path)
    {
        using var file = File.OpenRead(path);
        BitmapSource bitmap = BitmapFrame.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (converted.PixelWidth, converted.PixelHeight, pixels);
    }

    private static bool SamePixels(string first, string second) => ReadPixels(first).Pixels.SequenceEqual(ReadPixels(second).Pixels);

    private static string TrackTitle()
    {
        string title = PlayerField<string>(Player, "_danmakuTrackTitle");
        long id = PlayerField<long>(Player, "_danmakuTrackId");
        if (id < 0 || title.Length == 0) return "";
        int count = int.TryParse(ReadProperty(Player, "track-list/count"), out int tracks) ? tracks : 0;
        for (int index = 0; index < count; index++)
        {
            string prefix = "track-list/" + index.ToString(CultureInfo.InvariantCulture) + "/";
            if (ReadProperty(Player, prefix + "title") == title
                && ReadProperty(Player, prefix + "id") == id.ToString(CultureInfo.InvariantCulture)
                && ReadProperty(Player, prefix + "external") == "yes"
                && ReadProperty(Player, "sid") == id.ToString(CultureInfo.InvariantCulture)) return title;
        }
        return "";
    }

    private static int DanmakuTrackCount()
    {
        int count = int.TryParse(ReadProperty(Player, "track-list/count"), out int tracks) ? tracks : 0;
        int result = 0;
        for (int index = 0; index < count; index++)
            if (ReadProperty(Player, "track-list/" + index.ToString(CultureInfo.InvariantCulture) + "/title")
                .StartsWith("openhongguo-danmaku-", StringComparison.Ordinal)) result++;
        return result;
    }

    private static int AssWidth()
    {
        string? line = PlayerField<string>(Player, "_danmakuAss").Split('\n')
            .FirstOrDefault(value => value.StartsWith("PlayResX: ", StringComparison.Ordinal));
        return line != null && int.TryParse(line[10..].Trim(), out int width) ? width : 0;
    }

    private static int ExpectedStretchWidth()
    {
        double width = double.Parse(ReadProperty(Player, "osd-dimensions/w"), CultureInfo.InvariantCulture);
        double height = double.Parse(ReadProperty(Player, "osd-dimensions/h"), CultureInfo.InvariantCulture);
        return Math.Clamp((int)Math.Round(720 * Math.Max(1, width) / Math.Max(1, height)), 160, 5120);
    }

    private static string ReadProperty(MpvPlayer player, string name)
    {
        IntPtr module = GetModuleHandle("libmpv-2.dll");
        var get = Marshal.GetDelegateForFunctionPointer<GetPropertyString>(NativeLibrary.GetExport(module, "mpv_get_property_string"));
        var free = Marshal.GetDelegateForFunctionPointer<Free>(NativeLibrary.GetExport(module, "mpv_free"));
        IntPtr value = get(PlayerField<IntPtr>(player, "_handle"), name);
        try { return Marshal.PtrToStringUTF8(value) ?? ""; }
        finally { if (value != IntPtr.Zero) free(value); }
    }

    private static async Task Seek(double seconds)
    {
        Player.Seek(seconds);
        await Wait(() => Math.Abs(Player.GetStatus().Position - seconds) < 0.1, "暂停定位未达到目标时间");
        await Task.Delay(100);
    }

    private static async Task TrackReady(string text) => await Wait(() => TrackTitle().Length > 0
        && PlayerField<string>(Player, "_danmakuAss").Contains(text), "生产路径未挂载当前集的原生弹幕轨");
    private static async Task Ready(int width, int height) => await Wait(() => Field<MpvPlayer?>("_player")?.GetStatus() is MpvStatus status
        && status.FrameReady && status.VideoWidth == width && status.VideoHeight == height, "真实解码未达到预期尺寸");

    private static void LoadResources(System.Windows.Application app)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), resources.Elements());
        using var reader = dictionary.CreateReader();
        app.Resources = (ResourceDictionary)XamlReader.Load(reader);
    }

    private static async Task Check(string name, Func<Task<object>> test)
    {
        try { Record(name, true, await test()); }
        catch (Exception error) { Record(name, false, new { error = error.ToString() }); }
    }
    private static void Record(string name, bool passed, object detail)
    {
        if (!passed) Failures++;
        Results.Add(new { name, passed, detail });
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail));
    }
    private static async Task Wait(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > 15000) throw new TimeoutException(message);
            await Task.Delay(20);
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, Private)!.GetValue(Window) ?? default(T)!);
    private static T PlayerField<T>(MpvPlayer player, string name) => (T)(typeof(MpvPlayer).GetField(name, Private)!.GetValue(player) ?? default(T)!);
    private static void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(Window, value);
    private static T Control<T>(string name) where T : class => (T)Window.FindName(name)!;
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(Window, args);
    private static Task Call(string name, params object?[] args) => (Task)Invoke(name, args)!;
    private static Task<JsonElement> Stats() => (Task<JsonElement>)Backend.GetType().GetMethod("CallAsync", Private)!
        .Invoke(Backend, new object?[] { "fixture_stats", null, CancellationToken.None })!;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandString(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string command);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetPropertyString(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr value);
}
