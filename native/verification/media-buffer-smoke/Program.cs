using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Hongguo.Desktop;

internal static class Program
{
    private static readonly List<object> Results = [];

    [STAThread]
    private static int Main()
    {
        string output = Path.GetFullPath("results/media-buffer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var host = new NativeVideoHost();
        var window = new Window { Width = 720, Height = 480, Content = host, ShowInTaskbar = false, ShowActivated = false, Left = -3000, Top = -3000 };
        int exitCode = 0;
        window.Show();
        using var current = new MpvPlayer(host.VideoHandle);
        using var next = new MpvPlayer(host.StandbyVideoHandle);
        try
        {
            string sample = Path.GetFullPath("../mpv-smoke/sample.mp4");
            string? error = null;
            int currentEof = 0;
            int nextEof = 0;
            current.Error += (_, message) => error = message;
            next.Error += (_, message) => error = message;
            current.Ended += (_, _) => currentEof++;
            next.Ended += (_, _) => nextEof++;
            current.Load(sample, null, null);
            Wait(() => current.GetStatus().FrameReady, 10_000);
            next.SetMuted(true);
            next.Load(sample, null, null, paused: true);
            Wait(() => next.GetStatus().FrameReady && next.GetStatus().CacheSeconds >= 2, 10_000);
            MpvStatus prepared = next.GetStatus();
            Require(prepared.Loaded && prepared.FrameReady && prepared.Paused && prepared.VideoWidth == 360 && prepared.VideoHeight == 640,
                "standby_decoded_paused_frame", new { prepared.Loaded, prepared.FrameReady, prepared.Paused, prepared.VideoWidth, prepared.VideoHeight });
            Require(prepared.CacheSeconds >= 2 && prepared.CacheBytes > 0, "standby_real_media_cache",
                new { prepared.CacheSeconds, prepared.CacheBytes, prepared.CacheReachedEnd });
            double position = prepared.Position;
            Pump(500);
            Require(Math.Abs(next.GetStatus().Position - position) < 0.05 && nextEof == 0, "standby_does_not_advance", new { position = next.GetStatus().Position, nextEof });
            IntPtr first = host.VideoHandle;
            IntPtr second = host.StandbyVideoHandle;
            host.SetCursorHidden(true);
            var activePanel = (System.Windows.Forms.Panel)typeof(NativeVideoHost).GetField("_activePanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            var hiddenCursor = (System.Windows.Forms.Cursor)typeof(NativeVideoHost).GetField("_hiddenCursor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            Require(activePanel.Cursor.Handle == hiddenCursor.Handle, "native_local_cursor_hidden", new { localCursor = true });
            host.SetCursorHidden(false);
            Require(activePanel.Cursor.Handle == System.Windows.Forms.Cursors.Default.Handle, "native_cursor_restored", new { restored = true });
            current.Seek(Math.Max(0, current.GetStatus().Duration - 0.5));
            Wait(() => currentEof == 1, 5_000);
            var handoff = Stopwatch.StartNew();
            host.SwapVideoSurfaces();
            next.SetMuted(false);
            next.SetPaused(false);
            double readyMs = handoff.Elapsed.TotalMilliseconds;
            Wait(() => next.GetStatus().Position >= 0.05, 3_000);
            Require(host.VideoHandle == second && host.StandbyVideoHandle == first && next.GetStatus().FrameReady,
                "handoff_reuses_decoded_player", new { readyMs, progressionMs = handoff.Elapsed.TotalMilliseconds, next.GetStatus().Position });
            Require(error == null && currentEof == 1 && nextEof == 0, "handoff_event_isolation", new { error, currentEof, nextEof });
            next.Stop();
            Pump(200);
            Require(!next.GetStatus().Loaded && !next.GetStatus().FrameReady && nextEof == 0, "cancel_resets_frame_and_cache_generation", new { nextEof });
            // 首帧准备流程也覆盖既有的加密视频和独立音轨。
            next.SetMuted(true);
            next.Load(Path.GetFullPath("../mpv-smoke/encrypted.mp4"), Path.GetFullPath("../mpv-smoke/audio.m4a"), "00112233445566778899aabbccddeeff", paused: true);
            Wait(() => next.GetStatus().FrameReady && next.GetStatus().CacheSeconds >= 2, 10_000);
            MpvStatus encrypted = next.GetStatus();
            Require(encrypted.Paused && encrypted.FrameReady && encrypted.CacheBytes > 0 && error == null,
                "encrypted_separate_audio_prepares", new { encrypted.FrameReady, encrypted.CacheSeconds, encrypted.CacheBytes });
        }
        catch (Exception error)
        {
            exitCode = 1;
            Results.Add(new { name = "harness", passed = false, error = error.Message });
        }
        finally
        {
            window.Content = null;
            window.Close();
            File.WriteAllText(output, JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures = exitCode, tests = Results }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown();
        }
        Console.WriteLine(File.ReadAllText(output));
        return exitCode;
    }

    private static void Require(bool passed, string name, object detail)
    {
        Results.Add(new { name, passed, detail });
        if (!passed) throw new InvalidOperationException(name);
    }


    private static void Wait(Func<bool> predicate, int timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > timeout) throw new TimeoutException("媒体缓冲验证超时。");
            Pump(10);
        }
    }

    private static void Pump(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(2);
        }
    }
}

namespace Hongguo.Desktop
{
    internal static class BundledFiles
    {
        internal static string Extract(string name) => Path.GetFullPath(Path.Combine("../../runtime/mpv", name));
    }
}
