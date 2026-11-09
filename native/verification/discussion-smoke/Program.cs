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
using System.Windows.Documents;
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
    private static ObservableCollection<DiscussionEntry> Items => Field<ObservableCollection<DiscussionEntry>>("_discussionItems");

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
                File.WriteAllText("results/discussion-smoke.json", JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, failures = Failures, tests = Results, dispatcherErrors = DispatcherErrors,
                    naturallyClosed = Closed,
                    scope = "未显示的生产MainWindow、真实BackendClient与可控假stdio后端；检查讨论生命周期、取消、去重、分页、时间排序、预览边界和离屏WPF文本布局，不加载视频、不访问网络、不操作前台窗口"
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync()
    {
        CreateFixture();
        await Check("comments_preview_is_limited_deduplicated_and_has_no_more", async () =>
        {
            await Configure("normal", "comments");
            await Call("LoadDiscussionAsync", false);
            Require(Items.Count == 2 && Items.All(item => item.PositionMS == null), "普通评论去重或时间轴语义不正确");
            Require(Field<bool>("_discussionLimited") && !Field<bool>("_discussionHasMore")
                && Control<Button>("DiscussionMoreButton").Visibility == Visibility.Collapsed, "官网评论预览错误显示为可分页");
            Require(Control<TextBlock>("DiscussionStatus").Text.Contains("非完整评论列表"), "预览来源边界未显示");
            string artifact = RenderDiscussionPanel();
            return new { count = Items.Count, total = Field<long?>("_discussionTotal"), limited = true, hasMore = false, artifact };
        });

        await Check("slow_old_tab_does_not_replace_new_danmaku", async () =>
        {
            await Configure("slow-comments", "comments");
            Task old = Call("LoadDiscussionAsync", false);
            await WaitAsync(async () => (await Stats()).GetProperty("calls").GetArrayLength() == 1, "旧评论请求未发送");
            Set("_discussionTab", "danmaku");
            Invoke("ResetDiscussion");
            await Call("LoadDiscussionAsync", false);
            await old;
            await Task.Delay(320);
            Require(Items.Count > 0 && Items.All(item => item.Text.StartsWith("弹幕-", StringComparison.Ordinal)), "旧标签响应覆盖新标签");
            Require(Field<string>("_discussionTab") == "danmaku" && !Field<bool>("_discussionBusy"), "旧请求覆盖新标签状态");
            return new { count = Items.Count, canceledRequests = (await Stats()).GetProperty("cancels").GetInt32() };
        });

        await Check("slow_old_episode_does_not_replace_current_episode", async () =>
        {
            await Configure("slow-first-episode", "danmaku");
            Task old = Call("LoadDiscussionAsync", false);
            await WaitAsync(async () => (await Stats()).GetProperty("calls").GetArrayLength() == 1, "旧集请求未发送");
            Set("_currentEpisode", new EpisodeItem { Id = "100102", SeriesId = "1001", Number = 2 });
            Set("_detailVersion", Field<int>("_detailVersion") + 1);
            Invoke("ResetDiscussion");
            await Call("LoadDiscussionAsync", false);
            await old;
            await Task.Delay(320);
            Require(Items.Count == 2 && Items.All(item => item.Text.Contains("100102", StringComparison.Ordinal)), "旧集响应串入当前集");
            return new { episode = "100102", count = Items.Count, staleRows = 0 };
        });

        await Check("close_cancels_request_and_blocks_late_rows", async () =>
        {
            await Configure("slow-comments", "comments");
            Task pending = Call("LoadDiscussionAsync", false);
            await WaitAsync(async () => (await Stats()).GetProperty("calls").GetArrayLength() == 1, "待取消请求未发送");
            Invoke("CloseDiscussionPanel");
            await pending;
            await WaitAsync(async () => (await Stats()).GetProperty("cancels").GetInt32() > 0, "取消没有送达生产后端协议");
            await Task.Delay(320);
            Require(Items.Count == 0 && !Field<bool>("_discussionBusy")
                && Control<Border>("DiscussionPanel").Visibility == Visibility.Collapsed, "关闭后旧请求仍写入内容");
            return new { canceled = true, lateRows = Items.Count, panelShown = Window.IsVisible };
        });

        await Check("later_page_failure_preserves_rows_and_retry_sorts_deduplicates", async () =>
        {
            await Configure("page-retry", "danmaku");
            await Call("LoadDiscussionAsync", false);
            Require(Items.Select(item => item.PositionMS).SequenceEqual(new long?[] { 1000, 3000 }), "首段未按时间排序");
            string[] before = Items.Select(item => item.Id).ToArray();
            await Call("LoadDiscussionAsync", true);
            Require(Items.Select(item => item.Id).SequenceEqual(before) && Field<string>("_discussionCursor") == "page2",
                "下一段失败清空了正文或游标");
            Require(Control<Button>("DiscussionMoreButton").Content.ToString() == "重试" && Field<string>("_discussionError").Length > 0,
                "下一段失败缺少重试入口");
            await Call("LoadDiscussionAsync", true);
            Require(Items.Select(item => item.PositionMS).SequenceEqual(new long?[] { 1000, 2000, 3000 }), "追加页没有去重、过滤越界位置或按时间重排");
            Require(Field<bool>("_discussionLimited") && Field<string>("_discussionNote") == "首段为受限样本",
                "追加页抹掉前页的来源限制");
            Require(!Field<bool>("_discussionHasMore") && Control<Button>("DiscussionMoreButton").Content.ToString() == "重新读取弹幕",
                "受限末段没有重新读取入口");
            return new { count = Items.Count, positions = Items.Select(item => item.PositionMS).ToArray(), limitedPreserved = true, retryWorked = true };
        });

        await Check("first_page_failure_keeps_unknown_state_and_retry_recovers", async () =>
        {
            await Configure("first-error", "comments");
            await Call("LoadDiscussionAsync", false);
            Require(!Field<bool>("_discussionLoaded") && Items.Count == 0 && Field<long?>("_discussionTotal") == null,
                "首段错误被显示为已加载零评论");
            Require(Control<Button>("DiscussionMoreButton").Visibility == Visibility.Visible
                && Control<Button>("DiscussionMoreButton").Content.ToString() == "重试", "首段错误缺少重试入口");
            await Call("LoadDiscussionAsync", false);
            Require(Field<bool>("_discussionLoaded") && Items.Count == 2 && Field<string>("_discussionError") == "",
                "首段重试未恢复真实正文");
            return new { initialFailureWasUnknown = true, recoveredCount = Items.Count };
        });

        await Check("duration_readiness_blocks_stale_duration_and_allows_ready_retry", async () =>
        {
            await Configure("normal", "danmaku");
            Set("_currentPlaybackInfo", null);
            Set("_duration", 120d);
            await Call("LoadDiscussionAsync", false);
            Require((await Stats()).GetProperty("calls").GetArrayLength() == 0 && Field<string>("_discussionError").Contains("时长"),
                "尚无当前播放信息时沿用了旧时长");
            Set("_currentPlaybackInfo", new PlaybackInfo { DurationSeconds = double.NaN });
            Set("_duration", 0d);
            await Call("LoadDiscussionAsync", false);
            Require((await Stats()).GetProperty("calls").GetArrayLength() == 0, "无效时长仍发送弹幕请求");
            Set("_currentPlaybackInfo", new PlaybackInfo { DurationSeconds = 120 });
            await Call("LoadDiscussionAsync", false);
            Require(Field<bool>("_discussionLoaded") && Field<string>("_discussionError") == "" && Items.Count == 2,
                "时长就绪后的重新读取没有恢复");
            JsonElement sent = (await Stats()).GetProperty("calls")[0];
            Require(sent.GetProperty("duration_ms").GetInt64() == 120000, "弹幕时长未使用毫秒");
            return new { requestsBeforeReady = 0, durationMs = 120000, loadedAfterReady = true };
        });

        await Check("precise_duration_preserves_subsecond_tail_danmaku", async () =>
        {
            await Configure("precise-duration", "danmaku");
            Set("_currentPlaybackInfo", new PlaybackInfo { DurationSeconds = 88, DurationMS = 88400 });
            Set("_duration", 88d);
            await Call("LoadDiscussionAsync", false);
            JsonElement sent = (await Stats()).GetProperty("calls")[0];
            Require(sent.GetProperty("duration_ms").GetInt64() == 88400, "毫秒时长被整数秒覆盖");
            Require(Items.Count == 1 && Items[0].PositionMS == 88250, "片尾亚秒弹幕被裁掉或越界弹幕被接纳");
            return new { integerSeconds = 88, requestDurationMs = 88400, preservedTailMs = Items[0].PositionMS };
        });

        await Check("stuck_cursor_is_limited_and_offers_reload", async () =>
        {
            await Configure("stuck-cursor", "danmaku");
            await Call("LoadDiscussionAsync", false);
            Require(Items.Count == 2 && Field<bool>("_discussionLimited") && !Field<bool>("_discussionHasMore")
                && Field<string>("_discussionNote").Contains("游标未推进"), "游标卡住被误判为完整结束");
            Require(Control<Button>("DiscussionMoreButton").Visibility == Visibility.Visible
                && Control<Button>("DiscussionMoreButton").Content.ToString() == "重新读取弹幕", "游标卡住后缺少重新读取入口");
            return new { count = Items.Count, limited = true, reloadVisible = true };
        });

        await Check("discussion_metadata_handles_unknown_and_out_of_range_values", () =>
        {
            DiscussionEntry unknown = new() { Text = "真实正文", CreatedAt = long.MaxValue };
            DiscussionEntry positioned = new() { Text = "弹幕", PositionMS = 61900 };
            Require(unknown.Meta == "" && unknown.AuthorLabel == "观众", "未知时间或点赞被显示为虚构数值");
            Require(positioned.PositionSeconds == 61.9 && positioned.Meta.StartsWith("01:01", StringComparison.Ordinal), "弹幕时间换算错误");
            return Task.FromResult<object>(new { invalidTimeIgnored = true, positionSeconds = positioned.PositionSeconds });
        });
    }

    private static void CreateFixture()
    {
        StateDirectory = Path.Combine(Path.GetTempPath(), "openhongguo-discussion-" + Guid.NewGuid().ToString("N"));
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
        // 只替换后端进程；生产JSONL并发、取消与错误解析均保持原样。
        Backend = RuntimeHelpers.GetUninitializedObject(backendType);
        backendType.GetField("process", Private)!.SetValue(Backend, process);
        FieldInfo pending = backendType.GetField("pending", Private)!;
        pending.SetValue(Backend, Activator.CreateInstance(pending.FieldType));
        backendType.GetField("writeLock", Private)!.SetValue(Backend, new SemaphoreSlim(1, 1));
        _ = (Task)backendType.GetMethod("ReadResponsesAsync", Private)!.Invoke(Backend, null)!;
        // 不调用Show，不创建播放器，不触发Window_Loaded中的真实后端初始化。
        Window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
        Set("_configDir", StateDirectory);
        Set("_backend", Backend);
        Window.Closed += (_, _) => Closed = true;
    }

    private static async Task Configure(string scenario, string tab)
    {
        Invoke("ResetDiscussion");
        await BackendCall("fixture_set", new { scenario });
        Set("_discussionTab", tab);
        Set("_currentSeries", new SeriesItem { Id = "1001", Title = "本地讨论回归" });
        Set("_currentEpisode", new EpisodeItem { Id = "100101", SeriesId = "1001", Number = 1 });
        Set("_currentPlaybackInfo", new PlaybackInfo { DurationSeconds = 120 });
        Set("_duration", 120d);
        Set("_engagement", new EngagementInfo());
        Set("_detailVersion", Field<int>("_detailVersion") + 1);
        Control<Border>("DiscussionPanel").Visibility = Visibility.Visible;
    }

    private static string RenderDiscussionPanel()
    {
        Border panel = Control<Border>("DiscussionPanel");
        panel.Margin = new Thickness(0);
        panel.Width = 340;
        panel.Height = 680;
        panel.Measure(new Size(340, 680));
        panel.Arrange(new Rect(0, 0, 340, 680));
        panel.UpdateLayout();
        var bodyBlocks = Descendants<TextBlock>(panel).Where(item => item.Text.StartsWith("评论-", StringComparison.Ordinal)).ToArray();
        Require(bodyBlocks.Length == 2 && bodyBlocks.All(item => item.TextWrapping == TextWrapping.Wrap
            && item.TextTrimming == TextTrimming.None && item.ActualWidth > 0), "评论正文未按栏宽换行");
        foreach (TextBlock block in bodyBlocks)
        {
            Rect lastCharacter = block.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
            Require(!lastCharacter.IsEmpty && lastCharacter.Bottom <= block.ActualHeight + 1, "正文末行没有完整布局");
        }
        var image = new RenderTargetBitmap(340, 680, 96, 96, PixelFormats.Pbgra32);
        image.Render(panel);
        string path = Path.GetFullPath("results/discussion-panel-offscreen.png");
        using FileStream output = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(output);
        Require(!Window.IsVisible, "离屏验收意外显示了窗口");
        return path;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task<int> RunFixtureAsync()
    {
        object gate = new();
        object outputGate = new();
        string scenario = "normal";
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
                // 故意继续发送迟到响应，验证生产客户端和窗口各自隔离旧请求。
                lock (gate) cancels++;
                Send(new { id, result = new { canceled = true } });
                continue;
            }
            if (method is not ("comments" or "danmaku")) { Send(new { id, result = new { } }); continue; }
            string episode = args.GetProperty("episode_id").GetString()!;
            string cursor = args.TryGetProperty("cursor", out value) ? value.GetString() ?? "" : "";
            long duration = args.TryGetProperty("duration_ms", out value) ? value.GetInt64() : 0;
            string currentScenario;
            int attempt;
            lock (gate)
            {
                currentScenario = scenario;
                calls.Add(new { method, episode, cursor, duration_ms = duration });
                string key = method + ":" + episode + ":" + cursor;
                attempt = attempts.TryGetValue(key, out int previous) ? previous + 1 : 1;
                attempts[key] = attempt;
            }
            tasks.Add(Task.Run(async () =>
            {
                if (currentScenario == "slow-comments" && method == "comments" || currentScenario == "slow-first-episode" && episode == "100101")
                    await Task.Delay(250);
                if (currentScenario == "first-error" && attempt == 1 || currentScenario == "page-retry" && cursor == "page2" && attempt == 1)
                {
                    Send(new { id, error = new { code = "fixture_error", message = "受控后端错误" } });
                    return;
                }
                if (method == "comments")
                {
                    var first = new { id = "c1", author = "回归测试", text = "评论-" + episode + "：这是一条用于检查窄栏自动换行的评论。正文应完整排版，保持真实日期和点赞量，不用省略号截断正文。", created_at = 1700000000L, like_count = (long?)9 };
                    Send(new { id, result = new { items = new object[] { first, first, new { id = "c2", text = "评论-第二条", created_at = 0, like_count = (long?)null } }, total = 1300, source_limited = true, has_more = true, cursor = "fake-web-more", note = "官网仅展示部分评论。" } });
                    return;
                }
                object Row(string rowId, string text, long? position) => new { id = rowId, text, position_ms = position };
                object[] rows;
                bool hasMore = false;
                bool limited = false;
                string next = "";
                string note = "";
                if (currentScenario == "precise-duration")
                    rows = new[] { Row("tail", "弹幕-片尾", 88250), Row("outside", "弹幕-时长外", 88401) };
                else if (currentScenario == "page-retry")
                {
                    if (cursor == "")
                    {
                        rows = new[] { Row("d3", "弹幕-" + episode + "-3", 3000), Row("d1", "弹幕-" + episode + "-1", 1000) };
                        hasMore = true; next = "page2"; limited = true; note = "首段为受限样本";
                    }
                    else rows = new[] { Row("changed-id", "弹幕-" + episode + "-1", 1000), Row("d2", "弹幕-" + episode + "-2", 2000), Row("bad", "弹幕-越界", 121000), Row("missing", "弹幕-无位置", null) };
                }
                else
                {
                    rows = new[] { Row("d1", "弹幕-" + episode + "-1", 1000), Row("d2", "弹幕-" + episode + "-2", 2000) };
                    hasMore = currentScenario == "stuck-cursor";
                }
                Send(new { id, result = new { items = rows, total = rows.Length, source_limited = limited, has_more = hasMore, cursor = next, note } });
            }));
        }
        await Task.WhenAll(tasks);
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
    private static async Task WaitAsync(Func<Task<bool>> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!await condition()) { if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException(message); await Task.Delay(10); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, Private)!.GetValue(Window) ?? default(T)!);
    private static void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(Window, value);
    private static T Control<T>(string name) where T : class => (T)Window.FindName(name)!;
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(Window, args);
    private static Task Call(string name, params object?[] args) => (Task)Invoke(name, args)!;
    private static Task<JsonElement> BackendCall(string method, object? args = null) => (Task<JsonElement>)Backend.GetType().GetMethod("CallAsync", Private)!
        .Invoke(Backend, new object?[] { method, args, CancellationToken.None })!;
    private static Task<JsonElement> Stats() => BackendCall("fixture_stats");
}
