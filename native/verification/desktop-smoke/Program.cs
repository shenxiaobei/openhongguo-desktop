using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const string SeriesId = "7675400266065136665";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Results = new();
    private static readonly string Output = Path.Combine(Environment.CurrentDirectory, "results");
    private static MainWindow? _window;
    private static int _failures;
    private static readonly Stopwatch Startup = new();
    private static long _firstCatalogItemMs = -1;
    private static int _coverChanges;
    private static string _searchQuery = "全球死考";
    private static string? _sdkReport;
    private static SeriesItem[] _historySeeds = Array.Empty<SeriesItem>();
    private static bool _coversOnly;
    private static bool _redesign;
    private static bool _preloadOnly;
    private static long _cachedManualRequests;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--preload-only"))
        {
            // v0.3.2 只准备地址；双播放器媒体缓存由独立宿主验证，保留旧报告。
            Console.WriteLine("下一集媒体预加载验证已迁移到 desktop-buffer-smoke。请从项目根目录运行：");
            Console.WriteLine("dotnet run --project native/verification/desktop-buffer-smoke/DesktopBufferSmoke.csproj -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4");
            Console.WriteLine("本次没有执行旧预准备断言，没有生成或覆盖测试报告。退出码 2 表示需要使用新入口。");
            return 2;
        }
        if (args.Length > 0) _sdkReport = Path.GetFullPath(args[0]);
        if (args.Length > 1) _searchQuery = args[1];
        _coversOnly = args.Contains("--covers-only");
        _redesign = args.Contains("--redesign");
        _preloadOnly = args.Contains("--preload-only");
        _redesign |= _preloadOnly;
        Directory.CreateDirectory(Output);
        var app = new System.Windows.Application();
        // 直接复用应用样式，测试宿主自己创建窗口，避免生产 App 的 StartupUri 再开一个窗口。
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"));
        XElement resources = source.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), resources.Elements());
        using (var reader = dictionary.CreateReader()) app.Resources = (ResourceDictionary)XamlReader.Load(reader);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync();
            }
            catch (Exception error)
            {
                Record("harness", false, new { error = Clean(error.Message), type = error.GetType().Name });
            }
            finally
            {
                if (_window != null)
                {
                    SetField("_closing", true);
                    Invoke("RestoreWindowControls");
                    Invoke("CancelNextPlayback");
                    Invoke("CancelDiscoveryRequests");
                    // 测试直接跳过生产 Closing 保存流程，需要同步释放所有新增计时器与请求。
                    foreach (FieldInfo field in typeof(MainWindow).GetFields(PrivateInstance))
                    {
                        if (field.GetValue(_window) is DispatcherTimer timer) timer.Stop();
                        if (field.GetValue(_window) is not CancellationTokenSource request) continue;
                        request.Cancel();
                        request.Dispose();
                        field.SetValue(_window, null);
                    }
                    Field<MpvPlayer?>("_player")?.Dispose();
                    Field<MpvPlayer?>("_standbyPlayer")?.Dispose();
                    (Field<object?>("_backend") as IDisposable)?.Dispose();
                    Control<NativeVideoHost>("VideoHost").Dispose();
                    _window.Close();
                }
                SaveReport();
                app.Shutdown(_failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync()
    {
        string state = Path.Combine(Environment.CurrentDirectory, "state", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(state);
        string existing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openhongguo", "anonymous.json");
        if (File.Exists(existing)) File.Copy(existing, Path.Combine(state, "anonymous.json"));

        _window = new MainWindow
        {
            ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -3000, Top = 40, Opacity = 0
        };
        SetField("_configDir", state);
        ((INotifyCollectionChanged)Field<object>("_catalog")).CollectionChanged += (_, change) =>
        {
            if (_firstCatalogItemMs < 0 && change.NewItems?.Count > 0) _firstCatalogItemMs = Startup.ElapsedMilliseconds;
            if (change.NewItems == null) return;
            foreach (object value in change.NewItems)
            {
                if (value is System.ComponentModel.INotifyPropertyChanged changed)
                    changed.PropertyChanged += (_, property) => { if (property.PropertyName is "CoverUrl" or "CoverUri") _coverChanges++; };
            }
        };
        System.Windows.Application.Current.MainWindow = _window;
        Startup.Start();
        _window.Show();
        await Task.Delay(150);
        if (_preloadOnly)
        {
            await WaitAsync(() => Field<object?>("_backend") != null && !Field<bool>("_catalogBusy"), "backend startup did not complete");
            if (Field<Task?>("_catalogHydrationTask") is Task initialHydration) await initialHydration;
            await VerifyPreloadingAsync();
            return;
        }

        await CheckAsync("catalog", async () =>
        {
            await WaitAsync(() => !Field<bool>("_catalogBusy") && Catalog.Count > 0, "catalog returned no items");
            return new { count = Catalog.Count, status = Control<TextBlock>("CatalogStatus").Text,
                firstItemMs = _firstCatalogItemMs, readyMs = Startup.ElapsedMilliseconds,
                hydrationInProgress = Field<int>("_catalogHydrating"),
                hydrationTaskCompleted = Field<Task?>("_catalogHydrationTask")?.IsCompleted,
                initialCovers = Catalog.Count(value => value.CoverUri != null) };
        });

        await CheckAsync("background_catalog_hydration", async () =>
        {
            Task? hydration = Field<Task?>("_catalogHydrationTask");
            if (hydration != null) await hydration.WaitAsync(TimeSpan.FromSeconds(55));
            Require(Catalog.Count > 0 && !Field<bool>("_catalogBusy"), "catalog disappeared during hydration");
            return new { count = Catalog.Count, finalCovers = Catalog.Count(value => value.CoverUri != null), coverPropertyChanges = _coverChanges,
                hydrationInProgress = Field<int>("_catalogHydrating") };
        });

        await CheckAsync("catalog_cover_decode", async () =>
        {
            SeriesItem item = Catalog.FirstOrDefault(value => value.CoverUri != null) ?? throw new InvalidOperationException("no catalog cover URI");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            byte[] image = await http.GetByteArrayAsync(item.CoverUri);
            string format = ImageFormat(image);
            Require(image.Length > 100, "cover response is empty");
            await WaitAsync(() => VisualChildren<System.Windows.Controls.Image>(Control<ItemsControl>("CatalogItems"))
                .Any(value => value.DataContext is SeriesItem bound && bound.Id == item.Id && value.Source is BitmapSource bitmap && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0), "matching WPF cover decode timed out", 30_000);
            BitmapSource poster = (BitmapSource)VisualChildren<System.Windows.Controls.Image>(Control<ItemsControl>("CatalogItems"))
                .First(value => value.DataContext is SeriesItem bound && bound.Id == item.Id && value.Source is BitmapSource source && source.PixelWidth > 0).Source;
            Require(poster.PixelHeight > poster.PixelWidth, "catalog cover is a landscape video frame instead of a portrait poster");
            int expectedImages = Catalog.Count(value => value.CoverUri != null);
            await WaitAsync(() => VisualChildren<System.Windows.Controls.Image>(Control<ItemsControl>("CatalogItems")).Count(value => value.Source is BitmapSource source && source.PixelWidth > 0) >= expectedImages,
                "some catalog posters did not finish decoding", 25_000);
            return new { format, bytes = image.Length, posterWidth = poster.PixelWidth, posterHeight = poster.PixelHeight,
                expectedImages, renderedImages = VisualChildren<System.Windows.Controls.Image>(Control<ItemsControl>("CatalogItems")).Count(value => value.Source is BitmapSource source && source.PixelWidth > 0) };
        });
        await CheckAsync("catalog_layout", async () => await CaptureAsync("catalog.png"));
        await CheckAsync("persisted_catalog_before_backend", VerifySavedCatalogAsync);
        if (_coversOnly) return;

        for (int tabIndex = 0; tabIndex < 3; tabIndex++)
        {
            int index = tabIndex;
            await CheckAsync("search_tab_" + new[] { "all", "series", "video" }[index], async () => await VerifySearchAsync(index));
        }

        if (_redesign) await VerifyRankingAsync();

        await CheckAsync("network_playback", async () =>
        {
            await InvokeTaskAsync("OpenSeriesAsync", SeriesId, true, 0).WaitAsync(TimeSpan.FromSeconds(60));
            await WaitAsync(() => Player?.GetStatus().Loaded == true, "network player did not load");
            MpvStatus status = Player!.GetStatus();
            Require(status.VideoWidth > 0 && status.VideoHeight > 0 && status.Duration > 0, "video metadata missing");
            await WaitAsync(() => Control<Slider>("SeekSlider").IsEnabled, "timeline did not enable", 5_000);
            return new { series = CurrentSeries?.Id, episode = CurrentEpisode?.Number, width = status.VideoWidth, height = status.VideoHeight, duration = Math.Round(status.Duration, 2), timeline = Control<Slider>("SeekSlider").IsEnabled };
        });

        await CheckAsync("pause_seek_speed", async () =>
        {
            Require(Player?.GetStatus().Loaded == true, "playback prerequisite missing");
            Invoke("TogglePause");
            await WaitAsync(() => Player!.GetStatus().Paused, "pause failed", 5_000);
            Control<ComboBox>("SpeedCombo").SelectedIndex = 5;
            double target = Math.Min(8, Player!.GetStatus().Duration / 4);
            Player.Seek(target);
            await WaitAsync(() => Math.Abs(Player.GetStatus().Position - target) < 0.35, "seek failed", 10_000);
            Require(Math.Abs(Player.GetStatus().Speed - 2) < 0.01, "speed control failed");
            Invoke("TogglePause");
            await WaitAsync(() => !Player.GetStatus().Paused, "resume failed", 5_000);
            return new { target, position = Math.Round(Player.GetStatus().Position, 2), speed = Player.GetStatus().Speed };
        });

        await CheckAsync("speed_popup_readable", async () => await CaptureComboPopupAsync("SpeedCombo", "speed-popup.png"));
        await CheckAsync("quality_switch_preserves_state", VerifyQualitySwitchAsync);
        await CheckAsync("quality_popup_readable", async () => await CaptureComboPopupAsync("QualityCombo", "quality-popup.png"));

        await CheckAsync("resize_host", async () =>
        {
            _window.Width = 1100;
            _window.Height = 720;
            await SettleAsync();
            var small = HostSize();
            _window.Width = 1380;
            _window.Height = 900;
            await SettleAsync();
            var large = HostSize();
            Require(large.Width > small.Width + 100 && large.Height > small.Height + 50, "video host did not follow window resize");
            return new { small, large };
        });
        await CheckAsync("player_layout", async () => await CaptureAsync("player.png"));
        if (_redesign)
        {
            await CheckAsync("player_controls_minimum_window", VerifyCompactControlsAsync);
            await CheckAsync("player_control_styles", VerifyControlStylesAsync);
        }

        await CheckAsync("fullscreen", async () =>
        {
            Invoke("ToggleFullscreen");
            await SettleAsync();
            var full = HostSize();
            Require(Field<bool>("_fullscreen") && _window.WindowStyle == WindowStyle.None && _window.WindowState == WindowState.Maximized, "fullscreen window state failed");
            Require(Control<FrameworkElement>("NavigationPanel").Visibility == Visibility.Collapsed && Control<FrameworkElement>("EpisodeSidebar").Visibility == Visibility.Collapsed, "fullscreen chrome remains visible");
            await CaptureAsync("fullscreen.png");
            object? controls = null;
            if (_redesign) controls = await VerifyFullscreenControlsAsync();
            Invoke("ToggleFullscreen");
            _window.Left = -3000;
            await SettleAsync();
            Require(!Field<bool>("_fullscreen") && _window.WindowStyle == WindowStyle.SingleBorderWindow, "fullscreen restore failed");
            if (_redesign)
            {
                Require(Control<FrameworkElement>("NavigationPanel").Visibility == Visibility.Collapsed && Control<FrameworkElement>("HeaderPanel").Visibility == Visibility.Collapsed,
                    "leaving fullscreen did not restore the cinema layout");
                Require(ReferenceEquals(VisualTreeHelper.GetParent(Control<FrameworkElement>("PlayerControls")), Control<Grid>("CinemaLayout")),
                    "fullscreen controls did not return to the cinema grid");
                Require(Field<Popup?>("_fullscreenControls") == null, "fullscreen popup remained after exit");
            }
            return new { full, restored = HostSize(), controls };
        });

        int eof = 0;
        if (Player != null) Player.Ended += (_, _) => eof++;
        await CheckAsync("natural_eof_auto_next", async () =>
        {
            Require(Player?.GetStatus().Loaded == true && CurrentEpisode != null, "playback prerequisite missing");
            string previous = CurrentEpisode!.Id;
            int number = CurrentEpisode.Number;
            Control<CheckBox>("AutoNextCheck").IsChecked = true;
            Player!.SetPaused(false);
            Player.Seek(Math.Max(0, Player.GetStatus().Duration - 1.2));
            await WaitAsync(() => CurrentEpisode != null && CurrentEpisode.Id != previous && Player.GetStatus().Loaded, "automatic next episode timed out");
            Require(CurrentEpisode!.Number > number && eof == 1, "automatic next episode did not follow one natural EOF");
            return new { before = number, after = CurrentEpisode.Number, eof };
        });

        await CheckAsync("stop_no_auto_next", async () =>
        {
            Require(Player != null && CurrentEpisode != null, "player prerequisite missing");
            string episode = CurrentEpisode!.Id;
            int before = eof;
            Player!.Stop();
            await Task.Delay(1200);
            Require(eof == before && CurrentEpisode.Id == episode && !Player.GetStatus().Loaded, "stop triggered an episode transition");
            return new { eofDelta = eof - before, unchangedEpisode = true };
        });

        await CheckAsync("feed_distinct_series", async () =>
        {
            var played = new HashSet<string>();
            var unavailable = new List<object>();
            for (int attempt = 0; attempt < 5 && played.Count < 2; attempt++)
            {
                if (attempt == 0) await InvokeTaskAsync("RestartFeedAsync");
                else
                {
                    await Task.Delay(700);
                    await InvokeTaskAsync("MoveRecommendationAsync", 1);
                }
                await WaitAsync(() => Player?.GetStatus().Loaded == true || Control<FrameworkElement>("NoticeBar").Visibility == Visibility.Visible, "recommendation decoder timed out", 10_000);
                if (CurrentSeries != null && Player?.GetStatus().Loaded == true) played.Add(CurrentSeries.Id);
                else unavailable.Add(new { attempt, series = CurrentSeries?.Id, notice = Clean(Control<TextBlock>("NoticeText").Text) });
            }
            Require(played.Count >= 2, "fewer than two recommendations played within five attempts");
            Require(Field<bool>("_feedMode"), "feed mode is disabled");
            return new { played = played.ToArray(), unavailable, queued = Field<IList>("_feedQueue").Count, hasCover = Control<System.Windows.Controls.Image>("CurrentCover").Source is BitmapSource };
        });
        await CheckAsync("feed_layout", async () => await CaptureAsync("feed.png"));
        await CheckAsync("history_cover_cards", VerifyHistoryCardsAsync);
    }

    private static async Task VerifyRankingAsync()
    {
        await CheckAsync("ranking_first_page", async () =>
        {
            if (Field<Task?>("_catalogHydrationTask") is Task hydration) await hydration;
            JsonElement response = await CaptureSearchResponseAsync(() => Control<Button>("RankingNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Count > 0, "ranking first page did not load", 45_000);
            await SettleAsync();
            AssertRankingResponse(response, response.GetProperty("items").EnumerateArray().ToArray());
            Require(Control<FrameworkElement>("RankingScreen").Visibility == Visibility.Visible, "ranking page is not visible");
            Require(Field<string>("_rankContent") == "all" && Field<string>("_rankBoard") == "ranklist_hot_play_sc", "initial ranking is not the real hot-play board");
            Require(Control<ComboBox>("RankContentCombo").Items.Cast<RankContentOption>().All(item => item.Id != "ranklist_celebrity"), "actor rows were offered as playable series rankings");
            await WaitAsync(() => VisualChildren<System.Windows.Controls.Image>(Control<ItemsControl>("RankingItems")).Any(image => image.Source is BitmapSource), "ranking posters did not decode", 25_000);
            await CaptureAsync("ranking.png");
            return RankDetails();
        });

        await CheckAsync("ranking_second_page", async () =>
        {
            Require(Field<bool>("_rankHasMore"), "ranking returned no second page");
            RankedSeriesItem[] first = Ranking.ToArray();
            ScrollViewer scroll = Control<ScrollViewer>("RankScroll");
            scroll.ScrollToVerticalOffset(Math.Min(100, scroll.ScrollableHeight));
            await SettleAsync();
            double before = scroll.VerticalOffset;
            JsonElement response = await CaptureSearchResponseAsync(() => _ = InvokeTaskAsync("LoadRankingAsync", true));
            await WaitAsync(() => !Field<bool>("_rankBusy"), "ranking second page did not complete", 45_000);
            await SettleAsync();
            JsonElement[] expected = JsonSerializer.SerializeToElement(first).EnumerateArray().Concat(response.GetProperty("items").EnumerateArray()).ToArray();
            AssertRankingResponse(response, expected);
            Require(Ranking.Count > first.Length && Ranking[first.Length].Position > first[^1].Position, "ranking positions did not continue across pages");
            Require(Math.Abs(scroll.VerticalOffset - before) < 1, "ranking append reset the scroll position");
            return new { firstCount = first.Length, count = Ranking.Count, lastPosition = Ranking[^1].Position, before, after = scroll.VerticalOffset };
        });

        await CheckAsync("ranking_change_board", async () =>
        {
            ComboBox boards = Control<ComboBox>("RankBoardCombo");
            RankBoardOption target = boards.Items.Cast<RankBoardOption>().FirstOrDefault(item => item.Name == "新剧榜")
                ?? boards.Items.Cast<RankBoardOption>().First(item => item.Id != Field<string>("_rankBoard"));
            JsonElement response = await CaptureSearchResponseAsync(() => boards.SelectedItem = target);
            await WaitAsync(() => !Field<bool>("_rankBusy") && Field<string>("_rankBoard") == target.Id, "ranking board change did not complete", 45_000);
            await SettleAsync();
            AssertRankingResponse(response, response.GetProperty("items").EnumerateArray().ToArray());
            Require(Control<ScrollViewer>("RankScroll").VerticalOffset < 1, "new ranking board retained old scroll offset");
            return new { board = target.Id, name = target.Name, count = Ranking.Count };
        });

        await CheckAsync("ranking_change_content", async () =>
        {
            ComboBox contents = Control<ComboBox>("RankContentCombo");
            RankContentOption target = contents.Items.Cast<RankContentOption>().First(item => item.Id == "human");
            JsonElement response = await CaptureSearchResponseAsync(() => contents.SelectedItem = target);
            await WaitAsync(() => !Field<bool>("_rankBusy") && Field<string>("_rankContent") == target.Id, "ranking content change did not complete", 45_000);
            await SettleAsync();
            AssertRankingResponse(response, response.GetProperty("items").EnumerateArray().ToArray());
            Require(((RankBoardOption?)Control<ComboBox>("RankBoardCombo").SelectedItem)?.Id == Field<string>("_rankBoard"), "board selector did not follow the server default for the selected content");
            return RankDetails();
        });

        await CheckAsync("ranking_open_and_return", async () =>
        {
            Require(Ranking.Count > 0, "ranking has no series to open");
            string[] before = Ranking.Select(item => item.Id).ToArray();
            string content = Field<string>("_rankContent");
            string board = Field<string>("_rankBoard");
            ScrollViewer scroll = Control<ScrollViewer>("RankScroll");
            scroll.ScrollToVerticalOffset(Math.Min(100, scroll.ScrollableHeight));
            await SettleAsync();
            double offset = scroll.VerticalOffset;
            RankedSeriesItem item = Ranking[0];
            Invoke("RankSeries_Click", new Button { Tag = item }, new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => Control<FrameworkElement>("PlayerScreen").Visibility == Visibility.Visible && (CurrentSeries?.Id == item.Id || Control<FrameworkElement>("NoticeBar").Visibility == Visibility.Visible),
                "ranked series did not open its player", 45_000);
            await WaitAsync(() => Player?.GetStatus().Loaded == true || Control<FrameworkElement>("NoticeBar").Visibility == Visibility.Visible
                || (CurrentSeries?.Id == item.Id && CurrentEpisode == null && Control<TextBlock>("PlayingSubtitle").Text.Contains("其他剧集", StringComparison.Ordinal)),
                "ranked playback did not settle", 45_000);
            Require(ReferenceEquals(Field<UIElement?>("_playerReturnScreen"), Control<FrameworkElement>("RankingScreen")), "ranked playback lost its return destination");
            bool detailsOpened = CurrentSeries?.Id == item.Id;
            bool loaded = Player?.GetStatus().Loaded == true;
            string notice = Clean(Control<TextBlock>("NoticeText").Text);
            Invoke("Back_Click", _window!, new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => Control<FrameworkElement>("RankingScreen").Visibility == Visibility.Visible, "back did not return to ranking", 10_000);
            await SettleAsync();
            Require(content == Field<string>("_rankContent") && board == Field<string>("_rankBoard") && before.SequenceEqual(Ranking.Select(value => value.Id)), "returning from ranked playback changed the selected board or rows");
            Require(Math.Abs(offset - scroll.VerticalOffset) < 1, "returning from ranked playback reset scroll position");
            Require(Control<FrameworkElement>("NavigationPanel").Visibility == Visibility.Visible && Control<FrameworkElement>("HeaderPanel").Visibility == Visibility.Visible, "back did not restore browsing navigation");
            return new { series = item.Id, detailsOpened, loaded, notice, board, content, offset, returnedOffset = scroll.VerticalOffset };
        });
    }

    private static async Task VerifyPreloadingAsync()
    {
        MpvPlayer? decoder = null;
        await CheckAsync("preload_initial_playback", async () =>
        {
            await InvokeTaskAsync("OpenSeriesAsync", SeriesId, true, 0).WaitAsync(TimeSpan.FromSeconds(45));
            await WaitAsync(() => Player?.GetStatus().Loaded == true, "initial preload test episode did not load", 30_000);
            Player!.SetPaused(true);
            decoder = Player;
            PlaybackInfo playback = await WaitPreparedPlaybackAsync();
            EpisodeItem next = NextPlayableEpisode();
            Require(Field<string>("_nextPlaybackEpisodeId") == next.Id && Field<string>("_nextPlaybackQuality") == "best", "prepared playback does not match the next best-quality episode");
            return new { current = CurrentEpisode?.Number, prepared = next.Number, quality = Field<string>("_nextPlaybackQuality"), hasExpiry = playback.ExpiresAt > 0,
                expiresInSeconds = playback.ExpiresAt > 0 ? playback.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0 };
        });

        await CheckAsync("preload_consumes_cached_response_without_request", async () =>
        {
            PlaybackInfo prepared = await WaitPreparedPlaybackAsync();
            EpisodeItem next = NextPlayableEpisode();
            long before = BackendSequence();
            var take = (Task<PlaybackInfo?>)Invoke("TakePreparedPlaybackAsync", next.Id, "best", CancellationToken.None)!;
            PlaybackInfo? result = await take;
            long requests = BackendSequence() - before;
            Require(ReferenceEquals(prepared, result) && requests == 0, "prepared response was not reused locally");
            Require(Field<Task<PlaybackInfo?>?>("_nextPlaybackTask") == null, "consumed prepared slot was not cleared");
            Invoke("PrepareNextPlayback", true);
            await WaitPreparedPlaybackAsync();
            return new { preparedEpisode = next.Number, reusedSameResponse = true, backendRequests = requests };
        });

        await CheckAsync("preload_toggle_cancels_and_restores", async () =>
        {
            await WaitPreparedPlaybackAsync();
            CancellationTokenSource previous = Field<CancellationTokenSource>("_nextPlaybackRequest");
            Control<CheckBox>("AutoNextCheck").IsChecked = false;
            Require(Field<Task<PlaybackInfo?>?>("_nextPlaybackTask") == null && Field<string>("_nextPlaybackEpisodeId") == "" && previous.IsCancellationRequested,
                "disabling automatic next episode did not cancel its prepared slot");
            Control<CheckBox>("AutoNextCheck").IsChecked = true;
            await WaitPreparedPlaybackAsync();
            return new { cancelled = true, restoredEpisode = NextPlayableEpisode().Number, quality = Field<string>("_nextPlaybackQuality") };
        });

        await CheckAsync("preload_next_quality_remains_best", async () =>
        {
            Require(Player?.GetStatus().Loaded == true, "current playback missing before quality check");
            double position = Math.Min(8, Player!.GetStatus().Duration / 4);
            Player.Seek(position);
            await WaitAsync(() => Math.Abs(Player.GetStatus().Position - position) < 0.35, "preload quality setup seek failed", 5_000);
            await InvokeTaskAsync("ChangeQualityAsync", "360p").WaitAsync(TimeSpan.FromSeconds(35));
            await WaitAsync(() => Player.GetStatus().Loaded, "temporary 360p playback did not load", 25_000);
            await WaitPreparedPlaybackAsync();
            MpvStatus status = Player.GetStatus();
            Require(status.VideoHeight == 360 && status.Paused && Math.Abs(status.Position - position) < 1, "temporary quality switch changed paused progress");
            Require(Field<string>("_nextPlaybackQuality") == "best", "next episode inherited the current temporary quality");
            await CaptureAsync("player.png");
            return new { currentHeight = status.VideoHeight, nextQuality = Field<string>("_nextPlaybackQuality"), position = status.Position, paused = status.Paused };
        });
        await CheckAsync("preload_controls_capture", VerifyControlStylesAsync);
        await CheckAsync("preload_compact_controls", VerifyCompactControlsAsync);

        await CheckAsync("preload_natural_eof_latency", async () =>
        {
            Require(Player != null && CurrentEpisode != null, "playback missing before EOF latency check");
            await WaitPreparedPlaybackAsync();
            EpisodeItem next = NextPlayableEpisode();
            long eofTimestamp = 0;
            int eofCount = 0;
            EventHandler ended = (_, _) =>
            {
                Interlocked.CompareExchange(ref eofTimestamp, Stopwatch.GetTimestamp(), 0);
                Interlocked.Increment(ref eofCount);
            };
            Player!.Ended += ended;
            try
            {
                long before = BackendSequence();
                Player.SetPaused(false);
                Player.Seek(Math.Max(0, Player.GetStatus().Duration - 0.7));
                await WaitAsync(() => CurrentEpisode?.Id == next.Id && Player.GetStatus().Loaded, "prepared automatic next episode did not decode", 30_000);
                long loadedTimestamp = Stopwatch.GetTimestamp();
                Player.SetPaused(true);
                Require(eofTimestamp > 0 && eofCount == 1, "next episode did not follow exactly one natural EOF");
                Require(ReferenceEquals(decoder, Player), "prepared next episode created a second decoder");
                await WaitPreparedPlaybackAsync();
                double elapsed = (loadedTimestamp - eofTimestamp) * 1000.0 / Stopwatch.Frequency;
                Require(elapsed >= 0 && elapsed < 10_000, "prepared EOF transition exceeded the latency check bound");
                return new { episode = next.Number, eofToLoadedMs = Math.Round(elapsed, 1), pollResolutionMs = 80, backendRequestsIncludingHistoryAndFollowingPreparation = BackendSequence() - before,
                    sameDecoder = true, preparedFollowingEpisode = NextPlayableEpisode().Number, restoredBestHeight = Player.GetStatus().VideoHeight };
            }
            finally { Player.Ended -= ended; }
        });

        await CheckAsync("preload_manual_cached_transition", async () =>
        {
            await WaitPreparedPlaybackAsync();
            EpisodeItem next = NextPlayableEpisode();
            long before = BackendSequence();
            await InvokeTaskAsync("PlayEpisodeAsync", next, false).WaitAsync(TimeSpan.FromSeconds(35));
            await WaitAsync(() => CurrentEpisode?.Id == next.Id && Player?.GetStatus().Loaded == true, "cached manual transition did not decode", 25_000);
            Player!.SetPaused(true);
            await WaitPreparedPlaybackAsync();
            _cachedManualRequests = BackendSequence() - before;
            Require(ReferenceEquals(decoder, Player), "manual next episode created a second decoder");
            return new { episode = next.Number, backendRequestsIncludingHistoryAndFollowingPreparation = _cachedManualRequests, preparedFollowingEpisode = NextPlayableEpisode().Number };
        });

        await CheckAsync("preload_expired_response_falls_back", async () =>
        {
            await WaitPreparedPlaybackAsync();
            EpisodeItem next = NextPlayableEpisode();
            SetField("_nextPlaybackCreatedAt", DateTime.UtcNow.AddSeconds(-80));
            long before = BackendSequence();
            await InvokeTaskAsync("PlayEpisodeAsync", next, false).WaitAsync(TimeSpan.FromSeconds(35));
            await WaitAsync(() => CurrentEpisode?.Id == next.Id && Player?.GetStatus().Loaded == true, "expired preparation did not fall back to ordinary playback", 25_000);
            Player!.SetPaused(true);
            await WaitPreparedPlaybackAsync();
            long requests = BackendSequence() - before;
            Require(requests == _cachedManualRequests + 1, "expired preparation did not add exactly one formal playback request");
            return new { episode = next.Number, cachedTransitionRequests = _cachedManualRequests, expiredTransitionRequests = requests, additionalFormalPlaybackRequests = 1 };
        });

        await CheckAsync("preload_cancelled_request_falls_back", async () =>
        {
            EpisodeItem next = NextPlayableEpisode();
            object backend = Field<object>("_backend");
            var writer = (SemaphoreSlim)backend.GetType().GetField("writeLock", PrivateInstance)!.GetValue(backend)!;
            await writer.WaitAsync();
            Task<PlaybackInfo?> cancelled;
            try
            {
                Invoke("CancelNextPlayback");
                Invoke("PrepareNextPlayback", true);
                cancelled = Field<Task<PlaybackInfo?>>("_nextPlaybackTask");
                Field<CancellationTokenSource>("_nextPlaybackRequest").Cancel();
                Require(await cancelled.WaitAsync(TimeSpan.FromSeconds(5)) == null, "cancelled preparation did not settle as an optional miss");
            }
            finally { writer.Release(); }
            await InvokeTaskAsync("PlayEpisodeAsync", next, false).WaitAsync(TimeSpan.FromSeconds(35));
            await WaitAsync(() => CurrentEpisode?.Id == next.Id && Player?.GetStatus().Loaded == true, "cancelled preparation prevented ordinary playback", 25_000);
            Player!.SetPaused(true);
            await WaitPreparedPlaybackAsync();
            return new { episode = next.Number, cancelledPreparationReturnedNull = true, ordinaryPlaybackLoaded = true };
        });

        await CheckAsync("preload_manual_skip_replaces_next_slot", async () =>
        {
            await WaitPreparedPlaybackAsync();
            string skipped = Field<string>("_nextPlaybackEpisodeId");
            CancellationTokenSource old = Field<CancellationTokenSource>("_nextPlaybackRequest");
            EpisodeItem target = Field<IEnumerable<EpisodeItem>>("_episodes").First(item => item.Playable && item.Number > CurrentEpisode!.Number + 1);
            await InvokeTaskAsync("PlayEpisodeAsync", target, false).WaitAsync(TimeSpan.FromSeconds(35));
            await WaitAsync(() => CurrentEpisode?.Id == target.Id && Player?.GetStatus().Loaded == true, "manual skipped episode did not load", 25_000);
            Player!.SetPaused(true);
            await WaitPreparedPlaybackAsync();
            Require(old.IsCancellationRequested && Field<string>("_nextPlaybackEpisodeId") == NextPlayableEpisode().Id && Field<string>("_nextPlaybackEpisodeId") != skipped,
                "manual skip kept the old series position in the prepared slot");
            return new { skippedEpisodeId = skipped, currentEpisode = target.Number, preparedFollowingEpisode = NextPlayableEpisode().Number, oldPreparationCancelled = true };
        });

        await CheckAsync("preload_stop_does_not_advance", async () =>
        {
            string current = CurrentEpisode!.Id;
            int events = 0;
            EventHandler ended = (_, _) => events++;
            Player!.Ended += ended;
            try
            {
                Player.Stop();
                await Task.Delay(1000);
                Require(events == 0 && CurrentEpisode?.Id == current, "stop caused a prepared automatic transition");
                return new { events, sameEpisode = true };
            }
            finally { Player.Ended -= ended; }
        });

        await CheckAsync("redesign_ranking_action_capture", async () =>
        {
            Control<Button>("RankingNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Count > 0 && Control<FrameworkElement>("RankingScreen").Visibility == Visibility.Visible, "ranking refresh did not finish", 35_000);
            await WaitAsync(() => VisualChildren<System.Windows.Controls.Image>(Control<ItemsControl>("RankingItems")).Any(image => image.Source is BitmapSource), "ranking posters did not decode", 25_000);
            Require(VisualChildren<TextBlock>(Control<ItemsControl>("RankingItems")).Any(text => text.Text.Contains("立即观看", StringComparison.Ordinal)), "ranking row does not expose its watch action");
            return await CaptureAsync("ranking.png");
        });
    }

    private static async Task<PlaybackInfo> WaitPreparedPlaybackAsync()
    {
        await WaitAsync(() => Field<Task<PlaybackInfo?>?>("_nextPlaybackTask")?.IsCompleted == true, "next playback preparation did not complete", 35_000);
        PlaybackInfo? result = await Field<Task<PlaybackInfo?>>("_nextPlaybackTask");
        Require(result != null, "next playback preparation returned no metadata");
        return result!;
    }

    private static EpisodeItem NextPlayableEpisode() => Field<IEnumerable<EpisodeItem>>("_episodes").First(item => item.Playable && item.Number > CurrentEpisode!.Number);

    private static long BackendSequence()
    {
        object backend = Field<object>("_backend");
        return (long)backend.GetType().GetField("requestSequence", PrivateInstance)!.GetValue(backend)!;
    }

    private static void AssertRankingResponse(JsonElement response, JsonElement[] expected)
    {
        RankedSeriesItem[] actual = Ranking.ToArray();
        Require(actual.Length == expected.Length, "ranking UI row count differs from its own backend response");
        for (int index = 0; index < expected.Length; index++)
        {
            JsonElement row = expected[index];
            RankedSeriesItem displayed = actual[index];
            Require(displayed.Id == row.GetProperty("series_id").GetString(), "ranking series order differs from response");
            Require(displayed.Position == JsonInt(row, "position") && displayed.Rank == JsonInt(row, "rank"), "ranking position or service rank was rewritten");
            bool promoted = row.TryGetProperty("promoted", out JsonElement value) && value.GetBoolean();
            Require(displayed.IsPromoted == promoted, "ranking promoted flag differs from response");
            Require(displayed.Score == (row.TryGetProperty("score", out value) ? value.GetString() ?? "" : ""), "ranking score differs from response");
            Require(!promoted || displayed.ScoreLabel == "平台推荐", "promoted row is presented as organic heat");
        }
        string cursor = response.TryGetProperty("cursor", out JsonElement next) ? next.GetString() ?? "" : "";
        bool hasMore = response.GetProperty("has_more").GetBoolean() && cursor.Length > 0;
        Require(cursor == Field<string>("_rankCursor") && hasMore == Field<bool>("_rankHasMore"), "ranking cursor or hasMore differs from response");
    }

    private static int JsonInt(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement field) ? field.GetInt32() : 0;

    private static object RankDetails() => new
    {
        content = Field<string>("_rankContent"), board = Field<string>("_rankBoard"), count = Ranking.Count,
        rows = Ranking.Select(item => new { id = item.Id, position = item.Position, rank = item.Rank, promoted = item.IsPromoted, score = item.ScoreLabel }),
        hasMore = Field<bool>("_rankHasMore")
    };

    private static async Task<object> VerifyCompactControlsAsync()
    {
        _window!.Width = 950;
        _window.Height = 640;
        await SettleAsync();
        FrameworkElement controls = Control<FrameworkElement>("PlayerControls");
        string[] names = { "PreviousButton", "PauseButton", "NextButton", "SpeedCombo", "QualityCombo", "AutoNextCheck", "VolumeSlider", "FullscreenButton" };
        var bounds = new List<object>();
        foreach (string name in names)
        {
            FrameworkElement child = Control<FrameworkElement>(name);
            Point origin = child.TranslatePoint(new Point(), controls);
            Require(child.ActualWidth > 0 && child.ActualHeight > 0 && origin.X >= -1 && origin.Y >= -1 && origin.X + child.ActualWidth <= controls.ActualWidth + 1 && origin.Y + child.ActualHeight <= controls.ActualHeight + 1,
                "player control is outside its narrow-window toolbar: " + name);
            bounds.Add(new { name, x = Math.Round(origin.X, 1), y = Math.Round(origin.Y, 1), width = Math.Round(child.ActualWidth, 1), height = Math.Round(child.ActualHeight, 1) });
        }
        Require(Grid.GetRow(Control<FrameworkElement>("SettingsGroup")) == 1, "compact player controls did not wrap settings into a second row");
        Size host = HostSize();
        Require(host.Width > 400 && host.Height > 250, "minimum window leaves insufficient room for video");
        await CaptureAsync("player-small.png");
        _window.Width = 1380;
        _window.Height = 900;
        await SettleAsync();
        return new { width = controls.ActualWidth, compactHost = host, bounds };
    }

    private static async Task<object> VerifyControlStylesAsync()
    {
        Slider seek = Control<Slider>("SeekSlider");
        seek.ApplyTemplate();
        Track track = seek.Template.FindName("PART_Track", seek) as Track ?? throw new InvalidOperationException("styled seek slider lost its standard Track");
        Require(track.Thumb != null, "seek slider thumb is missing");
        track.Thumb!.ApplyTemplate();
        System.Windows.Shapes.Ellipse? dot = VisualChildren<System.Windows.Shapes.Ellipse>(track.Thumb).FirstOrDefault();
        Require(dot != null && dot.ActualWidth <= 16 && dot.ActualHeight <= 16, "seek slider does not render a small circular thumb");
        Require(seek.ActualHeight >= 20 && seek.ActualWidth > 200, "seek slider hit area is too small");
        Slider volume = Control<Slider>("VolumeSlider");
        volume.ApplyTemplate();
        Require(volume.Template.FindName("PART_Track", volume) is Track, "styled volume slider lost its standard Track");
        CheckBox autoNext = Control<CheckBox>("AutoNextCheck");
        autoNext.ApplyTemplate();
        var toggles = VisualChildren<Border>(autoNext).Where(border => border.ActualWidth >= 30 && border.ActualWidth <= 46 && border.CornerRadius.TopLeft >= 8).ToArray();
        Require(toggles.Length > 0, "automatic-next checkbox is not rendered as a compact switch");
        var scrollbars = VisualChildren<ScrollBar>(Control<FrameworkElement>("EpisodeSidebar")).Where(bar => bar.Orientation == Orientation.Vertical).ToArray();
        Require(scrollbars.Length > 0 && scrollbars.All(bar => bar.ActualWidth <= 12), "episode scrollbar still uses wide system chrome");
        Control<Button>("NextButton").Focus();
        await SettleAsync();
        FrameworkElement controls = Control<FrameworkElement>("PlayerControls");
        RenderVisual(controls, "player-controls.png", _window!.Background);
        await CaptureAsync("control-focus.png");
        return new { seekThumbWidth = dot!.ActualWidth, seekThumbHeight = dot.ActualHeight, seekHitHeight = seek.ActualHeight,
            switchTrackCount = toggles.Length, scrollbarWidths = scrollbars.Select(bar => bar.ActualWidth).ToArray() };
    }

    private static async Task<object> VerifyFullscreenControlsAsync()
    {
        Popup popup = Field<Popup?>("_fullscreenControls") ?? throw new InvalidOperationException("fullscreen controls popup is missing");
        Require(popup.IsOpen, "fullscreen controls did not initially appear");
        Size visible = HostSize();
        FrameworkElement root = (FrameworkElement)_window!.Content;
        Require(visible.Height >= root.ActualHeight - 2 && visible.Width >= root.ActualWidth - 2, "fullscreen toolbar still reserves video layout space");
        await WaitAsync(() => !popup.IsOpen, "fullscreen toolbar did not hide after idle", 6_000);
        Size hidden = HostSize();
        Require(visible == hidden, "hiding fullscreen controls resized the video host");
        // 只触发自有视频子控件的鼠标事件，不向系统发送输入。
        object panel = typeof(NativeVideoHost).GetField("_activePanel", PrivateInstance)!.GetValue(Control<NativeVideoHost>("VideoHost"))!;
        panel.GetType().GetMethod("OnMouseMove", PrivateInstance)!.Invoke(panel, new object[] { new System.Windows.Forms.MouseEventArgs(System.Windows.Forms.MouseButtons.None, 0, 42, 42, 0) });
        await WaitAsync(() => popup.IsOpen, "moving over the video did not reveal fullscreen controls", 2_000);
        await SettleAsync();
        Size revealed = HostSize();
        Require(visible == revealed, "showing fullscreen controls resized the video host");
        RenderVisual((FrameworkElement)popup.Child, "fullscreen-controls.png", _window.Background);
        ComboBox speed = Control<ComboBox>("SpeedCombo");
        speed.IsDropDownOpen = true;
        await SettleAsync();
        SetField("_lastControlActivity", DateTime.UtcNow.AddSeconds(-4));
        Invoke("UpdateFullscreenControls");
        Require(popup.IsOpen, "fullscreen controls hid while a settings menu was open");
        speed.IsDropDownOpen = false;
        return new { visible, hidden, revealed, popupAboveNativeVideo = popup.Child != null, dropdownKeepsControls = true };
    }

    private static async Task<object> VerifySearchAsync(int index)
    {
        string tab = new[] { "综合", "剧集", "视频" }[index];
        Control<TextBox>("SearchInput").Text = _searchQuery;
        ComboBox tabs = Control<ComboBox>("SearchTabCombo");
        if (Field<Task?>("_catalogHydrationTask") is Task previousHydration) await previousHydration;
        JsonElement firstResponse = await CaptureSearchResponseAsync(() =>
        {
            if (tabs.SelectedIndex != index) tabs.SelectedIndex = index;
            else _ = InvokeTaskAsync("SearchAsync");
        });
        await WaitAsync(() => !Field<bool>("_catalogBusy") && Field<string>("_query") == _searchQuery && Field<string>("_searchTab") == tab && Catalog.Count > 0, "search tab did not load", 45_000);
        await SettleAsync();
        Require(Control<ScrollViewer>("CatalogScroll").VerticalOffset < 1, "new search retained an old scroll offset");
        string[] firstIds = Catalog.Select(item => item.Id).ToArray();
        string firstCursor = Field<string>("_cursor");
        bool firstHasMore = Field<bool>("_hasMore");
        double appendOffsetBefore = 0;
        double appendOffsetAfter = 0;
        AssertSearchResponse(tab, 1, ResponseIds(firstResponse), firstResponse, firstIds);
        CompareSdkSearch(tab, 1, firstIds, firstCursor, firstHasMore);
        if (index == 1) _historySeeds = Catalog.Take(2).ToArray();
        if (firstHasMore)
        {
            ScrollViewer scroll = Control<ScrollViewer>("CatalogScroll");
            if (Field<Task?>("_catalogHydrationTask") is Task hydration) await hydration;
            appendOffsetBefore = scroll.ScrollableHeight;
            JsonElement secondResponse = await CaptureSearchResponseAsync(() =>
            {
                if (scroll.ScrollableHeight > 0) scroll.ScrollToEnd();
                else _ = InvokeTaskAsync("LoadCatalogAsync", true);
            });
            await WaitAsync(() => !Field<bool>("_catalogBusy"), "search page two did not complete", 40_000);
            await SettleAsync();
            appendOffsetAfter = scroll.VerticalOffset;
            Require(appendOffsetBefore < 1 || appendOffsetAfter >= appendOffsetBefore - 1, "appending results reset scroll position");
            Require(Catalog.Select(item => item.Id).Distinct().Count() == Catalog.Count, "search pagination contains duplicate IDs");
            AssertSearchResponse(tab, 2, ResponseIds(firstResponse).Concat(ResponseIds(secondResponse)).Distinct().ToArray(), secondResponse, Catalog.Select(item => item.Id).ToArray());
            CompareSdkSearch(tab, 2, Catalog.Select(item => item.Id).ToArray(), Field<string>("_cursor"), Field<bool>("_hasMore"));
        }
        await CaptureAsync("search-" + index + ".png");
        return new { query = _searchQuery, tab, firstIds, firstCursor, firstHasMore,
            mergedIds = Catalog.Select(item => item.Id).ToArray(), cursor = Field<string>("_cursor"), hasMore = Field<bool>("_hasMore"), appendOffsetBefore, appendOffsetAfter };
    }

    private static async Task<object> VerifySavedCatalogAsync()
    {
        string directory = Field<string>("_configDir");
        string path = Path.Combine(directory, "catalog-cache.json");
        await WaitAsync(() => File.Exists(path), "discovery cache was not saved", 5_000);
        string contents = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(contents);
        Require(document.RootElement.TryGetProperty("Items", out var items) && items.GetArrayLength() > 0, "discovery cache contains no items");
        Require(!contents.Contains("\"key\"", StringComparison.OrdinalIgnoreCase) && !contents.Contains("\"audio_url\"", StringComparison.OrdinalIgnoreCase), "playback fields were persisted in catalog cache");
        var next = new MainWindow { ShowInTaskbar = false };
        try
        {
            typeof(MainWindow).GetField("_configDir", PrivateInstance)!.SetValue(next, directory);
            var timer = Stopwatch.StartNew();
            typeof(MainWindow).GetMethod("LoadSavedDiscoveryCache", PrivateInstance)!.Invoke(next, null);
            int count = ((IList)typeof(MainWindow).GetField("_catalog", PrivateInstance)!.GetValue(next)!).Count;
            bool backendNotStarted = typeof(MainWindow).GetField("_backend", PrivateInstance)!.GetValue(next) == null;
            Require(count == items.GetArrayLength() && backendNotStarted, "cached cards were not available before backend startup");
            return new { count, elapsedMs = timer.ElapsedMilliseconds, backendNotStarted, rootProperties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray() };
        }
        finally
        {
            typeof(MainWindow).GetField("_closing", PrivateInstance)!.SetValue(next, true);
            next.Close();
        }
    }

    private static void CompareSdkSearch(string tab, int pages, string[] actual, string cursor, bool hasMore)
    {
        if (_sdkReport == null || !File.Exists(_sdkReport))
        {
            Observe("historical_sdk_parity_" + tab + "_" + pages, false, new { error = "SDK baseline report is missing" });
            return;
        }
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_sdkReport));
        JsonElement[] calls = document.RootElement.GetProperty("calls").EnumerateArray()
            .Where(call => call.GetProperty("query").GetString() == _searchQuery && call.GetProperty("tab").GetString() == tab && call.GetProperty("page").GetInt32() <= pages)
            .OrderBy(call => call.GetProperty("page").GetInt32()).ToArray();
        string[] expected = calls.SelectMany(call => call.GetProperty("items").EnumerateArray()).Select(item => item.GetProperty("id").GetString() ?? "").Where(id => id.Length > 0).Distinct().ToArray();
        string[] missing = expected.Except(actual).ToArray();
        string[] extra = actual.Except(expected).ToArray();
        string expectedCursor = calls.Length == 0 ? "" : calls[^1].GetProperty("sdk_cursor").GetString() ?? "";
        bool expectedHasMore = calls.Length > 0 && calls[^1].GetProperty("sdk_has_more").GetBoolean();
        bool pass = calls.Length == pages && missing.Length == 0 && extra.Length == 0 && cursor == expectedCursor && hasMore == expectedHasMore;
        Observe("historical_sdk_parity_" + tab + "_" + pages, pass, new { query = _searchQuery, tab, pages, expectedIds = expected, actualIds = actual,
            missing, extra, expectedCursor, cursor, expectedHasMore, hasMore });
    }

    private static async Task<JsonElement> CaptureSearchResponseAsync(Action trigger)
    {
        object backend = Field<object>("_backend");
        Type type = backend.GetType();
        var writer = (SemaphoreSlim)type.GetField("writeLock", PrivateInstance)!.GetValue(backend)!;
        var sequence = type.GetField("requestSequence", PrivateInstance)!;
        var pending = (System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>)type.GetField("pending", PrivateInstance)!.GetValue(backend)!;
        Task<JsonElement> response;
        await writer.WaitAsync();
        try
        {
            long before = (long)sequence.GetValue(backend)!;
            trigger();
            // 暂停发送几毫秒以取得当前请求的完成任务；不替换请求、响应或生产代码。
            await WaitAsync(() => (long)sequence.GetValue(backend)! > before, "search request was not issued", 5_000);
            string id = ((long)sequence.GetValue(backend)!).ToString();
            Require(pending.TryGetValue(id, out var completion), "search response observer missed pending request");
            response = completion!.Task;
        }
        finally
        {
            writer.Release();
        }
        return await response.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static string[] ResponseIds(JsonElement response) => response.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString() ?? "").Distinct().ToArray();

    private static void AssertSearchResponse(string tab, int pages, string[] expected, JsonElement response, string[] actual)
    {
        string cursor = response.TryGetProperty("cursor", out var value) ? value.GetString() ?? "" : "";
        bool hasMore = response.TryGetProperty("has_more", out var more) && more.GetBoolean() && cursor.Length > 0;
        bool pass = expected.Except(actual).Count() == 0 && actual.Except(expected).Count() == 0 && cursor == Field<string>("_cursor") && hasMore == Field<bool>("_hasMore");
        Record("same_response_search_" + tab + "_" + pages, pass, new { expectedIds = expected, actualIds = actual, cursor, hasMore });
    }

    private static async Task<object> VerifyQualitySwitchAsync()
    {
        Require(Player?.GetStatus().Loaded == true && CurrentEpisode != null, "playback prerequisite missing");
        ComboBox combo = Control<ComboBox>("QualityCombo");
        object[] qualities = combo.Items.Cast<object>().Where(item => !string.IsNullOrWhiteSpace(Property(item, "Definition")?.ToString())).ToArray();
        object[] offered = qualities.Where(item => Property(item, "Definition")?.ToString() is not ("best" or "worst") && Convert.ToInt32(Property(item, "Height") ?? 0) > 0)
            .OrderBy(item => Convert.ToInt32(Property(item, "Height") ?? 0)).ToArray();
        Require(offered.Length > 0, "no service-provided qualities shown");
        object target = offered[0];
        string definition = Property(target, "Definition")!.ToString()!;
        int height = Convert.ToInt32(Property(target, "Height"));
        string episode = CurrentEpisode!.Id;
        Control<ComboBox>("SpeedCombo").SelectedIndex = 4;
        Control<Slider>("VolumeSlider").Value = 37;
        Player!.SetPaused(true);
        Player.Seek(16);
        await WaitAsync(() => Math.Abs(Player.GetStatus().Position - 16) < 0.35, "pre-switch seek failed", 10_000);
        MpvStatus before = Player.GetStatus();
        await InvokeTaskAsync("ChangeQualityAsync", definition).WaitAsync(TimeSpan.FromSeconds(45));
        await WaitAsync(() => Player.GetStatus().Loaded && !Field<bool>("_playbackBusy"), "quality player did not load", 45_000);
        MpvStatus after = Player.GetStatus();
        Require(CurrentEpisode.Id == episode, "quality switch changed episode");
        Require(after.Paused && Math.Abs(after.Position - before.Position) < 1, "quality switch did not preserve paused position");
        Require(Math.Abs(after.Speed - before.Speed) < 0.01 && Math.Abs(after.Volume - before.Volume) < 0.01, "quality switch did not preserve speed or volume");
        Require(after.VideoHeight == height, "decoded resolution does not match selected quality");
        Player.SetPaused(false);
        return new { definition, offered = qualities.Select(item => new { definition = Property(item, "Definition"), width = Property(item, "Width"), height = Property(item, "Height"), bitrate = Property(item, "Bitrate") }),
            before = new { before.Position, before.Paused, before.Speed, before.Volume, before.VideoWidth, before.VideoHeight },
            after = new { after.Position, after.Paused, after.Speed, after.Volume, after.VideoWidth, after.VideoHeight },
            changedResolution = before.VideoHeight != after.VideoHeight };
    }

    private static async Task<object> CaptureComboPopupAsync(string name, string filename)
    {
        filename = ArtifactName(filename);
        ComboBox combo = Control<ComboBox>(name);
        combo.ApplyTemplate();
        Popup popup = combo.Template.FindName("PART_Popup", combo) as Popup ?? throw new InvalidOperationException("ComboBox PART_Popup missing");
        try
        {
            combo.IsDropDownOpen = true;
            await SettleAsync();
            Require(popup.IsOpen && popup.Child is FrameworkElement, "ComboBox popup did not open");
            var child = (FrameworkElement)popup.Child;
            Require(child.ActualHeight > 30 && child.ActualWidth > 50, "ComboBox popup has no layout");
            var foregrounds = VisualChildren<ComboBoxItem>(child).Select(item => item.Foreground as SolidColorBrush).Where(brush => brush != null).Select(brush => brush!.Color).ToArray();
            var backgrounds = VisualChildren<Border>(child).Select(item => item.Background as SolidColorBrush).Where(brush => brush != null && brush.Color.A == 255).Select(brush => brush!.Color).ToArray();
            Require(foregrounds.Length > 0 && foregrounds.All(color => Luminance(color) > 0.45), "popup item foreground is too dark");
            Require(backgrounds.Any(color => Luminance(color) < 0.15), "popup does not have a dark background");
            RenderVisual(child, filename, new SolidColorBrush(Color.FromRgb(25, 30, 36)));
            return new { filename, width = child.ActualWidth, height = child.ActualHeight,
                foregrounds = foregrounds.Select(color => color.ToString()).Distinct().ToArray(), backgrounds = backgrounds.Select(color => color.ToString()).Distinct().ToArray() };
        }
        finally
        {
            combo.IsDropDownOpen = false;
        }
    }

    private static async Task<object> VerifyHistoryCardsAsync()
    {
        // 使用独立测试配置写入已返回的真实目录项，避免历史布局测试依赖推荐接口的即时可用性。
        foreach (SeriesItem item in _historySeeds)
        {
            await BackendCallAsync("history_record", new { series_id = item.Id, title = item.Title, episode = 1, episodes = Math.Max(1, item.EpisodeCount), finished = false, position = 12 });
        }
        Control<Button>("HistoryNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitAsync(() => Control<FrameworkElement>("HistoryScreen").Visibility == Visibility.Visible && Field<IList>("_history").Count >= 2, "history cards did not load", 45_000);
        ItemsControl items = Control<ItemsControl>("HistoryItems");
        await SettleAsync();
        Require(VisualChildren<WrapPanel>(items).Any(), "history does not use a wrapping panel");
        await WaitAsync(() => VisualChildren<System.Windows.Controls.Image>(items).Count(image => image.Source is BitmapSource source && source.PixelWidth > 0) >= 2, "history covers did not decode", 30_000);
        FrameworkElement first = items.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement ?? throw new InvalidOperationException("first history container missing");
        FrameworkElement second = items.ItemContainerGenerator.ContainerFromIndex(1) as FrameworkElement ?? throw new InvalidOperationException("second history container missing");
        Point a = first.TransformToAncestor(items).Transform(new Point());
        Point b = second.TransformToAncestor(items).Transform(new Point());
        Require(Math.Abs(a.Y - b.Y) < 1 && b.X > a.X + 100, "history cards are not arranged side by side");
        await CaptureAsync("history.png");
        return new { count = items.Items.Count, first = new { a.X, a.Y }, second = new { b.X, b.Y }, decodedCovers = VisualChildren<System.Windows.Controls.Image>(items).Count(image => image.Source is BitmapSource source && source.PixelWidth > 0) };
    }

    private static object? Property(object value, string name) => value.GetType().GetProperty(name)?.GetValue(value);
    private static Task<JsonElement> BackendCallAsync(string method, object args)
    {
        object backend = Field<object>("_backend");
        return (Task<JsonElement>)backend.GetType().GetMethod("CallAsync", PrivateInstance)!.Invoke(backend, new object?[] { method, args, CancellationToken.None })!;
    }
    private static double Luminance(Color color)
    {
        double Channel(byte value)
        {
            double normalized = value / 255.0;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
    private static string ImageFormat(byte[] image)
    {
        if (image.Length > 3 && image[0] == 0xff && image[1] == 0xd8 && image[2] == 0xff) return "JPEG";
        if (image.Length > 8 && image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4e && image[3] == 0x47) return "PNG";
        if (image.Length > 12 && System.Text.Encoding.ASCII.GetString(image, 8, 4) == "WEBP") return "WebP";
        if (image.Length > 12 && System.Text.Encoding.ASCII.GetString(image, 4, 4) == "ftyp") return "ISO-BMFF/" + System.Text.Encoding.ASCII.GetString(image, 8, 4);
        return "other";
    }

    private static MpvPlayer? Player => Field<MpvPlayer?>("_player");
    private static SeriesItem? CurrentSeries => Field<SeriesItem?>("_currentSeries");
    private static EpisodeItem? CurrentEpisode => Field<EpisodeItem?>("_currentEpisode");
    private static List<SeriesItem> Catalog => ((IEnumerable<SeriesItem>)Field<object>("_catalog")).ToList();
    private static List<RankedSeriesItem> Ranking => ((IEnumerable<RankedSeriesItem>)Field<object>("_ranking")).ToList();

    private static async Task CheckAsync(string name, Func<Task<object>> test)
    {
        Console.WriteLine("START " + name);
        var timer = Stopwatch.StartNew();
        try
        {
            object details = await test().WaitAsync(TimeSpan.FromSeconds(60));
            Record(name, true, new { elapsedMs = timer.ElapsedMilliseconds, details });
        }
        catch (Exception error)
        {
            if (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            Record(name, false, new { elapsedMs = timer.ElapsedMilliseconds, error = Clean(error.Message), type = error.GetType().Name, notice = Clean(Control<TextBlock>("NoticeText").Text) });
        }
        SaveReport();
    }

    private static async Task WaitAsync(Func<bool> predicate, string label, int timeout = 60_000)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > timeout) throw new TimeoutException(label);
            await Task.Delay(80);
        }
    }

    private static async Task SettleAsync()
    {
        _window!.UpdateLayout();
        await _window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(300);
    }

    private static async Task<object> CaptureAsync(string filename)
    {
        filename = ArtifactName(filename);
        await SettleAsync();
        FrameworkElement root = (FrameworkElement)_window!.Content;
        int width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        RenderVisual(root, filename, _window.Background);
        return new { filename, width, height, note = "WPF render; embedded native video HWND is not captured" };
    }

    private static void RenderVisual(FrameworkElement root, string filename, Brush background)
    {
        filename = ArtifactName(filename);
        FrameworkElement scene = (FrameworkElement)_window!.Content;
        bool cropChild = root != scene && root.IsDescendantOf(scene);
        Rect bounds = cropChild ? root.TransformToAncestor(scene).TransformBounds(new Rect(0, 0, root.ActualWidth, root.ActualHeight)) : Rect.Empty;
        FrameworkElement renderRoot = cropChild ? scene : root;
        int width = Math.Max(1, (int)Math.Ceiling(renderRoot.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(renderRoot.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (DrawingContext drawing = backdrop.RenderOpen()) drawing.DrawRectangle(background, null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop);
        bitmap.Render(renderRoot);
        var encoder = new PngBitmapEncoder();
        if (cropChild)
        {
            // 普通子控件从完整窗口按真实布局边界裁切，避免父布局偏移造成空图。
            bounds.Intersect(new Rect(0, 0, width, height));
            int x = Math.Max(0, (int)Math.Floor(bounds.X));
            int y = Math.Max(0, (int)Math.Floor(bounds.Y));
            int croppedWidth = Math.Min(width - x, Math.Max(1, (int)Math.Ceiling(bounds.Right) - x));
            int croppedHeight = Math.Min(height - y, Math.Max(1, (int)Math.Ceiling(bounds.Bottom) - y));
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bitmap, new Int32Rect(x, y, croppedWidth, croppedHeight))));
        }
        else encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Output, filename));
        encoder.Save(file);
    }

    private static string ArtifactName(string filename) => _redesign && !filename.StartsWith("redesign-", StringComparison.Ordinal) ? "redesign-" + filename : filename;

    private static Size HostSize()
    {
        var host = Control<FrameworkElement>("VideoHost");
        return new Size(Math.Round(host.ActualWidth, 1), Math.Round(host.ActualHeight, 1));
    }

    private static T Control<T>(string name) where T : class => _window!.FindName(name) as T ?? throw new InvalidOperationException("control missing: " + name);
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, PrivateInstance)?.GetValue(_window) ?? default(T)!);
    private static void SetField(string name, object value) => (typeof(MainWindow).GetField(name, PrivateInstance) ?? throw new MissingFieldException(name)).SetValue(_window, value);
    private static object? Invoke(string name, params object?[] args) => (typeof(MainWindow).GetMethod(name, PrivateInstance) ?? throw new MissingMethodException(name)).Invoke(_window, args);
    private static Task InvokeTaskAsync(string name, params object?[] args) => Invoke(name, args) as Task ?? throw new InvalidOperationException("method did not return Task: " + name);
    private static void Require(bool value, string label)
    {
        if (!value) throw new InvalidOperationException(label);
    }

    private static string Clean(string value)
    {
        value = Regex.Replace(value, @"https?://\S+", "[URL]");
        value = Regex.Replace(value, @"\b[a-fA-F0-9]{32,}\b", "[VALUE]");
        return value.Length > 300 ? value[..300] : value;
    }

    private static void Record(string name, bool pass, object details)
    {
        if (!pass) _failures++;
        Results.Add(new { name, pass, details });
        Console.WriteLine((pass ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(details));
    }

    private static void Observe(string name, bool matches, object details)
    {
        Results.Add(new { name, observation = true, matches, details });
        Console.WriteLine("OBSERVE " + name + " " + JsonSerializer.Serialize(new { matches, details }));
    }

    private static void SaveReport() => File.WriteAllText(Path.Combine(Output, _preloadOnly ? "desktop-preload.json" : _coversOnly ? "desktop-covers.json" : _redesign ? "desktop-redesign.json" : "desktop-smoke.json"), JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures = _failures, tests = Results }, new JsonSerializerOptions { WriteIndented = true }));

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (T child in VisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
        }
    }
}
