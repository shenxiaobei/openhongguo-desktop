using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hongguo.Desktop;

public sealed class MpvStatus
{
    public double Position { get; init; }
    public double Duration { get; init; }
    public bool Paused { get; init; }
    public bool Muted { get; init; }
    public double Speed { get; init; } = 1;
    public double Volume { get; init; } = 100;
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
    public bool Loaded { get; init; }
    public bool FrameReady { get; init; }
    public double CacheSeconds { get; init; }
    public long CacheBytes { get; init; }
    public bool CacheReachedEnd { get; init; }
}

// 视频始终留在原生子窗口内；界面只负责控件与布局，不启动浏览器或外部播放器。
public sealed class MpvPlayer : IDisposable
{
    private readonly object _gate = new();
    private readonly Api _api;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _events;
    private IntPtr _handle;
    private long _generation;
    private long _entryId = -1;
    private long _startedEntryId = -1;
    private bool _loaded;
    private bool _frameReady;
    private bool _disposed;
    private double _speed = 1;
    private double _volume = 100;
    private MpvStatus _lastStatus = new();

    public event EventHandler? Ended;
    public event EventHandler<string>? Error;
    public long LastEndTimestamp { get; private set; }

    public MpvPlayer(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("视频窗口尚未准备好。", nameof(hwnd));
        _api = Api.Instance;
        _handle = _api.Create();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("播放器初始化失败。");

        try
        {
            SetOption("wid", hwnd.ToInt64().ToString(CultureInfo.InvariantCulture));
            SetOption("config", "no");
            SetOption("terminal", "no");
            SetOption("load-scripts", "no");
            SetOption("input-default-bindings", "no");
            SetOption("input-vo-keyboard", "no");
            SetOption("input-cursor", "no");
            SetOption("osc", "no");
            SetOption("osd-level", "0");
            SetOption("idle", "yes");
            SetOption("keep-open", "no");
            SetOption("keepaspect", "yes");
            SetOption("keepaspect-window", "no");
            SetOption("panscan", "0");
            SetOption("vo", "gpu-next");
            SetOption("gpu-api", "d3d11");
            SetOption("hwdec", "auto-safe");
            SetOption("ytdl", "no");
            // 暂停的备用播放器也继续读取媒体，但限制前向缓存，避免下载整集。
            SetOption("cache", "yes");
            // 视频缓冲只保留在内存中，切集或退出时随解码器释放。
            SetOption("cache-on-disk", "no");
            SetOption("cache-secs", "12");
            SetOption("demuxer-readahead-secs", "12");
            SetOption("demuxer-max-bytes", "24MiB");
            SetOption("demuxer-max-back-bytes", "4MiB");
            SetOption("cache-pause-initial", "no");
            Check(_api.Initialize(_handle), "播放器初始化");
        }
        catch
        {
            _api.Destroy(_handle);
            _handle = IntPtr.Zero;
            throw;
        }

        _events = Task.Run(ReadEventsAsync);
    }

    public void Load(string url, string? audioUrl, string? key, double startSeconds = 0, bool paused = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!double.IsFinite(startSeconds)) startSeconds = 0;
        lock (_gate)
        {
            ThrowIfDisposed();
            _generation++;
            _entryId = -1;
            _startedEntryId = -1;
            _loaded = false;
            _frameReady = false;
            LastEndTimestamp = 0;
            _lastStatus = new MpvStatus { Speed = _speed, Volume = _volume };

            // 每集选项随 loadfile 单独传递，避免沿用上一集的音轨或媒体密钥。
            using var nodes = new CommandNodes();
            var options = new Dictionary<string, MpvNode>
            {
                ["start"] = nodes.String(Math.Max(0, startSeconds).ToString("R", CultureInfo.InvariantCulture))
            };
            if (!string.IsNullOrWhiteSpace(audioUrl)) options["audio-file"] = nodes.String(audioUrl);
            if (!string.IsNullOrWhiteSpace(key)) options["demuxer-lavf-o"] = nodes.String("decryption_key=" + key);

            var command = nodes.Array([
                nodes.String("loadfile"), nodes.String(url), nodes.String("replace"),
                new MpvNode { Value = -1, Format = 4 }, nodes.Map(options)
            ]);
            SetProperty("pause", paused ? "yes" : "no");
            Check(_api.Command(_handle, ref command, IntPtr.Zero), "加载视频");
            _entryId = ReadLong("playlist/0/id", -1);
            SetProperty("pause", paused ? "yes" : "no");
            SetProperty("speed", _speed.ToString("R", CultureInfo.InvariantCulture));
            SetProperty("volume", _volume.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            SetProperty("pause", paused ? "yes" : "no");
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            SetProperty("mute", muted ? "yes" : "no");
        }
    }

    public void Seek(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_loaded) return;
            using var nodes = new CommandNodes();
            var command = nodes.Array([
                nodes.String("seek"), nodes.String(Math.Max(0, seconds).ToString("R", CultureInfo.InvariantCulture)),
                nodes.String("absolute+exact")
            ]);
            Check(_api.Command(_handle, ref command, IntPtr.Zero), "调整播放进度");
        }
    }

    public void SetSpeed(double speed)
    {
        if (!double.IsFinite(speed)) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            _speed = Math.Clamp(speed, 0.25, 4);
            SetProperty("speed", _speed.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    public void SetVolume(double volume)
    {
        if (!double.IsFinite(volume)) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            _volume = Math.Clamp(volume, 0, 100);
            SetProperty("volume", _volume.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _entryId = -1;
            _startedEntryId = -1;
            _loaded = false;
            _frameReady = false;
            using var nodes = new CommandNodes();
            var command = nodes.Array([nodes.String("stop")]);
            Check(_api.Command(_handle, ref command, IntPtr.Zero), "停止播放");
        }
    }

    public MpvStatus GetStatus()
    {
        lock (_gate)
        {
            if (_disposed) return _lastStatus;
            (long cacheBytes, bool cacheReachedEnd) = ReadCacheState();
            _lastStatus = new MpvStatus
            {
                Position = Math.Max(0, ReadDouble("time-pos", _lastStatus.Position)),
                Duration = Math.Max(0, ReadDouble("duration", _lastStatus.Duration)),
                Paused = ReadFlag("pause", _lastStatus.Paused),
                Muted = ReadFlag("mute", false),
                Speed = ReadDouble("speed", _speed),
                Volume = ReadDouble("volume", _volume),
                VideoWidth = (int)ReadLong("video-params/w", _lastStatus.VideoWidth),
                VideoHeight = (int)ReadLong("video-params/h", _lastStatus.VideoHeight),
                Loaded = _loaded,
                // playback-restart 在首帧解码和输出恢复后发出，区别于仅完成文件打开。
                FrameReady = _loaded && _frameReady && ReadFlag("vo-configured", false),
                CacheSeconds = Math.Max(0, ReadDouble("demuxer-cache-duration", 0)),
                CacheBytes = cacheBytes,
                CacheReachedEnd = cacheReachedEnd
            };
            return _lastStatus;
        }
    }

    private async Task ReadEventsAsync()
    {
        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    // 零超时排空事件，不把原生等待放在界面线程，也不占用关闭锁。
                    while (true)
                    {
                        IntPtr pointer = _api.WaitEvent(_handle, 0);
                        if (pointer == IntPtr.Zero) break;
                        var item = Marshal.PtrToStructure<MpvEvent>(pointer);
                        if (item.Id == 0) break;
                        HandleEvent(item);
                    }
                }
                await Task.Delay(15, _cancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch
        {
            PostEvent(_generation, "播放器状态读取失败，请重新打开视频。");
        }
    }

    private void HandleEvent(MpvEvent item)
    {
        if (item.Id == 6 && item.Data != IntPtr.Zero)
        {
            long entryId = Marshal.ReadInt64(item.Data);
            if (entryId == _entryId) _startedEntryId = entryId;
            return;
        }
        if (item.Id == 8 && _entryId >= 0 && _startedEntryId == _entryId)
        {
            _loaded = true;
            return;
        }
        if (item.Id == 21 && _loaded && _entryId >= 0 && _startedEntryId == _entryId)
        {
            _frameReady = true;
            return;
        }
        if (item.Id != 7 || item.Data == IntPtr.Zero) return;

        var end = Marshal.PtrToStructure<MpvEndFile>(item.Data);
        if (_entryId < 0 || end.EntryId != _entryId) return;
        _loaded = false;
        _frameReady = false;
        if (end.Reason == 0)
        {
            LastEndTimestamp = Stopwatch.GetTimestamp();
            PostEvent(_generation, null);
        }
        else if (end.Reason == 4)
        {
            PostEvent(_generation, "视频加载或解码失败（" + ErrorText(end.Error) + "），请重试或切换剧集。");
        }
    }

    private void PostEvent(long generation, string? error)
    {
        void Deliver(object? _)
        {
            lock (_gate)
            {
                if (_disposed || generation != _generation) return;
            }
            // 订阅者可能关闭窗口或切集，调用外部事件时释放锁，避免与事件循环互等。
            if (error == null) Ended?.Invoke(this, EventArgs.Empty);
            else Error?.Invoke(this, error);
        }

        // 回到创建播放器的界面上下文，并在真正交付时再检查切集代次。
        if (_context != null) _context.Post(Deliver, null);
        else ThreadPool.QueueUserWorkItem(Deliver);
    }

    private double ReadDouble(string name, double fallback)
    {
        if (_api.GetDouble(_handle, name, 5, out double value) < 0 || !double.IsFinite(value)) return fallback;
        return value;
    }

    private long ReadLong(string name, long fallback)
    {
        return _api.GetLong(_handle, name, 4, out long value) < 0 ? fallback : value;
    }

    private bool ReadFlag(string name, bool fallback)
    {
        return _api.GetFlag(_handle, name, 3, out int value) < 0 ? fallback : value != 0;
    }

    private (long Bytes, bool ReachedEnd) ReadCacheState()
    {
        // 此复合属性的子字段在当前 libmpv 不能直接按路径读取，需要读取原生 map。
        if (_api.GetNode(_handle, "demuxer-cache-state", 6, out MpvNode node) < 0) return (0, false);
        try
        {
            if (node.Format != 8 || node.Value == 0) return (0, false);
            var list = Marshal.PtrToStructure<MpvNodeList>(new IntPtr(node.Value));
            long bytes = 0;
            bool reachedEnd = false;
            for (int index = 0; index < list.Count; index++)
            {
                string? key = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(list.Keys, index * IntPtr.Size));
                var value = Marshal.PtrToStructure<MpvNode>(IntPtr.Add(list.Values, index * Marshal.SizeOf<MpvNode>()));
                if (key == "fw-bytes" && value.Format == 4) bytes = Math.Max(0, value.Value);
                if (key == "eof-cached" && value.Format == 3) reachedEnd = value.Value != 0;
            }
            return (bytes, reachedEnd);
        }
        finally
        {
            _api.FreeNode(ref node);
        }
    }

    private void SetOption(string name, string value) => Check(_api.SetOption(_handle, name, value), "设置播放器选项 " + name);
    private void SetProperty(string name, string value) => Check(_api.SetProperty(_handle, name, value), "设置播放状态 " + name);
    private string ErrorText(int code) => Marshal.PtrToStringUTF8(_api.ErrorString(code)) ?? code.ToString(CultureInfo.InvariantCulture);
    private void Check(int code, string operation)
    {
        if (code < 0) throw new InvalidOperationException(operation + "失败：" + ErrorText(code));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _cancellation.Cancel();
        }
        // 事件只异步投递，关闭时等待事件循环不会等待界面回调。
        _events.GetAwaiter().GetResult();
        lock (_gate)
        {
            _api.Destroy(_handle);
            _handle = IntPtr.Zero;
        }
        _cancellation.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MpvNode { public long Value; public int Format; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MpvNodeList { public int Count; public IntPtr Values; public IntPtr Keys; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEvent { public int Id; public int Error; public ulong UserData; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MpvEndFile { public int Reason; public int Error; public long EntryId; public long InsertId; public int InsertCount; }

    // 结构化命令保留 URL 中的逗号、反斜线等字符，所有临时原生内存统一释放。
    private sealed class CommandNodes : IDisposable
    {
        private readonly List<IntPtr> _allocations = [];
        public MpvNode String(string value)
        {
            IntPtr pointer = Marshal.StringToCoTaskMemUTF8(value);
            _allocations.Add(pointer);
            return new MpvNode { Value = pointer.ToInt64(), Format = 1 };
        }
        public MpvNode Array(MpvNode[] values) => List(values, null);
        public MpvNode Map(Dictionary<string, MpvNode> values) => List(values.Values.ToArray(), values.Keys.ToArray());
        private MpvNode List(MpvNode[] values, string[]? keys)
        {
            int size = Marshal.SizeOf<MpvNode>();
            IntPtr array = Allocate(size * values.Length);
            IntPtr keyArray = keys == null ? IntPtr.Zero : Allocate(IntPtr.Size * keys.Length);
            for (int index = 0; index < values.Length; index++)
            {
                Marshal.StructureToPtr(values[index], IntPtr.Add(array, index * size), false);
                if (keys != null) Marshal.WriteIntPtr(keyArray, index * IntPtr.Size, new IntPtr(String(keys[index]).Value));
            }
            IntPtr list = Allocate(Marshal.SizeOf<MpvNodeList>());
            Marshal.StructureToPtr(new MpvNodeList { Count = values.Length, Values = array, Keys = keyArray }, list, false);
            return new MpvNode { Value = list.ToInt64(), Format = keys == null ? 7 : 8 };
        }
        private IntPtr Allocate(int length)
        {
            IntPtr pointer = Marshal.AllocCoTaskMem(length);
            _allocations.Add(pointer);
            return pointer;
        }
        public void Dispose()
        {
            foreach (IntPtr pointer in _allocations) Marshal.FreeCoTaskMem(pointer);
            _allocations.Clear();
        }
    }

    private sealed class Api
    {
        private static readonly Lazy<Api> Shared = new(() => new Api());
        public static Api Instance => Shared.Value;
        private readonly IntPtr _vulkanLibrary;
        private readonly IntPtr _library;
        public readonly CreateDelegate Create;
        public readonly InitializeDelegate Initialize;
        public readonly DestroyDelegate Destroy;
        public readonly SetStringDelegate SetOption;
        public readonly SetStringDelegate SetProperty;
        public readonly GetDoubleDelegate GetDouble;
        public readonly GetLongDelegate GetLong;
        public readonly GetFlagDelegate GetFlag;
        public readonly GetNodeDelegate GetNode;
        public readonly FreeNodeDelegate FreeNode;
        public readonly CommandDelegate Command;
        public readonly WaitEventDelegate WaitEvent;
        public readonly ErrorStringDelegate ErrorString;
        private Api()
        {
            // 预加载随包提供的导入依赖，保持空白 Windows 环境也可独立启动。
            _vulkanLibrary = NativeLibrary.Load(BundledFiles.Extract("vulkan-1.dll"));
            _library = NativeLibrary.Load(BundledFiles.Extract("libmpv-2.dll"));
            Create = Export<CreateDelegate>("mpv_create");
            Initialize = Export<InitializeDelegate>("mpv_initialize");
            Destroy = Export<DestroyDelegate>("mpv_terminate_destroy");
            SetOption = Export<SetStringDelegate>("mpv_set_option_string");
            SetProperty = Export<SetStringDelegate>("mpv_set_property_string");
            GetDouble = Export<GetDoubleDelegate>("mpv_get_property");
            GetLong = Export<GetLongDelegate>("mpv_get_property");
            GetFlag = Export<GetFlagDelegate>("mpv_get_property");
            GetNode = Export<GetNodeDelegate>("mpv_get_property");
            FreeNode = Export<FreeNodeDelegate>("mpv_free_node_contents");
            Command = Export<CommandDelegate>("mpv_command_node");
            WaitEvent = Export<WaitEventDelegate>("mpv_wait_event");
            ErrorString = Export<ErrorStringDelegate>("mpv_error_string");
        }
        private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeDelegate(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyDelegate(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetStringDelegate(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDoubleDelegate(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetLongDelegate(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out long value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetFlagDelegate(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetNodeDelegate(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out MpvNode value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeNodeDelegate(ref MpvNode value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CommandDelegate(IntPtr handle, ref MpvNode command, IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr WaitEventDelegate(IntPtr handle, double timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorStringDelegate(int code);
}
