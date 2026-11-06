using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const long Caption = 0x00C00000;
    private const long SystemMenu = 0x00080000;
    private const long MinimizeBox = 0x00020000;
    private const long MaximizeBox = 0x00010000;
    private const long ThickFrame = 0x00040000;
    private const long DialogModalFrame = 0x00000001;
    private const uint GetIcon = 0x007F;
    private const uint SetIcon = 0x0080;
    private static readonly List<object> Tests = new();
    private static readonly List<string> DispatcherErrors = new();
    private static MainWindow Window = null!;
    private static string OutputDirectory = "";
    private static int Failures;
    private static bool Closed;
    private static int ClosingEvents;
    private static IntPtr BigIcon;

    [STAThread]
    private static int Main(string[] args)
    {
        OutputDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "results");
        Directory.CreateDirectory(OutputDirectory);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);
        app.DispatcherUnhandledException += (_, eventArgs) =>
        {
            DispatcherErrors.Add(eventArgs.Exception.GetType().Name + ": " + eventArgs.Exception.Message);
            eventArgs.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(app); }
            catch (Exception error) { Record("harness", false, new { error = error.ToString() }); }
            finally
            {
                if (Window != null && !Closed)
                {
                    // 测试异常时只回收自身窗口，不把兜底关闭计作自然关闭通过。
                    Set("_closing", true);
                    Set("_closeReady", true);
                    foreach (FieldInfo field in typeof(MainWindow).GetFields(Private))
                        if (field.GetValue(Window) is DispatcherTimer timer) timer.Stop();
                    Window.Close();
                }
                Record("dispatcher_errors", DispatcherErrors.Count == 0, DispatcherErrors);
                File.WriteAllText(Path.Combine(OutputDirectory, "caption-title-smoke.json"), JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now,
                    failures = Failures,
                    tests = Tests,
                    dispatcherErrors = DispatcherErrors,
                    sourceHashes = SourceHashes(),
                    scope = "生产 MainWindow 的空白标题与隐藏标题栏小图标定向验证。移除 Window_Loaded，不启动后端、不加载媒体、不读取或修改用户档案。使用本进程原生图标句柄作为小图标重置夹具；大图标保持原值。透明离屏窗口，不代表桌面合成截图、任务栏像素或真实内容播放验证。"
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync(System.Windows.Application app)
    {
        Window = new MainWindow
        {
            Width = 900,
            Height = 640,
            Left = -10000,
            Top = -10000,
            Opacity = 0,
            ShowActivated = false,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window,
            typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        Window.Closing += (_, _) => ClosingEvents++;
        Window.Closed += (_, _) => Closed = true;
        app.MainWindow = Window;
        Window.Show();
        await Settle();
        BigIcon = SendMessage(new WindowInteropHelper(Window).Handle, GetIcon, new IntPtr(1), IntPtr.Zero);

        await Check("empty_native_title_and_caption_buttons", () =>
        {
            object state = CaptionState(true);
            Require(AutomationProperties.GetName(Window) == "红果原生桌面版", "辅助功能名称丢失");
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            Require(process.MainWindowHandle != IntPtr.Zero, "空标题影响了进程主窗口发现");
            return new { state, mainWindowHandle = process.MainWindowHandle.ToInt64(), accessibilityNameRetained = true };
        });

        await Check("caption_icon_helper_clears_small_and_preserves_big", () =>
        {
            IntPtr handle = new WindowInteropHelper(Window).Handle;
            // 只给本测试窗口临时补回小图标，验证生产 helper 确实清除它而非仅依赖初始状态。
            IntPtr fixtureIcon = BigIcon != IntPtr.Zero ? BigIcon : LoadIcon(IntPtr.Zero, new IntPtr(32512));
            Require(fixtureIcon != IntPtr.Zero, "小图标验证夹具未建立");
            try
            {
                // 同时补一个非零大图标，避免初始大图标为零时出现零值对零值的空验证。
                SendMessage(handle, SetIcon, new IntPtr(1), fixtureIcon);
                SendMessage(handle, SetIcon, IntPtr.Zero, fixtureIcon);
                Require(SendMessage(handle, GetIcon, IntPtr.Zero, IntPtr.Zero) == fixtureIcon, "未补回测试窗口的小图标");
                Rect before = new(Window.Left, Window.Top, Window.Width, Window.Height);
                Invoke("UpdateCaptionIcon");
                Invoke("UpdateCaptionIcon");
                Rect after = new(Window.Left, Window.Top, Window.Width, Window.Height);
                Require(before == after, "刷新标题栏改变了窗口位置或尺寸");
                Require(SendMessage(handle, GetIcon, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero, "生产 helper 未清理小图标夹具");
                Require(SendMessage(handle, GetIcon, new IntPtr(1), IntPtr.Zero) == fixtureIcon, "生产 helper 改变了非零大图标夹具");
            }
            finally
            {
                SendMessage(handle, SetIcon, new IntPtr(1), BigIcon);
            }
            return new { state = CaptionState(true), smallIconFixtureCleared = true,
                nonzeroBigIconFixtureRetained = true, fixtureIcon = fixtureIcon.ToInt64(),
                helperIdempotent = true, geometryRetained = true, originalBigIcon = BigIcon.ToInt64() };
        });

        Window.Width = 642;
        Window.Height = 373;
        await Settle();
        await Check("resize_maximize_restore", () =>
        {
            Require(Math.Abs(Window.ActualWidth - 642) < 2 && Math.Abs(Window.ActualHeight - 373) < 2, "窗口缩放尺寸未生效");
            return new { width = Window.ActualWidth, height = Window.ActualHeight, state = CaptionState(true) };
        });
        Window.WindowState = WindowState.Maximized;
        await Settle();
        await Check("maximized_caption_retained", () =>
        {
            Require(Window.WindowState == WindowState.Maximized, "未进入最大化");
            return CaptionState(true);
        });
        Window.WindowState = WindowState.Normal;
        await Settle();
        await Check("restored_caption_and_dimensions", () =>
        {
            Require(Math.Abs(Window.ActualWidth - 642) < 2 && Math.Abs(Window.ActualHeight - 373) < 2, "还原后窗口尺寸发生变化");
            return CaptionState(true);
        });

        Invoke("ShowScreen", Window.FindName("PlayerScreen"));
        await Settle();
        Rect beforeCinema = new(Window.Left, Window.Top, Window.Width, Window.Height);
        Invoke("ToggleWindowCinema");
        await Settle();
        await Check("window_cinema_retains_caption_and_geometry", () =>
        {
            Require(Field<bool>("_windowCinema"), "未进入窗口内全屏");
            Rect afterCinema = new(Window.Left, Window.Top, Window.Width, Window.Height);
            Require(beforeCinema == afterCinema, "窗口内全屏改变了原窗口位置或尺寸");
            return new { state = CaptionState(true), geometryRetained = true };
        });

        Invoke("ToggleFullscreen");
        await Settle();
        await Check("screen_fullscreen_hides_caption", () =>
        {
            Require(Field<bool>("_fullscreen"), "未进入屏幕全屏");
            return CaptionState(false);
        });
        Invoke("ToggleFullscreen");
        await Settle();
        await Check("screen_fullscreen_exit_restores_empty_caption", () =>
        {
            Require(!Field<bool>("_fullscreen") && Field<bool>("_windowCinema"), "退出屏幕全屏未保留窗口内模式");
            return CaptionState(true);
        });
        Invoke("ToggleWindowCinema");
        await Settle();
        await Check("normal_layout_restores_empty_caption", () => CaptionState(true));

        Window.Close();
        var closeWait = Stopwatch.StartNew();
        while (!Closed && closeWait.ElapsedMilliseconds < 3000) await Task.Delay(20);
        await Check("one_explicit_close_cleans_resources", () =>
        {
            Require(Closed, "一次 Close 后窗口尚未自然关闭");
            Require(Field<object?>("_backend") == null && Field<object?>("_player") == null
                && Field<object?>("_standbyPlayer") == null, "测试意外启动后端或播放器");
            var runningTimers = new List<string>();
            foreach (FieldInfo field in typeof(MainWindow).GetFields(Private))
                if (field.GetValue(Window) is DispatcherTimer timer && timer.IsEnabled) runningTimers.Add(field.Name);
            Require(runningTimers.Count == 0, "关闭后计时器仍在运行");
            Require(Field<object?>("_presentationSource") == null, "关闭后原生消息源仍保留");
            return new { explicitCloseCalls = 1, closingEvents = ClosingEvents, closed = Closed, runningTimers,
                backendStarted = false, decoderStarted = false, messageHookReleased = true };
        });
    }

    private static object CaptionState(bool captionExpected)
    {
        IntPtr handle = new WindowInteropHelper(Window).Handle;
        long style = GetWindowLongPtr(handle, -16).ToInt64();
        long extendedStyle = GetWindowLongPtr(handle, -20).ToInt64();
        IntPtr smallIcon = SendMessage(handle, GetIcon, IntPtr.Zero, IntPtr.Zero);
        IntPtr bigIcon = SendMessage(handle, GetIcon, new IntPtr(1), IntPtr.Zero);
        IntPtr smallIcon2 = SendMessage(handle, GetIcon, new IntPtr(2), IntPtr.Zero);
        Require(Window.Title == "" && GetWindowTextLength(handle) == 0, "标题栏文字并非空白");
        Require(((style & Caption) == Caption) == captionExpected, "原生标题栏样式与预期不符");
        Require(((extendedStyle & DialogModalFrame) != 0) == captionExpected, "标题栏图标抑制样式与窗口模式不符");
        Require(smallIcon == IntPtr.Zero, "标题栏小图标句柄未清除");
        Require(bigIcon == BigIcon, "窗口大图标发生变化");
        if (captionExpected)
            Require((style & (SystemMenu | MinimizeBox | MaximizeBox | ThickFrame))
                == (SystemMenu | MinimizeBox | MaximizeBox | ThickFrame), "窗口按钮或可调边框样式丢失");
        return new { wpfTitle = Window.Title, nativeTitleLength = GetWindowTextLength(handle),
            nativeStyle = "0x" + style.ToString("X"), caption = (style & Caption) == Caption,
            extendedStyle = "0x" + extendedStyle.ToString("X"), dialogModalFrame = (extendedStyle & DialogModalFrame) != 0,
            smallIcon = smallIcon.ToInt64(), bigIcon = bigIcon.ToInt64(), smallIcon2 = smallIcon2.ToInt64(),
            systemMenu = (style & SystemMenu) != 0, minimizeBox = (style & MinimizeBox) != 0,
            maximizeBox = (style & MaximizeBox) != 0, thickFrame = (style & ThickFrame) != 0 };
    }

    private static void LoadResources(System.Windows.Application app)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"))
            .Root!.Element(presentation + "Application.Resources")!;
        var resources = new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), source.Elements());
        using var reader = resources.CreateReader();
        app.Resources = (ResourceDictionary)XamlReader.Load(reader);
    }

    private static Dictionary<string, string> SourceHashes()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../"));
        var hashes = new Dictionary<string, string>();
        foreach (string relative in new[] { "native/Hongguo.Desktop/MainWindow.xaml", "native/Hongguo.Desktop/MainWindow.xaml.cs",
            "native/Hongguo.Desktop/MainWindow.Presentation.cs", "native/Hongguo.Desktop/Hongguo.Desktop.csproj",
            "native/verification/caption-title-smoke/Program.cs" })
        {
            string path = Path.Combine(root, relative);
            hashes[relative] = File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "missing";
        }
        return hashes;
    }

    private static async Task Check(string name, Func<object> body)
    {
        try { Record(name, true, body()); }
        catch (Exception error) { Record(name, false, new { error = error.Message }); }
        await Task.CompletedTask;
    }

    private static void Record(string name, bool passed, object detail)
    {
        if (!passed) Failures++;
        Tests.Add(new { name, passed, detail });
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail));
    }

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
    }

    private static T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(Window)!;
    private static void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(Window, value);
    private static void Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(Window, args);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
}
