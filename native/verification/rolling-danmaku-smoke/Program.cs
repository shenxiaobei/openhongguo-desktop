using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    private static List<DiscussionEntry> Items => Field<List<DiscussionEntry>>("_rollingDanmakuItems");
    private static DanmakuSettings Settings => Field<DanmakuSettings>("_danmakuSettings");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--fixture") return RunFixtureAsync().GetAwaiter().GetResult();
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
            try { await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { error = error.Message, type = error.GetType().Name }); }
            finally
            {
                if (Window != null)
                {
                    Set("_currentEpisode", null);
                    Window.Close();
                    try { await Wait(() => Closed, "测试窗口未完成关闭"); }
                    catch (Exception error) { Record("cleanup", false, new { error = error.Message }); }
                }
                if (DispatcherErrors.Count > 0) Record("dispatcher_errors", false, DispatcherErrors);
                if (StateDirectory.Length > 0 && Directory.Exists(StateDirectory) && Closed)
                    Directory.Delete(StateDirectory, true);
                File.WriteAllText("results/rolling-danmaku-smoke.json", JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, failures = Failures, tests = Results, dispatcherErrors = DispatcherErrors,
                    naturallyClosed = Closed,
                    scope = "未显示的生产MainWindow、真实BackendClient与可控假stdio后端；验证滚动弹幕的自动读取、分页、跳转、取消、代次隔离、退避、段数上限及毫秒边界，不创建播放器、不访问网络、不验证视频像素"
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync()
    {
        CreateFixture();
        await Check("closed_discussion_panel_still_fetches_rolling_danmaku", async () =>
        {
            await Configure("paged");
            Invoke("PrepareRollingDanmakuForEpisode");
            await Idle();
            JsonElement[] calls = await Calls();
            Require(calls.Length == 1 && Items.Count > 0, "讨论区未打开时没有获取滚动弹幕");
            Require(Control<Border>("DiscussionPanel").Visibility == Visibility.Collapsed && !Window.IsVisible,
                "滚动弹幕读取意外打开了讨论区或窗口");
            Require(calls[0].GetProperty("method").GetString() == "danmaku", "滚动层读取了评论预览");
            return new { panelClosed = true, requests = calls.Length, count = Items.Count };
        });

        await Check("media_position_automatically_loads_next_segment_once", async () =>
        {
            await Configure("paged");
            Invoke("PrepareRollingDanmakuForEpisode");
            await Idle();
            Tick(22);
            await Idle();
            Tick(22);
            await Idle();
            JsonElement[] calls = await Calls();
            Require(calls.Length == 2 && Cursor(calls[0]) == "" && Cursor(calls[1]) == "30000",
                "播放位置推进未自动续段，或重复请求了已读取的时间段");
            Require(Items.Any(item => item.PositionMS == 31000), "后续时间段正文没有加入滚动层");
            return new { cursors = calls.Select(Cursor).ToArray(), latestPositionMS = Items.Max(item => item.PositionMS) };
        });

        await Check("distant_seek_fetches_near_target_without_reading_intermediate_pages", async () =>
        {
            await Configure("paged", 600000);
            Invoke("PrepareRollingDanmakuForEpisode");
            await Idle();
            Tick(310.25);
            await Idle();
            Tick(310.25);
            await Idle();
            JsonElement[] calls = await Calls();
            Require(calls.Length == 3 && Cursor(calls[1]) == "270000" && Cursor(calls[2]) == "300000",
                "拖动后没有直接获取目标附近含回看窗口的时间段");
            Require(Items.Any(item => item.PositionMS == 301000), "目标附近弹幕没有就绪");
            return new { positionSeconds = 310.25, cursors = calls.Select(Cursor).ToArray() };
        });

        await Check("slow_danmaku_seek_lookback_crosses_previous_segment_boundary", async () =>
        {
            var observations = new List<object>();
            try
            {
                foreach (double position in new[] { 45.5, 46d, 48d })
                {
                    await Configure("slow-boundary");
                    Settings.Speed = "slow";
                    Set("_position", position);
                    // 冷跳转没有早期缓存，必须由生产调度主动取得仍可能在屏的前一段。
                    Invoke("PrepareRollingDanmakuForEpisode");
                    await Idle();
                    Tick(position);
                    await Idle();
                    JsonElement[] calls = await Calls();
                    string expectedFirst = position < 48 ? "" : "30000";
                    Require(calls.Length == (position < 48 ? 2 : 1) && Cursor(calls[0]) == expectedFirst
                        && Cursor(calls[^1]) == "30000", "慢速回看跨三十秒边界时漏读前段，或在完整覆盖后仍重复请求");

                    double? visibleUntil = null;
                    if (position == 45.5)
                    {
                        DiscussionEntry previous = Items.Single(item => item.PositionMS == 29900);
                        Type renderer = typeof(MainWindow).Assembly.GetType("Hongguo.Desktop.DanmakuAssRenderer")!;
                        string ass = (string)renderer.GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic)!
                            .Invoke(null, new object[] { new[] { previous }, Settings, 1280, 720, 720 })!;
                        string dialogue = ass.Split('\n').Single(line => line.StartsWith("Dialogue:", StringComparison.Ordinal));
                        string[] fields = dialogue.Split(',');
                        double starts = TimeSpan.Parse(fields[1], CultureInfo.InvariantCulture).TotalSeconds;
                        visibleUntil = TimeSpan.Parse(fields[2], CultureInfo.InvariantCulture).TotalSeconds;
                        Require(starts < 30 && starts <= position && visibleUntil > position,
                            "用于回看边界的前段弹幕在目标媒体时刻已经结束，未构成真实慢速缺段场景");
                    }
                    observations.Add(new { positionSeconds = position, cursors = calls.Select(Cursor).ToArray(), previousSegmentVisibleUntil = visibleUntil });
                }
            }
            finally { Settings.Speed = "normal"; }
            return new { coldSeekCases = observations };
        });

        await Check("disable_cancels_inflight_and_blocks_new_requests", async () =>
        {
            await Configure("slow-all");
            Invoke("PrepareRollingDanmakuForEpisode");
            await WaitAsync(async () => (await Calls()).Length == 1, "慢请求未发送");
            Settings.Enabled = false;
            Invoke("ApplyDanmakuSettings");
            await WaitAsync(async () => (await Stats()).GetProperty("cancels").GetInt32() > 0, "关闭弹幕没有取消旧请求");
            Tick(65);
            await Task.Delay(650);
            Require((await Calls()).Length == 1 && Items.Count == 0 && !Field<bool>("_rollingDanmakuBusy"),
                "关闭后继续读取或接受了迟到正文");
            return new { requests = 1, canceled = true, lateResponseIgnored = true };
        });

        await Check("reenable_reuses_cached_current_episode", async () =>
        {
            await Configure("paged");
            Invoke("PrepareRollingDanmakuForEpisode");
            await Idle();
            string[] before = Items.Select(item => item.Text).ToArray();
            Settings.Enabled = false;
            Invoke("ApplyDanmakuSettings");
            Tick(10);
            Settings.Enabled = true;
            Invoke("ApplyDanmakuSettings");
            await Idle();
            Require((await Calls()).Length == 1 && before.SequenceEqual(Items.Select(item => item.Text)),
                "重新开启未复用本集已读取的时间段");
            return new { requests = 1, preservedItems = before.Length };
        });

        await Check("late_old_episode_response_does_not_replace_new_episode", async () =>
        {
            await Configure("slow-first-episode");
            Invoke("PrepareRollingDanmakuForEpisode");
            await WaitAsync(async () => (await Calls()).Length == 1, "旧集请求未发送");
            Set("_currentEpisode", new EpisodeItem { Id = "100102", SeriesId = "1001", Number = 2 });
            Invoke("PrepareRollingDanmakuForEpisode");
            await Idle();
            await Task.Delay(650);
            Require(Items.Count > 0 && Items.All(item => item.Text.Contains("100102", StringComparison.Ordinal)),
                "旧集的迟到响应混入了当前集");
            Require(Field<string>("_rollingDanmakuEpisodeId") == "100102" && !Field<bool>("_rollingDanmakuBusy"),
                "旧集响应改变了当前集身份或繁忙状态");
            Require((await Stats()).GetProperty("cancels").GetInt32() > 0, "切集没有取消旧滚动请求");
            return new { episode = "100102", count = Items.Count, canceled = true };
        });

        await Check("failures_back_off_preserve_content_and_recover", async () =>
        {
            await Configure("second-segment-errors");
            Invoke("PrepareRollingDanmakuForEpisode");
            await Idle();
            string[] preserved = Items.Select(item => item.Text).ToArray();
            var delays = new List<double>();
            foreach (int expected in new[] { 5, 15, 60 })
            {
                Set("_rollingDanmakuRetryAt", DateTime.UtcNow.AddSeconds(-1));
                await Call("LoadRollingDanmakuAsync", 22d);
                double seconds = (Field<DateTime>("_rollingDanmakuRetryAt") - DateTime.UtcNow).TotalSeconds;
                delays.Add(seconds);
                Require(seconds > expected - 2 && seconds <= expected + 1, "读取失败没有按预期设置退避时间");
                Require(preserved.SequenceEqual(Items.Select(item => item.Text)), "续段失败清除了已读取内容");
                int requests = (await Calls()).Length;
                for (int repeat = 0; repeat < 3; repeat++) Tick(22);
                await Idle();
                Require((await Calls()).Length == requests, "退避截止前提前重试");
            }
            Set("_rollingDanmakuRetryAt", DateTime.UtcNow.AddSeconds(-1));
            await Call("LoadRollingDanmakuAsync", 22d);
            Require(Field<int>("_rollingDanmakuFailures") == 0 && Field<DateTime>("_rollingDanmakuRetryAt") == DateTime.MinValue,
                "读取成功后未清除错误退避");
            Require(Items.Any(item => item.PositionMS == 31000), "退避后的重试没有加载新正文");
            return new { delaysSeconds = delays, preservedCount = preserved.Length, recoveredCount = Items.Count };
        });

        await Check("memory_retains_at_most_twelve_segments_with_sorted_deduplicated_items", async () =>
        {
            await Configure("paged", 1200000);
            Invoke("PrepareRollingDanmakuForEpisode");
            await Idle();
            Tick(22);
            await Idle();
            Require(Items.Count(item => item.PositionMS == 30000 && item.Text == "边界-100101-30000") == 1,
                "跨页相同时间与正文因ID变化而重复");
            for (int segment = 1; segment <= 16; segment++)
            {
                Tick(segment * 60 + 14);
                await Idle();
                Require(Field<IList>("_rollingDanmakuSegments").Count <= 12, "内存时间段超过十二段");
            }
            string[] keys = Items.Select(item => item.PositionMS + "\n" + item.Text).ToArray();
            Require(keys.Distinct(StringComparer.Ordinal).Count() == keys.Length, "滚动正文未按时间与正文去重");
            Require(Items.Select(item => item.PositionMS).SequenceEqual(Items.OrderBy(item => item.PositionMS).Select(item => item.PositionMS)),
                "滚动正文没有按媒体时间排序");
            Require(Items.Count > 0 && Items.Count <= 12 * 3, "条目随观看位置无限累积");
            return new { segments = Field<IList>("_rollingDanmakuSegments").Count, items = Items.Count, requests = (await Calls()).Length };
        });

        await Check("exact_duration_keeps_subsecond_tail_and_rejects_invalid_positions", async () =>
        {
            await Configure("precise-duration", 88400);
            Set("_currentPlaybackInfo", new PlaybackInfo { DurationSeconds = 88, DurationMS = 88400 });
            Set("_duration", 88d);
            Invoke("PrepareRollingDanmakuForEpisode");
            await Idle();
            JsonElement[] calls = await Calls();
            Require(calls.Length == 1 && calls[0].GetProperty("duration_ms").GetInt64() == 88400,
                "精确毫秒时长被整数秒覆盖");
            Require(Items.Count == 1 && Items[0].PositionMS == 88250, "片尾亚秒弹幕丢失或接纳了越界、缺失位置");
            Tick(88.35);
            await Idle();
            Require((await Calls()).Length == 1, "完整结束的时间段被重复读取");
            return new { integerSeconds = 88, requestedDurationMS = 88400, retainedTailMS = Items[0].PositionMS };
        });

        await Check("distant_seek_supersedes_pending_old_position_request", async () =>
        {
            await Configure("slow-initial", 600000);
            Invoke("PrepareRollingDanmakuForEpisode");
            await WaitAsync(async () => (await Calls()).Length == 1, "慢初始请求未发送");
            Tick(310.25);
            await WaitAsync(async () => (await Calls()).Any(call => Cursor(call) == "270000"),
                "远距离跳转仍在等待旧位置请求，没有优先读取目标附近", 400);
            Require((await Stats()).GetProperty("cancels").GetInt32() > 0, "跳转后没有取消仍在等待的旧位置请求");
            await Idle();
            string[] targetItems = Items.Select(item => item.PositionMS + "\n" + item.Text).ToArray();
            await Task.Delay(850);
            Require(Items.Any(item => item.PositionMS == 271000) && Items.All(item => item.PositionMS >= 270000),
                "跳转目标时间段丢失或旧位置正文混入");
            Require(targetItems.SequenceEqual(Items.Select(item => item.PositionMS + "\n" + item.Text))
                && !Field<bool>("_rollingDanmakuBusy"), "旧位置迟到响应改写了新位置正文或繁忙状态");
            return new { targetSeconds = 310.25, targetRequestedBeforeOldResponse = true, canceled = true };
        });
    }

    private static void CreateFixture()
    {
        StateDirectory = Path.Combine(Path.GetTempPath(), "openhongguo-rolling-danmaku-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StateDirectory);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = StateDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--fixture");
        Process process = Process.Start(start)!;
        _ = process.StandardError.ReadToEndAsync();
        Type backendType = typeof(MainWindow).Assembly.GetType("Hongguo.Desktop.BackendClient")!;
        // 只替换后端进程；JSONL响应、并发和取消均使用生产客户端。
        Backend = RuntimeHelpers.GetUninitializedObject(backendType);
        backendType.GetField("process", Private)!.SetValue(Backend, process);
        FieldInfo pending = backendType.GetField("pending", Private)!;
        pending.SetValue(Backend, Activator.CreateInstance(pending.FieldType));
        backendType.GetField("writeLock", Private)!.SetValue(Backend, new SemaphoreSlim(1, 1));
        _ = (Task)backendType.GetMethod("ReadResponsesAsync", Private)!.Invoke(Backend, null)!;
        // 离屏构造不触发Loaded，不启动真实SDK或播放器。
        Window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
        Set("_configDir", StateDirectory);
        Set("_backend", Backend);
        Window.Closed += (_, _) => Closed = true;
    }

    private static async Task Configure(string scenario, long durationMS = 240000)
    {
        Invoke("ResetRollingDanmaku");
        Invoke("ResetDiscussion");
        await BackendCall("fixture_set", new { scenario });
        Settings.Enabled = true;
        Set("_currentSeries", new SeriesItem { Id = "1001", Title = "本地滚动弹幕回归" });
        Set("_currentEpisode", new EpisodeItem { Id = "100101", SeriesId = "1001", Number = 1 });
        Set("_currentPlaybackInfo", new PlaybackInfo { DurationSeconds = durationMS / 1000d, DurationMS = durationMS });
        Set("_duration", durationMS / 1000d);
        Set("_position", 0d);
        Control<Border>("DiscussionPanel").Visibility = Visibility.Collapsed;
    }

    private static void Tick(double seconds)
    {
        Set("_position", seconds);
        Invoke("UpdateRollingDanmaku", new MpvStatus { Position = seconds, Loaded = true, FrameReady = false });
    }

    private static async Task<int> RunFixtureAsync()
    {
        object gate = new();
        object outputGate = new();
        string scenario = "paged";
        int cancels = 0;
        var calls = new List<object>();
        var attempts = new Dictionary<string, int>();
        var tasks = new List<Task>();
        void Send(object message) { lock (outputGate) Console.WriteLine(JsonSerializer.Serialize(message)); }
        while (await Console.In.ReadLineAsync() is string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement request = document.RootElement;
            string id = request.GetProperty("id").GetString()!;
            string method = request.GetProperty("method").GetString()!;
            JsonElement args = request.TryGetProperty("args", out JsonElement value) ? value.Clone() : default;
            if (method == "fixture_set")
            {
                lock (gate) { scenario = args.GetProperty("scenario").GetString()!; calls.Clear(); attempts.Clear(); cancels = 0; }
                Send(new { id, result = new { } });
                continue;
            }
            if (method == "fixture_stats")
            {
                lock (gate) Send(new { id, result = new { calls = calls.ToArray(), cancels } });
                continue;
            }
            if (method == "cancel")
            {
                // 故意不停止后台任务，用迟到响应检验客户端和窗口隔离。
                lock (gate) cancels++;
                Send(new { id, result = new { canceled = true } });
                continue;
            }
            if (method != "danmaku") { Send(new { id, result = new { } }); continue; }
            string episode = args.GetProperty("episode_id").GetString()!;
            string cursor = args.TryGetProperty("cursor", out value) ? value.GetString() ?? "" : "";
            long startMS = cursor.Length == 0 ? 0 : long.Parse(cursor, CultureInfo.InvariantCulture);
            long durationMS = args.GetProperty("duration_ms").GetInt64();
            string currentScenario;
            int attempt;
            lock (gate)
            {
                currentScenario = scenario;
                calls.Add(new { method, episode, cursor, duration_ms = durationMS });
                string key = episode + ":" + cursor;
                attempt = attempts.TryGetValue(key, out int previous) ? previous + 1 : 1;
                attempts[key] = attempt;
            }
            tasks.Add(Task.Run(async () =>
            {
                if (currentScenario == "slow-all" || currentScenario == "slow-first-episode" && episode == "100101")
                    await Task.Delay(550);
                if (currentScenario == "slow-initial" && startMS == 0) await Task.Delay(800);
                if (currentScenario == "second-segment-errors" && startMS == 30000 && attempt <= 3)
                {
                    Send(new { id, error = new { code = "fixture_error", message = "受控弹幕续段错误" } });
                    return;
                }
                object Row(string rowId, string text, long? position) => new { id = rowId, text, position_ms = position };
                if (currentScenario == "precise-duration")
                {
                    Send(new { id, result = new { items = new[]
                    {
                        Row("tail", "片尾-" + episode, 88250), Row("outside", "越界-" + episode, 88401),
                        Row("missing", "缺位置-" + episode, null), Row("negative", "负位置-" + episode, -1)
                    }, has_more = false, cursor = "", source_limited = false } });
                    return;
                }
                long endMS = Math.Min(durationMS, startMS + 30000);
                if (currentScenario == "slow-boundary")
                {
                    long positionMS = startMS == 0 ? 29900 : startMS + 1000;
                    string text = startMS == 0 ? new string('界', 80) : "目标段-" + episode;
                    Send(new { id, result = new
                    {
                        items = new[] { Row("slow-" + startMS, text, positionMS) },
                        has_more = endMS < durationMS, cursor = endMS.ToString(CultureInfo.InvariantCulture), source_limited = false
                    } });
                    return;
                }
                long bodyMS = Math.Min(durationMS, startMS + 1000);
                string body = "正文-" + episode + "-" + bodyMS;
                Send(new { id, result = new
                {
                    items = new[]
                    {
                        Row("body", body, bodyMS), Row("changed-id", body, bodyMS),
                        Row("head", "边界-" + episode + "-" + startMS, startMS),
                        Row("tail", "边界-" + episode + "-" + endMS, endMS)
                    },
                    has_more = endMS < durationMS, cursor = endMS.ToString(CultureInfo.InvariantCulture), source_limited = false
                } });
            }));
        }
        await Task.WhenAll(tasks);
        return 0;
    }

    private static void LoadResources(System.Windows.Application app)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!
            .Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), resources.Elements());
        using var reader = dictionary.CreateReader();
        app.Resources = (ResourceDictionary)XamlReader.Load(reader);
    }

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
        while (!condition()) { if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException(message); await Task.Delay(10); }
    }
    private static async Task WaitAsync(Func<Task<bool>> condition, string message, int timeoutMS = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (!await condition()) { if (timer.ElapsedMilliseconds > timeoutMS) throw new TimeoutException(message); await Task.Delay(10); }
    }
    private static Task Idle() => Wait(() => !Field<bool>("_rollingDanmakuBusy"), "弹幕读取没有结束");
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, Private)!.GetValue(Window) ?? default(T)!);
    private static void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(Window, value);
    private static T Control<T>(string name) where T : class => (T)Window.FindName(name)!;
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(Window, args);
    private static Task Call(string name, params object?[] args) => (Task)Invoke(name, args)!;
    private static Task<JsonElement> BackendCall(string method, object? args = null) => (Task<JsonElement>)Backend.GetType().GetMethod("CallAsync", Private)!
        .Invoke(Backend, new object?[] { method, args, CancellationToken.None })!;
    private static Task<JsonElement> Stats() => BackendCall("fixture_stats");
    private static async Task<JsonElement[]> Calls() => (await Stats()).GetProperty("calls").EnumerateArray().Select(item => item.Clone()).ToArray();
    private static string Cursor(JsonElement call) => call.GetProperty("cursor").GetString() ?? "";
}
