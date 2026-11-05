using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Hongguo.Desktop;

internal sealed class BackendRequestException(string code, string message) : IOException(message)
{
    internal string Code { get; } = code;
}

internal sealed class BackendClient : IDisposable
{
    private readonly Process process;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private long requestSequence;
    private int disposed;
    private Exception? terminalFailure;

    internal BackendClient(string configDir)
    {
        Directory.CreateDirectory(configDir);
        ProcessStartInfo start = new(BundledFiles.Extract("ohg-native-bridge.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = configDir
        };
        start.ArgumentList.Add("--config-dir");
        start.ArgumentList.Add(configDir);
        process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("后台组件启动失败，请重新打开程序");
        _ = ReadResponsesAsync();
        // 持续排空错误管道，防止子进程阻塞；媒体参数不写入客户端日志。
        _ = process.StandardError.ReadToEndAsync();
    }

    internal async Task<JsonElement> CallAsync(string method, object? args = null, CancellationToken ct = default)
    {
        ThrowIfTerminated();
        ct.ThrowIfCancellationRequested();
        string id = Interlocked.Increment(ref requestSequence).ToString();
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(70));
        try
        {
            // 注册后复查，覆盖后台恰好在注册前退出的情况。
            ThrowIfTerminated();
            await SendAsync(new { id, method, args }, deadline.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _ = CancelRequestAsync(id);
            if (!ct.IsCancellationRequested) throw new TimeoutException("请求超时，请稍后重试");
            throw;
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    private async Task SendAsync(object request, CancellationToken ct)
    {
        string line = JsonSerializer.Serialize(request);
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfTerminated();
            await process.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            throw new IOException("后台组件已退出，请重新打开程序");
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task CancelRequestAsync(string id)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            await SendAsync(new { id = "cancel-" + id, method = "cancel", args = new { request_id = id } }, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
    }

    private async Task ReadResponsesAsync()
    {
        Exception? failure = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is string line)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string id = root.GetProperty("id").GetString() ?? "";
                if (!pending.TryRemove(id, out TaskCompletionSource<JsonElement>? completion)) continue;
                if (root.TryGetProperty("error", out JsonElement error))
                {
                    string message = error.GetProperty("message").GetString() ?? "请求失败，请重试";
                    string code = error.TryGetProperty("code", out JsonElement value) ? value.GetString() ?? "" : "";
                    completion.TrySetException(new BackendRequestException(code, message));
                }
                else if (root.TryGetProperty("result", out JsonElement result))
                {
                    completion.TrySetResult(result.Clone());
                }
                else
                {
                    completion.TrySetException(new IOException("后台返回格式不正确"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ObjectDisposedException)
        {
            failure = ex;
        }
        finally
        {
            Exception stopped = new IOException("后台组件已退出，请重新打开程序", failure);
            Interlocked.CompareExchange(ref terminalFailure, stopped, null);
            FailPending(stopped);
        }
    }

    private void ThrowIfTerminated()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Exception? error = Volatile.Read(ref terminalFailure);
        if (error != null) throw new IOException("后台组件已退出，请重新打开程序", error);
    }

    private void FailPending(Exception error)
    {
        foreach (string id in pending.Keys)
        {
            if (pending.TryRemove(id, out TaskCompletionSource<JsonElement>? completion))
                completion.TrySetException(error);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        FailPending(new ObjectDisposedException(nameof(BackendClient)));
        // 输入流可能仍在写入；在后台协调关闭，界面线程的退出等待始终有上限。
        _ = Task.Run(() =>
        {
            if (!writeLock.Wait(300)) return;
            try { process.StandardInput.Close(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
            finally { writeLock.Release(); }
        });
        try
        {
            // 管道关闭失败也继续执行兜底回收，只结束本窗口创建的进程。
            if (!process.WaitForExit(1500))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        finally
        {
            process.Dispose();
        }
    }
}
