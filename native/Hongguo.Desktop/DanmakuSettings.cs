using System.Text.Json.Serialization;

namespace Hongguo.Desktop;

// 弹幕偏好只描述显示方式，不保存评论正文或用户资料。
public sealed class DanmakuSettings
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("area")] public string Area { get; set; } = "half";
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 0.85;
    [JsonPropertyName("font_size")] public string FontSize { get; set; } = "medium";
    [JsonPropertyName("speed")] public string Speed { get; set; } = "normal";
    [JsonPropertyName("density")] public string Density { get; set; } = "normal";

    // 配置可能由旧版本或手工修改产生；非法值回到可预期的默认档位。
    public void Normalize()
    {
        Area = Area?.Trim().ToLowerInvariant() switch { "top" => "top", "full" => "full", _ => "half" };
        FontSize = FontSize?.Trim().ToLowerInvariant() switch { "small" => "small", "large" => "large", _ => "medium" };
        Speed = Speed?.Trim().ToLowerInvariant() switch { "slow" => "slow", "fast" => "fast", _ => "normal" };
        Density = Density?.Trim().ToLowerInvariant() switch { "sparse" => "sparse", "dense" => "dense", _ => "normal" };
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0.2, 1) : 0.85;
    }
}
