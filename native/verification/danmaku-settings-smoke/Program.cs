using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Hongguo.Desktop;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Results = new();
    private static MainWindow Window = null!;
    private static string StateDirectory = "";
    private static string SettingsPath => Path.Combine(StateDirectory, "danmaku-settings.json");
    private static DanmakuSettings Settings => Field<DanmakuSettings>("_danmakuSettings");
    private static Border Menu = null!;
    private static ScrollViewer Scroll = null!;
    private static bool Closed;
    private static int Failures;

    [STAThread]
    private static int Main()
    {
        Directory.CreateDirectory("results");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LoadResources(app);
        app.Startup += async (_, _) =>
        {
            try { Run(); }
            catch (Exception error) { Record("harness", false, new { error = error.Message, type = error.GetType().Name }); }
            finally
            {
                if (Window != null)
                {
                    Set("_playbackOptionsPopup", null);
                    Window.Close();
                    var timer = Stopwatch.StartNew();
                    while (!Closed && timer.ElapsedMilliseconds < 5000) await Task.Delay(10);
                }
                if (!Closed) Record("cleanup", false, new { naturallyClosed = false });
                if (StateDirectory.Length > 0 && Directory.Exists(StateDirectory) && Closed) Directory.Delete(StateDirectory, true);
                File.WriteAllText("results/danmaku-settings-smoke.json", JsonSerializer.Serialize(new
                {
                    at = DateTimeOffset.Now, failures = Failures, tests = Results, naturallyClosed = Closed,
                    scope = "真实设置模型与生产齿轮设置页，隔离目录读写和原子替换，Popup始终关闭且MainWindow不Show；不创建播放器或请求网络，离屏WPF截图不包含视频与桌面"
                }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(Failures == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static void Run()
    {
        StateDirectory = Path.Combine(Path.GetTempPath(), "openhongguo-danmaku-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StateDirectory);
        Window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
        Window.Closed += (_, _) => Closed = true;
        Set("_configDir", StateDirectory);

        Check("defaults_and_normalization_are_bounded", () =>
        {
            var defaults = new DanmakuSettings();
            Require(defaults.Enabled && defaults.Area == "half" && defaults.Opacity == 0.85 && defaults.FontSize == "medium"
                && defaults.Speed == "normal" && defaults.Density == "normal", "默认弹幕偏好不符合契约");
            var settings = new DanmakuSettings { Area = " FULL ", FontSize = " LARGE ", Speed = " FAST ", Density = " DENSE ", Opacity = 5 };
            settings.Normalize();
            Require(settings.Area == "full" && settings.FontSize == "large" && settings.Speed == "fast" && settings.Density == "dense" && settings.Opacity == 1,
                "合法档位归一或上界失败");
            settings.Area = null!; settings.FontSize = "invalid"; settings.Speed = null!; settings.Density = "invalid"; settings.Opacity = -1;
            settings.Normalize();
            Require(settings.Area == "half" && settings.FontSize == "medium" && settings.Speed == "normal" && settings.Density == "normal" && settings.Opacity == 0.2,
                "无效档位回退或下界失败");
            foreach (double opacity in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                settings.Opacity = opacity; settings.Normalize();
                Require(settings.Opacity == 0.85, "非有限不透明度没有回默认");
            }
            return new { defaultArea = defaults.Area, defaultOpacity = defaults.Opacity, lower = 0.2, upper = 1 };
        });

        Check("missing_preferences_use_defaults_without_creating_file", () =>
        {
            Invoke("LoadDanmakuSettings");
            Require(Settings.Enabled && Settings.Area == "half" && !File.Exists(SettingsPath), "首次加载偏好提前写文件或没有默认值");
            return new { defaults = true, fileCreated = false };
        });

        Check("preferences_round_trip_atomically_and_contain_only_settings", () =>
        {
            Set("_danmakuSettings", new DanmakuSettings { Enabled = false, Area = "top", Opacity = 0.5, FontSize = "large", Speed = "slow", Density = "sparse" });
            Invoke("SaveDanmakuSettings");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            string[] names = document.RootElement.EnumerateObject().Select(item => item.Name).Order().ToArray();
            Require(names.SequenceEqual(new[] { "area", "density", "enabled", "font_size", "opacity", "speed" }), "偏好包含非设置字段");
            Set("_danmakuSettings", new DanmakuSettings());
            Invoke("LoadDanmakuSettings");
            Require(!Settings.Enabled && Settings.Area == "top" && Settings.Opacity == 0.5 && Settings.FontSize == "large"
                && Settings.Speed == "slow" && Settings.Density == "sparse", "设置重启恢复失败");
            Settings.Enabled = true; Invoke("SaveDanmakuSettings");
            Require(Directory.GetFiles(StateDirectory, "*.tmp").Length == 0, "原子保存留下临时文件");
            return new { fields = names, roundTrip = true, stagingFiles = 0 };
        });

        Check("corrupt_null_and_oversized_preferences_fall_back", () =>
        {
            foreach (string content in new[] { "{broken", "null", new string('x', 65537) })
            {
                File.WriteAllText(SettingsPath, content);
                Invoke("LoadDanmakuSettings");
                Require(Settings.Enabled && Settings.Area == "half" && Settings.Opacity == 0.85, "坏偏好没有回退默认");
            }
            File.WriteAllText(SettingsPath, "{\"enabled\":false,\"area\":\"invalid\",\"opacity\":9,\"font_size\":\" LARGE \"}");
            Invoke("LoadDanmakuSettings");
            Require(!Settings.Enabled && Settings.Area == "half" && Settings.Opacity == 1 && Settings.FontSize == "large", "可读的旧偏好没有归一");
            Set("_danmakuSettings", new DanmakuSettings());
            Invoke("SaveDanmakuSettings");
            return new { malformedRecovered = true, oversizedRecovered = true, invalidValuesNormalized = true };
        });

        Check("root_menu_opens_live_danmaku_settings_without_popup", () =>
        {
            Scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 364 };
            Menu = new Border { Child = Scroll, Width = 240, MaxHeight = 380, Padding = new Thickness(7), Background = new SolidColorBrush(Color.FromRgb(23, 27, 34)), CornerRadius = new CornerRadius(12) };
            Set("_playbackOptionsPopup", new Popup { Child = Menu, AllowsTransparency = true });
            Invoke("SetPlaybackMenuPage", "root");
            Find<Button>("PlaybackDanmakuMenuButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Find<CheckBox>("DanmakuEnabledToggle").IsChecked == true && Find<Slider>("DanmakuOpacitySlider").Value == 85,
                "弹幕设置入口或初始值不正确");
            Require(Find<TextBlock>("DanmakuSubtitleProtectionNote").Text.Contains("字幕"), "全屏设置没有说明字幕留白");
            Require(!Field<Popup>("_playbackOptionsPopup").IsOpen && !Window.IsVisible, "设置验证意外打开系统窗口");
            return new { menuEntry = true, popupOpened = false, defaultOpacity = 85 };
        });

        Check("all_settings_update_selection_summary_and_saved_preferences", () =>
        {
            Find<Button>("DanmakuAreafull").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<Button>("DanmakuFontlarge").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<Button>("DanmakuSpeedfast").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<Button>("DanmakuDensitydense").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<Slider>("DanmakuOpacitySlider").Value = 50;
            Require(Settings.Area == "full" && Settings.FontSize == "large" && Settings.Speed == "fast" && Settings.Density == "dense" && Settings.Opacity == 0.5,
                "设置点击未修改实际模型");
            string summary = Find<TextBlock>("DanmakuSettingsSummary").Text;
            Require(summary.Contains("全屏") && summary.Contains("大号字") && summary.Contains("快速") && summary.Contains("密集")
                && Find<TextBlock>("DanmakuOpacityValue").Text.Contains("50"), "当前配置摘要未即时更新");
            Require(Equals(Find<Button>("DanmakuAreafull").Background, Window.FindResource("AccentSoftBrush")), "选中档位没有可见反馈");
            var enabled = Find<CheckBox>("DanmakuEnabledToggle");
            enabled.IsChecked = false; enabled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!Settings.Enabled && Find<TextBlock>("DanmakuSettingsSummary").Text.Contains("已关闭"), "弹幕开关没有更新摘要");
            Invoke("LoadDanmakuSettings");
            Require(!Settings.Enabled && Settings.Area == "full" && Settings.Opacity == 0.5, "即时选择没有持久化");
            enabled.IsChecked = true; enabled.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return new { area = Settings.Area, size = Settings.FontSize, speed = Settings.Speed, density = Settings.Density, opacity = Settings.Opacity, persisted = true };
        });

        Check("short_narrow_menu_scrolls_and_keeps_last_controls_reachable", () =>
        {
            Menu.Measure(new Size(240, double.PositiveInfinity));
            Menu.Arrange(new Rect(0, 0, 240, Menu.DesiredSize.Height));
            Menu.UpdateLayout();
            Require(Scroll.ScrollableHeight > 0 && Scroll.ViewportHeight > 0, "短窗设置没有提供滚动");
            string top = Render("danmaku-settings-top-offscreen.png");
            Scroll.ScrollToEnd(); Menu.UpdateLayout();
            Scroll.ApplyTemplate();
            FrameworkElement viewport = (FrameworkElement)Scroll.Template.FindName("PART_ScrollContentPresenter", Scroll)!;
            Button last = Find<Button>("DanmakuDensitydense");
            Rect bounds = last.TransformToAncestor(Menu).TransformBounds(new Rect(last.RenderSize));
            Rect viewportBounds = viewport.TransformToAncestor(Menu).TransformBounds(new Rect(viewport.RenderSize));
            Require(bounds.Top >= viewportBounds.Top - 1 && bounds.Bottom <= viewportBounds.Bottom + 1, "滚动到底后密度选项仍被裁剪");
            string bottom = Render("danmaku-settings-bottom-offscreen.png");
            Menu.Width = 180;
            Menu.Measure(new Size(180, double.PositiveInfinity));
            Menu.Arrange(new Rect(0, 0, 180, Menu.DesiredSize.Height)); Menu.UpdateLayout();
            Require(Descendants<Button>(Menu).Where(button => button.Name.StartsWith("Danmaku", StringComparison.Ordinal)).All(button => button.ActualWidth > 0),
                "更窄窗口选项没有有效宽度");
            return new { width = 240, maximumHeight = 380, scrollableHeight = Scroll.ScrollableHeight, lastOptionVisible = true, top, bottom, narrowerWidth = 180 };
        });

        Check("save_failure_preserves_live_setting_and_shows_notice", () =>
        {
            string blocked = Path.Combine(StateDirectory, "blocked-directory");
            File.WriteAllText(blocked, "fixture");
            Set("_configDir", blocked);
            Find<Slider>("DanmakuOpacitySlider").Value = 75;
            Require(Settings.Opacity == 0.75 && Field<string>("_danmakuSettingsSaveError").Length > 0
                && Find<TextBlock>("DanmakuSettingsSaveStatus").Visibility == Visibility.Visible,
                "保存失败丢失当前设置或没有通知");
            Set("_configDir", StateDirectory);
            Find<Slider>("DanmakuOpacitySlider").Value = 80;
            Require(Field<string>("_danmakuSettingsSaveError") == "" && Find<TextBlock>("DanmakuSettingsSaveStatus").Visibility == Visibility.Collapsed,
                "保存恢复后旧错误没有清除");
            Require(Directory.GetFiles(StateDirectory, "*.tmp").Length == 0, "保存失败留下临时文件");
            return new { settingStayedActive = true, failureNotice = true, recovered = true, stagingFiles = 0 };
        });
    }

    private static string Render(string name)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(Menu.ActualWidth), (int)Math.Ceiling(Menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(Menu);
        string path = Path.GetFullPath(Path.Combine("results", name));
        using FileStream output = File.Create(path);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(output);
        return path;
    }
    private static T Find<T>(string name) where T : FrameworkElement
    {
        Menu.Measure(new Size(Menu.Width, double.PositiveInfinity));
        Menu.Arrange(new Rect(0, 0, Menu.Width, Menu.DesiredSize.Height));
        Menu.UpdateLayout();
        return Descendants<T>(Menu).Single(item => item.Name == name);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void LoadResources(System.Windows.Application app)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml")).Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName), resources.Elements());
        using var reader = dictionary.CreateReader(); app.Resources = (ResourceDictionary)XamlReader.Load(reader);
    }
    private static void Check(string name, Func<object> test)
    {
        try { Record(name, true, test()); }
        catch (Exception error) { Record(name, false, new { error = error.Message, type = error.GetType().Name }); }
    }
    private static void Record(string name, bool passed, object detail)
    {
        if (!passed) Failures++;
        Results.Add(new { name, passed, detail });
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(detail));
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static T Field<T>(string name) => (T)(typeof(MainWindow).GetField(name, Private)!.GetValue(Window) ?? default(T)!);
    private static void Set(string name, object? value) => typeof(MainWindow).GetField(name, Private)!.SetValue(Window, value);
    private static object? Invoke(string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(Window, args);
}
