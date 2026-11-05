using System;
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
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Hongguo.Desktop;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Results = new();
    private static MainWindow Window = null!;
    private static int Failures;
    private static MpvPlayer Player => Field<MpvPlayer>("_player");
    private static MpvPlayer Standby => Field<MpvPlayer>("_standbyPlayer");
    private static EpisodeItem Current => Field<EpisodeItem>("_currentEpisode");
    private static EpisodeItem Next => Field<IEnumerable<EpisodeItem>>("_episodes").First(item => item.Playable && item.Number > Current.Number);

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"));
        XElement resources = source.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), resources.Elements());
        using (var reader = dictionary.CreateReader()) app.Resources = (ResourceDictionary)XamlReader.Load(reader);
        app.Startup += async (_, _) =>
        {
            try { await RunAsync(); }
            catch (Exception error) { Record("harness", false, new { error = Clean(error.Message), type = error.GetType().Name }); }
            finally
            {
                if (Window != null)
                {
                    Set("_closing", true);
                    Field<DispatcherTimer>("_playerTimer").Stop();
                    Invoke("CancelNextPlayback");
                    Invoke("CancelDiscoveryRequests");
                    Field<MpvPlayer?>("_player")?.Dispose();
                    Field<MpvPlayer?>("_standbyPlayer")?.Dispose();
                    (Field<object?>("_backend") as IDisposable)?.Dispose();
                    Window.Close();
                }
                File.WriteAllText("results/desktop-buffer.json", JsonSerializer.Serialize(new { at = DateTimeOffset.Now, failures = Failures, tests = Results }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static async Task RunAsync()
    {
        string state = Path.GetFullPath(Path.Combine("state", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(state);
        string anonymous = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openhongguo", "anonymous.json");
        if (File.Exists(anonymous)) File.Copy(anonymous, Path.Combine(state, "anonymous.json"));
        Window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -3000, Top = 40, Opacity = 0 };
        Set("_configDir", state);
        System.Windows.Application.Current.MainWindow = Window;
        Window.Show();
        await WaitAsync(() => Field<object?>("_backend") != null && !Field<bool>("_catalogBusy"), "backend startup", 40_000);
        await Call("OpenSeriesAsync", "7675400266065136665", true, 0).WaitAsync(TimeSpan.FromSeconds(45));
        await WaitAsync(() => Player.GetStatus().FrameReady, "initial decoded frame", 30_000);
        Player.SetPaused(true);

        await Check("online_standby_real_cache_and_frame", async () =>
        {
            await WaitBufferedAsync();
            MpvStatus status = Standby.GetStatus();
            Require(status.Paused && status.Muted && status.Position < 0.1 && status.CacheBytes > 0 && !Player.GetStatus().Muted, "standby must be paused, muted and buffered");
            return new { current = Current.Number, prepared = Next.Number, status.FrameReady, status.CacheSeconds, status.CacheBytes, status.CacheReachedEnd, status.Paused, status.Muted };
        });
        await Check("production_natural_eof_to_predecoded_frame", async () =>
        {
            await WaitBufferedAsync();
            MpvPlayer old = Player;
            MpvPlayer prepared = Standby;
            EpisodeItem next = Next;
            int eof = 0;
            old.Ended += (_, _) => eof++;
            Control<Slider>("VolumeSlider").Value = 35;
            Player.SetPaused(false);
            Player.Seek(Math.Max(0, Player.GetStatus().Duration - 0.65));
            await WaitAsync(() => Current.Id == next.Id && ReferenceEquals(Player, prepared) && Player.GetStatus().FrameReady, "native EOF handoff", 30_000, 5);
            double frameMs = ElapsedMs(old.LastEndTimestamp);
            bool spinner = Control<FrameworkElement>("VideoPlaceholder").Visibility == Visibility.Visible;
            await WaitAsync(() => Player.GetStatus().Position >= 0.05, "next playback progressing", 5_000, 5);
            double progressionMs = ElapsedMs(old.LastEndTimestamp);
            MpvStatus status = Player.GetStatus();
            Player.SetPaused(true);
            Require(eof == 1 && !spinner && !status.Muted && !status.Paused && Math.Abs(status.Volume - 35) < 0.1, "foreground state after handoff");
            Require(frameMs >= 0 && frameMs < 500 && progressionMs < 700, "prepared natural transition latency");
            return new { episode = next.Number, nativeEndEventToPreparedFrameMs = Math.Round(frameMs, 2), nativeEndEventToProgressionMs = Math.Round(progressionMs, 2), pollResolutionMs = 5, eof, spinner, status.Muted, status.Volume, reusedPreparedDecoder = true };
        });
        await Check("manual_next_reuses_predecoded_player", async () =>
        {
            await WaitBufferedAsync();
            MpvPlayer prepared = Standby;
            EpisodeItem next = Next;
            var timer = Stopwatch.StartNew();
            Task playing = Call("PlayEpisodeAsync", next, false);
            await WaitAsync(() => Current.Id == next.Id && ReferenceEquals(Player, prepared) && Player.GetStatus().FrameReady, "manual prepared handoff", 5_000, 5);
            double frameMs = timer.Elapsed.TotalMilliseconds;
            await playing;
            Player.SetPaused(true);
            Require(!Player.GetStatus().Muted && frameMs < 500, "manual prepared handoff was slow or silent");
            return new { episode = next.Number, invocationToPreparedFrameMs = Math.Round(frameMs, 2), reusedPreparedDecoder = true, muted = Player.GetStatus().Muted };
        });
        await Check("disable_cancels_real_standby", async () =>
        {
            await WaitBufferedAsync();
            Control<CheckBox>("AutoNextCheck").IsChecked = false;
            Require(Field<Task<PlaybackInfo?>?>("_nextPlaybackTask") == null && !Standby.GetStatus().Loaded && !Standby.GetStatus().FrameReady, "disable left a standby decoder active");
            Control<CheckBox>("AutoNextCheck").IsChecked = true;
            await WaitBufferedAsync();
            return new { cancelled = true, restoredFrame = Standby.GetStatus().FrameReady, restoredMuted = Standby.GetStatus().Muted };
        });
        await Check("quality_change_keeps_paused_position_and_best_standby", async () =>
        {
            double position = Math.Min(8, Player.GetStatus().Duration / 4);
            Player.Seek(position);
            await WaitAsync(() => Math.Abs(Player.GetStatus().Position - position) < 0.3, "quality setup seek", 5_000);
            await Call("ChangeQualityAsync", "360p").WaitAsync(TimeSpan.FromSeconds(40));
            await WaitAsync(() => Player.GetStatus().FrameReady, "quality decoded frame", 25_000);
            await WaitBufferedAsync();
            MpvStatus status = Player.GetStatus();
            Require(status.Paused && !status.Muted && Math.Abs(status.Position - position) < 1 && status.VideoHeight == 360, "quality change state");
            Require(Field<string>("_nextPlaybackQuality") == "best" && Standby.GetStatus().Muted && Standby.GetStatus().Paused, "standby inherited foreground quality or sound");
            return new { status.VideoHeight, status.Position, status.Paused, status.Muted, nextQuality = Field<string>("_nextPlaybackQuality"), standbyHeight = Standby.GetStatus().VideoHeight };
        });
        await Check("expired_buffer_falls_back_to_fresh_playback", async () =>
        {
            await WaitBufferedAsync();
            EpisodeItem next = Next;
            MpvPlayer foreground = Player;
            Set("_nextPlaybackCreatedAt", DateTime.UtcNow.AddSeconds(-80));
            await Call("PlayEpisodeAsync", next, false).WaitAsync(TimeSpan.FromSeconds(40));
            await WaitAsync(() => Player.GetStatus().FrameReady, "expired buffer fallback", 25_000);
            Player.SetPaused(true);
            Require(ReferenceEquals(foreground, Player) && Current.Id == next.Id && !Player.GetStatus().Muted, "expired buffered decoder was promoted");
            return new { ordinaryForegroundDecoder = true, episode = Current.Number, Player.GetStatus().FrameReady };
        });
        await Check("manual_skip_replaces_buffer_and_stop_cleans_both", async () =>
        {
            await WaitBufferedAsync();
            string previous = Field<string>("_nextPlaybackEpisodeId");
            EpisodeItem target = Field<IEnumerable<EpisodeItem>>("_episodes").First(item => item.Playable && item.Number > Current.Number + 1);
            await Call("PlayEpisodeAsync", target, false).WaitAsync(TimeSpan.FromSeconds(40));
            await WaitAsync(() => Player.GetStatus().FrameReady, "manual skip decoded frame", 25_000);
            Player.SetPaused(true);
            await WaitBufferedAsync();
            Require(Field<string>("_nextPlaybackEpisodeId") == Next.Id && previous != Next.Id, "manual skip kept stale next slot");
            await Call("StopCurrentAsync");
            await Task.Delay(100);
            Require(!Player.GetStatus().Loaded && !Standby.GetStatus().Loaded && Field<EpisodeItem?>("_currentEpisode") == null, "navigation did not stop both players");
            return new { replacementSlot = true, bothStopped = true };
        });
    }

    private static async Task WaitBufferedAsync()
    {
        Invoke("PrepareNextPlayback", true);
        await WaitAsync(() =>
        {
            if (Field<MpvPlayer?>("_standbyPlayer") is not MpvPlayer standby) return false;
            MpvStatus status = standby.GetStatus();
            return status.Loaded && status.FrameReady && (status.CacheSeconds >= 2 || status.CacheReachedEnd || status.CacheBytes >= 16 * 1024 * 1024);
        }, "standby media cache and decoded frame", 35_000);
    }

    private static async Task Check(string name, Func<Task<object>> test)
    {
        try { Record(name, true, await test()); }
        catch (Exception error) { Record(name, false, new { error = Clean(error.Message), type = error.GetType().Name }); }
    }
    private static void Record(string name, bool passed, object detail)
    {
        if (!passed) Failures++;
        Results.Add(new { name, passed, detail });
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail));
    }
    private static async Task WaitAsync(Func<bool> condition, string label, int timeout, int poll = 40)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > timeout) throw new TimeoutException(label);
            await Task.Delay(poll);
        }
    }
    private static double ElapsedMs(long timestamp) => timestamp == 0 ? -1 : (Stopwatch.GetTimestamp() - timestamp) * 1000.0 / Stopwatch.Frequency;
    private static string Clean(string value) => Regex.Replace(value, @"https?://\S+", "[URL]");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(Window) ?? default(T)!);
    private static void Set(string name, object value) => typeof(MainWindow).GetField(name, PrivateInstance)!.SetValue(Window, value);
    private static T Control<T>(string name) where T : class => (T)Window.FindName(name)!;
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(Window, args);
    private static Task Call(string name, params object?[] args) => (Task)Invoke(name, args)!;
}
