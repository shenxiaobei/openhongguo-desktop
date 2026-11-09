using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Hongguo.Desktop;

public partial class MainWindow
{
    private DanmakuSettings _danmakuSettings = new();
    private string _danmakuSettingsSaveError = "";

    private void LoadDanmakuSettings()
    {
        _danmakuSettings = new DanmakuSettings();
        _danmakuSettingsSaveError = "";
        string path = Path.Combine(_configDir, "danmaku-settings.json");
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 64 * 1024) return;
            DanmakuSettings? saved = JsonSerializer.Deserialize<DanmakuSettings>(File.ReadAllText(path));
            if (saved == null) return;
            saved.Normalize();
            _danmakuSettings = saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // 损坏或不可读的偏好不影响播放，本次使用默认值。
        }
    }

    private void SaveDanmakuSettings()
    {
        _danmakuSettings.Normalize();
        string temporary = Path.Combine(_configDir, "danmaku-settings." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(_configDir);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_danmakuSettings, new JsonSerializerOptions { WriteIndented = true }));
            // 同一目录写完后再替换，避免中途退出留下半个配置文件。
            File.Move(temporary, Path.Combine(_configDir, "danmaku-settings.json"), overwrite: true);
            _danmakuSettingsSaveError = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _danmakuSettingsSaveError = "本次设置已生效，偏好保存失败，重启后可能恢复原设置。";
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private string DanmakuSettingsSummary()
    {
        if (!_danmakuSettings.Enabled) return "已关闭";
        string area = _danmakuSettings.Area switch { "top" => "顶部 1/4", "full" => "全屏", _ => "半屏" };
        return area + " · " + _danmakuSettings.Opacity.ToString("P0");
    }

    private void ShowDanmakuMenu(StackPanel content)
    {
        _danmakuSettings.Normalize();
        string Describe()
        {
            string size = _danmakuSettings.FontSize switch { "small" => "小号字", "large" => "大号字", _ => "中号字" };
            string speed = _danmakuSettings.Speed switch { "slow" => "慢速", "fast" => "快速", _ => "标准速度" };
            string density = _danmakuSettings.Density switch { "sparse" => "稀疏", "dense" => "密集", _ => "标准密度" };
            return DanmakuSettingsSummary() + " · " + size + "\n" + speed + " · " + density;
        }
        Button back = CreatePlaybackMenuRow("‹  弹幕设置", "返回", () => SetPlaybackMenuPage("root"));
        back.Name = "PlaybackMenuBack";
        content.Children.Add(back);
        var summary = new TextBlock
        {
            Name = "DanmakuSettingsSummary", Text = Describe(), FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(11, 2, 11, 8)
        };
        content.Children.Add(summary);

        Grid enabledRow = CreatePlaybackSettingRow("显示滚动弹幕");
        var enabled = new CheckBox
        {
            Name = "DanmakuEnabledToggle", IsChecked = _danmakuSettings.Enabled,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(enabled, "显示滚动弹幕");
        AutomationProperties.SetAutomationId(enabled, enabled.Name);
        Grid.SetColumn(enabled, 1);
        enabledRow.Children.Add(enabled);
        content.Children.Add(enabledRow);
        AddPlaybackMenuDivider(content);

        // 保持选择页打开，让用户能边看画面边调整；全部档位沿用已有菜单视觉。
        var opacityValue = new TextBlock
        {
            Name = "DanmakuOpacityValue", Text = _danmakuSettings.Opacity.ToString("P0"), FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center
        };
        var saveStatus = new TextBlock
        {
            Name = "DanmakuSettingsSaveStatus", Text = _danmakuSettingsSaveError, FontSize = 10,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(11, 10, 11, 4), Visibility = _danmakuSettingsSaveError.Length == 0 ? Visibility.Collapsed : Visibility.Visible
        };
        var selectedButtons = new List<(Button Button, Func<bool> Selected)>();
        void AddChoices(string title, string name, (string Value, string Label)[] choices, Func<string> current, Action<string> change)
        {
            content.Children.Add(new TextBlock
            {
                Text = title, FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
                Margin = new Thickness(11, 10, 11, 7)
            });
            var row = new Grid { Margin = new Thickness(8, 0, 8, 2) };
            foreach ((string value, string label) in choices)
            {
                int index = row.ColumnDefinitions.Count;
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var button = new Button
                {
                    Name = name + value, Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }, Tag = value,
                    Style = (Style)FindResource("GhostButton"), FontSize = 10,
                    Padding = new Thickness(4, 7, 4, 7), Margin = new Thickness(index == 0 ? 0 : 4, 0, 0, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Center, MinWidth = 0
                };
                AutomationProperties.SetName(button, title + "，" + label);
                AutomationProperties.SetAutomationId(button, button.Name);
                selectedButtons.Add((button, () => current() == value));
                button.Click += (_, _) => { change(value); Commit(); };
                Grid.SetColumn(button, index);
                row.Children.Add(button);
            }
            content.Children.Add(row);
        }

        AddChoices("显示范围", "DanmakuArea", [("top", "顶部 1/4"), ("half", "半屏"), ("full", "全屏")],
            () => _danmakuSettings.Area, value => _danmakuSettings.Area = value);
        content.Children.Add(new TextBlock
        {
            Name = "DanmakuSubtitleProtectionNote", Text = "全屏范围仍为底部字幕留出空间。", FontSize = 10,
            Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(11, 6, 11, 1)
        });
        AddChoices("字号", "DanmakuFont", [("small", "小"), ("medium", "中"), ("large", "大")],
            () => _danmakuSettings.FontSize, value => _danmakuSettings.FontSize = value);

        Grid opacityRow = CreatePlaybackSettingRow("不透明度");
        Grid.SetColumn(opacityValue, 1);
        opacityRow.Children.Add(opacityValue);
        content.Children.Add(opacityRow);
        var opacity = new Slider
        {
            Name = "DanmakuOpacitySlider", Style = (Style)FindResource("VolumeSlider"),
            Minimum = 20, Maximum = 100, Value = _danmakuSettings.Opacity * 100,
            TickFrequency = 5, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true,
            Height = 24, Margin = new Thickness(12, 0, 12, 2)
        };
        AutomationProperties.SetName(opacity, "弹幕不透明度，百分比");
        AutomationProperties.SetAutomationId(opacity, opacity.Name);
        content.Children.Add(opacity);
        AddChoices("滚动速度", "DanmakuSpeed", [("slow", "慢"), ("normal", "标准"), ("fast", "快")],
            () => _danmakuSettings.Speed, value => _danmakuSettings.Speed = value);
        AddChoices("弹幕密度", "DanmakuDensity", [("sparse", "稀疏"), ("normal", "标准"), ("dense", "密集")],
            () => _danmakuSettings.Density, value => _danmakuSettings.Density = value);
        content.Children.Add(saveStatus);

        void Refresh()
        {
            summary.Text = Describe();
            opacityValue.Text = _danmakuSettings.Opacity.ToString("P0");
            foreach ((Button button, Func<bool> selected) in selectedButtons)
            {
                button.Background = (System.Windows.Media.Brush)FindResource(selected() ? "AccentSoftBrush" : "RaisedBrush");
                button.Foreground = (System.Windows.Media.Brush)FindResource(selected() ? "AccentBrush" : "MutedBrush");
            }
            saveStatus.Text = _danmakuSettingsSaveError;
            saveStatus.Visibility = _danmakuSettingsSaveError.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        void Commit()
        {
            _danmakuSettings.Normalize();
            ApplyDanmakuSettings();
            SaveDanmakuSettings();
            Refresh();
        }
        enabled.Click += (_, _) => { _danmakuSettings.Enabled = enabled.IsChecked == true; Commit(); };
        opacity.ValueChanged += (_, _) => { _danmakuSettings.Opacity = opacity.Value / 100; Commit(); };
        Refresh();
    }
}
