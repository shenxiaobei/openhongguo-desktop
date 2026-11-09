using System.Globalization;
using System.Text;

namespace Hongguo.Desktop;

// 弹幕作为原生 ASS 时间轴渲染，暂停、跳转和倍速由解码器统一同步。
internal static class DanmakuAssRenderer
{
    internal static string Render(IReadOnlyList<DiscussionEntry> entries, DanmakuSettings settings, int viewportWidth, int viewportHeight, int displayHeight = 720)
    {
        const int height = 720;
        int width = Math.Clamp((int)Math.Round(height * Math.Max(1, viewportWidth) / (double)Math.Max(1, viewportHeight)), 160, 5120);
        int layoutFontSize = settings.FontSize switch { "small" => 23, "large" => 36, _ => 29 };
        // 大窗口的中号字稍收敛，小窗口仍保留原字号与行道，避免缩窗后难读。
        int fontSize = settings.FontSize == "medium" && displayHeight >= 540 ? 26 : layoutFontSize;
        double fraction = settings.Area switch { "top" => 0.25, "full" => 0.8, _ => 0.5 };
        int lineHeight = (int)Math.Ceiling(layoutFontSize * 1.5);
        int top = Math.Max(10, layoutFontSize / 2);
        int maximumLanes = Math.Max(1, (int)((height * fraction - top) / lineHeight));
        double density = settings.Density switch { "sparse" => 0.35, "dense" => 1, _ => 0.65 };
        int lanes = Math.Clamp((int)Math.Round(maximumLanes * density), 1, 18);
        double crossingSeconds = settings.Speed switch { "slow" => 16, "fast" => 8, _ => 12 };
        int maximumTextUnits = Math.Clamp((int)(width * 1.6 / (fontSize * 1.12)), 8, 80);
        int maximumTextWidth = (int)Math.Ceiling((maximumTextUnits + 1) * fontSize * 1.12) + fontSize;
        double pixelsPerSecond = (width + fontSize / 2 + maximumTextWidth) / crossingSeconds;
        double opacity = Math.Clamp(settings.Opacity, 0.2, 1);
        int alpha = (int)Math.Round((1 - opacity) * 255);
        // 字面保留用户选择的可见度，轮廓和阴影只提供轻微衬托，避免粗黑边像贴纸。
        int outlineAlpha = (int)Math.Round((1 - opacity * 0.5) * 255);
        int shadowAlpha = (int)Math.Round((1 - opacity * 0.25) * 255);
        var output = new StringBuilder();
        output.AppendLine("[Script Info]");
        output.AppendLine("ScriptType: v4.00+");
        output.AppendLine("WrapStyle: 2");
        output.AppendLine("ScaledBorderAndShadow: yes");
        output.AppendLine("PlayResX: " + width.ToString(CultureInfo.InvariantCulture));
        output.AppendLine("PlayResY: 720");
        output.AppendLine("[V4+ Styles]");
        output.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        output.AppendLine(FormattableString.Invariant($"Style: Danmaku,Microsoft YaHei,{fontSize},&H{alpha:X2}DCD9D5,&H{alpha:X2}DCD9D5,&H{outlineAlpha:X2}181818,&H{shadowAlpha:X2}000000,0,0,0,0,100,100,0,0,1,0.65,0.65,7,0,0,0,1"));
        output.AppendLine("[Events]");
        output.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        if (!settings.Enabled) return output.ToString();

        var availableAt = new double[lanes];
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenText = new HashSet<(long, string)>();
        int nextLane = 0;
        foreach (var entry in entries.Where(value => value != null && value.PositionMS is >= 0 and <= 86_400_000)
                     .OrderBy(value => value.PositionMS).Take(5000))
        {
            string text = PlainText(entry.Text, maximumTextUnits);
            if (text.Length == 0 || !seenText.Add((entry.PositionMS!.Value, text))) continue;
            if (!string.IsNullOrEmpty(entry.Id) && !seenIds.Add(entry.Id)) continue;

            double start = entry.PositionMS.Value / 1000d;
            int lane = -1;
            for (int offset = 0; offset < lanes; offset++)
            {
                int candidate = (nextLane + offset) % lanes;
                if (availableAt[candidate] <= start)
                {
                    lane = candidate;
                    break;
                }
            }
            // 没有空行道时跳过此条，不推迟原始时间，也不让后发文字追上前条。
            if (lane < 0) continue;
            nextLane = (lane + 1) % lanes;
            int textWidth = (int)Math.Ceiling(text.EnumerateRunes().Count() * fontSize * 1.12) + fontSize;
            int startX = width + fontSize / 2;
            int endX = -textWidth;
            int y = top + lane * lineHeight;
            long moveMilliseconds = Math.Min((long)(crossingSeconds * 1000), (long)Math.Ceiling((startX - endX) / pixelsPerSecond * 1000 / 10) * 10);
            availableAt[lane] = start + (textWidth + fontSize) / pixelsPerSecond;
            long startCentiseconds = entry.PositionMS.Value / 10;
            long endCentiseconds = startCentiseconds + moveMilliseconds / 10;
            long fadeInMilliseconds = Math.Min(220, moveMilliseconds / 4);
            long fadeOutMilliseconds = Math.Min(300, moveMilliseconds / 4);
            output.Append("Dialogue: 0,").Append(Time(startCentiseconds)).Append(',').Append(Time(endCentiseconds));
            output.Append(",Danmaku,,0,0,0,,");
            // 淡入淡出跟随原始媒体时间，跳转或倍速时不会重新从头播放。
            output.Append(FormattableString.Invariant($"{{\\an7\\q2\\blur0.4\\fad({fadeInMilliseconds},{fadeOutMilliseconds})\\move({startX},{y},{endX},{y},0,{moveMilliseconds})}}"));
            output.AppendLine(text);
        }
        return output.ToString();
    }

    private static string PlainText(string? source, int maximumElements)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        if (source.Length > 4096) source = source[..(char.IsHighSurrogate(source[4095]) ? 4095 : 4096)];
        var output = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(source);
        int count = 0;
        bool previousSpace = false;
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(element, 0);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                element = " ";
            else element = element.Replace("\\", "＼", StringComparison.Ordinal).Replace("{", "｛", StringComparison.Ordinal).Replace("}", "｝", StringComparison.Ordinal);
            bool space = string.IsNullOrWhiteSpace(element);
            if (space && (previousSpace || output.Length == 0)) continue;
            int units = element.EnumerateRunes().Count();
            if (count + units > maximumElements)
            {
                output.Append('…');
                break;
            }
            output.Append(space ? " " : element);
            previousSpace = space;
            count += units;
        }
        return output.ToString().Trim();
    }

    private static string Time(long centiseconds)
    {
        long seconds = centiseconds / 100;
        return FormattableString.Invariant($"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}.{centiseconds % 100:00}");
    }
}
