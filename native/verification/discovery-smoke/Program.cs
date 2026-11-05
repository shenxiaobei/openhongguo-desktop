using System;
using System.Collections;
using System.Collections.Concurrent;
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
using System.Windows.Controls.Primitives;
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
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
                    (Field<object?>("_player") as IDisposable)?.Dispose();
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
        Window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = 40, Opacity = 0 };
        SetField("_configDir", state);
        System.Windows.Application.Current.MainWindow = Window;
        Window.Show();
        await WaitAsync(() => Field<object?>("_backend") != null && !Field<bool>("_catalogBusy") && Catalog.Count > 0, "首页未完成");
        await HydrationAsync();

        await CheckAsync("ranking_first_visit_waits_for_platform_directory", async () =>
        {
            Require(Field<List<RankContentOption>>("_rankContents").Count == 0, "首次榜单目录前提不成立");
            object backend = Field<object>("_backend");
            FieldInfo sequence = backend.GetType().GetField("requestSequence", PrivateInstance)!;
            long before = (long)sequence.GetValue(backend)!;
            await InvokeTaskAsync("RefreshRankGenreOptionsAsync");
            Require((long)sequence.GetValue(backend)! == before, "平台作品目录未返回时先发出了题材请求");
            Require(Control<ComboBox>("RankThemeCombo").Items.Count == 0 && !Control<ComboBox>("RankThemeCombo").IsEnabled, "首次榜单目录返回前已开放题材操作");
            return new { noPrematureFilterRequest = true, controlsRemainDisabled = true };
        });

        await CheckAsync("horizontal_ranking_wide", async () =>
        {
            JsonElement response = await ObserveResponseAsync(() => Control<Button>("RankingNav").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Count > 0, "热播榜未返回");
            await SettleAsync();
            Require(response.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("series_id").GetString()).SequenceEqual(Ranking.Select(item => item.Id)), "榜单顺序与真实响应不一致");
            Require(Field<string>("_rankBoard") == "ranklist_hot_play_sc", "首页并非真实热播榜");
            object layout = RankLayout();
            await WaitAsync(() => VisualChildren<System.Windows.Controls.Image>(Control<ItemsControl>("RankingItems")).Count(image => image.Source is BitmapSource) >= Ranking.Count(item => !string.IsNullOrWhiteSpace(item.CoverUrl)), "榜单海报未解码", 25000);
            await CaptureAsync("ranking-wide.png");
            return new { count = Ranking.Count, board = Field<string>("_rankBoard"), layout, explanation = Control<TextBlock>("RankExplanation").Text };
        });
        await CheckAsync("horizontal_ranking_compact", async () =>
        {
            Window.Width = 950;
            Window.Height = 640;
            await SettleAsync();
            object layout = RankLayout();
            await CaptureAsync("ranking-compact.png");
            return layout;
        });
        await CheckAsync("recommendation_board_explained", async () =>
        {
            ComboBox boards = Control<ComboBox>("RankBoardCombo");
            RankBoardOption recommended = boards.Items.Cast<RankBoardOption>().First(item => item.Id == "ranklist_hot_sc");
            JsonElement response = await ObserveResponseAsync(() => boards.SelectedItem = recommended);
            await WaitAsync(() => !Field<bool>("_rankBusy") && Field<string>("_rankBoard") == recommended.Id, "推荐榜切换未完成");
            string explanation = Control<TextBlock>("RankExplanation").Text;
            Require(explanation.Contains("推荐", StringComparison.Ordinal) && explanation.Contains("热播", StringComparison.Ordinal), "推荐榜与热播榜含义未解释");
            Require(response.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("series_id").GetString()).SequenceEqual(Ranking.Select(item => item.Id)), "推荐榜被自行重排");
            await CaptureAsync("ranking-recommendation.png");
            return new { board = recommended.Id, explanation, promoted = Ranking.Count(item => item.IsPromoted) };
        });
        await VerifyRankTabsAsync();
        await VerifyRankGenresAsync();
        await VerifyDailyRankingAsync();

        await CheckAsync("real_genre_groups", async () =>
        {
            Window.Width = 1380;
            Window.Height = 900;
            Control<TextBlock>("PageTitle").Text = "发现好剧";
            Invoke("ShowScreen", Control<FrameworkElement>("CatalogScreen"));
            await InvokeTaskAsync("RefreshGenreOptionsAsync");
            await SettleAsync();
            JsonElement panelResponse = await BackendCallAsync("filter_options", new { genre = "" });
            GenrePanel panel = JsonSerializer.Deserialize<GenrePanel>(panelResponse)!;
            string[] keys = panel.Rows.Select(row => row.Key).ToArray();
            Require(keys.Contains("category_dim_theme") && keys.Contains("category_dim_role") && keys.Contains("category_dim_epoch"), "主题、设定或背景面板缺失");
            var groups = Control<Panel>("GenreGroupsPanel");
            Require(groups.Children.Count >= 3, "题材未按真实维度分组");
            Require(VisualChildren<Button>(groups).Any(button => button.Tag is ValueTuple<string, string> option && option.Item1 == "category_dim_theme" && option.Item2 == "cate_165"), "真实悬疑选项未展示");
            await CaptureAsync("genre-groups.png");
            return new { keys, groupCount = groups.Children.Count, rows = panel.Rows.Select(row => new { row.Key, row.Name, options = row.Options.Count }), hint = Control<TextBlock>("GenreHint").Text };
        });
        await CheckAsync("genre_filter_first_page_matches_response", async () =>
        {
            Button suspense = VisualChildren<Button>(Control<FrameworkElement>("GenreGroupsPanel")).First(button => button.Tag is ValueTuple<string, string> option && option.Item1 == "category_dim_theme" && option.Item2 == "cate_165");
            JsonElement response = await ObserveResponseAsync(() => suspense.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await WaitAsync(() => !Field<bool>("_catalogBusy") && Catalog.Count > 0, "题材筛选未完成");
            await SettleAsync();
            AssertCatalog(response);
            Require(Field<Dictionary<string, string>>("_genreSelections").TryGetValue("category_dim_theme", out string? value) && value == "cate_165", "主题选择未使用真实键值");
            Require(Control<ScrollViewer>("CatalogScroll").VerticalOffset < 1, "新筛选未回到顶部");
            await HydrationAsync();
            await WaitCatalogCoversAsync();
            await CaptureAsync("genre-suspense.png");
            return new { count = Catalog.Count, titles = Catalog.Select(item => item.Title), hasMore = Field<bool>("_hasMore") };
        });
        await CheckAsync("genre_second_page_unique_and_same_response", async () =>
        {
            string[] first = Catalog.Select(item => item.Id).ToArray();
            Require(Field<bool>("_hasMore"), "题材结果未给续页");
            JsonElement response = await ObserveResponseAsync(() => _ = InvokeTaskAsync("LoadCatalogAsync", true));
            await WaitAsync(() => !Field<bool>("_catalogBusy") && Catalog.Count > first.Length, "题材第二页未追加");
            string[] second = ResponseIds(response);
            Require(!first.Intersect(second).Any(), "真实题材第二页与首页重复");
            Require(first.Concat(second).Distinct().SequenceEqual(Catalog.Select(item => item.Id)), "题材续页结果或顺序与响应不一致");
            return new { firstCount = first.Length, secondCount = second.Length, total = Catalog.Count, repeated = first.Intersect(second).Count() };
        });
        await CheckAsync("search_then_restore_genre", async () =>
        {
            string[] filterIds = Catalog.Select(item => item.Id).ToArray();
            string beforeKey = (string)typeof(MainWindow).GetProperty("CatalogKey", PrivateInstance)!.GetValue(Window)!;
            Control<TextBox>("SearchInput").Text = "全球死考";
            JsonElement search = await ObserveResponseAsync(() => _ = InvokeTaskAsync("SearchAsync"));
            await WaitAsync(() => !Field<bool>("_catalogBusy") && Field<string>("_query") == "全球死考", "搜索未完成");
            AssertCatalog(search);
            Require(Catalog.Any(item => item.Title.Contains("全球死考", StringComparison.Ordinal)), "搜索缺少全球死考");
            Require(Control<ScrollViewer>("CatalogScroll").VerticalOffset < 1, "搜索未回到顶部");
            string searchKey = (string)typeof(MainWindow).GetProperty("CatalogKey", PrivateInstance)!.GetValue(Window)!;
            Require(beforeKey != searchKey, "搜索缓存与题材缓存混用");
            Control<TextBox>("SearchInput").Text = "";
            JsonElement restored = await ObserveResponseAsync(() => _ = InvokeTaskAsync("SearchAsync"));
            await WaitAsync(() => !Field<bool>("_catalogBusy") && Field<string>("_query") == "", "清除搜索未完成");
            AssertCatalog(restored);
            Require(Field<Dictionary<string, string>>("_genreSelections").GetValueOrDefault("category_dim_theme") == "cate_165", "搜索清除后丢失主题条件");
            string afterKey = (string)typeof(MainWindow).GetProperty("CatalogKey", PrivateInstance)!.GetValue(Window)!;
            Require(beforeKey == afterKey, "清除搜索未还原标题材缓存");
            return new { searchCount = ResponseIds(search).Length, restoredCount = Catalog.Count, separateCacheKey = true, filterWasRetained = true, previouslyLoaded = filterIds.Length };
        });
        await CheckAsync("genre_switch_ignores_stale_buttons_and_restores_discovery", async () =>
        {
            Button oldSuspense = VisualChildren<Button>(Control<FrameworkElement>("GenreGroupsPanel"))
                .First(button => button.Tag is ValueTuple<string, string> option && option == ("category_dim_theme", "cate_165"));
            Button comic = VisualChildren<Button>(Control<FrameworkElement>("GenreGroupsPanel"))
                .First(button => button.Tag is ValueTuple<string, string> option && option == ("genre", "comic_series"));
            comic.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            oldSuspense.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dictionary<string, string> selections = Field<Dictionary<string, string>>("_genreSelections");
            Require(selections.Count == 1 && selections.GetValueOrDefault("genre") == "comic_series", "旧面板按钮把悬疑条件带入漫剧");
            await WaitAsync(() => !Field<bool>("_catalogBusy") && Field<GenrePanel?>("_visibleGenrePanel")?.Rows
                .Any(row => row.Key == "category_dim_theme" && row.Options.Any(option => option.Value == "cate_316")) == true, "漫剧新面板没有完成");
            Require(selections.Count == 1 && !selections.ContainsKey("category_dim_theme"), "漫剧请求仍保留隐藏的旧主题");
            Control<TextBox>("SearchInput").Text = "全球死考";
            JsonElement search = await ObserveResponseAsync(() => _ = InvokeTaskAsync("SearchAsync"));
            await WaitAsync(() => !Field<bool>("_catalogBusy"), "漫剧筛选切出搜索未完成");
            AssertCatalog(search);
            Require(Catalog.Any(item => item.Title.Contains("全球死考", StringComparison.Ordinal)), "体裁筛选错误地污染搜索");
            Control<TextBox>("SearchInput").Text = "";
            await InvokeTaskAsync("SearchAsync");
            await WaitAsync(() => !Field<bool>("_catalogBusy"), "搜索清空后漫剧筛选未恢复");
            Require(Field<Dictionary<string, string>>("_genreSelections").GetValueOrDefault("genre") == "comic_series", "清空搜索丢失漫剧体裁");
            Button recommendation = Control<Panel>("ChannelTabs").Children.OfType<Button>().First(button => button.Tag?.ToString() == "推荐");
            recommendation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !Field<bool>("_catalogBusy") && Field<Dictionary<string, string>>("_genreSelections").Count == 0 && Field<GenrePanel?>("_visibleGenrePanel") != null, "推荐频道没有重置筛选并恢复默认面板");
            Require(VisualChildren<Button>(Control<FrameworkElement>("GenreGroupsPanel")).Any(button => button.Tag is ValueTuple<string, string> option && option == ("category_dim_theme", "cate_165")), "推荐频道没有恢复全部主题");
            return new { staleSelectionIgnored = true, comicOnlyGenreSelection = true, searchCount = ResponseIds(search).Length, restoredRecommendation = true };
        });
        await CheckAsync("engagement_counts_and_honest_discussion", async () =>
        {
            JsonElement detail = await BackendCallAsync("series", new { id = "7503744492189535257" });
            JsonElement series = detail.GetProperty("series");
            JsonElement episode = detail.GetProperty("episodes")[0];
            SetField("_currentSeries", JsonSerializer.Deserialize<SeriesItem>(series)!);
            SetField("_currentEpisode", JsonSerializer.Deserialize<EpisodeItem>(episode)!);
            Invoke("ShowScreen", Control<FrameworkElement>("PlayerScreen"));
            await InvokeTaskAsync("RefreshEngagementAsync");
            EngagementInfo stats = Field<EngagementInfo>("_engagement");
            Require(stats.SeriesId == series.GetProperty("id").GetString() && stats.EpisodeId == episode.GetProperty("id").GetString(), "互动信息用了错误的剧集ID");
            Require(stats.PlayCount == KnownCount(series, "play_count") && stats.FollowCount == KnownCount(series, "follow_count") && stats.SeriesLikeCount == KnownCount(series, "digg_count"), "剧统计与真实详情不一致");
            Require(stats.EpisodeLikeCount == KnownCount(episode, "digg_count") && stats.CommentCount == KnownCount(episode, "comment_count"), "分集统计与真实详情不一致");
            Require(stats.PlayCount > 0 && stats.EpisodeLikeCount > 0 && stats.CommentCount > 0, "真实统计样本未返回正数");
            Require(stats.FavoriteCount == null && !stats.CommentsAvailable && !stats.DanmakuAvailable && stats.FavoriteLabel == "—", "缺失能力被伪造成可用或零");
            Control<Button>("DiscussionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync();
            Require(Control<FrameworkElement>("DiscussionPanel").Visibility == Visibility.Visible && Control<ItemsControl>("DiscussionItems").Items.Count == 0, "评论面板未打开或包含伪造评论");
            string commentStatus = Control<TextBlock>("DiscussionStatus").Text;
            Require(commentStatus.Contains("未", StringComparison.Ordinal), "评论正文缺失原因未说明");
            await CaptureAsync("engagement-comments.png");
            Control<Button>("DanmakuTabButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync();
            string danmakuStatus = Control<TextBlock>("DiscussionStatus").Text;
            Require(Control<ItemsControl>("DiscussionItems").Items.Count == 0 && danmakuStatus.Contains("未", StringComparison.Ordinal), "弹幕缺失状态不正确");
            await CaptureAsync("engagement-danmaku.png");
            Invoke("CloseDiscussionPanel");
            Require(Control<FrameworkElement>("DiscussionPanel").Visibility == Visibility.Collapsed, "讨论面板未关闭");
            return new { stats.PlayCount, stats.FollowCount, stats.SeriesLikeCount, stats.EpisodeLikeCount, stats.CommentCount, stats.FavoriteCount, stats.CommentsAvailable, stats.DanmakuAvailable, summary = Control<TextBlock>("EngagementText").Text, commentStatus, danmakuStatus };
        });
        await CheckAsync("story_change_clears_previous_discussion_immediately", async () =>
        {
            Control<Button>("DiscussionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Control<FrameworkElement>("DiscussionPanel").Visibility == Visibility.Visible && Field<EngagementInfo?>("_engagement") != null, "前一部剧的统计未准备完成");
            string previous = Field<EngagementInfo>("_engagement").SeriesId;
            Control<CheckBox>("AutoNextCheck").IsChecked = false;
            Control<Slider>("VolumeSlider").Value = 0;
            Invoke("EnsurePlayer");
            Field<MpvPlayer>("_player").SetVolume(0);
            object backend = Field<object>("_backend");
            var writer = (SemaphoreSlim)backend.GetType().GetField("writeLock", PrivateInstance)!.GetValue(backend)!;
            Task opening;
            string loadingText;
            await writer.WaitAsync();
            try
            {
                // 暂停本宿主的发送锁，确保即时断言发生在新详情响应之前。
                opening = InvokeTaskAsync("OpenSeriesAsync", "7675400266065136665", true, 0);
                loadingText = Control<TextBlock>("EngagementText").Text;
                Require(!opening.IsCompleted && Control<FrameworkElement>("DiscussionPanel").Visibility == Visibility.Collapsed, "换故事加载期间还保留旧讨论面板");
                Require(Field<EngagementInfo?>("_engagement") == null && Control<ItemsControl>("DiscussionItems").Items.Count == 0, "换故事没有立即清除旧互动对象");
                Require(loadingText.Contains("正在", StringComparison.Ordinal) && !loadingText.Contains("148", StringComparison.Ordinal), "换故事的占位仍带有上一集计数");
            }
            finally { writer.Release(); }
            await opening.WaitAsync(TimeSpan.FromSeconds(60));
            await WaitAsync(() => Field<EngagementInfo?>("_engagement")?.SeriesId == "7675400266065136665", "新故事互动统计未更新", 45000);
            EngagementInfo current = Field<EngagementInfo>("_engagement");
            Require(current.SeriesId != previous && current.EpisodeId == Field<EpisodeItem>("_currentEpisode").Id, "换故事完成后仍显示旧集统计");
            await InvokeTaskAsync("StopCurrentAsync");
            return new { clearedSynchronously = true, loadingText, newSeries = current.SeriesId, newEpisode = current.EpisodeId, mutedPlayback = true };
        });
        await CheckAsync("fullscreen_alpha_and_local_cursor_restore", async () =>
        {
            Border controls = Control<Border>("PlayerControls");
            Brush original = controls.Background;
            DependencyObject originalParent = VisualTreeHelper.GetParent(controls);
            Invoke("ToggleFullscreen");
            await SettleAsync();
            Popup popup = Field<Popup>("_fullscreenControls");
            Require(popup.AllowsTransparency && ReferenceEquals(popup.Child, controls), "全屏控制条没有透明覆盖层");
            Require(controls.Opacity == 1 && controls.Background is LinearGradientBrush, "控件与文字被整体变透明");
            var gradient = (LinearGradientBrush)controls.Background;
            int[] alphas = gradient.GradientStops.Select(stop => (int)stop.Color.A).ToArray();
            Require(alphas.SequenceEqual(new[] { 0, 56, 140 }), "全屏底板渐变透明度不正确");
            RenderControlAlpha(controls, "fullscreen-controls-alpha.png");
            Invoke("SetFullscreenCursorHidden", true);
            Require(ReferenceEquals(Window.Cursor, System.Windows.Input.Cursors.None), "WPF 窗口指针未隐藏");
            NativeVideoHost host = Control<NativeVideoHost>("VideoHost");
            var active = (System.Windows.Forms.Panel)typeof(NativeVideoHost).GetField("_activePanel", PrivateInstance)!.GetValue(host)!;
            var standby = (System.Windows.Forms.Panel)typeof(NativeVideoHost).GetField("_standbyPanel", PrivateInstance)!.GetValue(host)!;
            Require(!active.Cursor.Equals(System.Windows.Forms.Cursors.Default) && !standby.Cursor.Equals(System.Windows.Forms.Cursors.Default), "视频双面板指针未同步隐藏");
            Invoke("SetFullscreenCursorHidden", false);
            Require(Window.Cursor == null && active.Cursor.Equals(System.Windows.Forms.Cursors.Default) && standby.Cursor.Equals(System.Windows.Forms.Cursors.Default), "本窗口指针未恢复");
            Invoke("ToggleFullscreen");
            Window.Left = -3000;
            await SettleAsync();
            Require(Field<Popup?>("_fullscreenControls") == null && ReferenceEquals(VisualTreeHelper.GetParent(controls), originalParent), "退出全屏后控制条未返回原容器");
            Require(ReferenceEquals(controls.Background, original), "退出全屏后原底板没有恢复");
            return new { alphas, controlsOpacity = controls.Opacity, restoredParent = true, restoredBackground = true, localCursorRestore = true, timerNotTested = "透明非激活宿主仅核验本窗口光标状态与恢复，不争夺焦点；不含原生视频像素" };
        });
    }

    private static async Task VerifyRankTabsAsync()
    {
        await CheckAsync("ranking_two_rows_use_real_horizontal_tabs", async () =>
        {
            Window!.Width = 1380;
            Window.Height = 900;
            foreach (string name in new[] { "RankContentTabs", "RankBoardTabs" })
            {
                StackPanel panel = Control<StackPanel>(name);
                Require(panel.Orientation == Orientation.Horizontal && panel.Children.OfType<Button>().Count() > 1, "排行榜没有真实横向标签：" + name);
                await SettleAsync();
                Point[] positions = panel.Children.OfType<Button>().Select(button => button.TransformToAncestor(panel).Transform(new Point())).ToArray();
                Require(positions.All(point => Math.Abs(point.Y - positions[0].Y) < 1) && positions[1].X > positions[0].X, "标签发生换行或仍按纵向堆叠：" + name);
            }
            string[] expectedContents = Field<List<RankContentOption>>("_rankContents").Select(option => option.Id).ToArray();
            string[] shownContents = Control<StackPanel>("RankContentTabs").Children.OfType<Button>().Select(button => ((RankContentOption)button.Tag).Id).ToArray();
            Require(expectedContents.SequenceEqual(shownContents), "作品类型标签没有使用真实目录");
            Require(Control<ComboBox>("RankContentCombo").Visibility == Visibility.Collapsed && Control<ComboBox>("RankBoardCombo").Visibility == Visibility.Collapsed, "旧下拉框仍出现在横向标签界面");
            Require(Control<FrameworkElement>("RankGenrePanel").Visibility == Visibility.Collapsed, "分类面板没有默认收起");
            await CaptureAsync("ranking-horizontal-tabs.png");
            return new { contents = shownContents, boards = Control<StackPanel>("RankBoardTabs").Children.OfType<Button>().Select(button => ((RankBoardOption)button.Tag).Id), categoriesInitiallyCollapsed = true };
        });
        await CheckAsync("ranking_tabs_scroll_at_compact_width", async () =>
        {
            Window!.Width = 950;
            Window.Height = 640;
            await SettleAsync();
            var evidence = new List<object>();
            foreach (string name in new[] { "RankContentTabsScroll", "RankBoardTabsScroll" })
            {
                ScrollViewer scroll = Control<ScrollViewer>(name);
                Require(scroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled, "横向标签没有配置水平滚动：" + name);
                double width = scroll.Width;
                HorizontalAlignment alignment = scroll.HorizontalAlignment;
                try
                {
                    // 缩窄本宿主内的标签视口，实际核验标签超长时仍能水平续看。
                    scroll.Width = 160;
                    scroll.HorizontalAlignment = HorizontalAlignment.Left;
                    await SettleAsync();
                    Require(scroll.ScrollableWidth > 0, "窄视口中标签被裁切而没有可滚动内容");
                    scroll.ScrollToHorizontalOffset(Math.Min(40, scroll.ScrollableWidth));
                    await SettleAsync();
                    Require(scroll.HorizontalOffset > 0, "横向标签不能水平滚动");
                    evidence.Add(new { name, scrollableWidth = scroll.ScrollableWidth, offset = scroll.HorizontalOffset });
                }
                finally
                {
                    scroll.ScrollToLeftEnd();
                    scroll.Width = width;
                    scroll.HorizontalAlignment = alignment;
                }
            }
            await CaptureAsync("ranking-horizontal-tabs-compact.png");
            Window.Width = 1380;
            Window.Height = 900;
            return new { scrolling = evidence };
        });
        await CheckAsync("ranking_type_and_board_tabs_trigger_real_queries", async () =>
        {
            RankTab("RankContentTabs", "human").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !Field<bool>("_rankBusy") && Field<string>("_rankContent") == "human", "真人剧标签没有切换真实目录");
            await InvokeTaskAsync("RefreshRankGenreOptionsAsync");
            RankBoardOption target = Control<StackPanel>("RankBoardTabs").Children.OfType<Button>().Select(button => (RankBoardOption)button.Tag)
                .First(option => option.Id != Field<string>("_rankBoard"));
            JsonElement response = await ObserveResponseAsync(() => RankTab("RankBoardTabs", target.Id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await WaitAsync(() => !Field<bool>("_rankBusy") && Field<string>("_rankBoard") == target.Id, "榜单标签没有切换真实榜");
            Require(response.GetProperty("content").GetString() == "human" && response.GetProperty("board").GetString() == target.Id, "标签请求不是所选真人剧榜");
            Require(response.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("series_id").GetString()).SequenceEqual(Ranking.Select(item => item.Id)), "标签切换结果与真实榜单响应不一致");
            RankTab("RankContentTabs", "all").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => !Field<bool>("_rankBusy") && Field<string>("_rankContent") == "all", "全部标签未恢复");
            return new { selectedContent = "human", selectedBoard = target.Id, sameResponse = true, restoredAll = true };
        });
    }

    private static Button RankTab(string panel, string id) => Control<StackPanel>(panel).Children.OfType<Button>().First(button => button.Tag is RankBoardOption option && option.Id == id);

    private static async Task VerifyDailyRankingAsync()
    {
        string nativeKey = RankingUpdateKey();
        await CheckAsync("ranking_daily_refresh_once_per_local_day", async () =>
        {
            Dictionary<string, DateOnly> days = Field<Dictionary<string, DateOnly>>("_rankingUpdatedDays");
            DateTime now = DateTime.Now;
            DateOnly today = DateOnly.FromDateTime(now);
            Require(days.GetValueOrDefault(nativeKey) == today && !string.IsNullOrWhiteSpace(Control<TextBlock>("RankingFreshnessText").Text), "成功加载平台榜后没有记录当天更新时间");
            long before = BackendSequence();
            await InvokeTaskAsync("CheckDailyRankingUpdateAsync", now);
            Require(BackendSequence() == before, "同一天已更新的平台榜又发出重复刷新");
            days[nativeKey] = today.AddDays(-1);
            JsonElement response = await ObserveResponseAsync(() => _ = InvokeTaskAsync("CheckDailyRankingUpdateAsync", now));
            await WaitAsync(() => !Field<bool>("_checkingDailyRanking") && !Field<bool>("_rankBusy"), "跨日平台榜自动更新没有完成");
            Require(response.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("series_id").GetString()).SequenceEqual(Ranking.Select(item => item.Id)), "自动更新没有展示当天真实榜单响应");
            Require(days.GetValueOrDefault(nativeKey) == today, "自动更新成功后没有记录当天日期");
            before = BackendSequence();
            await InvokeTaskAsync("CheckDailyRankingUpdateAsync", now);
            Require(BackendSequence() == before, "跨日更新成功后同日仍重复刷新");
            return new { updatedLocalDay = today.ToString(), sameDayNoDuplicate = true, expiredNativeRankingRefreshed = true, count = Ranking.Count, freshness = Control<TextBlock>("RankingFreshnessText").Text };
        });
        await CheckAsync("ranking_daily_dates_are_isolated_for_topic_conditions", async () =>
        {
            ComboBox themes = Control<ComboBox>("RankThemeCombo");
            themes.SelectedItem = themes.Items.OfType<GenreOption>().First(option => option.Value == "cate_165");
            await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Any(item => item.IsCategoryResult), "每日更新题材样本未完成");
            await WaitRankingCoversAsync();
            DateTime now = DateTime.Now;
            DateOnly today = DateOnly.FromDateTime(now);
            Dictionary<string, DateOnly> days = Field<Dictionary<string, DateOnly>>("_rankingUpdatedDays");
            string genreKey = RankingUpdateKey();
            Require(genreKey != nativeKey && days.GetValueOrDefault(genreKey) == today, "平台榜与题材热榜共用了更新时间键");
            days[nativeKey] = today.AddDays(-1);
            long before = BackendSequence();
            await InvokeTaskAsync("CheckDailyRankingUpdateAsync", now);
            Require(BackendSequence() == before && days[nativeKey] < today, "不活跃平台榜日期污染了当天题材榜");
            days[genreKey] = today.AddDays(-1);
            JsonElement response = await ObserveResponseAsync(() => _ = InvokeTaskAsync("CheckDailyRankingUpdateAsync", now));
            await WaitAsync(() => !Field<bool>("_checkingDailyRanking") && !Field<bool>("_rankBusy"), "题材热榜跨日更新未完成");
            AssertGenreRanking(response);
            Require(days[genreKey] == today && days[nativeKey] < today, "更新题材榜错误地标记了另一榜单条件");
            Dictionary<string, string[]> query = ReadFilterCursorSelections(response.GetProperty("cursor").GetString()!);
            Require(query.GetValueOrDefault("category_dim_theme")?.SequenceEqual(new[] { "cate_165" }) == true && query.GetValueOrDefault("sort")?.SequenceEqual(new[] { "hot_score" }) == true, "题材榜每日更新丢失了真实条件与热度排序");
            return new { distinctKeys = true, nativeRemainedExpired = true, topicUpdatedToday = true, query, count = Ranking.Count };
        });
        await CheckAsync("ranking_daily_cancel_keeps_old_data_and_retries", async () =>
        {
            await WaitRankingCoversAsync();
            string[] previous = Ranking.Select(item => item.Id).ToArray();
            string cursor = Field<string>("_rankCursor");
            bool hasMore = Field<bool>("_rankHasMore");
            ScrollViewer scroll = Control<ScrollViewer>("RankScroll");
            scroll.ScrollToVerticalOffset(Math.Min(80, scroll.ScrollableHeight));
            await SettleAsync();
            double offset = scroll.VerticalOffset;
            DateTime now = DateTime.Now;
            DateOnly today = DateOnly.FromDateTime(now);
            string key = RankingUpdateKey();
            Dictionary<string, DateOnly> days = Field<Dictionary<string, DateOnly>>("_rankingUpdatedDays");
            days[key] = today.AddDays(-1);
            object backend = Field<object>("_backend");
            var writer = (SemaphoreSlim)backend.GetType().GetField("writeLock", PrivateInstance)!.GetValue(backend)!;
            Task refreshing;
            await writer.WaitAsync();
            try
            {
                refreshing = InvokeTaskAsync("CheckDailyRankingUpdateAsync", now);
                Field<CancellationTokenSource>("_rankRequest").Cancel();
            }
            finally { writer.Release(); }
            await refreshing.WaitAsync(TimeSpan.FromSeconds(15));
            await SettleAsync();
            Require(days[key] < today, "取消的每日更新仍被标记成功");
            Require(previous.SequenceEqual(Ranking.Select(item => item.Id)) && cursor == Field<string>("_rankCursor") && hasMore == Field<bool>("_rankHasMore") && Math.Abs(offset - scroll.VerticalOffset) < 1, "更新取消后没有保留旧榜单、游标、续页状态或滚动位置");
            Require(Field<Dictionary<string, DateTime>>("_rankingRetryAfter").GetValueOrDefault(key) > now, "更新未成功后没有设置重试间隔");
            long before = BackendSequence();
            await InvokeTaskAsync("CheckDailyRankingUpdateAsync", now.AddMinutes(1));
            Require(BackendSequence() == before, "重试间隔内又重复请求更新");
            JsonElement response = await ObserveResponseAsync(() => _ = InvokeTaskAsync("CheckDailyRankingUpdateAsync", now.AddMinutes(11)));
            await WaitAsync(() => !Field<bool>("_checkingDailyRanking") && !Field<bool>("_rankBusy"), "重试每日更新没有完成");
            AssertGenreRanking(response);
            Require(days[key] == today && !Field<Dictionary<string, DateTime>>("_rankingRetryAfter").ContainsKey(key), "重试成功后日期或重试状态未恢复");
            await WaitRankingCoversAsync();
            await CaptureAsync("ranking-daily-updated.png");
            return new { preservedCount = previous.Length, oldCursorRetainedOnCancel = true, oldScrollRetainedOnCancel = offset, sameIntervalNoDuplicate = true, retriedSuccessfully = true, freshness = Control<TextBlock>("RankingFreshnessText").Text, systemClockUnchanged = true };
        });
    }

    private static string RankingUpdateKey() => (string)typeof(MainWindow).GetProperty("RankingUpdateKey", PrivateInstance)!.GetValue(Window)!;
    private static long BackendSequence()
    {
        object backend = Field<object>("_backend");
        return (long)backend.GetType().GetField("requestSequence", PrivateInstance)!.GetValue(backend)!;
    }

    private static async Task VerifyRankGenresAsync()
    {
        await CheckAsync("ranking_genre_controls_visible", async () =>
        {
            await InvokeTaskAsync("RefreshRankGenreOptionsAsync");
            await WaitAsync(() => Control<ComboBox>("RankThemeCombo").Items.Count > 1, "排行榜主题选项未加载");
            Control<Button>("RankCategoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await SettleAsync();
            Require(Control<FrameworkElement>("RankGenrePanel").Visibility == Visibility.Visible, "分类按钮没有展开三个题材下拉框");
            foreach (string name in new[] { "RankThemeCombo", "RankSettingCombo", "RankBackgroundCombo" })
            {
                ComboBox combo = Control<ComboBox>(name);
                Require(combo.Visibility == Visibility.Visible && combo.ActualWidth > 60 && combo.Items.Count > 1, "排行榜分类未直接展示：" + name);
            }
            Require(Control<ComboBox>("RankThemeCombo").Items.OfType<GenreOption>().Any(option => option.Value == "cate_165"), "排行榜缺少真实悬疑分类");
            Window!.Width = 950;
            Window.Height = 640;
            await SettleAsync();
            await CaptureAsync("ranking-genres-compact.png");
            Window.Width = 1380;
            Window.Height = 900;
            return new { themeOptions = Control<ComboBox>("RankThemeCombo").Items.Count, settingOptions = Control<ComboBox>("RankSettingCombo").Items.Count, backgroundOptions = Control<ComboBox>("RankBackgroundCombo").Items.Count };
        });
        await CheckAsync("ranking_suspense_uses_real_heat_sort_and_cursor", async () =>
        {
            ComboBox themes = Control<ComboBox>("RankThemeCombo");
            GenreOption suspense = themes.Items.OfType<GenreOption>().First(option => option.Value == "cate_165");
            JsonElement response = await ObserveResponseAsync(() => themes.SelectedItem = suspense);
            await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Count > 0, "排行榜悬疑结果未完成");
            AssertGenreRanking(response);
            Dictionary<string, string[]> query = ReadFilterCursorSelections(response.GetProperty("cursor").GetString()!);
            Require(query.TryGetValue("sort", out string[]? sort) && sort.SequenceEqual(new[] { "hot_score" }), "排行榜请求没有实际使用平台热度排序");
            Require(query.TryGetValue("category_dim_theme", out string[]? theme) && theme.SequenceEqual(new[] { "cate_165" }), "排行榜请求的真实悬疑值不正确");
            Require(!query.ContainsKey("genre"), "全部榜被错误地固定到某个体裁");
            await WaitRankingCoversAsync();
            await CaptureAsync("ranking-suspense-hot.png");
            return new { count = Ranking.Count, query, explanation = Control<TextBlock>("RankExplanation").Text, firstTitles = Ranking.Take(5).Select(item => item.Title), listPositions = Ranking.Take(5).Select(item => item.PositionLabel) };
        });
        await CheckAsync("ranking_suspense_second_page_matches_response", async () =>
        {
            string[] first = Ranking.Select(item => item.Id).ToArray();
            Require(Field<bool>("_rankHasMore"), "题材热榜没有续页");
            JsonElement response = await ObserveResponseAsync(() => _ = InvokeTaskAsync("LoadRankingAsync", true));
            await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Count > first.Length, "题材热榜第二页未追加");
            string[] second = ResponseIds(response);
            Require(!first.Intersect(second).Any() && first.Concat(second).Distinct().SequenceEqual(Ranking.Select(item => item.Id)), "题材热榜分页顺序或去重与真实响应不同");
            Require(response.GetProperty("cursor").GetString() == Field<string>("_rankCursor"), "题材热榜续页游标丢失");
            Require(Ranking.Select(item => item.Position).SequenceEqual(Enumerable.Range(1, Ranking.Count)), "题材热榜分页的列表序号不连续");
            return new { first = first.Length, second = second.Length, total = Ranking.Count, repeated = first.Intersect(second).Count() };
        });
        await CheckAsync("ranking_return_resumes_cancelled_cover_hydration", async () =>
        {
            string[] before = Ranking.Select(item => item.Id).ToArray();
            string cursor = Field<string>("_rankCursor");
            string content = Field<string>("_rankContent");
            string board = Field<string>("_rankBoard");
            var selections = new Dictionary<string, string>(Field<Dictionary<string, string>>("_rankGenreSelections"));
            ScrollViewer scroll = Control<ScrollViewer>("RankScroll");
            scroll.ScrollToVerticalOffset(Math.Min(100, scroll.ScrollableHeight));
            await SettleAsync();
            double offset = scroll.VerticalOffset;
            CancellationTokenSource cancelled = Field<CancellationTokenSource>("_rankRequest");
            cancelled.Cancel();
            Invoke("ShowScreen", Control<FrameworkElement>("PlayerScreen"));
            Require(cancelled.IsCancellationRequested && Control<FrameworkElement>("PlayerScreen").Visibility == Visibility.Visible, "点播导航没有取消旧榜单生命周期");
            Button back = VisualChildren<Button>(Control<FrameworkElement>("PlayerScreen")).First(button => button.ToolTip?.ToString() == "返回");
            back.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitAsync(() => Control<FrameworkElement>("RankingScreen").Visibility == Visibility.Visible, "返回没有恢复排行榜");
            CancellationTokenSource resumed = Field<CancellationTokenSource>("_rankRequest");
            Require(!ReferenceEquals(cancelled, resumed) && !resumed.IsCancellationRequested, "返回题材榜没有恢复新的补图生命周期");
            await Field<Task>("_rankGenreHydrationTask").WaitAsync(TimeSpan.FromSeconds(55));
            await WaitRankingCoversAsync();
            Require(before.SequenceEqual(Ranking.Select(item => item.Id)) && cursor == Field<string>("_rankCursor"), "返回补图重置了题材列表或续页位置");
            Require(content == Field<string>("_rankContent") && board == Field<string>("_rankBoard")
                && selections.OrderBy(pair => pair.Key).SequenceEqual(Field<Dictionary<string, string>>("_rankGenreSelections").OrderBy(pair => pair.Key)), "返回补图丢失了作品类型或题材选择");
            Require(Math.Abs(scroll.VerticalOffset - offset) < 1 && Control<ComboBox>("RankThemeCombo").SelectedValue?.ToString() == "cate_165", "返回补图改变了滚动位置或主题控件");
            await CaptureAsync("ranking-return-hydrated.png");
            return new { count = Ranking.Count, offset, returnedOffset = scroll.VerticalOffset, resumedWithNewToken = true, hydrationCompleted = true, covers = Ranking.Count(item => !string.IsNullOrWhiteSpace(item.CoverUrl)), selectionsRetained = true, cursorRetained = true };
        });
        await CheckAsync("ranking_content_changes_reset_genres_and_use_correct_type", async () =>
        {
            var evidence = new List<object>();
            foreach ((string contentId, string genre) in new[] { ("human", "short_play"), ("comic_series_rank", "comic_series"), ("ai_playlet", "ai_series") })
            {
                ComboBox contents = Control<ComboBox>("RankContentCombo");
                RankContentOption selected = contents.Items.OfType<RankContentOption>().First(item => item.Id == contentId);
                contents.SelectedItem = selected;
                Require(Field<Dictionary<string, string>>("_rankGenreSelections").Count == 0, "切换排行榜体裁没有立即清除旧题材");
                await WaitAsync(() => !Field<bool>("_rankBusy") && Field<string>("_rankContent") == contentId, "排行榜体裁切换未完成");
                await InvokeTaskAsync("RefreshRankGenreOptionsAsync");
                await WaitAsync(() => Control<ComboBox>("RankThemeCombo").Items.OfType<GenreOption>().Any(option => !string.IsNullOrWhiteSpace(option.Value)), "当前体裁的题材面板未就绪");
                ComboBox themes = Control<ComboBox>("RankThemeCombo");
                GenreOption option = themes.Items.OfType<GenreOption>().First(item => !string.IsNullOrWhiteSpace(item.Value));
                JsonElement response = await ObserveResponseAsync(() => themes.SelectedItem = option);
                await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Count > 0, "当前体裁的题材热榜未完成");
                AssertGenreRanking(response);
                Dictionary<string, string[]> query = ReadFilterCursorSelections(response.GetProperty("cursor").GetString()!);
                Require(query.GetValueOrDefault("genre")?.SequenceEqual(new[] { genre }) == true, "排行榜体裁没有映射真实筛选值");
                Require(query.GetValueOrDefault("sort")?.SequenceEqual(new[] { "hot_score" }) == true, "当前体裁题材热榜没有使用热度排序");
                evidence.Add(new { content = contentId, genre, theme = option.Value, count = Ranking.Count, query });
            }
            return new { mappings = evidence };
        });
        await CheckAsync("ranking_album_and_return_keep_platform_rank", async () =>
        {
            ComboBox contents = Control<ComboBox>("RankContentCombo");
            contents.SelectedItem = contents.Items.OfType<RankContentOption>().First(item => item.Id == "series_album");
            await WaitAsync(() => !Field<bool>("_rankBusy") && Field<string>("_rankContent") == "series_album", "系列剧榜没有返回");
            Require(Field<Dictionary<string, string>>("_rankGenreSelections").Count == 0, "系列剧榜继承了错误题材");
            Require(Control<FrameworkElement>("RankGenreControls").Visibility != Visibility.Visible || !Control<ComboBox>("RankThemeCombo").IsEnabled, "不支持题材的系列剧榜仍展示可交互分类");
            Require(Ranking.All(item => !item.IsCategoryResult), "系列剧榜被误换为短剧筛选结果");
            contents.SelectedItem = contents.Items.OfType<RankContentOption>().First(item => item.Id == "all");
            await WaitAsync(() => !Field<bool>("_rankBusy") && Field<string>("_rankContent") == "all", "返回全部平台榜未完成");
            await InvokeTaskAsync("RefreshRankGenreOptionsAsync");
            ComboBox themes = Control<ComboBox>("RankThemeCombo");
            themes.SelectedItem = themes.Items.OfType<GenreOption>().First(option => option.Value == "cate_165");
            await WaitAsync(() => !Field<bool>("_rankBusy") && Ranking.Count > 0 && Ranking[0].IsCategoryResult, "返回全部后未进入题材热榜");
            JsonElement response = await ObserveResponseAsync(() => Control<Button>("ClearRankGenresButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await WaitAsync(() => !Field<bool>("_rankBusy") && Field<Dictionary<string, string>>("_rankGenreSelections").Count == 0, "清空分类没有恢复平台榜");
            JsonElement[] items = response.GetProperty("items").EnumerateArray().ToArray();
            Require(items.Select(item => item.GetProperty("series_id").GetString()).SequenceEqual(Ranking.Select(item => item.Id)), "清空题材后平台榜顺序不正确");
            for (int index = 0; index < items.Length; index++)
            {
                JsonElement raw = items[index];
                RankedSeriesItem shown = Ranking[index];
                int rank = raw.TryGetProperty("rank", out JsonElement rankValue) ? rankValue.GetInt32() : 0;
                Require(!shown.IsCategoryResult && shown.Rank == rank, "恢复平台榜后官方名次被题材序号覆盖");
            }
            await WaitRankingCoversAsync();
            await CaptureAsync("ranking-platform-restored.png");
            return new { content = Field<string>("_rankContent"), board = Field<string>("_rankBoard"), count = Ranking.Count, restoredRealRank = true, albumNotFiltered = true };
        });
    }

    private static void AssertGenreRanking(JsonElement response)
    {
        Require(ResponseIds(response).SequenceEqual(Ranking.Select(item => item.Id)), "题材热榜与同一次筛选响应的顺序不同");
        Require(response.GetProperty("cursor").GetString() == Field<string>("_rankCursor"), "题材热榜游标与真实筛选响应不同");
        Require(Ranking.All(item => item.IsCategoryResult && item.Rank == 0 && !item.IsPromoted && item.ScoreLabel == "平台热度排序"), "题材筛选伪造了官方排名、热度分或推广位");
    }

    private static Dictionary<string, string[]> ReadFilterCursorSelections(string cursor)
    {
        // SDK 自己的续页模型保存了本次筛选条件；只检查条件，不记录会话或已看 ID。
        Require(cursor.StartsWith("s1.", StringComparison.Ordinal), "服务端筛选结果没有 SDK 续页模型");
        string payload = cursor[3..].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
        return document.RootElement.GetProperty("q").Deserialize<Dictionary<string, string[]>>()!;
    }

    private static async Task WaitRankingCoversAsync()
    {
        if (Ranking.Any(item => item.IsCategoryResult) && Field<Task?>("_rankGenreHydrationTask") is Task hydration)
            await hydration.WaitAsync(TimeSpan.FromSeconds(55));
        int expected = Math.Min(5, Ranking.Count(item => !string.IsNullOrWhiteSpace(item.CoverUrl)));
        await WaitAsync(() => VisualChildren<System.Windows.Controls.Image>(Control<ItemsControl>("RankingItems")).Count(image => image.Source is BitmapSource) >= expected, "排行榜首排封面未解码", 25000);
        await SettleAsync();
    }

    private static object RankLayout()
    {
        ItemsControl items = Control<ItemsControl>("RankingItems");
        Require(VisualChildren<WrapPanel>(items).Any(), "排行榜未采用横向换行布局");
        FrameworkElement first = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(0)!;
        FrameworkElement second = (FrameworkElement)items.ItemContainerGenerator.ContainerFromIndex(1)!;
        Point a = first.TransformToAncestor(items).Transform(new Point());
        Point b = second.TransformToAncestor(items).Transform(new Point());
        Require(Math.Abs(a.Y - b.Y) < 1 && b.X > a.X + 100, "排行榜首两卡没有横向并排");
        double maxRight = 0;
        foreach (RankedSeriesItem item in Ranking)
        {
            FrameworkElement container = (FrameworkElement)items.ItemContainerGenerator.ContainerFromItem(item)!;
            Rect bounds = container.TransformToAncestor(items).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            maxRight = Math.Max(maxRight, bounds.Right);
        }
        Require(maxRight <= items.ActualWidth + 1, "横向排行榜超出容器边界");
        Require(Control<ScrollViewer>("RankScroll").ScrollableWidth < 1, "窄窗口产生横向溢出");
        return new { width = Window!.ActualWidth, first = new { a.X, a.Y }, second = new { b.X, b.Y }, maxRight, containerWidth = items.ActualWidth };
    }

    private static void AssertCatalog(JsonElement response)
    {
        Require(ResponseIds(response).SequenceEqual(Catalog.Select(item => item.Id)), "目录与同一次真实响应不一致");
        string cursor = response.TryGetProperty("cursor", out JsonElement value) ? value.GetString() ?? "" : "";
        Require(cursor == Field<string>("_cursor"), "目录续页游标丢失");
    }

    private static long? KnownCount(JsonElement item, string key) => item.TryGetProperty(key, out JsonElement value) && value.GetInt64() > 0 ? value.GetInt64() : null;
    private static string[] ResponseIds(JsonElement response) => response.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString() ?? "").ToArray();
    private static List<SeriesItem> Catalog => ((IEnumerable<SeriesItem>)Field<object>("_catalog")).ToList();
    private static List<RankedSeriesItem> Ranking => ((IEnumerable<RankedSeriesItem>)Field<object>("_ranking")).ToList();

    private static async Task<JsonElement> ObserveResponseAsync(Action trigger)
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
            await WaitAsync(() => (long)sequence.GetValue(backend)! > before, "界面没有发出请求", 5000);
            string id = (before + 1).ToString();
            Require(pending.TryGetValue(id, out TaskCompletionSource<JsonElement>? completion), "没有观察到当前界面的请求任务");
            response = completion!.Task;
        }
        finally { writer.Release(); }
        return await response.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static Task<JsonElement> BackendCallAsync(string method, object args)
    {
        object backend = Field<object>("_backend");
        return (Task<JsonElement>)backend.GetType().GetMethod("CallAsync", PrivateInstance)!.Invoke(backend, new object?[] { method, args, CancellationToken.None })!;
    }

    private static async Task HydrationAsync()
    {
        if (Field<Task?>("_catalogHydrationTask") is Task hydration) await hydration.WaitAsync(TimeSpan.FromSeconds(55));
    }

    private static async Task WaitCatalogCoversAsync()
    {
        int expected = Math.Min(6, Catalog.Count(item => item.CoverUri != null));
        await WaitAsync(() => VisualChildren<System.Windows.Controls.Image>(Control<ItemsControl>("CatalogItems"))
            .Count(image => image.Source is BitmapSource) >= expected, "题材首排封面未解码", 25000);
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

    private static void RenderControlAlpha(FrameworkElement control, string filename)
    {
        int width = Math.Max(1, (int)Math.Ceiling(control.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(control.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
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
            object result = await test().WaitAsync(TimeSpan.FromSeconds(80));
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
    private static void SaveReport() => File.WriteAllText(Path.Combine(Output, "discovery-smoke.json"), JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures = Failures, tests = Results }, new JsonSerializerOptions { WriteIndented = true }));
    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (T child in VisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
