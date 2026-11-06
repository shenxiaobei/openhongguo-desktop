using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
using System.Windows.Threading;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Tests = new();
    private static readonly List<object> Errors = new();
    private static readonly List<object> Observations = new();
    private static readonly Stopwatch DiagnosticClock = Stopwatch.StartNew();
    private static readonly WinEventCallback ForegroundCallback = ForegroundChanged;
    private static string Phase = "startup";
    private static IntPtr ForegroundHook;
    private static MainWindow Window = null!;
    private static Process? Helper;
    private static IntPtr HelperHandle;
    private static int Failures;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--helper")) return RunHelper();
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);
        app.DispatcherUnhandledException += (_, error) =>
        {
            Errors.Add(new { type = error.Exception.GetType().Name, stack = error.Exception.StackTrace });
            error.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { error = error.ToString() }); }
            finally
            {
                if (ForegroundHook != IntPtr.Zero) UnhookWinEvent(ForegroundHook);
                if (Window != null)
                {
                    Set("_closing", true);
                    Set("_closeReady", true);
                    Invoke("RestoreWindowControls");
                    foreach (var field in typeof(MainWindow).GetFields(Private))
                        if (field.GetValue(Window) is DispatcherTimer timer) timer.Stop();
                    Window.Close();
                }
                if (Helper != null && !Helper.HasExited)
                {
                    await Helper.StandardInput.WriteLineAsync("close");
                    if (!Helper.WaitForExit(3000)) Helper.Kill();
                }
                if (Errors.Count > 0) Record("dispatcher_errors", false, Errors);
                File.WriteAllText("results/foreground-smoke.json", JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, failures = Failures, tests = Tests, dispatcherErrors = Errors, observations = Observations,
                    scope = "生产MainWindow及Popup；另一个自有进程的可见普通窗口，真实GetForegroundWindow、HWND owner、扩展样式和Z序。无媒体、无SDK后端、无用户应用操作；不代表视频桌面合成验收。"
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static void LoadResources(System.Windows.Application app)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Element(presentation + "Application.Resources")!;
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), source.Elements());
        using var reader = resources.CreateReader();
        app.Resources = (ResourceDictionary)XamlReader.Load(reader);
    }

    private static int RunHelper()
    {
        var app = new System.Windows.Application();
        var window = new System.Windows.Window
        {
            Title = "红果前台作用域验证 · 自有跨进程对照窗口", Left = 100, Top = 70, Width = 1040, Height = 580,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = true,
            Content = new TextBlock { Text = "仅用于播放器浮层跨进程验证，测试后自动关闭", Margin = new Thickness(28), FontSize = 20 }
        };
        app.Startup += (_, _) =>
        {
            window.Show();
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            Console.WriteLine(hwnd.ToInt64());
            _ = Task.Run(async () =>
            {
                while (await Console.In.ReadLineAsync() is string command)
                {
                    string response = await window.Dispatcher.InvokeAsync(() =>
                    {
                        string[] parts = command.Split(' ');
                        if (parts[0] == "close") { window.Close(); return "closed"; }
                        if (parts[0] == "yield")
                        {
                            AllowSetForegroundWindow(uint.Parse(parts[1]));
                            return "yielded";
                        }
                        window.Activate();
                        bool accepted = SetForegroundWindow(hwnd);
                        IntPtr target = parts.Length > 1 ? new IntPtr(long.Parse(parts[1])) : IntPtr.Zero;
                        return JsonSerializer.Serialize(new
                        {
                            accepted, foreground = GetForegroundWindow().ToInt64(), helper = hwnd.ToInt64(),
                            target = Snapshot(target), helperIndex = ZIndex(hwnd), targetIndex = ZIndex(target)
                        });
                    });
                    Console.WriteLine(response);
                    if (command == "close") return;
                }
            });
        };
        return app.Run();
    }

    private static async Task RunAsync()
    {
        Window = new MainWindow
        {
            Title = "红果 1.0 · 跨进程浮层回归", Left = 100, Top = 70, Width = 1040, Height = 580,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = true
        };
        Window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), Window,
            typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        System.Windows.Application.Current.MainWindow = Window;
        Window.Show();
        Window.Activated += (_, _) => Observe("main_activated");
        Window.Deactivated += (_, _) => Observe("main_deactivated");
        Invoke("ShowScreen", Control<Grid>("PlayerScreen"));
        Control<FrameworkElement>("VideoHost").Visibility = Visibility.Hidden;
        await Settle();
        Helper = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--helper")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            CreateNoWindow = true
        })!;
        HelperHandle = new IntPtr(long.Parse((await Helper.StandardOutput.ReadLineAsync())!));
        ForegroundHook = SetWinEventHook(3, 3, IntPtr.Zero, ForegroundCallback, 0, 0, 0);
        Require(Helper.Id != Environment.ProcessId, "对照窗口没有运行在另一个进程");
        await ActivateMain();
        Click("FullscreenButton");
        await Settle();
        Popup controls = Field<Popup>("_fullscreenControls");

        await Check("popup_native_owner_and_non_topmost_styles", async () =>
        {
            await ActivateMain();
            Invoke("RevealFullscreenControls");
            await Wait(() => controls.IsOpen, 2000);
            Click("PlaybackOptionsButton");
            await Settle();
            Popup menu = Field<Popup>("_playbackOptionsPopup");
            IntPtr controlHandle = PopupHandle(controls), menuHandle = PopupHandle(menu);
            var detail = new { main = MainHandle.ToInt64(), controls = Snapshot(controlHandle), settings = Snapshot(menuHandle) };
            Invoke("ClosePlaybackOptions");
            await Settle();
            Require(GetWindow(controlHandle, 4) == MainHandle, "播放浮层 HWND 未归属于主窗口 / " + JsonSerializer.Serialize(detail));
            Require(GetWindow(menuHandle, 4) == MainHandle, "设置浮层 HWND 未归属于主窗口 / " + JsonSerializer.Serialize(detail));
            Require(!Topmost(controlHandle) && !Topmost(menuHandle), "播放或设置浮层仍有 WS_EX_TOPMOST / " + JsonSerializer.Serialize(detail));
            return detail;
        });

        await Check("cross_process_switch_hides_overlay_and_blocks_activity", async () =>
        {
            await ActivateMain();
            Observe("switch_before_reveal");
            Invoke("RevealFullscreenControls");
            Click("PlaybackOptionsButton");
            Observe("switch_after_menu_click");
            await Settle();
            Observe("switch_menu_settled");
            Popup menu = Field<Popup>("_playbackOptionsPopup");
            IntPtr controlHandle = PopupHandle(controls), menuHandle = PopupHandle(menu);
            var elapsed = Stopwatch.StartNew();
            object probe = await ActivateHelper(controlHandle);
            Observe("switch_helper_activated");
            await Wait(() => !controls.IsOpen && !menu.IsOpen, 1500);
            Observe("switch_popups_closed");
            double closeMs = elapsed.Elapsed.TotalMilliseconds;
            Invoke("RevealFullscreenControls");
            Observe("switch_after_background_reveal");
            Invoke("HandlePlayerPointerActivity", new Point(20, 10));
            Observe("switch_after_background_pointer");
            Invoke("UpdateFullscreenControls");
            Observe("switch_after_background_update");
            await Task.Delay(350);
            Observe("switch_after_background_delay");
            Require(GetForegroundWindow() == HelperHandle, "后台活动抢走了真实系统前台窗口");
            Require(!controls.IsOpen && !menu.IsOpen, "后台事件重开了播放或设置浮层");
            Require(Window.Cursor != Cursors.None, "后台没有恢复局部鼠标");
            Require(!IsWindowVisible(controlHandle) && !IsWindowVisible(menuHandle), "WPF关闭后原生浮层窗口仍可见");
            return new { helperPid = Helper!.Id, mainPid = Environment.ProcessId, closeMs, mainIsActive = Window.IsActive,
                foreground = GetForegroundWindow().ToInt64(), probe, controlNativeVisible = IsWindowVisible(controlHandle), settingsNativeVisible = IsWindowVisible(menuHandle) };
        });

        await Check("cross_process_probe_while_owner_dispatcher_is_busy", async () =>
        {
            await ActivateMain();
            Observe("return_main_activated");
            Invoke("RevealFullscreenControls");
            await Settle();
            IntPtr handle = PopupHandle(controls);
            AllowSetForegroundWindow((uint)Helper!.Id);
            await Helper.StandardInput.WriteLineAsync("activate " + handle.ToInt64());
            // 主UI短暂忙碌时仍要求原生层级不压住其他前台；对照进程独立采样。
            Thread.Sleep(500);
            string response = (await Helper.StandardOutput.ReadLineAsync())!;
            using var probe = JsonDocument.Parse(response);
            JsonElement root = probe.RootElement;
            bool visible = root.GetProperty("target").GetProperty("visible").GetBoolean();
            bool topmost = root.GetProperty("target").GetProperty("topmost").GetBoolean();
            int helperIndex = root.GetProperty("helperIndex").GetInt32(), targetIndex = root.GetProperty("targetIndex").GetInt32();
            Require(root.GetProperty("foreground").GetInt64() == HelperHandle.ToInt64(), "跨进程采样时对照窗口没有成为前台 / " + response);
            Require(!visible || (!topmost && targetIndex > helperIndex), "主UI忙碌期间浮层压住跨进程前台 / " + response);
            await Wait(() => !controls.IsOpen, 1500);
            return JsonSerializer.Deserialize<JsonElement>(response);
        });

        await Check("returning_foreground_restores_settings_interaction", async () =>
        {
            await ActivateMain();
            Invoke("RevealFullscreenControls");
            await Wait(() => controls.IsOpen, 2000);
            Click("PlaybackOptionsButton");
            Observe("return_menu_clicked");
            await Settle();
            Observe("return_menu_settled");
            Popup menu = Field<Popup>("_playbackOptionsPopup");
            Require(menu.IsOpen, "回到前台后设置打不开");
            FrameworkElement content = (FrameworkElement)menu.Child;
            content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            await Settle();
            Require(menu.IsOpen && controls.IsOpen, "设置键盘焦点被误判为其他应用前台");
            Require(GetForegroundWindow() == MainHandle || GetAncestor(GetForegroundWindow(), 3) == MainHandle,
                "设置交互没有保持在播放器拥有的窗口链");
            IntPtr reopenedMenuHandle = PopupHandle(menu);
            Require(GetWindow(reopenedMenuHandle, 4) == MainHandle && !Topmost(reopenedMenuHandle),
                "重新打开的设置浮层未保持主窗口owner及非置顶样式 / " + JsonSerializer.Serialize(Snapshot(reopenedMenuHandle)));
            var result = new { foreground = GetForegroundWindow().ToInt64(), main = MainHandle.ToInt64(), menu = Snapshot(reopenedMenuHandle),
                reopenedMenuOwnerAndNonTopmostAsserted = true, keyboardFocus = Keyboard.FocusedElement?.GetType().Name };
            Invoke("ClosePlaybackOptions");
            return result;
        });
        Require(Field<object?>("_backend") == null && Field<object?>("_player") == null, "浮层测试意外启动SDK或媒体");
    }

    private static async Task ActivateMain()
    {
        if (Helper != null)
        {
            await Helper.StandardInput.WriteLineAsync("yield " + Environment.ProcessId);
            Require(await Helper.StandardOutput.ReadLineAsync() == "yielded", "对照进程未交回前台权限");
        }
        Window.Activate();
        SetForegroundWindow(MainHandle);
        await Wait(() => GetForegroundWindow() == MainHandle && Window.IsActive, 3000);
        await Settle();
    }

    private static async Task<object> ActivateHelper(IntPtr target)
    {
        AllowSetForegroundWindow((uint)Helper!.Id);
        await Helper.StandardInput.WriteLineAsync("activate " + target.ToInt64());
        string response = (await Helper.StandardOutput.ReadLineAsync())!;
        await Wait(() => GetForegroundWindow() == HelperHandle, 3000);
        return JsonSerializer.Deserialize<JsonElement>(response);
    }

    private static async Task Check(string name, Func<Task<object>> action)
    {
        Phase = name;
        try { Record(name, true, await action()); }
        catch (Exception error) { Record(name, false, new { error = error.Message, state = DiagnosticSnapshot() }); }
    }
    private static void Record(string name, bool passed, object detail)
    {
        if (!passed) Failures++;
        Tests.Add(new { name, passed, detail });
        Console.WriteLine(name + ": " + passed + " / " + JsonSerializer.Serialize(detail));
    }
    private static IntPtr MainHandle => new WindowInteropHelper(Window).Handle;
    private static void Observe(string action) => Observations.Add(new { elapsedMs = DiagnosticClock.Elapsed.TotalMilliseconds, phase = Phase, action, state = DiagnosticSnapshot() });
    private static void ForegroundChanged(IntPtr hook, uint eventId, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
        => Observe("native_foreground_changed");
    private static object DiagnosticSnapshot()
    {
        IntPtr foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out uint foregroundPid);
        Popup? menu = Field<Popup?>("_playbackOptionsPopup"), controls = Field<Popup?>("_fullscreenControls");
        return new
        {
            foreground = foreground.ToInt64(), rootOwner = GetAncestor(foreground, 3).ToInt64(), foregroundPid,
            foregroundRole = foregroundPid == Environment.ProcessId ? "main-process" : foregroundPid == Helper?.Id ? "helper-process" : "other-process",
            main = MainHandle.ToInt64(), helper = HelperHandle.ToInt64(), mainIsActive = Window.IsActive,
            hasPlaybackForeground = Invoke("HasPlaybackForeground"), keyboardFocus = Keyboard.FocusedElement?.GetType().Name,
            menuRegistered = menu != null, menuOpen = menu?.IsOpen, menu = PopupSnapshot(menu),
            controlsOpen = controls?.IsOpen, controls = PopupSnapshot(controls)
        };
    }
    private static object? PopupSnapshot(Popup? popup)
        => popup?.Child != null && PresentationSource.FromVisual(popup.Child) is HwndSource source ? Snapshot(source.Handle) : null;
    private static IntPtr PopupHandle(Popup popup) => ((HwndSource)PresentationSource.FromVisual(popup.Child)).Handle;
    private static bool Topmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, -20).ToInt64() & 8) != 0;
    private static object Snapshot(IntPtr hwnd) => new { hwnd = hwnd.ToInt64(), owner = GetWindow(hwnd, 4).ToInt64(), rootOwner = GetAncestor(hwnd, 3).ToInt64(), visible = IsWindowVisible(hwnd), topmost = Topmost(hwnd), exStyle = GetWindowLongPtr(hwnd, -20).ToInt64(), zIndex = ZIndex(hwnd) };
    private static int ZIndex(IntPtr target)
    {
        if (target == IntPtr.Zero) return -1;
        int index = 0;
        for (IntPtr current = GetTopWindow(IntPtr.Zero); current != IntPtr.Zero; current = GetWindow(current, 2))
        {
            if (current == target) return index;
            if (++index > 10000) break;
        }
        return -1;
    }
    private static T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(Window)!;
    private static void Set(string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(Window, value);
    private static object? Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(Window, args);
    private static T Control<T>(string name) where T : class => (T)Window.FindName(name);
    private static void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> predicate, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException("真实窗口状态等待超时");
            await Task.Delay(20);
        }
    }
    private static async Task Settle()
    {
        await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Window.UpdateLayout();
        await Task.Delay(120);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    private delegate void WinEventCallback(IntPtr hook, uint eventId, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
}
