using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Results = new();
    private static readonly string[] Cases = ["640x420", "640x480", "800x540", "950x640", "1380x900"];
    private static string CurrentCase = "";
    private static MainWindow Window = null!;
    private static int Failures;

    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory("results");
        if (args.FirstOrDefault() != "--case") return RunCases();
        CurrentCase = args[1];
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { error = error.Message, type = error.GetType().Name }); }
            finally
            {
                Cleanup();
                File.WriteAllText("results/responsive-" + CurrentCase + ".json", JsonSerializer.Serialize(new { at = DateTimeOffset.Now, size = CurrentCase, failures = Failures, tests = Results }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static int RunCases()
    {
        var tests = new List<JsonElement>();
        int failures = 0;
        foreach (string name in Cases)
        {
            string report = "results/responsive-" + name + ".json";
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
            if (!File.Exists(report))
            {
                failures++;
                using JsonDocument missing = JsonDocument.Parse(JsonSerializer.Serialize(new { name, passed = false, detail = new { process.ExitCode, error } }));
                tests.Add(missing.RootElement.Clone());
                continue;
            }
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
            failures += document.RootElement.GetProperty("failures").GetInt32();
            foreach (JsonElement test in document.RootElement.GetProperty("tests").EnumerateArray()) tests.Add(test.Clone());
            if (process.ExitCode != 0 && document.RootElement.GetProperty("failures").GetInt32() == 0) failures++;
        }
        File.WriteAllText("results/responsive-smoke.json", JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures, tests }, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }

    private static void LoadResources(System.Windows.Application app)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), resources.Elements());
        using var reader = dictionary.CreateReader();
        app.Resources = (ResourceDictionary)XamlReader.Load(reader);
    }

    private static async Task RunAsync()
    {
        int[] size = CurrentCase.Split('x').Select(int.Parse).ToArray();
        Window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = 40, Opacity = 0 };
        var loaded = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window, typeof(MainWindow).GetMethod("Window_Loaded", PrivateInstance)!);
        Window.Loaded -= loaded;
        InputMethod.SetIsInputMethodEnabled(Window, false);
        InputMethod.SetIsInputMethodSuspended(Window, true);
        System.Windows.Application.Current.MainWindow = Window;
        SeedContent();
        Window.Width = size[0];
        Window.Height = size[1];
        Window.Show();
        await SettleAsync();

        await Check("minimum_and_catalog_" + CurrentCase, async () =>
        {
            Require(Window.MinWidth <= 640 && Window.MinHeight <= 420, "window minimum remains larger than compact target");
            double expectedWidth = Math.Min(size[0], SystemParameters.MaximumWindowTrackWidth);
            double expectedHeight = Math.Min(size[1], SystemParameters.MaximumWindowTrackHeight);
            Require(Math.Abs(Window.ActualWidth - expectedWidth) < 1 && Math.Abs(Window.ActualHeight - expectedHeight) < 1, "requested window size was clamped beyond Windows tracking limits");
            foreach (string name in new[] { "DiscoverNav", "FeedNav", "RankingNav", "HistoryNav", "SearchInput", "SearchTabCombo", "SearchButton" }) RequireInside(Control<FrameworkElement>(name), Control<FrameworkElement>("RootLayout"), name);
            foreach (Button channel in Control<StackPanel>("ChannelTabs").Children.OfType<Button>()) RequireInside(channel, Control<FrameworkElement>("RootLayout"), "channel " + channel.Content);
            if (size[0] < 1000)
            {
                Require(Control<ColumnDefinition>("NavColumn").ActualWidth <= 80, "compact navigation did not become an icon rail");
                foreach (string name in new[] { "DiscoverNav", "FeedNav", "RankingNav", "HistoryNav" })
                    Require(Control<Button>(name).ToolTip != null, "icon navigation is missing a tooltip: " + name);
            }
            ScrollViewer catalog = Control<ScrollViewer>("CatalogScroll");
            Require(catalog.ActualHeight >= 80 && catalog.ViewportHeight > 0, "catalog filters consumed the compact content area");
            await CaptureAsync("catalog");
            return new { requested = size, window = new[] { Window.ActualWidth, Window.ActualHeight }, maximumTrack = new[] { SystemParameters.MaximumWindowTrackWidth, SystemParameters.MaximumWindowTrackHeight }, minimum = new[] { Window.MinWidth, Window.MinHeight }, navWidth = Control<ColumnDefinition>("NavColumn").ActualWidth, catalogViewport = catalog.ViewportHeight, backendStarted = Field<object?>("_backend") != null };
        });

        await Check("compact_filter_scrolling_and_collapse_" + CurrentCase, async () =>
        {
            ScrollViewer options = Control<ScrollViewer>("GenreOptionsScroll");
            bool originallyVisible = options.IsVisible;
            Button toggle = Control<Button>("GenrePanelToggleButton");
            if (!originallyVisible) toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync();
            Require(options.IsVisible && options.ActualHeight > 0 && options.ActualHeight <= 118, "expanded genre options are not bounded");
            Require(options.ScrollableHeight > 0, "many genre options did not become vertically scrollable");
            options.ScrollToBottom();
            await SettleAsync();
            Require(options.VerticalOffset > 0 && Control<ScrollViewer>("CatalogScroll").ActualHeight >= 45, "genre scrolling consumed the catalog or did not move");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync();
            Require(!options.IsVisible, "genre options did not collapse after clicking its header");
            if (originallyVisible) toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync();
            Require(options.IsVisible == originallyVisible, "genre panel did not restore its collapsed state");
            return new { optionsHeight = options.ActualHeight, canScroll = options.ScrollableHeight > 0, collapsedInitially = !originallyVisible, restored = true };
        });

        await Check("ranking_rows_tabs_and_daily_update_" + CurrentCase, async () =>
        {
            Invoke("ShowScreen", Control<FrameworkElement>("RankingScreen"));
            await SettleAsync();
            ScrollViewer scroll = Control<ScrollViewer>("RankScroll");
            Require(scroll.ActualHeight >= 80 && scroll.ViewportHeight > 0, "ranking header consumed compact list viewport");
            ItemsControl items = Control<ItemsControl>("RankingItems");
            Button[] rows = VisualChildren<Button>(items).Where(button => button.Tag is RankedSeriesItem).ToArray();
            Require(rows.Length >= 2, "seed ranking rows were not materialized");
            Rect first = Bounds(rows[0], items);
            Rect second = Bounds(rows[1], items);
            Require(Math.Abs(first.X - second.X) < 2 && second.Y >= first.Bottom - 1, "ranking is no longer one full-width work per row");
            Require(first.Width >= items.ActualWidth - 5 && first.Right <= items.ActualWidth + 2, "ranking row does not fill available width");
            RequireInside(Control<FrameworkElement>("RankContentTabsScroll"), Control<FrameworkElement>("RootLayout"), "rank content tabs");
            RequireInside(Control<FrameworkElement>("RankBoardTabsScroll"), Control<FrameworkElement>("RootLayout"), "rank board tabs");
            Require(Control<StackPanel>("RankContentTabs").Children.Count > 0 && Control<StackPanel>("RankBoardTabs").Children.Count > 0, "horizontal rank tabs disappeared");
            Require(Control<TextBlock>("RankingFreshnessText").Text.Contains("每日", StringComparison.Ordinal), "daily ranking update text disappeared");
            RequireInside(Control<TextBlock>("RankingFreshnessText"), Control<FrameworkElement>("RootLayout"), "daily ranking update text");
            await CaptureAsync("ranking");
            Button category = Control<Button>("RankCategoryButton");
            category.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync();
            ScrollViewer genres = Control<ScrollViewer>("RankGenreScroll");
            Require(genres.IsVisible && genres.ActualHeight > 0 && genres.ActualHeight <= 118, "ranking genre panel was not bounded");
            Require(scroll.ActualHeight >= 25, "ranking category panel removed all list space");
            category.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync();
            Require(Control<FrameworkElement>("RankGenrePanel").Visibility != Visibility.Visible, "ranking category panel could not collapse");
            return new { rowWidth = first.Width, availableWidth = items.ActualWidth, rowY = new[] { first.Y, second.Y }, viewport = scroll.ViewportHeight, tabsHorizontal = true, dailyUpdateVisible = true };
        });

        await Check("player_controls_and_episode_picker_" + CurrentCase, async () =>
        {
            Invoke("ShowScreen", Control<FrameworkElement>("PlayerScreen"));
            await SettleAsync();
            FrameworkElement controls = Control<FrameworkElement>("PlayerControls");
            FrameworkElement root = Control<FrameworkElement>("RootLayout");
            foreach (string name in new[] { "PauseButton", "PreviousButton", "NextButton", "SeekSlider", "SpeedCombo", "QualityCombo", "AutoNextCheck", "FullscreenButton" })
            {
                RequireInside(Control<FrameworkElement>(name), controls, name);
                RequireInside(Control<FrameworkElement>(name), root, name + " root");
            }
            FrameworkElement host = Control<FrameworkElement>("VideoHost");
            Require(host.ActualWidth >= 300 && host.ActualHeight >= 100, "compact playback area is too small");
            Button picker = Control<Button>("EpisodePickerButton");
            if (size[0] < 1000)
            {
                Require(picker.Visibility == Visibility.Visible && picker.ActualWidth > 0, "compact episode picker entry missing");
                Require(Control<FrameworkElement>("EpisodeSidebar").Visibility != Visibility.Visible, "compact player still reserves wide episode sidebar");
                picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await SettleAsync();
                Popup? popup = typeof(MainWindow).GetFields(PrivateInstance).Select(field => field.GetValue(Window)).OfType<Popup>().FirstOrDefault(value => value.IsOpen && value.Child != null && VisualChildren<Button>(value.Child).Any(button => button.Tag is EpisodeItem));
                Require(popup != null, "episode picker did not open a usable popup");
                Button[] episodes = VisualChildren<Button>(popup!.Child).Where(button => button.Tag is EpisodeItem).ToArray();
                Require(episodes.Length >= 20 && episodes.All(button => button.IsEnabled), "episode picker did not expose playable choices");
                await CapturePopupAsync("episode-picker", popup.Child);
                popup.IsOpen = false;
            }
            else Require(Control<FrameworkElement>("EpisodeSidebar").Visibility == Visibility.Visible, "wide window lost its episode sidebar");
            await CaptureAsync("player");
            return new { video = new[] { host.ActualWidth, host.ActualHeight }, controlsHeight = controls.ActualHeight, pickerVisible = picker.Visibility == Visibility.Visible, controlsFit = true };
        });

        await Check("fullscreen_restore_" + CurrentCase, async () =>
        {
            double width = Window.Width, height = Window.Height;
            Invoke("ToggleFullscreen");
            await SettleAsync();
            Require(Field<bool>("_fullscreen") && Control<ColumnDefinition>("EpisodeColumn").ActualWidth == 0, "fullscreen retained sidebar");
            Popup? popup = Field<Popup?>("_fullscreenControls");
            Require(popup?.IsOpen == true && popup.Child == Control<FrameworkElement>("PlayerControls"), "fullscreen controls did not open");
            Invoke("ToggleFullscreen");
            await SettleAsync();
            Require(!Field<bool>("_fullscreen") && Window.WindowStyle == WindowStyle.SingleBorderWindow, "fullscreen did not restore normal window");
            Require(Math.Abs(Window.Width - width) < 1 && Math.Abs(Window.Height - height) < 1, "fullscreen restore changed compact size");
            Require(Control<FrameworkElement>("PlayerControls").Parent == Control<Grid>("CinemaLayout"), "fullscreen controls were not restored to player grid");
            foreach (string name in new[] { "SpeedCombo", "QualityCombo", "AutoNextCheck", "FullscreenButton" }) RequireInside(Control<FrameworkElement>(name), Control<FrameworkElement>("PlayerControls"), "restored " + name);
            return new { restoredSize = new[] { Window.Width, Window.Height }, controlsRestored = true };
        });
    }

    private static void SeedContent()
    {
        ObservableCollection<SeriesItem> catalog = Field<ObservableCollection<SeriesItem>>("_catalog");
        for (int index = 1; index <= 12; index++) catalog.Add(new SeriesItem { Id = index.ToString(), Title = index % 2 == 0 ? "全球死考 · 全新一季" : "故事从这里开始", EpisodeCount = 20 + index, Category = "悬疑" });
        var panel = new GenrePanel { Rows = Enumerable.Range(1, 5).Select(index => new GenreRow { Key = new[] { "category_dim_theme", "category_dim_role", "category_dim_epoch", "creation_status", "sort" }[index - 1], Name = new[] { "主题", "人物", "背景", "状态", "排序" }[index - 1], Options = Enumerable.Range(1, 12).Select(value => new GenreOption { Name = "题材选项" + value, Value = "value" + value }).ToList() }).ToList() };
        Invoke("RenderGenreOptions", panel);
        ObservableCollection<RankedSeriesItem> ranking = Field<ObservableCollection<RankedSeriesItem>>("_ranking");
        for (int index = 1; index <= 8; index++) ranking.Add(new RankedSeriesItem { Id = index.ToString(), Title = "第" + index + "部 · 全球死考与迷雾中的故事", Position = index, Rank = index, EpisodeCount = 24, Score = (912345 - index * 5421).ToString(CultureInfo.InvariantCulture), Intro = "一段关于选择与成长的悬疑故事，从这一刻开始。" });
        var boards = new List<RankBoardOption> { new() { Id = "ranklist_hot_play_sc", Name = "热播榜" }, new() { Id = "ranklist_hot_sc", Name = "推荐榜" }, new() { Id = "new", Name = "新剧榜" } };
        Invoke("UpdateRankOptions", new List<RankContentOption> { new() { Id = "all", Name = "全部", Boards = boards }, new() { Id = "human", Name = "真人剧", Boards = boards }, new() { Id = "comic_series_rank", Name = "漫剧", Boards = boards }, new() { Id = "ai_playlet", Name = "AI剧", Boards = boards }, new() { Id = "series_album", Name = "系列剧", Boards = boards } });
        Invoke("RenderRankGenreOptions", panel, "all");
        Control<TextBlock>("RankExplanation").Text = "榜单顺序、名次与热度来自平台；推荐位单独展示。";
        Control<TextBlock>("RankStatus").Text = "已加载 8 部 · 榜单顺序与热度由平台提供";
        Control<TextBlock>("RankingFreshnessText").Text = "每日自动更新 · 更新于 11-05 20:00";
        ObservableCollection<EpisodeItem> episodes = Field<ObservableCollection<EpisodeItem>>("_episodes");
        for (int index = 1; index <= 36; index++) episodes.Add(new EpisodeItem { Id = "ep" + index, SeriesId = "test", Number = index, Playable = true, Title = "第" + index + "集" });
        SetField("_currentSeries", new SeriesItem { Id = "test", Title = "全球死考 · 窗口布局验证", EpisodeCount = 36 });
        SetField("_currentEpisode", episodes[0]);
        Control<TextBlock>("PlayingTitle").Text = "全球死考 · 窗口布局验证";
        Control<TextBlock>("PlayingSubtitle").Text = "第 1 集 · 自适应画面 · 1 倍速 · 1080p";
        Control<TextBlock>("DetailTitle").Text = "全球死考 · 窗口布局验证";
        Control<TextBlock>("DetailDescription").Text = "此处使用本地数据检查窗口与控件布局，无网络请求。";
        Control<TextBlock>("EpisodeCountText").Text = "共 36 集";
        Control<TextBlock>("DetailCount").Text = "36 集";
        ObservableCollection<PlaybackQuality> qualities = Field<ObservableCollection<PlaybackQuality>>("_qualities");
        qualities.Add(new PlaybackQuality { Definition = "best" });
        qualities.Add(new PlaybackQuality { Definition = "1080p", Width = 1920, Height = 1080 });
        qualities.Add(new PlaybackQuality { Definition = "360p", Width = 640, Height = 360 });
        Control<ComboBox>("QualityCombo").SelectedIndex = 0;
        foreach (string name in new[] { "PauseButton", "PreviousButton", "NextButton", "SeekSlider", "QualityCombo" }) Control<UIElement>(name).IsEnabled = true;
        Control<Slider>("SeekSlider").Maximum = 180;
        Control<Slider>("SeekSlider").Value = 64;
        Control<TextBlock>("TimeText").Text = "01:04 / 03:00";
        Invoke("HighlightEpisode");
    }

    private static void Cleanup()
    {
        if (Window == null) return;
        SetField("_closing", true);
        Invoke("RestoreWindowControls");
        foreach (FieldInfo field in typeof(MainWindow).GetFields(PrivateInstance))
        {
            if (field.GetValue(Window) is Popup popup) popup.IsOpen = false;
            if (field.GetValue(Window) is DispatcherTimer timer) timer.Stop();
            if (field.GetValue(Window) is not CancellationTokenSource request) continue;
            request.Cancel();
            request.Dispose();
            field.SetValue(Window, null);
        }
        Control<NativeVideoHost>("VideoHost").Dispose();
        Window.Close();
    }

    private static async Task Check(string name, Func<Task<object>> test)
    { try { Record(name, true, await test()); } catch (Exception error) { Record(name, false, new { error = error.Message, type = error.GetType().Name }); } }
    private static void Record(string name, bool passed, object detail)
    { if (!passed) Failures++; Results.Add(new { name, passed, detail }); Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail)); }
    private static void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    private static Rect Bounds(FrameworkElement item, FrameworkElement ancestor) => item.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, item.ActualWidth, item.ActualHeight));
    private static void RequireInside(FrameworkElement item, FrameworkElement ancestor, string label)
    {
        Require(item.Visibility == Visibility.Visible && item.ActualWidth > 0 && item.ActualHeight > 0, label + " is hidden or empty");
        Rect bounds = Bounds(item, ancestor);
        Require(bounds.Left >= -2 && bounds.Right <= ancestor.ActualWidth + 2 && bounds.Top >= -2 && bounds.Bottom <= ancestor.ActualHeight + 2, label + " overflows: " + bounds + " within " + ancestor.ActualWidth + "x" + ancestor.ActualHeight);
    }
    private static async Task SettleAsync() { await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); Window.UpdateLayout(); await Task.Delay(100); }
    private static async Task CaptureAsync(string name) { await SettleAsync(); SaveImage(Control<FrameworkElement>("RootLayout"), "results/" + CurrentCase + "-" + name + ".png"); }
    private static async Task CapturePopupAsync(string name, UIElement child) { await SettleAsync(); SaveImage((FrameworkElement)child, "results/" + CurrentCase + "-" + name + ".png"); }
    private static void SaveImage(FrameworkElement root, string path)
    {
        int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext drawing = backdrop.RenderOpen()) drawing.DrawRectangle((Brush)Window.FindResource("WindowBrush"), null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T value) yield return value;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (T child in VisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static T Control<T>(string name) where T : class => Window.FindName(name) as T ?? throw new InvalidOperationException("control missing: " + name);
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(Window) ?? default(T)!);
    private static void SetField(string name, object value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(Window, value);
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(Window, args);
}
