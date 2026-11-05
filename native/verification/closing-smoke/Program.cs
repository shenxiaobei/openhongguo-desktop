using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Tests = new();
    private static readonly List<object> DispatcherErrors = new();
    private static int Failures;
    private static bool Baseline;
    private static MainWindow? Current;

    [STAThread]
    private static int Main(string[] args)
    {
        Baseline = args.Contains("--baseline");
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Element(presentation + "Application.Resources")!;
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), source.Elements());
        using (var reader = resources.CreateReader()) app.Resources = (ResourceDictionary)XamlReader.Load(reader);
        app.DispatcherUnhandledException += (_, error) =>
        {
            // 堆栈只有本地方法及代码位置，不复制异常消息或媒体参数。
            DispatcherErrors.Add(new { type = error.Exception.GetType().Name, stack = error.Exception.StackTrace });
            error.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            try
            {
                await Check("idle_close_without_backend", () => CheckCloseAsync("idle", false, false, false));
                if (!Baseline)
                {
                    await Check("close_with_synchronous_failed_history", () => CheckCloseAsync("failed-history", true, true, false));
                    await Check("close_with_real_history_and_both_decoders", () => CheckCloseAsync("media-history", true, false, true));
                    await Check("close_from_fullscreen", () => CheckCloseAsync("fullscreen", false, false, false, true));
                    await Check("close_with_episode_popup_open", () => CheckCloseAsync("episode-popup", false, false, false, popup: "episode"));
                    await Check("close_with_playback_options_open", () => CheckCloseAsync("options-popup", false, false, false, popup: "options"));
                    await Check("repeated_close_waits_for_history_cleanup", () => CheckCloseAsync("repeat-close", true, false, false, repeatClose: true));
                }
            }
            catch (Exception error) { Record("harness", false, new { type = error.GetType().Name, error = error.Message }); }
            finally
            {
                if (Current != null) ForceTestCleanup(Current);
                string report = Baseline ? "results/closing-baseline.json" : "results/closing-smoke.json";
                File.WriteAllText(report, JsonSerializer.Serialize(new { at = DateTimeOffset.Now, baseline = Baseline, failures = Failures, tests = Tests, dispatcherErrors = DispatcherErrors }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task<object> CheckCloseAsync(string name, bool backend, bool failBackend, bool media, bool fullscreen = false, string popup = "", bool repeatClose = false)
    {
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -4000, Top = 20, Opacity = 0, Width = 640, Height = 420 };
        Current = window;
        window.Loaded -= (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, typeof(MainWindow).GetMethod("Window_Loaded", Private)!);
        InputMethod.SetIsInputMethodEnabled(window, false);
        InputMethod.SetIsInputMethodSuspended(window, true);
        System.Windows.Application.Current.MainWindow = window;
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult(true);
        int closingEvents = 0;
        window.Closing += (_, _) => closingEvents++;
        window.Show();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        string config = Path.GetFullPath(Path.Combine("results", name + "-profile-" + Guid.NewGuid().ToString("N")));
        int ownedProcessId = -1;
        object? client = null;
        if (backend)
        {
            Type type = typeof(MainWindow).Assembly.GetType("Hongguo.Desktop.BackendClient")!;
            client = Activator.CreateInstance(type, Private, null, new object[] { config }, null)!;
            Set(window, "_backend", client);
            ownedProcessId = ((Process)type.GetField("process", Private)!.GetValue(client)!).Id;
            Set(window, "_currentSeries", new SeriesItem { Id = "731001", Title = "本地关闭验证", EpisodeCount = 2 });
            Set(window, "_currentEpisode", new EpisodeItem { Id = "731002", Number = 1, Playable = true });
            Set(window, "_position", 2.5);
            Set(window, "_duration", 6.0);
            if (failBackend) ((IDisposable)client).Dispose();
        }
        MpvPlayer? player = null, standby = null;
        if (media)
        {
            Invoke(window, "ShowScreen", Control<Grid>(window, "PlayerScreen"));
            window.UpdateLayout();
            NativeVideoHost host = Control<NativeVideoHost>(window, "VideoHost");
            player = new MpvPlayer(host.VideoHandle);
            standby = new MpvPlayer(host.StandbyVideoHandle);
            Set(window, "_player", player);
            Set(window, "_standbyPlayer", standby);
            string sample = Path.GetFullPath("../mpv-smoke/sample.mp4");
            player.Load(sample, null, null, paused: true);
            standby.SetMuted(true);
            standby.Load(sample, null, null, paused: true);
            await Wait(() => player.GetStatus().Loaded && standby.GetStatus().Loaded, 8000);
            Set(window, "_nextPlaybackRequest", new CancellationTokenSource());
            Set(window, "_catalogRequest", new CancellationTokenSource());
            Set(window, "_historyRequest", new CancellationTokenSource());
        }
        if (fullscreen)
        {
            Invoke(window, "ShowScreen", Control<Grid>(window, "PlayerScreen"));
            Invoke(window, "ToggleFullscreen");
        }
        Popup? openedPopup = null;
        if (popup != "")
        {
            window.Width = 480;
            window.Height = 320;
            Invoke(window, "ShowScreen", Control<Grid>(window, "PlayerScreen"));
            window.UpdateLayout();
            string button = popup == "episode" ? "EpisodePickerButton" : "PlaybackOptionsButton";
            Control<Button>(window, button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string field = popup == "episode" ? "_episodePickerPopup" : "_playbackOptionsPopup";
            openedPopup = (Popup?)typeof(MainWindow).GetField(field, Private)!.GetValue(window);
            Require(openedPopup?.IsOpen == true, "测试弹层没有打开");
        }
        SemaphoreSlim? heldHistoryWrite = null;
        if (repeatClose)
        {
            heldHistoryWrite = (SemaphoreSlim)client!.GetType().GetField("writeLock", Private)!.GetValue(client)!;
            Require(heldHistoryWrite.Wait(0), "测试未取得历史写锁");
        }
        int errorsBefore = DispatcherErrors.Count;
        var elapsed = Stopwatch.StartNew();
        window.Close();
        if (repeatClose)
        {
            await Task.Delay(120);
            Require(!closed.Task.IsCompleted, "历史清理完成前窗口已提前销毁");
            window.Close();
            await Task.Delay(120);
            Require(!closed.Task.IsCompleted, "再次点击关闭绕过正在进行的历史保存");
            heldHistoryWrite!.Release();
        }
        if (Baseline)
        {
            await Task.Delay(400);
            Require(DispatcherErrors.Count > errorsBefore && !closed.Task.IsCompleted, "未复现首次关闭异常");
            object detail = new { closed = false, dispatcherErrorCount = DispatcherErrors.Count - errorsBefore, closingEvents, reproduced = true };
            ForceTestCleanup(window);
            Current = null;
            return detail;
        }
        await Wait(() => closed.Task.IsCompleted, 7000);
        await Task.Delay(100);
        Require(DispatcherErrors.Count == errorsBefore, "首次关闭产生Dispatcher异常");
        Require(closingEvents == (repeatClose ? 3 : 2), "关闭没有通过取消/完成两阶段正常结束");
        Require(!window.IsVisible && PresentationSource.FromVisual(window) == null, "Closed后仍有窗口资源");
        foreach (FieldInfo field in typeof(MainWindow).GetFields(Private))
            if (field.GetValue(window) is DispatcherTimer timer) Require(!timer.IsEnabled, "关闭后计时器仍在运行");
        bool playersDisposed = player == null || IsDisposed(player) && IsDisposed(standby!);
        Require(playersDisposed, "关闭后解码器仍未释放");
        Require(openedPopup == null || !openedPopup.IsOpen, "关闭后弹层仍然开启");
        bool historySaved = !backend || failBackend || File.Exists(Path.Combine(config, "history.json"));
        Require(historySaved, "正常关闭未写入测试观看记录");
        // BackendClient.Dispose会释放Process对象，因此用启动时保存的PID检查退出。
        bool childExited = !ProcessExists(ownedProcessId);
        Require(childExited, "关闭后测试桥接进程仍存活");
        Current = null;
        return new { closed = true, elapsedMs = elapsed.Elapsed.TotalMilliseconds, closingEvents, dispatcherErrorCount = 0, playersDisposed, historySaved, childExited, popupClosed = openedPopup == null || !openedPopup.IsOpen };
    }

    private static bool ProcessExists(int id)
    {
        if (id < 0) return false;
        try { using Process process = Process.GetProcessById(id); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static bool IsDisposed(MpvPlayer player) => (bool)typeof(MpvPlayer).GetField("_disposed", Private)!.GetValue(player)!;
    private static void ForceTestCleanup(MainWindow window)
    {
        Set(window, "_closing", true);
        FieldInfo? complete = typeof(MainWindow).GetField("_closeReady", Private);
        complete?.SetValue(window, true);
        foreach (FieldInfo field in typeof(MainWindow).GetFields(Private))
            if (field.GetValue(window) is DispatcherTimer timer) timer.Stop();
        window.Close();
    }
    private static async Task Wait(Func<bool> condition, int timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > timeout) throw new TimeoutException("关闭状态等待超时");
            await Task.Delay(20);
        }
    }
    private static async Task Check(string name, Func<Task<object>> test)
    {
        try { Record(name, true, await test()); }
        catch (Exception error) { Record(name, false, new { type = error.GetType().Name, error = error.Message }); }
    }
    private static void Record(string name, bool passed, object detail) { if (!passed) Failures++; Tests.Add(new { name, passed, detail }); Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail)); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Control<T>(MainWindow window, string name) where T : class => (T)window.FindName(name);
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
