using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
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
    private static bool Closed;
    private static int Failures;
    private static string StateDirectory = "";
    private static MpvPlayer Player => Field<MpvPlayer>("_player");
    private static PlaybackInfo Playback => Field<PlaybackInfo>("_currentPlaybackInfo");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fixture") return RunFixture(args[1], args[2]);
        string portrait = Path.GetFullPath(args.Length > 0 ? args[0] : "../mpv-smoke/sample.mp4");
        string landscape = Path.GetFullPath(args.Length > 1 ? args[1] : "../mpv-smoke/sample-landscape.mp4");
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
            try { await RunAsync(portrait, landscape); }
            catch (Exception error) { Record("harness", false, new { error = error.Message, type = error.GetType().Name }); }
            finally
            {
                if (Window != null && !Closed)
                {
                    Window.Close();
                    try { await Wait(() => Closed, "测试窗口未自然关闭"); }
                    catch (Exception error) { Record("cleanup", false, new { error = error.Message }); }
                }
                if (DispatcherErrors.Count > 0) Record("dispatcher_errors", false, DispatcherErrors);
                if (StateDirectory.Length > 0 && Directory.Exists(StateDirectory) && Closed)
                    Directory.Delete(StateDirectory, true);
                File.WriteAllText("results/quality-selection-smoke.json", JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, failures = Failures, tests = Results, dispatcherErrors = DispatcherErrors,
                    naturallyClosed = Closed,
                    scope = "假stdio后端、生产JSONL客户端与播放/画质切换方法、真实libmpv横竖屏样片；直接调用与ComboBox状态及未打开Popup的离屏WPF菜单渲染，不验证鼠标菜单、系统前台、桌面视频合成、线上接口、实际网络码率或手机画质"
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
        EpisodeItem first = new() { Id = "100101", SeriesId = "1001", Number = 1, Playable = true };
        EpisodeItem second = new() { Id = "100102", SeriesId = "1001", Number = 2, Playable = true };
        Set("_currentSeries", new SeriesItem { Id = "1001", Title = "本地画质回归", EpisodeCount = 2 });
        var episodes = Field<ObservableCollection<EpisodeItem>>("_episodes");
        episodes.Add(first);
        episodes.Add(second);
        Invoke("ShowScreen", Control<Grid>("PlayerScreen"));

        await Check("default_best_uses_decoded_landscape_and_actual_description", async () =>
        {
            await Call("PlayEpisodeAsync", first, false);
            await Ready(1280, 720);
            Player.SetPaused(true);
            Require(Field<string>("_quality") == "best", "首次播放未使用最高画质");
            Require(Playback.Width == 1280 && Playback.Height == 720 && Playback.Bitrate == 4_000_000,
                "播放信息未保留实际来源尺寸与码率");
            string text = Description();
            Require(text.Contains("720p") && text.Contains("1280×720"), "当前画质说明缺少真实档位和解码尺寸");
            return Snapshot(text);
        });

        await Check("combobox_low_quality_changes_real_decoder_and_preserves_pause_position", async () =>
        {
            Player.Seek(2);
            await Wait(() => Math.Abs(Player.GetStatus().Position - 2) < 0.15, "暂停定位未就绪");
            Control<ComboBox>("QualityCombo").SelectedItem = Field<ObservableCollection<PlaybackQuality>>("_qualities")
                .Single(item => item.Definition == "worst");
            await Ready(360, 640);
            await Wait(() => !Field<bool>("_playbackBusy") && Playback.Width == 360, "低画质切换未完成");
            Require(Player.GetStatus().Paused && Math.Abs(Player.GetStatus().Position - 2) < 0.2, "切画质丢失暂停或进度");
            Require(Playback.Bitrate == 500_000 && Description().Contains("360×640"), "低画质来源说明未随解码更新");
            Require((await Stats()).GetProperty("qualities").EnumerateArray().Last().GetString() == "worst", "低画质请求未传给后端");
            return Snapshot(Description());
        });

        await Check("explicit_720p_reloads_high_stream_without_resetting_position", async () =>
        {
            await Call("ChangeQualityAsync", "720p");
            await Ready(1280, 720);
            Require(Field<string>("_quality") == "720p" && Playback.Bitrate == 4_000_000,
                "具体档位未使用后端返回的高码率来源");
            Require(Player.GetStatus().Paused && Math.Abs(Player.GetStatus().Position - 2) < 0.2, "恢复高画质丢失暂停或进度");
            Require((await Stats()).GetProperty("qualities").EnumerateArray().Last().GetString() == "720p", "具体画质请求值不符");
            return Snapshot(Description());
        });

        await Check("unoffered_quality_does_not_reload_or_replace_active_metadata", async () =>
        {
            int before = (await Stats()).GetProperty("qualities").GetArrayLength();
            PlaybackInfo playback = Playback;
            await Call("ChangeQualityAsync", "2160p");
            await Task.Delay(100);
            Require((await Stats()).GetProperty("qualities").GetArrayLength() == before, "未提供画质被提交为有效请求");
            Require(ReferenceEquals(playback, Playback) && Field<string>("_quality") == "720p", "无效画质覆盖当前信息");
            return new { noReload = true, selected = Field<string>("_quality"), metadataPreserved = true };
        });

        MpvPlayer prepared = null!;
        await Check("next_preload_does_not_overwrite_current_quality_description", async () =>
        {
            Control<CheckBox>("AutoNextCheck").IsChecked = true;
            await Wait(() => Field<MpvPlayer?>("_standbyPlayer")?.GetStatus().FrameReady == true
                && Field<PlaybackInfo?>("_bufferedPlayback") != null, "下一集未准备真实首帧");
            prepared = Field<MpvPlayer>("_standbyPlayer");
            await Wait(() => prepared.GetStatus().CacheReachedEnd || prepared.GetStatus().CacheSeconds >= 2,
                "下一集缓存未达到交接条件");
            Require(prepared.GetStatus().VideoWidth == 360 && prepared.GetStatus().VideoHeight == 640, "备用解码器未加载下一集样片");
            Require(Playback.Width == 1280 && Playback.Bitrate == 4_000_000 && Description().Contains("1280×720"),
                "预加载信息覆盖了当前集画质");
            Require(Field<string>("_nextPlaybackQuality") == "best", "下一集预加载未使用最高画质");
            return new { current = Snapshot(Description()), nextWidth = prepared.GetStatus().VideoWidth, nextHeight = prepared.GetStatus().VideoHeight, preloadQuality = "best" };
        });

        await Check("buffered_next_handoff_updates_metadata_and_uses_best", async () =>
        {
            Require(prepared != null, "缺少上一用例已验证的备用实例");
            await Call("PlayEpisodeAsync", second, false);
            await Ready(360, 640);
            Player.SetPaused(true);
            Require(ReferenceEquals(Player, prepared), "切集未实际使用预解码实例");
            Require(Field<string>("_quality") == "best" && Playback.Definition == "360p"
                && Playback.Width == 360 && Playback.Height == 640 && Playback.Bitrate == 1_200_000,
                "下一集最高画质或播放信息沿用了上一集");
            Require(Description().Contains("360×640") && !Description().Contains("1280×720"), "交接后说明仍显示上一集解码尺寸");
            return new { reusedPreparedDecoder = true, current = Snapshot(Description()) };
        });

        await Check("offscreen_quality_menu_fits_viewport_and_prefers_real_decode_dimensions", async () =>
        {
            await Ready(360, 640);
            PlaybackInfo playback = Playback;
            int originalWidth = playback.Width, originalHeight = playback.Height;
            string actualDescription;
            try
            {
                // 故意制造错误元数据，证明说明中的尺寸来自解码器，而非原样回显接口字段。
                playback.Width = 111;
                playback.Height = 222;
                actualDescription = Description();
                Require(actualDescription.Contains("360×640") && !actualDescription.Contains("111×222"),
                    "画质说明未优先使用实际解码分辨率");
            }
            finally
            {
                playback.Width = originalWidth;
                playback.Height = originalHeight;
            }

            Require(Field<Popup?>("_playbackOptionsPopup") == null, "离屏渲染前存在已打开菜单");
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
            var popup = new Popup { Child = border, AllowsTransparency = true, Width = 240 };
            Set("_playbackOptionsPopup", popup);
            try
            {
                // 只构造生产选项页；Popup始终关闭，不触发前台、系统窗口或鼠标输入。
                Invoke("SetPlaybackMenuPage", "quality");
                border.Measure(new Size(240, double.PositiveInfinity));
                border.Arrange(new Rect(0, 0, 240, border.DesiredSize.Height));
                border.UpdateLayout();
                Require(!popup.IsOpen, "离屏菜单意外创建前台浮层");
                var panel = (StackPanel)scroll.Content;
                TextBlock info = panel.Children.OfType<TextBlock>().Single(item => item.Name == "QualityPlaybackInfo");
                Require(info.Text == Description() && info.Text.Contains("解码分辨率") && info.Text.Contains("码率"),
                    "画质选项页未展示当前真实分辨率和码率");
                Require(info.TextWrapping == TextWrapping.Wrap && info.TextTrimming == TextTrimming.None,
                    "画质信息使用了会截断文字的布局策略");
                scroll.ApplyTemplate();
                var viewport = scroll.Template.FindName("PART_ScrollContentPresenter", scroll) as FrameworkElement
                    ?? throw new InvalidOperationException("离屏菜单未生成滚动内容宿主");
                Require(viewport.ActualWidth > 0 && viewport.ActualHeight > 0,
                    "离屏菜单未生成可用视口");
                Rect infoBounds = info.TransformToAncestor(border).TransformBounds(new Rect(info.RenderSize));
                Rect viewportBounds = viewport.TransformToAncestor(border).TransformBounds(new Rect(viewport.RenderSize));
                Require(infoBounds.Left >= viewportBounds.Left - 1 && infoBounds.Right <= viewportBounds.Right + 1
                    && infoBounds.Top >= viewportBounds.Top - 1 && infoBounds.Bottom <= viewportBounds.Bottom + 1,
                    "当前画质信息被菜单视口裁剪");
                Rect lastCharacter = info.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                Require(!lastCharacter.IsEmpty && lastCharacter.Height > 0 && lastCharacter.Bottom <= info.ActualHeight + 1,
                    "末行画质信息没有完整布局");
                int width = (int)Math.Ceiling(border.ActualWidth), height = (int)Math.Ceiling(border.ActualHeight);
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                image.Render(border);
                string artifact = Path.GetFullPath("results/quality-menu-offscreen.png");
                using (FileStream output = File.Create(artifact))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(output);
                }
                return new { artifact, actualDescription, wrongMetadataIgnored = true, width, height,
                    infoBounds, viewportBounds, lastCharacter, popupOpened = popup.IsOpen,
                    offscreenWpfOnly = true, desktopVideoCompositeIncluded = false };
            }
            finally
            {
                popup.Child = null;
                Set("_playbackOptionsPopup", null);
            }
        });

        await Check("real_mpv_load_error_clears_previous_source_description", async () =>
        {
            await Ready(360, 640);
            string previousDescription = Description();
            Require(previousDescription.Contains("360×640") && previousDescription.Contains("码率"),
                "错误回归前缺少已验证的片源信息");
            Control<CheckBox>("AutoNextCheck").IsChecked = false;
            string missingFile = Path.Combine(StateDirectory, "missing-quality-fixture.mp4");
            Require(!File.Exists(missingFile), "预期不存在的测试媒体意外存在");
            MpvPlayer player = Player;
            bool errorObserved = false;
            string errorMessage = "";
            EventHandler<string> observeError = (sender, message) =>
            {
                errorObserved = ReferenceEquals(sender, player);
                errorMessage = message;
            };
            player.Error += observeError;
            try
            {
                // 加载真实不存在的文件，由libmpv事件循环触发生产错误订阅者，不伪造事件或清空元数据。
                player.Load(missingFile, null, null);
                await Wait(() => errorObserved, "真实libmpv未返回本地文件加载错误");
                Require(Field<PlaybackInfo?>("_currentPlaybackInfo") == null,
                    "生产错误处理保留了上一片源元数据");
                string description = Description();
                Require(description == "当前片源信息尚未就绪"
                    && !description.Contains("360×640") && !description.Contains("码率"),
                    "播放出错后仍显示旧尺寸或码率");
                Require(Control<TextBlock>("PlayingSubtitle").Text.Contains("播放遇到问题"),
                    "真实错误未到达生产窗口错误处理");
                MpvStatus failedStatus = player.GetStatus();
                Require(!failedStatus.Loaded && !failedStatus.FrameReady,
                    "文件加载错误后解码器仍标记媒体就绪");
                await Call("StopCurrentAsync");
                MpvStatus stoppedStatus = player.GetStatus();
                Require(!stoppedStatus.Loaded && !stoppedStatus.FrameReady
                    && Field<PlaybackInfo?>("_currentPlaybackInfo") == null,
                    "错误后的正常停止未清空播放状态");
                return new { errorEventObserved = errorObserved, errorMessage, previousDescription,
                    description, metadataCleared = true, missingFileExists = File.Exists(missingFile),
                    failedStatus.Loaded, failedStatus.FrameReady, productionStopCompleted = true };
            }
            finally
            {
                player.Error -= observeError;
                await Call("StopCurrentAsync");
            }
        });
    }

    private static void CreateFixture(string portrait, string landscape)
    {
        StateDirectory = Path.Combine(Path.GetTempPath(), "openhongguo-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StateDirectory);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = StateDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--fixture");
        start.ArgumentList.Add(portrait);
        start.ArgumentList.Add(landscape);
        Process process = Process.Start(start)!;
        _ = process.StandardError.ReadToEndAsync();
        Type backendType = typeof(MainWindow).Assembly.GetType("Hongguo.Desktop.BackendClient")!;
        // 仅替换测试后端进程，生产JSONL解析、并发响应和取消语义保持原样。
        Backend = RuntimeHelpers.GetUninitializedObject(backendType);
        backendType.GetField("process", Private)!.SetValue(Backend, process);
        FieldInfo pending = backendType.GetField("pending", Private)!;
        pending.SetValue(Backend, Activator.CreateInstance(pending.FieldType));
        backendType.GetField("writeLock", Private)!.SetValue(Backend, new SemaphoreSlim(1, 1));
        _ = (Task)backendType.GetMethod("ReadResponsesAsync", Private)!.Invoke(Backend, null)!;
        Window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = 40, Opacity = 0 };
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
                bool next = episode == "100102";
                bool low = quality == "worst" || next;
                string definition = low ? "360p" : "720p";
                int width = low ? 360 : 1280, height = low ? 640 : 720;
                long bitrate = next ? 1_200_000 : low ? 500_000 : 4_000_000;
                result = new
                {
                    url = low ? portrait : landscape, duration_seconds = 120,
                    expires_at = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), definition, width, height, bitrate,
                    available_qualities = next
                        ? new[] { new { definition = "360p", width = 360, height = 640, bitrate = 1_200_000 } }
                        : new[] { new { definition = "360p", width = 360, height = 640, bitrate = 500_000 }, new { definition = "720p", width = 1280, height = 720, bitrate = 4_000_000 } }
                };
            }
            else if (method == "fixture_stats") result = new { qualities = qualities.ToArray() };
            else if (method == "history") result = Array.Empty<object>();
            Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
        }
        return 0;
    }

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

    private static object Snapshot(string description)
    {
        MpvStatus status = Player.GetStatus();
        return new { selected = Field<string>("_quality"), Playback.Definition, Playback.Width, Playback.Height, Playback.Bitrate,
            decodedWidth = status.VideoWidth, decodedHeight = status.VideoHeight, status.Paused, status.Position, description };
    }
    private static string Description() => (string)Invoke("GetCurrentQualityDescription")!;
    private static async Task Ready(int width, int height) => await Wait(() => Field<MpvPlayer?>("_player")?.GetStatus() is MpvStatus status
        && status.FrameReady && status.VideoWidth == width && status.VideoHeight == height, "真实解码尺寸未达到预期");
    private static async Task Check(string name, Func<Task<object>> test)
    {
        try { Record(name, true, await test()); }
        catch (Exception error) { Record(name, false, new { error = error.Message, type = error.GetType().Name }); }
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
            if (timer.ElapsedMilliseconds > 12000) throw new TimeoutException(message);
            await Task.Delay(20);
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, Private)!.GetValue(Window) ?? default(T)!);
    private static void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(Window, value);
    private static T Control<T>(string name) where T : class => (T)Window.FindName(name)!;
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(Window, args);
    private static Task Call(string name, params object?[] args) => (Task)Invoke(name, args)!;
    private static Task<JsonElement> Stats() => (Task<JsonElement>)Backend.GetType().GetMethod("CallAsync", Private)!
        .Invoke(Backend, new object?[] { "fixture_stats", null, CancellationToken.None })!;
}
