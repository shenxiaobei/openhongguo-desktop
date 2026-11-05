using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Results = new();
    private static MainWindow Window = null!;
    private static int Failures;
    private static readonly string Sample = Path.GetFullPath("../mpv-smoke/sample.mp4");
    private static string CaseFilter = "";
    private static readonly string[] Cases =
    [
        "content_unavailable_skips_only_one_real_candidate", "slow_detail_stays_single_inflight_after_debounce",
        "playback_content_error_skips_candidate_then_decodes", "navigation_cancels_pending_candidate_without_reopening",
        "channel_change_clears_queue_cursor_and_source", "candidate_error_stops_verification_required",
        "candidate_error_stops_rate_limited", "candidate_error_stops_device_rejected", "candidate_error_stops_network_error",
        "recommend_failure_preserves_home_and_error_code", "candidate_failure_from_player_shows_consistent_failure_view",
        "recommend_request_error_preserves_existing_player", "catalog_fallback_source_label_is_visible",
        "unavailable_candidates_are_bounded", "retry_after_bound_continues_untried_local_queue",
        "season_natural_eof_uses_real_adjacent_id_before_feed", "season_auto_next_off_then_on", "season-unreleased",
        "season-unavailable-episode", "season_error_stops_without_unrelated_recommendation", "season_old_response_ignored_after_navigation",
        "season_switch_cancels_old_series_response", "season_rejects_mismatched_series", "season_rejects_mismatched_album", "season_rejects_mismatched_index", "season_rejects_mismatched_current_index"
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fixture") return RunFixtureAsync(args[1], args[2]).GetAwaiter().GetResult();
        Directory.CreateDirectory("results");
        if (args.FirstOrDefault() != "--case") return RunCases();
        CaseFilter = args[1];
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), resources.Elements());
        using (var reader = dictionary.CreateReader()) app.Resources = (ResourceDictionary)XamlReader.Load(reader);
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { error = error.Message, type = error.GetType().Name }); }
            finally
            {
                File.WriteAllText("results/case-" + CaseFilter + ".json", JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures = Failures, tests = Results }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static int RunCases()
    {
        // 每项使用独立 WPF Dispatcher 进程，避免宿主输入法在批量重建窗口后残留 COM 状态。
        var tests = new List<JsonElement>();
        int failures = 0;
        foreach (string name in Cases)
        {
            string report = "results/case-" + name + ".json";
            if (File.Exists(report)) File.Delete(report);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--case");
            start.ArgumentList.Add(name);
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45_000)) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Console.Write(stdout.GetAwaiter().GetResult());
            string error = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(report))
            {
                failures++;
                using JsonDocument failed = JsonDocument.Parse(JsonSerializer.Serialize(new { name, passed = false, detail = new { process.ExitCode, error } }));
                tests.Add(failed.RootElement.Clone());
                Console.WriteLine("FAIL " + name + " process=" + process.ExitCode);
                continue;
            }
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
            foreach (JsonElement test in document.RootElement.GetProperty("tests").EnumerateArray()) tests.Add(test.Clone());
            failures += document.RootElement.GetProperty("failures").GetInt32();
        }
        File.WriteAllText("results/feed-regression.json", JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures, tests }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        await Check("content_unavailable_skips_only_one_real_candidate", async () =>
        {
            using var fixture = new Fixture("skip");
            await Call("MoveRecommendationAsync", 1);
            await WaitAsync(() => Field<MpvPlayer?>("_player")?.GetStatus().FrameReady == true, "fixture media did not decode");
            Field<MpvPlayer>("_player").SetPaused(true);
            JsonElement stats = await BackendCall("fixture_stats");
            Require(stats.GetProperty("seriesIds").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "1001", "1002" }), "candidate skip sequence");
            Require(Field<SeriesItem>("_currentSeries").Id == "1002" && Field<bool>("_feedMode") && !Field<bool>("_feedBusy"), "feed did not commit successful candidate");
            Require(Control<TextBlock>("FeedHint").Text.Contains("热门目录（推荐暂不可用）", StringComparison.Ordinal)
                && Control<TextBlock>("PlayingSubtitle").Text.Contains("热门目录（推荐暂不可用）", StringComparison.Ordinal), "fallback source not visible");
            return new { seriesCalls = stats.GetProperty("seriesCalls").GetInt32(), decoded = true, correctSourceLabel = true };
        });
        await Check("slow_detail_stays_single_inflight_after_debounce", async () =>
        {
            using var fixture = new Fixture("delay");
            Task first = Call("MoveRecommendationAsync", 1);
            await WaitAsync(() => Control<FrameworkElement>("PlayerScreen").Visibility == Visibility.Visible, "detail did not start");
            await Task.Delay(800);
            Require(Field<bool>("_feedBusy"), "detail opening released feed guard");
            await Call("MoveRecommendationAsync", 1);
            await first;
            JsonElement stats = await BackendCall("fixture_stats");
            Require(stats.GetProperty("seriesCalls").GetInt32() == 1 && stats.GetProperty("maxSeriesInFlight").GetInt32() == 1, "duplicate inflight candidate");
            return new { detailDelayMs = 1400, secondClickAfterMs = 800, seriesCalls = 1, maxSeriesInFlight = 1 };
        });
        await Check("playback_content_error_skips_candidate_then_decodes", async () =>
        {
            using var fixture = new Fixture("playback:CONTENT_UNAVAILABLE");
            await Call("MoveRecommendationAsync", 1);
            await WaitAsync(() => Field<MpvPlayer?>("_player")?.GetStatus().FrameReady == true, "second candidate did not decode after playback error");
            Field<MpvPlayer>("_player").SetPaused(true);
            JsonElement stats = await BackendCall("fixture_stats");
            Require(Field<SeriesItem>("_currentSeries").Id == "1002" && stats.GetProperty("seriesCalls").GetInt32() == 2, "playback error was not classified as candidate content failure");
            return new { seriesCalls = 2, decoded = true, candidate = "1002" };
        });
        await Check("navigation_cancels_pending_candidate_without_reopening", async () =>
        {
            using var fixture = new Fixture("delay");
            Task opening = Call("MoveRecommendationAsync", 1);
            await WaitAsync(() => Control<FrameworkElement>("PlayerScreen").Visibility == Visibility.Visible, "detail did not start");
            Control<Button>("DiscoverNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await opening;
            await WaitAsync(() => !Field<bool>("_catalogBusy"), "navigation catalog incomplete");
            await Task.Delay(1500);
            Require(Control<FrameworkElement>("CatalogScreen").Visibility == Visibility.Visible && !Field<bool>("_feedMode")
                && !Field<bool>("_feedBusy") && Field<EpisodeItem?>("_currentEpisode") == null, "stale candidate reopened after navigation");
            return new { catalogVisible = true, feedMode = false, lateCandidateIgnored = true };
        });
        await Check("channel_change_clears_queue_cursor_and_source", async () =>
        {
            using var fixture = new Fixture("channel");
            await Call("MoveRecommendationAsync", 1);
            await Call("StopCurrentAsync");
            Set("_feedMode", false);
            Invoke("ShowScreen", Control<FrameworkElement>("CatalogScreen"));
            var channelButton = new Button { Tag = "漫剧" };
            Invoke("Channel_Click", channelButton, new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !Field<bool>("_catalogBusy"), "changed channel catalog incomplete");
            Require(Field<List<SeriesItem>>("_feedQueue").Count == 0 && Field<string>("_feedCursor") == "" && Field<string>("_feedSourceLabel") == "", "old recommendation context survived channel change");
            await Call("MoveRecommendationAsync", 1);
            JsonElement stats = await BackendCall("fixture_stats");
            JsonElement[] requests = stats.GetProperty("recommendRequests").EnumerateArray().ToArray();
            Require(requests.Length == 2 && requests[1].GetProperty("channel").GetString() == "漫剧" && requests[1].GetProperty("cursor").GetString() == "", "new channel reused old cursor");
            Require(Field<List<SeriesItem>>("_feedQueue").All(item => item.Id.StartsWith("2", StringComparison.Ordinal)), "new channel retained old queue");
            return new { requestChannels = requests.Select(item => item.GetProperty("channel").GetString()), newCursorEmpty = true, queueReplaced = true };
        });
        foreach (string code in new[] { "VERIFICATION_REQUIRED", "RATE_LIMITED", "DEVICE_REJECTED", "NETWORK_ERROR" })
        {
            await Check("candidate_error_stops_" + code.ToLowerInvariant(), async () =>
            {
                using var fixture = new Fixture("candidate:" + code);
                string title = Control<TextBlock>("PageTitle").Text;
                await Call("MoveRecommendationAsync", 1);
                JsonElement stats = await BackendCall("fixture_stats");
                Require(stats.GetProperty("seriesCalls").GetInt32() == 1, "protected error skipped candidate");
                Require(Control<FrameworkElement>("CatalogScreen").Visibility == Visibility.Visible && !Field<bool>("_feedMode")
                    && !Field<bool>("_feedBusy") && Control<TextBlock>("PageTitle").Text == title, "failure did not restore prior navigation");
                Require(Control<TextBlock>("NoticeText").Text == "fixture " + code, "original error not visible");
                return new { code, seriesCalls = 1, restoredScreenTitleMode = true, originalErrorVisible = true };
            });
        }
        await Check("recommend_failure_preserves_home_and_error_code", async () =>
        {
            using var fixture = new Fixture("request:ANONYMOUS_LIMITED");
            Control<Button>("FeedNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !Field<bool>("_feedBusy"), "recommend error did not settle");
            Require(!Field<bool>("_feedMode") && Control<TextBlock>("PageTitle").Text == "发现好剧"
                && Control<FrameworkElement>("CatalogScreen").Visibility == Visibility.Visible, "failed recommendation changed home mode");
            string? preservedCode = null;
            try { await BackendCall("fixture_failure", new { code = "NETWORK_ERROR" }); }
            catch (IOException error) { preservedCode = (string?)error.GetType().GetProperty("Code", PrivateInstance)?.GetValue(error); }
            Require(preservedCode == "NETWORK_ERROR", "backend exception discarded code");
            return new { preservedCode, homePreserved = true, originalError = Control<TextBlock>("NoticeText").Text };
        });
        await Check("candidate_failure_from_player_shows_consistent_failure_view", async () =>
        {
            using var fixture = new Fixture("candidate:NETWORK_ERROR");
            await Call("OpenSeriesAsync", "9001", true, 0);
            await WaitAsync(() => Field<MpvPlayer?>("_player")?.GetStatus().FrameReady == true, "original player did not decode");
            Field<MpvPlayer>("_player").SetPaused(true);
            string originalTitle = Control<TextBlock>("PlayingTitle").Text;
            await Call("MoveRecommendationAsync", 1);
            Require(Control<FrameworkElement>("PlayerScreen").Visibility == Visibility.Visible && !Field<bool>("_feedMode"), "player failure navigation state");
            Require(Control<FrameworkElement>("VideoPlaceholder").Visibility == Visibility.Visible && !Field<MpvPlayer>("_player").GetStatus().Loaded
                && Control<TextBlock>("PlayingTitle").Text != originalTitle && Control<TextBlock>("DetailTitle").Text == Control<TextBlock>("PlayingTitle").Text,
                "failure view claimed restoration of previous playback");
            Require(Control<TextBlock>("PlayingSubtitle").Text.Contains("没有成功加载", StringComparison.Ordinal), "failure placeholder not explicit");
            return new { playerScreen = true, oldPlaybackClaimed = false, consistentTitle = Control<TextBlock>("PlayingTitle").Text, failedPlaceholder = true };
        });
        await Check("recommend_request_error_preserves_existing_player", async () =>
        {
            using var fixture = new Fixture("request:NETWORK_ERROR");
            await Call("OpenSeriesAsync", "9001", true, 0);
            await WaitAsync(() => Field<MpvPlayer?>("_player")?.GetStatus().FrameReady == true, "original player did not decode");
            MpvPlayer player = Field<MpvPlayer>("_player");
            player.SetPaused(true);
            string title = Control<TextBlock>("PlayingTitle").Text;
            await Call("MoveRecommendationAsync", 1);
            Require(ReferenceEquals(player, Field<MpvPlayer>("_player")) && player.GetStatus().Loaded && Field<SeriesItem>("_currentSeries").Id == "9001"
                && Control<TextBlock>("PlayingTitle").Text == title && !Field<bool>("_feedMode"), "request error disturbed existing playback");
            return new { sameDecoder = true, sameSeries = true, sameTitle = true, feedMode = false };
        });
        await Check("catalog_fallback_source_label_is_visible", async () =>
        {
            using var fixture = new Fixture("channel");
            await Call("LoadCatalogAsync", false);
            Require(Control<TextBlock>("CatalogStatus").Text.Contains("热门目录（推荐暂不可用）", StringComparison.Ordinal), "catalog fallback source not visible");
            return new { sourceVisible = true };
        });
        await Check("unavailable_candidates_are_bounded", async () =>
        {
            using var fixture = new Fixture("bound");
            await Call("MoveRecommendationAsync", 1);
            JsonElement stats = await BackendCall("fixture_stats");
            Require(stats.GetProperty("seriesCalls").GetInt32() == 5 && stats.GetProperty("recommendCalls").GetInt32() == 1, "candidate attempts not bounded");
            Require(!Field<bool>("_feedBusy") && !Field<bool>("_feedMode"), "bounded failure left feed active");
            return new { seriesCalls = 5, recommendCalls = 1, bounded = true };
        });
        await Check("retry_after_bound_continues_untried_local_queue", async () =>
        {
            using var fixture = new Fixture("bound-tail");
            await Call("MoveRecommendationAsync", 1);
            await Task.Delay(700);
            Func<Task> retry = Field<Func<Task>>("_retryAction");
            await retry();
            await WaitAsync(() => Field<MpvPlayer?>("_player")?.GetStatus().FrameReady == true, "remaining queue candidate not played");
            JsonElement stats = await BackendCall("fixture_stats");
            Require(Field<SeriesItem>("_currentSeries").Id == "1006" && stats.GetProperty("recommendCalls").GetInt32() == 1
                && stats.GetProperty("seriesCalls").GetInt32() == 6, "retry restarted page instead of consuming remaining candidate");
            return new { lastCandidate = "1006", seriesCalls = 6, recommendCalls = 1 };
        });
        await Check("season_natural_eof_uses_real_adjacent_id_before_feed", async () =>
        {
            using var fixture = new Fixture("season");
            await OpenSeasonAsync();
            Set("_feedMode", true);
            await NaturalEndAsync();
            await WaitAsync(() => Field<SeriesItem?>("_currentSeries")?.Id == "3002" && Field<MpvPlayer>("_player").GetStatus().FrameReady, "next season did not decode");
            Field<MpvPlayer>("_player").SetPaused(true);
            JsonElement stats = await BackendCall("fixture_stats");
            Require(stats.GetProperty("recommendCalls").GetInt32() == 0 && Field<bool>("_feedMode") && Field<EpisodeItem>("_currentEpisode").Number == 1, "season did not precede unrelated recommendation");
            return new { from = "3001", next = "3002", firstEpisode = 1, recommendCalls = 0, feedModePreserved = true };
        });
        await Check("season_auto_next_off_then_on", async () =>
        {
            using var fixture = new Fixture("season");
            await OpenSeasonAsync();
            Control<CheckBox>("AutoNextCheck").IsChecked = false;
            await NaturalEndAsync();
            Require(Field<SeriesItem>("_currentSeries").Id == "3001", "disabled auto-next changed season");
            Control<CheckBox>("AutoNextCheck").IsChecked = true;
            await Call("PlayEpisodeAsync", Field<EpisodeItem>("_currentEpisode"), false);
            await WaitAsync(() => Field<MpvPlayer>("_player").GetStatus().FrameReady, "replay not ready");
            await NaturalEndAsync();
            await WaitAsync(() => Field<SeriesItem?>("_currentSeries")?.Id == "3002", "reenabled auto-next did not change season");
            return new { offStayed = true, onAdvanced = true };
        });
        foreach (string scenario in new[] { "season-unreleased", "season-unavailable-episode" })
        {
            await Check(scenario, async () =>
            {
                using var fixture = new Fixture(scenario);
                await OpenSeasonAsync();
                Set("_feedMode", true);
                await NaturalEndAsync();
                await Task.Delay(100);
                JsonElement stats = await BackendCall("fixture_stats");
                Require(Field<SeriesItem>("_currentSeries").Id == "3001" && stats.GetProperty("recommendCalls").GetInt32() == 0, "unreleased season or episode was skipped");
                return new { current = "3001", recommendCalls = 0, status = Control<TextBlock>("PlayingSubtitle").Text };
            });
        }
        await Check("season_error_stops_without_unrelated_recommendation", async () =>
        {
            using var fixture = new Fixture("season-error");
            await OpenSeasonAsync();
            Set("_feedMode", true);
            await NaturalEndAsync();
            await Task.Delay(100);
            JsonElement stats = await BackendCall("fixture_stats");
            Require(Field<SeriesItem>("_currentSeries").Id == "3001" && stats.GetProperty("recommendCalls").GetInt32() == 0
                && Control<TextBlock>("NoticeText").Text == "fixture NETWORK_ERROR", "season error was hidden or changed feed");
            return new { originalErrorVisible = true, recommendCalls = 0, current = "3001" };
        });
        await Check("season_old_response_ignored_after_navigation", async () =>
        {
            using var fixture = new Fixture("season-delay");
            await OpenSeasonAsync();
            object request = Field<object>("_seasonsRequest");
            Task old = Field<Task>("_seasonsTask");
            Control<Button>("DiscoverNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await old;
            await Task.Delay(1500);
            Require(Control<FrameworkElement>("CatalogScreen").Visibility == Visibility.Visible && Field<SeriesItem?>("_currentSeries") == null
                && Field<Task?>("_seasonsTask") == null, "old season response survived navigation");
            return new { cancelled = true, lateSeasonIgnored = true, catalogVisible = true };
        });
        await Check("season_switch_cancels_old_series_response", async () =>
        {
            using var fixture = new Fixture("season-delay");
            await OpenSeasonAsync();
            await Call("OpenSeriesAsync", "9001", true, 0);
            await Task.Delay(1500);
            SeasonPage? page = await Field<Task<SeasonPage?>>("_seasonsTask");
            Require(Field<SeriesItem>("_currentSeries").Id == "9001" && Field<string>("_seasonsSeriesId") == "9001"
                && (page == null || page.SeriesId == "9001"), "old season info replaced current story");
            return new { current = "9001", oldResponseIgnored = true };
        });
        foreach (string mismatch in new[] { "series", "album", "index", "current_index" })
        {
            await Check("season_rejects_mismatched_" + mismatch, async () =>
            {
                using var fixture = new Fixture("season-mismatch:" + mismatch);
                await OpenSeasonAsync();
                Set("_feedMode", true);
                await NaturalEndAsync();
                JsonElement stats = await BackendCall("fixture_stats");
                Require(Field<SeriesItem>("_currentSeries").Id == "3001" && stats.GetProperty("seriesCalls").GetInt32() == 1
                    && stats.GetProperty("recommendCalls").GetInt32() == 0, "mismatched season response was followed");
                return new { mismatch, current = "3001", wrongSeasonOpened = false };
            });
        }
    }

    private static async Task OpenSeasonAsync()
    {
        Control<CheckBox>("AutoNextCheck").IsChecked = true;
        await Call("OpenSeriesAsync", "3001", true, 0);
        await WaitAsync(() => Field<MpvPlayer?>("_player")?.GetStatus().FrameReady == true, "season video not ready");
        Field<MpvPlayer>("_player").SetPaused(true);
    }

    private static async Task NaturalEndAsync()
    {
        MpvPlayer player = Field<MpvPlayer>("_player");
        player.SetPaused(false);
        player.Seek(Math.Max(0, player.GetStatus().Duration - 0.25));
        await WaitAsync(() => player.LastEndTimestamp > 0, "natural EOF missing");
        await Task.Delay(100);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly object _backend;
        public Fixture(string scenario)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            start.ArgumentList.Add("--fixture");
            start.ArgumentList.Add(scenario);
            start.ArgumentList.Add(Sample);
            Process process = Process.Start(start)!;
            _ = process.StandardError.ReadToEndAsync();
            Type backendType = typeof(MainWindow).Assembly.GetType("Hongguo.Desktop.BackendClient")!;
            // 测试仅替换自有 stdio 进程，保持生产 JSONL 解码、异常和取消路径。
            _backend = RuntimeHelpers.GetUninitializedObject(backendType);
            backendType.GetField("process", PrivateInstance)!.SetValue(_backend, process);
            FieldInfo pending = backendType.GetField("pending", PrivateInstance)!;
            pending.SetValue(_backend, Activator.CreateInstance(pending.FieldType));
            backendType.GetField("writeLock", PrivateInstance)!.SetValue(_backend, new SemaphoreSlim(1, 1));
            _ = (Task)backendType.GetMethod("ReadResponsesAsync", PrivateInstance)!.Invoke(_backend, null)!;
            Window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = 40, Opacity = 0 };
            InputMethod.SetIsInputMethodEnabled(Window, false);
            InputMethod.SetIsInputMethodSuspended(Window, true);
            var loaded = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window, typeof(MainWindow).GetMethod("Window_Loaded", PrivateInstance)!);
            Window.Loaded -= loaded;
            Set("_backend", _backend);
            Control<CheckBox>("AutoNextCheck").IsChecked = false;
            Control<Slider>("VolumeSlider").Value = 0;
            System.Windows.Application.Current.MainWindow = Window;
            Window.Show();
        }

        public void Dispose()
        {
            Set("_closing", true);
            Invoke("RestoreWindowControls");
            Invoke("CancelNextPlayback");
            Invoke("CancelSeasons");
            Invoke("CancelDiscoveryRequests");
            foreach (FieldInfo field in typeof(MainWindow).GetFields(PrivateInstance))
            {
                if (field.GetValue(Window) is DispatcherTimer timer) timer.Stop();
                if (field.GetValue(Window) is not CancellationTokenSource request) continue;
                request.Cancel();
                request.Dispose();
                field.SetValue(Window, null);
            }
            Field<MpvPlayer?>("_player")?.Dispose();
            Field<MpvPlayer?>("_standbyPlayer")?.Dispose();
            ((IDisposable)_backend).Dispose();
            Control<NativeVideoHost>("VideoHost").Dispose();
            Window.Close();
        }
    }

    private static async Task<int> RunFixtureAsync(string scenario, string sample)
    {
        object sync = new();
        var active = new ConcurrentDictionary<string, CancellationTokenSource>();
        var tasks = new List<Task>();
        var seriesIds = new List<string>();
        var recommendations = new List<object>();
        int activeSeries = 0, maxSeries = 0, recommendCalls = 0;
        void Reply(object response) { lock (sync) Console.WriteLine(JsonSerializer.Serialize(response)); }
        async Task Handle(string id, string method, JsonElement arguments, CancellationToken token)
        {
            try
            {
                object result;
                if (method == "recommend")
                {
                    string channel = arguments.GetProperty("channel").GetString() ?? "";
                    string cursor = arguments.GetProperty("cursor").GetString() ?? "";
                    lock (sync) { recommendCalls++; recommendations.Add(new { channel, cursor }); }
                    if (scenario.StartsWith("request:", StringComparison.Ordinal))
                    { string code = scenario[8..]; Reply(new { id, error = new { code, message = "fixture " + code } }); return; }
                    int first = channel == "漫剧" ? 2001 : 1001;
                    result = new { items = Enumerable.Range(first, scenario.StartsWith("bound", StringComparison.Ordinal) ? 8 : 3).Select(value => new { id = value.ToString(), title = "本地候选 " + value, episode_count = 1 }), cursor = scenario == "bound-tail" ? "" : "cursor:" + channel, has_more = scenario != "bound-tail", source = "fixture", source_label = "热门目录（推荐暂不可用）", fallback_reason = "fixture" };
                }
                else if (method == "series")
                {
                    string series = arguments.GetProperty("id").GetString()!;
                    lock (sync) { seriesIds.Add(series); activeSeries++; maxSeries = Math.Max(maxSeries, activeSeries); }
                    try
                    {
                        if (scenario == "delay") await Task.Delay(1400, token);
                        string? code = scenario.StartsWith("candidate:", StringComparison.Ordinal) && series.StartsWith("1", StringComparison.Ordinal) ? scenario[10..]
                            : scenario == "bound" || scenario == "bound-tail" && int.Parse(series) < 1006 || scenario == "skip" && series == "1001" ? "CONTENT_UNAVAILABLE" : null;
                        if (code != null) { Reply(new { id, error = new { code, message = "fixture " + code } }); return; }
                        object[] episodes = scenario == "season-unavailable-episode" && series == "3001"
                            ? new object[] { new { id = series + "01", series_id = series, number = 1, playable = true }, new { id = series + "02", series_id = series, number = 2, playable = false } }
                            : new object[] { new { id = series + "01", series_id = series, number = 1, playable = true } };
                        result = new { series = new { id = series, title = "本地候选 " + series, episode_count = episodes.Length, is_multi_season = series.StartsWith("3", StringComparison.Ordinal), related_album_id = series.StartsWith("3", StringComparison.Ordinal) ? "fixture-album" : "", season_index = series == "3001" ? 1 : series == "3002" ? 2 : 0 }, episodes };
                    }
                    finally { lock (sync) activeSeries--; }
                }
                else if (method == "playback")
                {
                    string episode = arguments.GetProperty("episode_id").GetString()!;
                    if (scenario.StartsWith("playback:", StringComparison.Ordinal) && episode.StartsWith("1001", StringComparison.Ordinal))
                    { string code = scenario[9..]; Reply(new { id, error = new { code, message = "fixture " + code } }); return; }
                    result = new { url = sample, duration_seconds = 6, expires_at = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), definition = "local" };
                }
                else if (method == "fixture_stats") { lock (sync) result = new { seriesCalls = seriesIds.Count, seriesIds = seriesIds.ToArray(), maxSeriesInFlight = maxSeries, recommendCalls, recommendRequests = recommendations.ToArray() }; }
                else if (method == "fixture_failure") { string code = arguments.GetProperty("code").GetString()!; Reply(new { id, error = new { code, message = "fixture " + code } }); return; }
                else if (method == "seasons")
                {
                    string series = arguments.GetProperty("id").GetString()!;
                    if (scenario == "season-delay" && series == "3001") await Task.Delay(1400); // 刻意忽略取消，验证前端旧响应隔离。
                    if (scenario == "season-error") { Reply(new { id, error = new { code = "NETWORK_ERROR", message = "fixture NETWORK_ERROR" } }); return; }
                    bool linked = series.StartsWith("3", StringComparison.Ordinal);
                    bool next = series == "3001" && scenario != "season-unreleased";
                    result = new { series_id = scenario == "season-mismatch:series" ? "9999" : series,
                        album_id = scenario == "season-mismatch:album" ? "different-album" : linked ? "fixture-album" : "",
                        link_verified = linked, current_index = scenario == "season-mismatch:current_index" ? 3 : linked ? int.Parse(series[^1..]) : 0,
                        next_series_id = next ? "3002" : "", next_index = scenario == "season-mismatch:index" ? 3 : next ? 2 : 0,
                        has_next = next, note = scenario == "season-unreleased" ? "下一季暂未上线。" : "本季已结束。" };
                }
                else if (method == "history") result = Array.Empty<object>();
                else if (method == "filter_options") result = new { rows = Array.Empty<object>() };
                else if (method is "discover" or "hydrate") result = new { items = Array.Empty<object>(), cursor = "", has_more = false, source_label = "热门目录（推荐暂不可用）" };
                else result = new { };
                token.ThrowIfCancellationRequested();
                Reply(new { id, result });
            }
            catch (OperationCanceledException) { Reply(new { id, error = new { code = "CANCELED", message = "fixture canceled" } }); }
            finally { if (active.TryRemove(id, out var request)) request.Dispose(); }
        }
        while (Console.ReadLine() is string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string id = root.GetProperty("id").GetString()!;
            string method = root.GetProperty("method").GetString()!;
            JsonElement arguments = root.TryGetProperty("args", out var args) ? args.Clone() : default;
            if (method == "cancel")
            {
                if (active.TryGetValue(arguments.GetProperty("request_id").GetString()!, out var request)) request.Cancel();
                Reply(new { id, result = new { } });
                continue;
            }
            var cancellation = new CancellationTokenSource();
            active[id] = cancellation;
            tasks.Add(Task.Run(() => Handle(id, method, arguments, cancellation.Token)));
        }
        foreach (var request in active.Values) request.Cancel();
        await Task.WhenAll(tasks);
        return 0;
    }

    private static async Task Check(string name, Func<Task<object>> test)
    {
        if (name != CaseFilter) return;
        try { Record(name, true, await test()); }
        catch (Exception error) { Record(name, false, new { error = error.Message, type = error.GetType().Name }); }
    }
    private static void Record(string name, bool passed, object detail)
    { if (!passed) Failures++; Results.Add(new { name, passed, detail }); Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail)); }
    private static async Task WaitAsync(Func<bool> condition, string label)
    { var timer = Stopwatch.StartNew(); while (!condition()) { if (timer.ElapsedMilliseconds > 12_000) throw new TimeoutException(label); await Task.Delay(20); } }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(Window) ?? default(T)!);
    private static void Set(string name, object value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(Window, value);
    private static T Control<T>(string name) where T : class => (T)Window.FindName(name)!;
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(Window, args);
    private static Task Call(string name, params object?[] args) => (Task)Invoke(name, args)!;
    private static Task<JsonElement> BackendCall(string method, object? args = null)
    { object backend = Field<object>("_backend"); return (Task<JsonElement>)backend.GetType().GetMethod("CallAsync", PrivateInstance)!.Invoke(backend, new object?[] { method, args, CancellationToken.None })!; }
}
