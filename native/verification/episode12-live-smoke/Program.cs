using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml.Linq;
using Hongguo.Desktop;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Results = [];
    private static int Failures;
    private static bool NaturallyClosed;
    private static bool StateRemoved;
    private static string ActualTitle = "";
    private static string ActualSeriesId = "";
    private static int EpisodeCount;

    [STAThread]
    private static int Main(string[] args)
    {
        // 显式指定系列与集号，避免同名不同季被硬编码测试混淆。
        if (args.Length < 3 || !Regex.IsMatch(args[0], "^[0-9]+$") ||
            !Regex.IsMatch(args[1], "^[1-9][0-9]*(,[1-9][0-9]*)*$"))
        {
            Console.WriteLine("用法：<series-id> <episode[,episode...]> <report.json> [--auto-next]");
            return 2;
        }
        string seriesId = args[0];
        int[] episodes = args[1].Split(',').Select(int.Parse).ToArray();
        string reportPath = Path.GetFullPath(args[2]);
        bool autoNext = args.Contains("--auto-next");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        Application app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadProductionStyles(app);
        app.Startup += async (_, _) =>
        {
            try { await VerifyEpisodesAsync(seriesId, episodes, autoNext); }
            catch (Exception error) { RecordFailure("harness", 0, error, null); }
            finally
            {
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, requestedSeriesId = seriesId, requestedEpisodes = episodes,
                    actualSeriesId = ActualSeriesId, actualTitle = ActualTitle, episodeCount = EpisodeCount,
                    failures = Failures, tests = Results, naturallyClosed = NaturallyClosed, isolatedStateRemoved = StateRemoved,
                    scope = "隔离配置、生产窗口与JSONL桥、真实线上分集及libmpv首帧/进度；不含系统桌面合成或人工点击验收"
                }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task VerifyEpisodesAsync(string seriesId, int[] requestedEpisodes, bool autoNext)
    {
        // 只复制匿名设备档案，历史及偏好全部写入一次性目录。
        DirectoryInfo state = Directory.CreateTempSubdirectory("hongguo-target-live-smoke-");
        string existing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openhongguo", "anonymous.json");
        if (File.Exists(existing)) File.Copy(existing, Path.Combine(state.FullName, "anonymous.json"));
        MainWindow? window = null;
        try
        {
            window = new MainWindow
            {
                ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -3000, Top = 40, Opacity = 0
            };
            Field("_configDir").SetValue(window, state.FullName);
            Application.Current.MainWindow = window;
            window.Show();
            Control<Slider>(window, "VolumeSlider").Value = 0;
            Control<CheckBox>(window, "AutoNextCheck").IsChecked = autoNext;
            await WaitForAsync(() => Field("_backend").GetValue(window) != null && !(bool)Field("_catalogBusy").GetValue(window)!, 40_000, "backend_start");
            bool opened = await ((Task<bool>)Invoke(window, "OpenSeriesAsync", seriesId, false, 0)!).WaitAsync(TimeSpan.FromSeconds(75));
            if (!opened) throw new InvalidOperationException("open_series_failed");
            SeriesItem series = (SeriesItem)Field("_currentSeries").GetValue(window)!;
            EpisodeItem[] episodes = ((IEnumerable<EpisodeItem>)Field("_episodes").GetValue(window)!).ToArray();
            ActualSeriesId = series.Id;
            ActualTitle = series.Title;
            EpisodeCount = episodes.Length;
            Console.WriteLine($"TARGET series_id={series.Id} title={series.Title} episode_count={episodes.Length}");
            if (series.Id != seriesId) throw new InvalidOperationException("wrong_series");
            foreach (int number in requestedEpisodes)
            {
                try
                {
                    EpisodeItem episode = episodes.Single(item => item.Number == number);
                    await ((Task)Invoke(window, "PlayEpisodeAsync", episode, false)!).WaitAsync(TimeSpan.FromSeconds(75));
                    if (Field("_lastPlaybackFailure").GetValue(window) is Exception failure) throw failure;
                    await VerifyCurrentAsync(window, episode, "direct");
                    if (autoNext) await VerifyAutoNextAsync(window, episode, episodes);
                }
                catch (Exception error) { RecordFailure("episode_playback", number, error, window); }
            }
        }
        finally
        {
            if (window != null)
            {
                TaskCompletionSource<bool> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => finished.TrySetResult(true);
                window.Close();
                try
                {
                    await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    NaturallyClosed = true;
                }
                catch (TimeoutException) { RecordFailure("window_close", 0, new TimeoutException("window_close"), window); }
            }
            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            string statePath = Path.GetFullPath(state.FullName);
            if (NaturallyClosed && statePath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                state.Delete(recursive: true);
                StateRemoved = !Directory.Exists(statePath);
            }
        }
    }

    private static async Task VerifyCurrentAsync(MainWindow window, EpisodeItem expected, string route)
    {
        EpisodeItem current = (EpisodeItem)Field("_currentEpisode").GetValue(window)!;
        if (current.Id != expected.Id || current.Number != expected.Number) throw new InvalidOperationException("wrong_episode");
        MpvPlayer player = (MpvPlayer?)Field("_player").GetValue(window) ?? throw new InvalidOperationException("player_missing");
        await WaitForAsync(() => player.GetStatus().Loaded && player.GetStatus().FrameReady, 30_000, "first_frame");
        MpvStatus first = player.GetStatus();
        PlaybackInfo playback = (PlaybackInfo?)Field("_currentPlaybackInfo").GetValue(window) ?? throw new InvalidOperationException("playback_missing");
        if (first.VideoWidth <= 0 || first.VideoHeight <= 0 || first.Duration <= 0) throw new InvalidOperationException("video_metadata_missing");
        if ((string)Field("_quality").GetValue(window)! != "best") throw new InvalidOperationException("not_best_selector");
        PlaybackQuality highest = playback.AvailableQualities.OrderBy(item => Math.Min(item.Width, item.Height)).ThenBy(item => item.Bitrate).Last();
        if (Math.Min(playback.Width, playback.Height) != Math.Min(highest.Width, highest.Height) || playback.Bitrate != highest.Bitrate)
            throw new InvalidOperationException("not_highest_available");
        await WaitForAsync(() => player.GetStatus().Position >= first.Position + 0.6, 10_000, "progress");
        MpvStatus advanced = player.GetStatus();
        Results.Add(new
        {
            name = route, passed = true, episode = current.Number, episodeId = current.Id,
            frameReady = first.FrameReady, width = first.VideoWidth, height = first.VideoHeight,
            definition = playback.Definition, bitrate = playback.Bitrate, sourceWidth = playback.Width,
            sourceHeight = playback.Height, qualitySelector = "best", highestAvailable = true,
            mediaKeyPresent = !string.IsNullOrWhiteSpace(playback.Key),
            duration = first.Duration, positionFrom = first.Position, positionTo = advanced.Position
        });
        Console.WriteLine($"PASS route={route} episode={current.Number} id={current.Id} frame_ready={first.FrameReady} decoded={first.VideoWidth}x{first.VideoHeight} bitrate={playback.Bitrate} progress={first.Position:F2}->{advanced.Position:F2}");
    }

    private static async Task VerifyAutoNextAsync(MainWindow window, EpisodeItem current, EpisodeItem[] episodes)
    {
        EpisodeItem next = episodes.First(item => item.Playable && item.Number > current.Number);
        await WaitForAsync(() => Field("_standbyPlayer").GetValue(window) is MpvPlayer standby && standby.GetStatus().FrameReady, 35_000, "preload_frame");
        MpvPlayer player = (MpvPlayer)Field("_player").GetValue(window)!;
        player.Seek(Math.Max(0, player.GetStatus().Duration - 1.2));
        player.SetPaused(false);
        await WaitForAsync(() => Field("_currentEpisode").GetValue(window) is EpisodeItem item && item.Id == next.Id && !(bool)Field("_playbackBusy").GetValue(window)!, 35_000, "auto_next");
        await VerifyCurrentAsync(window, next, "natural_eof_after_seek");
    }

    private static void RecordFailure(string name, int episode, Exception error, MainWindow? window)
    {
        if (error is TargetInvocationException { InnerException: not null } invocation) error = invocation.InnerException!;
        string? code = error.GetType().GetProperty("Code", PrivateInstance | BindingFlags.Public)?.GetValue(error) as string;
        if (code == null || !Regex.IsMatch(code, "^[A-Z_]{1,50}$")) code = "UNCLASSIFIED";
        string stage = error.Message;
        if (!Regex.IsMatch(stage, "^[a-z_]{1,50}$")) stage = name;
        EpisodeItem? actual = window == null ? null : Field("_currentEpisode").GetValue(window) as EpisodeItem;
        Results.Add(new { name, passed = false, episode, actualEpisode = actual?.Number, actualEpisodeId = actual?.Id, stage, code, type = error.GetType().Name });
        Failures++;
        Console.WriteLine($"FAIL name={name} episode={episode} actual_episode={actual?.Number} code={code} stage={stage} type={error.GetType().Name}");
    }

    private static FieldInfo Field(string name) => typeof(MainWindow).GetField(name, PrivateInstance) ?? throw new MissingFieldException(name);
    private static object? Invoke(MainWindow window, string name, params object[] args) =>
        (typeof(MainWindow).GetMethod(name, PrivateInstance) ?? throw new MissingMethodException(name)).Invoke(window, args);
    private static T Control<T>(MainWindow window, string name) where T : FrameworkElement => (T)window.FindName(name);

    private static async Task WaitForAsync(Func<bool> predicate, int timeoutMs, string stage)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(stage);
            await Task.Delay(80);
        }
    }

    private static void LoadProductionStyles(Application app)
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
}
