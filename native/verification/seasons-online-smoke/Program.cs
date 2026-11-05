using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string FirstSeason = "7675400266065136665";
    private const string SecondSeason = "7680524320346475582";
    private const string Album = "7679071746556693528";
    private static readonly List<object> Results = new();
    private static MainWindow Window = null!;
    private static RequestRecorder Recorder = null!;
    private static string State = "";
    private static int Failures;
    private static MpvPlayer Player => Field<MpvPlayer>("_player");
    private static EpisodeItem Current => Field<EpisodeItem>("_currentEpisode");
    private static SeriesItem Series => Field<SeriesItem>("_currentSeries");

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"));
        XElement resources = source.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), resources.Elements());
        using (var reader = dictionary.CreateReader()) app.Resources = (ResourceDictionary)XamlReader.Load(reader);
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { error = Clean(error.Message), type = error.GetType().Name }); }
            finally
            {
                if (Window != null)
                {
                    Set("_closing", true);
                    Field<DispatcherTimer>("_playerTimer").Stop();
                    Invoke("CancelSeasons");
                    Invoke("CancelNextPlayback");
                    Invoke("CancelDiscoveryRequests");
                    Field<MpvPlayer?>("_player")?.Dispose();
                    Field<MpvPlayer?>("_standbyPlayer")?.Dispose();
                    (Field<object?>("_backend") as IDisposable)?.Dispose();
                    Window.Close();
                }
                File.WriteAllText("results/seasons-online-smoke.json", JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now,
                    stateDirectory = State,
                    failures = Failures,
                    tests = Results,
                    requests = Recorder?.Snapshot()
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync()
    {
        State = Path.GetFullPath(Path.Combine("state", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(State);
        string anonymous = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openhongguo", "anonymous.json");
        if (File.Exists(anonymous)) File.Copy(anonymous, Path.Combine(State, "anonymous.json"));
        Window = new MainWindow
        {
            ShowInTaskbar = false, ShowActivated = false, Width = 1380, Height = 900,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = 40, Opacity = 0
        };
        Set("_configDir", State);
        System.Windows.Application.Current.MainWindow = Window;
        Window.Show();
        Control<Slider>("VolumeSlider").Value = 0;
        Control<CheckBox>("AutoNextCheck").IsChecked = false;
        await WaitAsync(() => Field<object?>("_backend") != null && !Field<bool>("_catalogBusy"), "backend startup", 40_000);
        Invoke("CancelDiscoveryRequests");
        InstallRecorder();
        string feedCursor = Field<string>("_feedCursor");
        int feedIndex = Field<int>("_feedIndex");
        int feedCount = Field<IEnumerable<SeriesItem>>("_feedQueue").Count();
        Require((await HistoryAsync()).Count == 0, "fresh isolated state unexpectedly contains history");

        EpisodeItem last = null!;
        SeasonPage page = null!;
        bool opened = await Check("official_seasons_and_real_last_episode", async () =>
        {
            await Call("OpenSeriesAsync", FirstSeason, false, 0).WaitAsync(TimeSpan.FromSeconds(45));
            Task<SeasonPage?> seasons = Field<Task<SeasonPage?>?>("_seasonsTask") ?? throw new InvalidOperationException("production seasons request missing");
            page = await seasons.WaitAsync(TimeSpan.FromSeconds(45)) ?? throw new InvalidOperationException(Clean(Field<Exception?>("_seasonsFailure")?.Message ?? "empty seasons response"));
            Require(Series.Id == FirstSeason && Series.RelatedAlbumId == Album && page.SeriesId == FirstSeason && page.AlbumId == Album, "current series/album mismatch");
            Require(page.LinkVerified && page.HasNext && page.CurrentIndex == 1 && page.NextIndex == 2 && page.NextSeriesId == SecondSeason, "official adjacent-season relation not confirmed");
            EpisodeItem[] episodes = Field<IEnumerable<EpisodeItem>>("_episodes").ToArray();
            last = episodes.Where(item => item.Playable).OrderBy(item => item.Number).Last();
            Require(last.Number == 152 && episodes.Max(item => item.Number) == last.Number, "sample final playable episode changed");
            await Call("PlayEpisodeAsync", last, false).WaitAsync(TimeSpan.FromSeconds(45));
            await WaitAsync(() => Player.GetStatus().FrameReady && Player.GetStatus().Duration > 1, "last episode decoded frame", 30_000);
            Player.SetPaused(true);
            return new { seriesId = Series.Id, page.AlbumId, page.LinkVerified, page.CurrentIndex, page.NextIndex, page.NextSeriesId, lastEpisode = last.Number, playableEpisodes = episodes.Count(item => item.Playable), Player.GetStatus().FrameReady };
        });
        if (!opened) return;

        await Check("autonext_off_natural_eof_stays_in_first_season", async () =>
        {
            long previousEnd = Player.LastEndTimestamp;
            Player.SetPaused(false);
            Player.Seek(Player.GetStatus().Duration - 0.65);
            await WaitAsync(() => Player.LastEndTimestamp > previousEnd && Field<bool>("_episodeCompleted"), "natural EOF with autonext off", 30_000, 5);
            await Task.Delay(250);
            Require(Series.Id == FirstSeason && Current.Id == last.Id && !Field<bool>("_feedMode"), "disabled autonext changed series or episode");
            HistoryItem history = (await HistoryAsync()).Single(item => item.SeriesId == FirstSeason);
            Require(history.Episode == 152 && history.Finished && !Recorder.Snapshot().Any(item => item.Method == "series" && item.Target == SecondSeason), "disabled EOF did not persist completion or opened next season");
            return new { seriesId = Series.Id, episode = Current.Number, naturalEndTimestamp = Player.LastEndTimestamp, history.Finished, history.Position, nextSeasonOpened = false };
        });

        bool transitioned = await Check("autonext_on_natural_eof_to_second_season_first_frame_and_history", async () =>
        {
            Control<CheckBox>("AutoNextCheck").IsChecked = true;
            await Call("PlayEpisodeAsync", last, false).WaitAsync(TimeSpan.FromSeconds(45));
            await WaitAsync(() => Player.GetStatus().FrameReady && Player.GetStatus().Duration > 1 && Player.GetStatus().Position < 2, "last episode replay frame", 30_000);
            long eofTimestamp = 0;
            int endEvents = 0;
            MpvPlayer oldPlayer = Player;
            EventHandler ended = (_, _) => { eofTimestamp = oldPlayer.LastEndTimestamp; endEvents++; };
            oldPlayer.Ended += ended;
            try
            {
                Player.SetPaused(false);
                Player.Seek(Player.GetStatus().Duration - 0.65);
                await WaitAsync(() => Field<SeriesItem?>("_currentSeries")?.Id == SecondSeason && Field<EpisodeItem?>("_currentEpisode") != null && Player.GetStatus().FrameReady, "natural EOF to second season first frame", 60_000, 5);
                double frameMs = ElapsedMs(eofTimestamp);
                await WaitAsync(() => Player.GetStatus().Position > 0.05, "second season progressing", 10_000, 5);
                double progressionMs = ElapsedMs(eofTimestamp);
                EpisodeItem first = Field<IEnumerable<EpisodeItem>>("_episodes").Where(item => item.Playable).OrderBy(item => item.Number).First();
                Require(eofTimestamp > 0 && endEvents == 1 && Series.RelatedAlbumId == Album && Current.Id == first.Id && Current.Number == 1, "cross-season transition did not reach the actual first playable episode");
                Player.SetPaused(true);
                await Call("SaveHistoryAsync", false);
                List<HistoryItem> history = await HistoryAsync();
                HistoryItem previous = history.Single(item => item.SeriesId == FirstSeason);
                HistoryItem current = history.Single(item => item.SeriesId == SecondSeason);
                Require(previous.Episode == 152 && previous.Finished && current.Episode == first.Number && !current.Finished && current.Position > 0, "independent history missing finished season or newly playing first episode");
                return new
                {
                    from = FirstSeason, to = Series.Id, episodeId = Current.Id, episode = Current.Number,
                    naturalEndEvents = endEvents, Player.GetStatus().FrameReady, Player.GetStatus().Position,
                    nativeEndToFirstObservedFrameMs = Math.Round(frameMs, 2),
                    nativeEndToProgressionMs = Math.Round(progressionMs, 2), pollResolutionMs = 5,
                    latencyIsObservationOnly = true,
                    previousHistory = new { previous.SeriesId, previous.Episode, previous.Finished },
                    currentHistory = new { current.SeriesId, current.Episode, current.Finished, current.Position }
                };
            }
            finally { oldPlayer.Ended -= ended; }
        });

        await Check("stop_clears_media_seasons_and_does_not_request_unrelated_recommendations", async () =>
        {
            await Call("StopCurrentAsync");
            await Task.Delay(100);
            MpvPlayer? standby = Field<MpvPlayer?>("_standbyPlayer");
            Require(!Player.GetStatus().Loaded && (standby == null || !standby.GetStatus().Loaded) && Field<EpisodeItem?>("_currentEpisode") == null, "stop left an active media decoder");
            Require(Field<Task<SeasonPage?>?>("_seasonsTask") == null && Field<Task<PlaybackInfo?>?>("_nextPlaybackTask") == null, "stop kept season/preload requests");
            RequestItem[] calls = Recorder.Snapshot();
            Require(!calls.Any(item => item.Method is "feed" or "recommend") && Field<string>("_feedCursor") == feedCursor && Field<int>("_feedIndex") == feedIndex && Field<IEnumerable<SeriesItem>>("_feedQueue").Count() == feedCount && !Field<bool>("_feedMode"), "ordinary season playback changed recommendation state");
            Require(transitioned && calls.Count(item => item.Method == "series" && item.Target == SecondSeason) == 1, "expected exactly one second-season open");
            return new { bothDecodersStopped = true, seasonsCancelled = true, recommendationRequests = 0, recommendationStateUnchanged = true, methods = calls.GroupBy(item => item.Method).ToDictionary(group => group.Key, group => group.Count()) };
        });
    }

    private static void InstallRecorder()
    {
        object backend = Field<object>("_backend");
        Process process = (Process)backend.GetType().GetField("process", PrivateInstance)!.GetValue(backend)!;
        FieldInfo input = typeof(Process).GetField("_standardInput", PrivateInstance) ?? throw new MissingFieldException("Process._standardInput");
        Recorder = new RequestRecorder(process.StandardInput);
        // 仅代理本验证宿主自有进程的输入，真实请求仍逐字写给真实后端。
        input.SetValue(process, Recorder);
    }

    private static async Task<List<HistoryItem>> HistoryAsync()
    {
        object backend = Field<object>("_backend");
        Task<JsonElement> request = (Task<JsonElement>)backend.GetType().GetMethod("CallAsync", PrivateInstance)!.Invoke(backend, new object?[] { "history", null, CancellationToken.None })!;
        JsonElement result = await request.WaitAsync(TimeSpan.FromSeconds(20));
        return result.Deserialize<List<HistoryItem>>() ?? new List<HistoryItem>();
    }

    private static async Task<bool> Check(string name, Func<Task<object>> test)
    {
        try { Record(name, true, await test()); return true; }
        catch (Exception error) { Record(name, false, new { error = Clean(error.Message), type = error.GetType().Name }); return false; }
    }
    private static void Record(string name, bool passed, object detail)
    {
        if (!passed) Failures++;
        Results.Add(new { name, passed, detail });
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail));
    }
    private static async Task WaitAsync(Func<bool> condition, string label, int timeout, int poll = 40)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > timeout) throw new TimeoutException(label);
            await Task.Delay(poll);
        }
    }
    private static double ElapsedMs(long timestamp) => timestamp == 0 ? -1 : (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;
    private static string Clean(string value) => Regex.Replace(value, @"https?://\S+", "[URL]");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(Window) ?? default(T)!);
    private static void Set(string name, object value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(Window, value);
    private static T Control<T>(string name) where T : class => (T)Window.FindName(name)!;
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(Window, args);
    private static Task Call(string name, params object?[] args) => (Task)Invoke(name, args)!;

    private sealed record RequestItem(string Method, string Target);

    private sealed class RequestRecorder(StreamWriter destination) : StreamWriter(Stream.Null)
    {
        private readonly List<RequestItem> requests = new();
        public RequestItem[] Snapshot() { lock (requests) return requests.ToArray(); }

        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            await destination.WriteLineAsync(buffer, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(buffer.ToString());
            JsonElement root = document.RootElement;
            string method = root.GetProperty("method").GetString() ?? "";
            string target = "";
            if (root.TryGetProperty("args", out JsonElement args) && args.ValueKind == JsonValueKind.Object)
            {
                // 报告仅保留方法与对象ID，不记录媒体地址、密钥或匿名凭据。
                foreach (string key in new[] { "id", "series_id", "episode_id" })
                {
                    if (args.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    {
                        target = value.GetString() ?? "";
                        break;
                    }
                }
            }
            lock (requests) requests.Add(new RequestItem(method, target));
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) destination.Dispose();
            base.Dispose(disposing);
        }
    }
}
