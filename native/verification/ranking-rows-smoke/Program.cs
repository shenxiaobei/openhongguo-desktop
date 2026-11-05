using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string Output = Path.Combine(Environment.CurrentDirectory, "results");
    private static readonly List<object> Results = new();
    private static MainWindow? Window;
    private static int Failures;

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory(Output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"));
        XElement resources = source.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), resources.Elements());
        using (var reader = dictionary.CreateReader()) app.Resources = (ResourceDictionary)XamlReader.Load(reader);
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { error = Clean(error.Message) }); }
            finally
            {
                if (Window != null)
                {
                    SetField("_closing", true);
                    Field<DispatcherTimer>("_playerTimer").Stop();
                    Field<DispatcherTimer>("_rankingUpdateTimer").Stop();
                    Invoke("CancelDiscoveryRequests");
                    Field<CancellationTokenSource?>("_catalogRequest")?.Cancel();
                    Field<CancellationTokenSource?>("_catalogHydration")?.Cancel();
                    Field<CancellationTokenSource?>("_rankRequest")?.Cancel();
                    (Field<object?>("_player") as IDisposable)?.Dispose();
                    (Field<object?>("_standbyPlayer") as IDisposable)?.Dispose();
                    (Field<object?>("_backend") as IDisposable)?.Dispose();
                    Window.Close();
                }
                SaveReport();
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync()
    {
        string state = Path.Combine(Environment.CurrentDirectory, "state", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(state);
        string existing = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "..", "..", "..", ".analysis-cache", "rank-regression", "state", "anonymous.json"));
        if (!File.Exists(existing)) existing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openhongguo", "anonymous.json");
        if (File.Exists(existing)) File.Copy(existing, Path.Combine(state, "anonymous.json"));
        Window = new MainWindow
        {
            ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -3000, Top = 40, Opacity = 0
        };
        SetField("_configDir", state);
        Application.Current.MainWindow = Window;
        Window.Show();
        Control<Slider>("VolumeSlider").Value = 0;
        Control<CheckBox>("AutoNextCheck").IsChecked = false;
        await WaitAsync(() => Field<object?>("_backend") != null && !Field<bool>("_catalogBusy")
            && ((IEnumerable<SeriesItem>)Field<object>("_catalog")).Any(), "隔离首页未完成");
        await Field<Task>("_catalogHydrationTask").WaitAsync(TimeSpan.FromSeconds(55));

        await CheckAsync("platform_rows_wide_match_real_response", async () =>
        {
            JsonElement response = await ObserveResponseAsync(() => Control<Button>("RankingNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Count > 1, "真实平台榜单未返回");
            AssertPlatformResponse(response);
            Window.Width = 1380;
            Window.Height = 900;
            await SettleAsync();
            object geometry = RowGeometry();
            object[] covers = await FirstCoversAsync();
            object titles = TitleLayout();
            await CaptureAsync("ranking-rows-wide.png");
            return new { count = Ranking.Count, content = Field<string>("_rankContent"), board = Field<string>("_rankBoard"), geometry, covers, titles };
        });

        await CheckAsync("platform_rows_compact_full_width_without_overflow", async () =>
        {
            Window.Width = 950;
            Window.Height = 640;
            await SettleAsync();
            object geometry = RowGeometry();
            object titles = TitleLayout();
            object[] covers = await FirstCoversAsync();
            await CaptureAsync("ranking-rows-compact.png");
            return new { geometry, titles, covers };
        });

        await CheckAsync("horizontal_tabs_category_and_daily_stamp_retained", async () =>
        {
            foreach (string name in new[] { "RankContentTabs", "RankBoardTabs" })
            {
                StackPanel panel = Control<StackPanel>(name);
                Button[] buttons = panel.Children.OfType<Button>().ToArray();
                Require(panel.Orientation == Orientation.Horizontal && buttons.Length > 1, "原横向标签行丢失：" + name);
                Point[] positions = buttons.Select(button => button.TranslatePoint(new Point(), panel)).ToArray();
                Require(positions.All(point => Math.Abs(point.Y - positions[0].Y) < 1) && positions[1].X > positions[0].X, "标签未保持单行横向：" + name);
            }
            string[] expected = Field<List<RankContentOption>>("_rankContents").Select(item => item.Id).ToArray();
            Require(expected.SequenceEqual(Control<StackPanel>("RankContentTabs").Children.OfType<Button>().Select(button => ((RankContentOption)button.Tag).Id)), "作品类型未沿用真实平台目录");
            Require(!expected.Contains("ranklist_celebrity"), "系列剧模型出现演员入口");
            Require(Control<ComboBox>("RankContentCombo").Visibility == Visibility.Collapsed && Control<ComboBox>("RankBoardCombo").Visibility == Visibility.Collapsed, "旧下拉主入口重新显示");
            Require(Control<FrameworkElement>("RankGenrePanel").Visibility == Visibility.Collapsed, "分类未默认收起");
            Control<Button>("RankCategoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await InvokeTaskAsync("RefreshRankGenreOptionsAsync");
            await WaitAsync(() => Control<ComboBox>("RankThemeCombo").Items.Count > 1, "真实主题未完成");
            await SettleAsync();
            Require(Control<FrameworkElement>("RankGenrePanel").Visibility == Visibility.Visible, "分类入口未展开");
            foreach (string name in new[] { "RankThemeCombo", "RankSettingCombo", "RankBackgroundCombo" })
                Require(Control<ComboBox>(name).IsVisible && Control<ComboBox>(name).ActualWidth > 60, "题材分类控件未展示：" + name);
            Control<Button>("RankCategoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Control<FrameworkElement>("RankGenrePanel").Visibility == Visibility.Collapsed, "分类入口未收起");
            string freshness = Control<TextBlock>("RankingFreshnessText").Text;
            Require(freshness.Contains("每日自动更新", StringComparison.Ordinal) && freshness.Contains(DateTime.Now.ToString("MM-dd"), StringComparison.Ordinal), "每日更新时间未保留");
            return new { contents = expected, boardCount = Control<StackPanel>("RankBoardTabs").Children.Count, categoryToggle = true, freshness };
        });

        await CheckAsync("topic_rows_follow_real_heat_sort", async () =>
        {
            Window.Width = 1380;
            Window.Height = 900;
            Control<Button>("RankCategoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ComboBox themes = Control<ComboBox>("RankThemeCombo");
            GenreOption suspense = themes.Items.OfType<GenreOption>().First(item => item.Value == "cate_165");
            JsonElement response = await ObserveResponseAsync(() => themes.SelectedItem = suspense);
            await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Any(item => item.IsCategoryResult), "真实悬疑热门列表未返回");
            AssertTopicResponse(response);
            Dictionary<string, string[]> query = CursorSelections(response.GetProperty("cursor").GetString()!);
            Require(query.GetValueOrDefault("category_dim_theme")?.SequenceEqual(new[] { "cate_165" }) == true
                && query.GetValueOrDefault("sort")?.SequenceEqual(new[] { "hot_score" }) == true, "实际筛选不是悬疑加平台热度排序");
            await Field<Task>("_rankGenreHydrationTask").WaitAsync(TimeSpan.FromSeconds(55));
            object[] covers = await FirstCoversAsync();
            Control<Button>("RankCategoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync();
            object geometry = RowGeometry();
            Require(Control<FrameworkElement>("RankCategorySummary").Visibility == Visibility.Visible
                && Control<TextBlock>("RankCategorySummary").Text.Contains("悬疑", StringComparison.Ordinal), "收起分类后所选题材摘要丢失");
            await CaptureAsync("ranking-rows-topic.png");
            return new { count = Ranking.Count, query, firstTitles = Ranking.Take(5).Select(item => item.Title), geometry, covers, summary = Control<TextBlock>("RankCategorySummary").Text };
        });

        await CheckAsync("topic_rows_second_page_keep_source_order", async () =>
        {
            string[] first = Ranking.Select(item => item.Id).ToArray();
            Require(Field<bool>("_rankHasMore"), "悬疑热门列表没有续页");
            JsonElement response = await ObserveResponseAsync(() => _ = InvokeTaskAsync("LoadRankingAsync", true));
            await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Count > first.Length, "悬疑第二页未追加");
            string[] second = TopicIds(response);
            Require(!first.Intersect(second).Any() && first.Concat(second).SequenceEqual(Ranking.Select(item => item.Id)), "悬疑续页顺序或去重不符真实响应");
            Require(Ranking.Select(item => item.Position).SequenceEqual(Enumerable.Range(1, Ranking.Count)), "分类序号没有连续追加");
            await SettleAsync();
            object geometry = RowGeometry();
            return new { first = first.Length, second = second.Length, total = Ranking.Count, repeated = first.Intersect(second).Count(), geometry };
        });

        await CheckAsync("row_selection_uses_real_id_and_back_preserves_list", async () =>
        {
            await Field<Task>("_rankGenreHydrationTask").WaitAsync(TimeSpan.FromSeconds(55));
            string[] ids = Ranking.Select(item => item.Id).ToArray();
            string cursor = Field<string>("_rankCursor");
            var selections = new Dictionary<string, string>(Field<Dictionary<string, string>>("_rankGenreSelections"));
            ScrollViewer scroll = Control<ScrollViewer>("RankScroll");
            scroll.ScrollToVerticalOffset(Math.Min(100, scroll.ScrollableHeight));
            await SettleAsync();
            double offset = scroll.VerticalOffset;
            RankedSeriesItem selected = Ranking[1];
            Button row = RowButton(1);
            Require(ReferenceEquals(row.Tag, selected), "行按钮没有绑定对应真实剧目");
            JsonElement detail = await ObserveResponseAsync(() => row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)), invalidateDetail: true);
            await SettleAsync();
            Require(detail.GetProperty("series").GetProperty("id").GetString() == selected.Id, "点击榜单行请求的剧目ID不一致");
            Require(Control<FrameworkElement>("PlayerScreen").Visibility == Visibility.Visible && Field<object?>("_player") == null, "导航验证意外启动媒体或没有进入播放器页");
            Button back = VisualChildren<Button>(Control<FrameworkElement>("PlayerScreen")).First(button => button.ToolTip?.ToString() == "返回");
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => Control<FrameworkElement>("RankingScreen").Visibility == Visibility.Visible, "真实返回按钮没有回到排行榜");
            await Field<Task>("_rankGenreHydrationTask").WaitAsync(TimeSpan.FromSeconds(55));
            await SettleAsync();
            Require(ids.SequenceEqual(Ranking.Select(item => item.Id)) && cursor == Field<string>("_rankCursor"), "返回改变了榜单行或续页游标");
            Require(selections.OrderBy(pair => pair.Key).SequenceEqual(Field<Dictionary<string, string>>("_rankGenreSelections").OrderBy(pair => pair.Key)), "返回丢失题材条件");
            Require(Math.Abs(offset - scroll.VerticalOffset) < 1, "返回丢失纵向滚动位置");
            return new { selectedSeriesId = selected.Id, responseMatched = true, navigationOnly = true, staleDetailGuardUsedToAvoidPlayback = true, mediaStarted = false, preservedCount = Ranking.Count, offset = scroll.VerticalOffset, geometry = RowGeometry() };
        });
    }

    private static object RowGeometry()
    {
        ItemsControl items = Control<ItemsControl>("RankingItems");
        Panel host = VisualChildren<Panel>(items).First(panel => panel.IsItemsHost);
        Require(host is StackPanel stack && stack.Orientation == Orientation.Vertical, "榜单不是单列纵向ItemsPanel");
        Require(Ranking.Count > 1, "真实榜单行不足两项");
        var bounds = new List<Rect>();
        for (int index = 0; index < Ranking.Count; index++)
        {
            Button row = RowButton(index);
            Rect box = row.TransformToAncestor(items).TransformBounds(new Rect(0, 0, row.ActualWidth, row.ActualHeight));
            Require(Math.Abs(box.Left) < 1 && Math.Abs(box.Width - items.ActualWidth) < 2, "榜单行没有占满可用宽度");
            Require(box.Right <= items.ActualWidth + 1, "榜单行超出横向边界");
            if (index > 0) Require(box.Top >= bounds[index - 1].Bottom, "榜单行重叠或横向并排");
            bounds.Add(box);
        }
        Require(Math.Abs(bounds[0].Left - bounds[1].Left) < 1 && bounds[1].Top > bounds[0].Top + 100, "前两项没有同X纵向依次展示");
        ScrollViewer scroll = Control<ScrollViewer>("RankScroll");
        Require(scroll.ScrollableWidth < 1 && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled, "榜单产生水平溢出");
        Require(scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && scroll.ScrollableHeight > 0, "榜单没有纵向滚动");
        return new
        {
            windowWidth = Window!.ActualWidth, windowHeight = Window.ActualHeight, rows = bounds.Count,
            first = new { bounds[0].X, bounds[0].Y, bounds[0].Width, bounds[0].Height },
            second = new { bounds[1].X, bounds[1].Y, bounds[1].Width, bounds[1].Height },
            containerWidth = items.ActualWidth, maxRight = bounds.Max(box => box.Right), scroll.ScrollableWidth, scroll.ScrollableHeight
        };
    }

    private static object TitleLayout()
    {
        var evidence = new List<object>();
        for (int index = 0; index < Math.Min(2, Ranking.Count); index++)
        {
            TextBlock title = VisualChildren<TextBlock>(RowButton(index)).First(block => block.FontSize == 15 && block.Text == Ranking[index].Title);
            Require(title.TextWrapping == TextWrapping.Wrap && title.TextTrimming == TextTrimming.None && double.IsPositiveInfinity(title.MaxHeight), "主标题仍被强制省略或限制行数");
            Require(title.ActualWidth > 100, "窄窗口主标题空间不足");
            evidence.Add(new { title.Text, title.ActualWidth, title.ActualHeight, wrapping = title.TextWrapping.ToString(), trimming = title.TextTrimming.ToString(), unlimitedHeight = true });
        }
        return evidence;
    }

    private static async Task<object[]> FirstCoversAsync()
    {
        await WaitAsync(() => Enumerable.Range(0, 2).All(index => VisualChildren<Image>(RowButton(index)).Any(image => image.Source is BitmapSource bitmap && bitmap.PixelWidth > 40 && bitmap.PixelHeight > 40)), "首两项真实封面未解码", 30000);
        return Enumerable.Range(0, 2).Select(index =>
        {
            Image image = VisualChildren<Image>(RowButton(index)).Single();
            Require(CoverImages.GetUrl(image) == Ranking[index].CoverUrl && Uri.IsWellFormedUriString(Ranking[index].CoverUrl, UriKind.Absolute), "封面未绑定对应剧目的真实URL");
            var bitmap = (BitmapSource)image.Source;
            int stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
            byte[] pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            return (object)new { index, bitmap.PixelWidth, bitmap.PixelHeight, image.ActualWidth, image.ActualHeight, pixelSha256 = Convert.ToHexString(SHA256.HashData(pixels)), matchesRealCoverBinding = true };
        }).ToArray();
    }

    private static void AssertPlatformResponse(JsonElement response)
    {
        JsonElement[] items = response.GetProperty("items").EnumerateArray().ToArray();
        Require(items.Select(item => item.GetProperty("series_id").GetString()).SequenceEqual(Ranking.Select(item => item.Id)), "平台榜单行顺序与同一次响应不同");
        for (int index = 0; index < items.Length; index++)
        {
            int rank = items[index].TryGetProperty("rank", out JsonElement rawRank) ? rawRank.GetInt32() : 0;
            bool promoted = items[index].TryGetProperty("promoted", out JsonElement rawPromoted) && rawPromoted.GetBoolean();
            Require(Ranking[index].Rank == rank && Ranking[index].IsPromoted == promoted && !Ranking[index].IsCategoryResult, "原平台名次或推荐位被修改");
        }
    }

    private static void AssertTopicResponse(JsonElement response)
    {
        Require(TopicIds(response).SequenceEqual(Ranking.Select(item => item.Id)), "题材行顺序与同一次响应不同");
        Require(response.GetProperty("cursor").GetString() == Field<string>("_rankCursor"), "真实题材游标未保留");
        Require(Ranking.All(item => item.IsCategoryResult && item.Rank == 0 && !item.IsPromoted && item.ScoreLabel == "平台热度排序" && item.RankContextLabel == "列表序号"), "题材结果伪造官方排名或热度分");
    }

    private static Dictionary<string, string[]> CursorSelections(string cursor)
    {
        // 只读取 SDK 续页模型中的筛选条件，不把完整游标和会话信息写入报告。
        Require(cursor.StartsWith("s1.", StringComparison.Ordinal), "缺少SDK题材游标");
        string payload = cursor[3..].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
        return document.RootElement.GetProperty("q").Deserialize<Dictionary<string, string[]>>()!;
    }

    private static async Task<JsonElement> ObserveResponseAsync(Action trigger, bool invalidateDetail = false)
    {
        object backend = Field<object>("_backend");
        Type type = backend.GetType();
        var writer = (SemaphoreSlim)type.GetField("writeLock", PrivateInstance)!.GetValue(backend)!;
        FieldInfo sequence = type.GetField("requestSequence", PrivateInstance)!;
        var pending = (ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>)type.GetField("pending", PrivateInstance)!.GetValue(backend)!;
        Task<JsonElement> response;
        await writer.WaitAsync();
        try
        {
            long before = (long)sequence.GetValue(backend)!;
            trigger();
            await WaitAsync(() => (long)sequence.GetValue(backend)! > before, "操作没有发出真实后端请求", 5000);
            string id = (before + 1).ToString();
            Require(pending.TryGetValue(id, out TaskCompletionSource<JsonElement>? completion), "没有找到本次请求的响应任务");
            response = completion!.Task;
            if (invalidateDetail)
            {
                // 点击仍发送真实剧目请求，只让其详情代次过期，避免导航检查启动媒体。
                SetField("_detailVersion", Field<int>("_detailVersion") + 1);
            }
        }
        finally { writer.Release(); }
        return await response.WaitAsync(TimeSpan.FromSeconds(65));
    }

    private static Button RowButton(int index)
    {
        ItemsControl items = Control<ItemsControl>("RankingItems");
        var container = (FrameworkElement?)items.ItemContainerGenerator.ContainerFromIndex(index);
        Require(container != null, "榜单容器尚未生成");
        return VisualChildren<Button>(container!).Single(button => button.Tag is RankedSeriesItem);
    }

    private static async Task SettleAsync()
    {
        Window!.UpdateLayout();
        await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(250);
    }

    private static async Task CaptureAsync(string filename)
    {
        await SettleAsync();
        FrameworkElement root = (FrameworkElement)Window!.Content;
        int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext drawing = background.RenderOpen()) drawing.DrawRectangle(Window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(Path.Combine(Output, filename));
        encoder.Save(file);
    }

    private static async Task CheckAsync(string name, Func<Task<object>> test)
    {
        Console.WriteLine("START " + name);
        var elapsed = Stopwatch.StartNew();
        try
        {
            object result = await test().WaitAsync(TimeSpan.FromSeconds(90));
            Record(name, true, new { elapsedMs = elapsed.ElapsedMilliseconds, result });
        }
        catch (Exception error)
        {
            if (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Record(name, false, new { elapsedMs = elapsed.ElapsedMilliseconds, error = Clean(error.Message), type = error.GetType().Name });
        }
        SaveReport();
    }

    private static async Task WaitAsync(Func<bool> predicate, string message, int timeout = 60000)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            if (elapsed.ElapsedMilliseconds > timeout) throw new TimeoutException(message);
            await Task.Delay(80);
        }
    }

    private static List<RankedSeriesItem> Ranking => ((IEnumerable<RankedSeriesItem>)Field<object>("_ranking")).ToList();
    private static string[] TopicIds(JsonElement response) => response.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString() ?? "").ToArray();
    private static T Control<T>(string name) where T : class => Window!.FindName(name) as T ?? throw new InvalidOperationException("缺少控件：" + name);
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, PrivateInstance)?.GetValue(Window) ?? default(T)!);
    private static void SetField(string name, object value) => (typeof(MainWindow).GetField(name, PrivateInstance) ?? throw new MissingFieldException(name)).SetValue(Window, value);
    private static object? Invoke(string name, params object?[] args) => (typeof(MainWindow).GetMethod(name, PrivateInstance) ?? throw new MissingMethodException(name)).Invoke(Window, args);
    private static Task InvokeTaskAsync(string name, params object?[] args) => Invoke(name, args) as Task ?? throw new InvalidOperationException("方法未返回任务：" + name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Clean(string value) => Regex.Replace(Regex.Replace(value, @"https?://\S+", "[URL]"), @"\b[a-fA-F0-9]{32,}\b", "[VALUE]");
    private static void Record(string name, bool passed, object details)
    {
        if (!passed) Failures++;
        Results.Add(new { name, passed, details });
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(details));
    }
    private static void SaveReport() => File.WriteAllText(Path.Combine(Output, "ranking-rows-smoke.json"), JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures = Failures, tests = Results }, new JsonSerializerOptions { WriteIndented = true }));
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (T child in VisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
