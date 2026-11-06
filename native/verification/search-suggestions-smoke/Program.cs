using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static MainWindow Window = null!;
    private static object Backend = null!;
    private static readonly List<object> Tests = new();
    private static readonly List<string> Errors = new();
    private static int Failures;
    private static string Profile = "";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--fixture")) return FixtureAsync().GetAwaiter().GetResult();
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Element(p + "Application.Resources")!;
        var resources = new XElement(p + "ResourceDictionary", new XAttribute("xmlns", p.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", x.NamespaceName), source.Elements());
        using (var reader = resources.CreateReader()) app.Resources = (ResourceDictionary)XamlReader.Load(reader);
        app.DispatcherUnhandledException += (_, e) => { Errors.Add(e.Exception.ToString()); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception e) { Record("harness", false, new { error = e.ToString() }); }
            finally
            {
                if (Window != null)
                {
                    Invoke("CloseSearchSuggestions");
                    Set("_closing", true); Set("_closeReady", true);
                    foreach (FieldInfo field in typeof(MainWindow).GetFields(Private))
                        if (field.GetValue(Window) is DispatcherTimer timer) timer.Stop();
                    Window.Close();
                }
                if (Backend != null) ((IDisposable)Backend).Dispose();
                if (Profile != "" && Directory.Exists(Profile)) Directory.Delete(Profile, true);
                if (Errors.Count > 0) Record("dispatcher_errors", false, Errors);
                File.WriteAllText("results/search-suggestions-smoke.json", JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures = Failures, tests = Tests, dispatcherErrors = Errors, scope = "本地假后端、生产窗口与JSONL；无线上内容、无用户配置、无桌面视频合成" }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }


    private static async Task RunAsync()
    {
        Profile = Path.Combine(Path.GetTempPath(), "openhongguo-suggest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Profile);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Profile, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("--fixture");
        Process process = Process.Start(start)!;
        _ = process.StandardError.ReadToEndAsync();
        Type type = typeof(MainWindow).Assembly.GetType("Hongguo.Desktop.BackendClient")!;
        Backend = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("process", Private)!.SetValue(Backend, process);
        FieldInfo pending = type.GetField("pending", Private)!;
        pending.SetValue(Backend, Activator.CreateInstance(pending.FieldType));
        type.GetField("writeLock", Private)!.SetValue(Backend, new SemaphoreSlim(1, 1));
        _ = (Task)type.GetMethod("ReadResponsesAsync", Private)!.Invoke(Backend, null)!;
        Window = new MainWindow { Title = "1.0 搜索联想本地验证", Width = 960, Height = 650, Left = 50, Top = 40, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = true };
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window, typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        Set("_backend", Backend); Set("_configDir", Profile);
        InputMethod.SetIsInputMethodEnabled(Window, false);
        System.Windows.Application.Current.MainWindow = Window;
        Window.Show(); Window.Activate();
        await Focus();

        await Check("debounce_single_request_and_no_catalog_navigation", async () =>
        {
            Input.Text = "全"; await Task.Delay(70); Input.Text = "全球"; await Task.Delay(70); Input.Text = "全球死";
            await Wait(() => Words.SequenceEqual(new[] { "全球死考", "全球死局" }));
            JsonElement stats = await Call("stats");
            Require(stats.GetProperty("suggest").GetArrayLength() == 1, "防抖重复请求");
            Require((string)Field("_query")! == "" && Control<TextBlock>("PageTitle").Text == "发现好剧", "联想修改了搜索页");
            return new { requests = 1, words = Words, navigationUnchanged = true };
        });
        await Check("arrow_selection_enter_submits_display_title", async () =>
        {
            RaiseKey(Key.Down); Require(List.SelectedIndex == 0, "下方向未选第一项");
            RaiseKey(Key.Up); Require(List.SelectedIndex == 1, "上方向未循环到末项");
            RaiseKey(Key.Enter);
            await Wait(() => (string)Field("_query")! == "全球死局" && !Popup.IsOpen);
            await Task.Delay(120);
            JsonElement stats = await Call("stats");
            Require(stats.GetProperty("search").EnumerateArray().Count(item => item.GetString() == "全球死局") == 1, "显示词未提交或重复提交");
            return new { submitted = "全球死局", closed = true };
        });
        await Check("cache_reuses_prefix_and_escape_cancels", async () =>
        {
            await Focus(); Input.Text = "全球死";
            await Wait(() => Words.Length == 2 && Popup.IsOpen);
            int before = (await Call("stats")).GetProperty("suggest").GetArrayLength();
            RaiseKey(Key.Escape); await Task.Delay(450);
            Require(!Popup.IsOpen, "Esc后重开");
            Require((await Call("stats")).GetProperty("suggest").GetArrayLength() == before, "缓存命中仍发请求");
            return new { cached = true, escaped = true };
        });
        await Check("rapid_refocus_keeps_new_request_after_old_popup_close", async () =>
        {
            Input.Text = "快速聚焦"; await Wait(() => Popup.IsOpen && Words.Length == 2);
            for (int index = 0; index < 5; index++)
            {
                Control<Button>("SearchButton").Focus();
                Input.Focus(); Keyboard.Focus(Input);
                await Wait(() => Popup.IsOpen && Words.Length == 2);
            }
            return new { refocusCycles = 5, latestRequestPreserved = true };
        });
        await Check("late_old_prefix_never_overwrites_new", async () =>
        {
            Input.Text = "慢旧";
            await Wait(async () => (await Call("stats")).GetProperty("suggest").EnumerateArray().Any(item => item.GetString() == "慢旧"));
            Input.Text = "新词";
            await Wait(() => Words.SequenceEqual(new[] { "新词一", "新词二" }));
            await Task.Delay(950);
            Require(Words.SequenceEqual(new[] { "新词一", "新词二" }), "旧词回包覆盖当前");
            return new { staleIgnored = true, words = Words };
        });
        await Check("empty_text_and_ime_composition_do_not_query", async () =>
        {
            Input.Text = ""; await Task.Delay(400); Require(!Popup.IsOpen, "空输入仍显示");
            int before = (await Call("stats")).GetProperty("suggest").GetArrayLength();
            Set("_searchComposition", true); Input.Text = "全球死"; await Task.Delay(450);
            Require(!Popup.IsOpen && (await Call("stats")).GetProperty("suggest").GetArrayLength() == before, "组合态请求联想");
            int searches = (await Call("stats")).GetProperty("search").GetArrayLength();
            RaiseKey(Key.Enter); await Task.Delay(100);
            Require((await Call("stats")).GetProperty("search").GetArrayLength() == searches, "输入法确认回车触发搜索");
            Set("_searchComposition", false);
            return new { emptyClosed = true, cachedCompositionSuppressed = true, composingEnterSuppressed = true };
        });
        await Check("first_enter_without_suggestions_searches_once", async () =>
        {
            Input.Text = "首次回车"; RaiseKey(Key.Enter);
            await Wait(() => (string)Field("_query")! == "首次回车");
            await Task.Delay(450);
            JsonElement stats = await Call("stats");
            Require(stats.GetProperty("search").EnumerateArray().Count(item => item.GetString() == "首次回车") == 1, "首次回车重复搜索");
            Require(!stats.GetProperty("suggest").EnumerateArray().Any(item => item.GetString() == "首次回车") && !Popup.IsOpen, "提交后仍触发联想");
            return new { searches = 1, noSuggestionAfterSubmit = true };
        });
        await Check("empty_and_error_keep_direct_enter_search", async () =>
        {
            Input.Text = "空结果"; await Wait(() => Popup.IsOpen && Control<TextBlock>("SearchSuggestionsStatus").Text.Contains("暂无联想"));
            Input.Text = "错误"; await Wait(() => Control<TextBlock>("SearchSuggestionsStatus").Text.Contains("暂未取得"));
            RaiseKey(Key.Enter); await Wait(() => (string)Field("_query")! == "错误" && !Popup.IsOpen);
            return new { errorVisible = true, directSearch = true };
        });
        await Check("mouse_choice_submits_without_changing_focus", async () =>
        {
            await Focus(); Input.Text = "鼠标"; await Wait(() => Words.Length == 2);
            List.UpdateLayout();
            var row = (ListBoxItem)List.ItemContainerGenerator.ContainerFromIndex(0);
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            await Wait(() => (string)Field("_query")! == "鼠标一" && !Popup.IsOpen);
            return new { submitted = "鼠标一" };
        });
        await Check("focus_background_resize_and_hidden_close_popup", async () =>
        {
            await Focus(); Input.Text = "生命周期"; await Wait(() => Popup.IsOpen && Words.Length == 2);
            Control<Button>("SearchButton").Focus(); await Task.Delay(80); Require(!Popup.IsOpen, "失焦未关闭");
            await Focus(); await Wait(() => Popup.IsOpen);
            Window.Width = 642; await Task.Delay(80); Require(!Popup.IsOpen, "缩窗未关闭");
            Input.Text = "慢后台"; await Task.Delay(350);
            var other = new System.Windows.Window { Title = "联想对照窗口", Width = 200, Height = 120, Left = 20, Top = 20 };
            other.Show(); other.Activate(); await Task.Delay(1200); Require(!Popup.IsOpen, "后台回包重开联想"); other.Close();
            await Focus(); await Wait(() => Popup.IsOpen);
            Control<Border>("HeaderPanel").Visibility = Visibility.Collapsed; await Task.Delay(80); Require(!Popup.IsOpen, "隐藏搜索栏未关闭");
            Control<Border>("HeaderPanel").Visibility = Visibility.Visible;
            return new { lostFocus = true, resized = true, deactivated = true, hidden = true };
        });
        await Check("narrow_window_popup_stays_inside_client", async () =>
        {
            Window.Width = 480; Window.Height = 360; await Focus();
            Input.Text = "窄窗"; await Wait(() => Popup.IsOpen && Words.Length == 2);
            var border = (FrameworkElement)Popup.Child;
            Point root = Control<Grid>("RootLayout").PointToScreen(new Point());
            Point panel = border.PointToScreen(new Point());
            DpiScale dpi = VisualTreeHelper.GetDpi(Window);
            FrameworkElement layout = Control<Grid>("RootLayout");
            IntPtr popupHandle = ((HwndSource)PresentationSource.FromVisual(border)!).Handle;
            Require(GetWindow(popupHandle, 4) == new WindowInteropHelper(Window).Handle, "联想原生窗口未归属主窗");
            Require((GetWindowLongPtr(popupHandle, -20).ToInt64() & 8) == 0, "联想原生窗口仍是跨应用置顶");
            Require(panel.X >= root.X - 1 && panel.X + border.ActualWidth * dpi.DpiScaleX <= root.X + layout.ActualWidth * dpi.DpiScaleX + 1, "下拉越过客户区横边界");
            Require(panel.Y >= root.Y - 1 && panel.Y + border.ActualHeight * dpi.DpiScaleY <= root.Y + layout.ActualHeight * dpi.DpiScaleY + 1, "下拉越过客户区底部");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(border.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(border.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(border);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create("results/search-suggestions-menu.png")) encoder.Save(stream);
            return new { width = border.ActualWidth, height = border.ActualHeight, dpi = dpi.DpiScaleX, nativeOwnerVerified = true, nativeNotTopmost = true };
        });
        await Check("close_cancels_pending_suggestion", async () =>
        {
            Input.Text = "慢关闭"; await Task.Delay(350);
            bool closed = false; Window.Closed += (_, _) => closed = true;
            Window.Close(); await Wait(() => closed);
            Require(!Popup.IsOpen && Field("_searchSuggestionRequest") == null, "关闭后请求或浮层残留");
            return new { closed = true, requestCancelled = true };
        });
    }

    private static TextBox Input => Control<TextBox>("SearchInput");
    private static ListBox List => Control<ListBox>("SearchSuggestionsList");
    private static Popup Popup => Control<Popup>("SearchSuggestionsPopup");
    private static string[] Words => List.Items.Cast<string>().ToArray();
    private static async Task Focus()
    {
        Window.Activate();
        bool accepted = SetForegroundWindow(new WindowInteropHelper(Window).Handle);
        Input.Focus(); Keyboard.Focus(Input);
        try
        {
            var timeout = Stopwatch.StartNew();
            Console.WriteLine($"FOCUS_WAIT pid={Environment.ProcessId} hwnd={new WindowInteropHelper(Window).Handle.ToInt64()} title=1.0 搜索联想本地验证");
            while (!(bool)Invoke("HasPlaybackForeground")!)
            {
                if (timeout.Elapsed.TotalSeconds > 30) throw new TimeoutException();
                await Task.Delay(40);
            }
        }
        catch { throw new InvalidOperationException($"自有窗口前台未就绪 accepted={accepted}, active={Window.IsActive}, foreground={GetForegroundWindow().ToInt64()}, expected={new WindowInteropHelper(Window).Handle.ToInt64()}, root={GetAncestor(GetForegroundWindow(), 3).ToInt64()}, closing={Field("_closing")}"); }
        await Task.Delay(100);
    }
    private static void RaiseKey(Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Input.RaiseEvent(args);
        if (args.Handled) return;
        args.RoutedEvent = Keyboard.KeyDownEvent;
        Input.RaiseEvent(args);
    }
    private static object? Field(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(Window);
    private static void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(Window, value);
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(Window, args);
    private static T Control<T>(string name) where T : class => (T)Window.FindName(name);
    private static Task<JsonElement> Call(string method) => (Task<JsonElement>)Backend.GetType().GetMethod("CallAsync", Private)!.Invoke(Backend, new object?[] { method, null, CancellationToken.None })!;
    private static async Task Wait(Func<bool> condition) { var timer = Stopwatch.StartNew(); while (!condition()) { if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException("联想状态超时"); await Task.Delay(20); } }
    private static async Task Wait(Func<Task<bool>> condition) { var timer = Stopwatch.StartNew(); while (!await condition()) { if (timer.ElapsedMilliseconds > 5000) throw new TimeoutException("后端状态超时"); await Task.Delay(20); } }
    private static async Task Check(string name, Func<Task<object>> test) { try { Record(name, true, await test()); } catch (Exception e) { Record(name, false, new { error = e.ToString(), state = new { Window.IsActive, foreground = GetForegroundWindow().ToInt64(), owner = new WindowInteropHelper(Window).Handle.ToInt64(), foregroundAllowed = Invoke("HasPlaybackForeground"), focused = Input.IsKeyboardFocusWithin, Input.IsVisible, popup = Popup.IsOpen, query = Input.Text, version = Field("_searchSuggestionVersion"), request = Field("_searchSuggestionRequest") != null, composition = Field("_searchComposition") } }); } }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Record(string name, bool passed, object detail) { Tests.Add(new { name, passed, detail }); if (!passed) Failures++; Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail)); }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flag);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    private static async Task<int> FixtureAsync()
    {
        object sync = new();
        var suggests = new List<string>(); var searches = new List<string>(); var tasks = new List<Task>();
        void Reply(object response) { lock (sync) Console.WriteLine(JsonSerializer.Serialize(response)); }
        async Task Handle(string id, string method, JsonElement arguments)
        {
            object result;
            if (method == "suggest")
            {
                string query = arguments.GetProperty("query").GetString()!;
                lock (sync) suggests.Add(query);
                await Task.Delay(query.StartsWith("慢", StringComparison.Ordinal) ? 1000 : 45);
                if (query == "错误") { Reply(new { id, error = new { code = "network_error", message = "fixture" } }); return; }
                result = query == "空结果" ? Array.Empty<string>() : query == "全球死" ? new[] { "全球死考", "全球死局" } : new[] { query + "一", query + "二" };
            }
            else if (method == "stats") { lock (sync) result = new { suggest = suggests.ToArray(), search = searches.ToArray() }; }
            else if (method == "search") { lock (sync) searches.Add(arguments.GetProperty("query").GetString()!); result = new { items = Array.Empty<object>(), has_more = false, cursor = "" }; }
            else if (method == "cancel") return;
            else result = new { };
            Reply(new { id, result });
        }
        while (await Console.In.ReadLineAsync() is string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            tasks.Add(Handle(root.GetProperty("id").GetString()!, root.GetProperty("method").GetString()!, root.GetProperty("args").Clone()));
        }
        await Task.WhenAll(tasks);
        return 0;
    }
}
