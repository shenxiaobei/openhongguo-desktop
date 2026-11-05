using System.Diagnostics;
using System.Windows.Forms;
using Hongguo.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var window = new Form
        {
            Width = 720, Height = 480, ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(-3000, -3000)
        };
        window.Show();
        using var player = new MpvPlayer(window.Handle);
        string sample = Path.GetFullPath(args.Length > 0 ? args[0] : "sample.mp4");
        string? key = args.Length > 1 ? args[1] : null;
        string? audio = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
        int ended = 0;
        string? error = null;
        player.Ended += (_, _) => ended++;
        player.Error += (_, value) => error = value;
        player.Load(sample, audio, key);
        Wait(() => player.GetStatus().Loaded, 10_000);
        var initial = player.GetStatus();
        Require(initial.VideoWidth == 360 && initial.VideoHeight == 640, "vertical video dimensions");
        Require(initial.Duration > 5.9, "duration");
        Wait(() => player.GetStatus().Position > 0.4, 5_000);
        player.SetPaused(true);
        Wait(() => player.GetStatus().Paused, 1_000);
        double pausedAt = player.GetStatus().Position;
        Pump(400);
        Require(Math.Abs(player.GetStatus().Position - pausedAt) < 0.1, "pause position");
        player.Seek(2);
        Wait(() => Math.Abs(player.GetStatus().Position - 2) < 0.15, 3_000);
        player.SetSpeed(2);
        player.SetVolume(35);
        Require(Math.Abs(player.GetStatus().Speed - 2) < 0.01, "speed");
        Require(Math.Abs(player.GetStatus().Volume - 35) < 0.01, "volume");
        player.SetPaused(false);
        Wait(() => ended == 1, 5_000);
        Require(error == null, "playback error");
        player.Load(sample, audio, key, 1);
        Wait(() => player.GetStatus().Loaded, 10_000);
        Wait(() => player.GetStatus().Position >= 1, 3_000);
        player.Stop();
        Pump(500);
        Require(ended == 1, "Stop must not trigger Ended");
        player.Load(sample, audio, key);
        player.Load(sample, audio, key);
        Wait(() => player.GetStatus().Loaded, 10_000);
        player.Stop();
        Pump(500);
        Require(ended == 1, "replacement must not trigger Ended");
        Require(error == null, "replacement error");
        Console.WriteLine("PASS dimensions=360x640 duration pause seek speed=2 volume=35 EOF=1 Stop=0 replacement=0");
        window.Close();
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + label);
    }

    private static void Wait(Func<bool> predicate, int timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > timeout) throw new TimeoutException("smoke predicate timed out");
            Pump(20);
        }
    }

    private static void Pump(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(5);
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
