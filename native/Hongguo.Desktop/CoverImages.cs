using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using Image = System.Windows.Controls.Image;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace Hongguo.Desktop;

public static class CoverImages
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly SemaphoreSlim LoadingSlots = new(4, 4);
    private static readonly ConcurrentDictionary<string, Lazy<Task<BitmapSource?>>> Pending = new();
    private static readonly ConditionalWeakTable<Image, ImageRequest> Requests = new();
    private static readonly ImageSource Placeholder = CreatePlaceholder();
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenHongguo", "covers");

    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(CoverImages), new PropertyMetadata("", OnUrlChanged));

    public static void SetUrl(DependencyObject target, string? value) => target.SetValue(UrlProperty, value);
    public static string? GetUrl(DependencyObject target) => (string?)target.GetValue(UrlProperty);

    private sealed class ImageRequest
    {
        internal long Generation;
    }

    private static void OnUrlChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not Image image) return;
        ImageRequest request = Requests.GetValue(image, static _ => new ImageRequest());
        long generation = ++request.Generation;
        image.Source = Placeholder;
        string? url = change.NewValue as string;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return;
        _ = UpdateImageAsync(image, url!, request, generation);
    }

    private static async Task UpdateImageAsync(Image image, string url, ImageRequest request, long generation)
    {
        try
        {
            Lazy<Task<BitmapSource?>> operation = Pending.GetOrAdd(url,
                address => new Lazy<Task<BitmapSource?>>(() => Task.Run(() => LoadAsync(address))));
            BitmapSource? bitmap;
            try
            {
                bitmap = await operation.Value.ConfigureAwait(false);
            }
            finally
            {
                Pending.TryRemove(new KeyValuePair<string, Lazy<Task<BitmapSource?>>>(url, operation));
            }
            if (bitmap == null || image.Dispatcher.HasShutdownStarted) return;
            await image.Dispatcher.InvokeAsync(() =>
            {
                // 列表复用控件或快速切换搜索时，旧请求只能更新它原先对应的 URL。
                if (request.Generation != generation || !string.Equals(GetUrl(image), url, StringComparison.Ordinal)) return;
                image.Source = bitmap;
            }).Task.ConfigureAwait(false);
        }
        catch
        {
            // 下载、解码及窗口退出失败均保留占位，不把图片地址或异常交给界面。
        }
    }

    private static async Task<BitmapSource?> LoadAsync(string url)
    {
        await LoadingSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
            string cache = Path.Combine(CacheDirectory, key + ".png");
            try
            {
                // 键包含完整签名 URL；已经下载的图像可在 24 小时内离线复用。
                if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromHours(24)
                    && new FileInfo(cache).Length <= MaximumBytes)
                    return DecodeBitmap(await File.ReadAllBytesAsync(cache).ConfigureAwait(false));
            }
            catch
            {
                // 损坏或不可读的缓存按未命中处理。
            }

            byte[] bytes = await DownloadAsync(url).ConfigureAwait(false);
            BitmapSource bitmap;
            if (IsHeif(bytes))
            {
                bitmap = DecodeWithMagick(bytes);
            }
            else
            {
                try { bitmap = DecodeBitmap(bytes); }
                catch { bitmap = DecodeWithMagick(bytes); }
            }

            await CacheAsync(cache, bitmap).ConfigureAwait(false);
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            LoadingSlots.Release();
        }
    }

    private static async Task<byte[]> DownloadAsync(string url)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using HttpResponseMessage response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("封面尺寸超出限制");
        using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int count = await input.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaximumBytes) throw new InvalidDataException("封面尺寸超出限制");
            output.Write(buffer, 0, count);
        }
    }

    private static bool IsHeif(byte[] bytes)
    {
        return bytes.Length >= 12 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p';
    }

    private static BitmapSource DecodeWithMagick(byte[] bytes)
    {
        MagickReadSettings settings = new() { FrameIndex = 0, FrameCount = 1 };
        using MagickImage image = new(bytes, settings);
        image.AutoOrient();
        // 只缩小到封面显示需要的范围，保持比例，不裁剪真正的海报。
        image.Resize(new MagickGeometry(480, 720) { Greater = true });
        return DecodeBitmap(image.ToByteArray(MagickFormat.Png));
    }

    private static BitmapSource DecodeBitmap(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 480;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        if (image.PixelHeight <= 720) return image;
        double scale = 720.0 / image.PixelHeight;
        TransformedBitmap resized = new(image, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    private static async Task CacheAsync(string destination, BitmapSource bitmap)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using MemoryStream png = new();
            encoder.Save(png);
            await File.WriteAllBytesAsync(temporary, png.ToArray()).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            // 缓存目录只读或被其他实例占用时，当前已解码的封面仍可显示。
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private static ImageSource CreatePlaceholder()
    {
        DrawingGroup drawing = new();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(28, 36, 52)), null, new RectangleGeometry(new Rect(0, 0, 120, 160))));
        Pen outline = new(new SolidColorBrush(Color.FromRgb(78, 91, 114)), 2);
        drawing.Children.Add(new GeometryDrawing(null, outline, new RectangleGeometry(new Rect(32, 58, 56, 44), 4, 4)));
        drawing.Children.Add(new GeometryDrawing(null, outline, Geometry.Parse("M 33,94 L 48,77 61,90 71,82 87,98")));
        drawing.Children.Add(new GeometryDrawing(outline.Brush, null, new EllipseGeometry(new Point(73, 70), 4, 4)));
        drawing.Freeze();
        DrawingImage image = new(drawing);
        image.Freeze();
        return image;
    }
}
