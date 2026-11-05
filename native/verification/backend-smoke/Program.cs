using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type BackendType = typeof(Hongguo.Desktop.App).Assembly.GetType("Hongguo.Desktop.BackendClient", true)!;
    private static readonly MethodInfo CallMethod = BackendType.GetMethod("CallAsync", PrivateInstance)!;
    private static readonly FieldInfo ProcessField = BackendType.GetField("process", PrivateInstance)!;
    private static readonly FieldInfo TerminalField = BackendType.GetField("terminalFailure", PrivateInstance)!;
    private static readonly FieldInfo WriteLockField = BackendType.GetField("writeLock", PrivateInstance)!;
    private static readonly FieldInfo PendingField = BackendType.GetField("pending", PrivateInstance)!;
    private static readonly List<object> Results = new();
    private static readonly string Root = Environment.CurrentDirectory;
    private static int failures;

    private static async Task<int> Main()
    {
        Directory.CreateDirectory(Path.Combine(Root, "results"));
        try
        {
            await VerifyExitAsync();
            await VerifyDisposeAsync(false);
            await VerifyDisposeAsync(true);
        }
        catch (Exception error)
        {
            Record("harness", false, new { exception = error.GetType().Name, message = error.Message });
        }

        string report = Path.Combine(Root, "results", "report.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { checkedAt = DateTimeOffset.Now, failures, results = Results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("REPORT " + report);
        return failures == 0 ? 0 : 1;
    }

    private static object CreateBackend(string name)
    {
        string config = Path.Combine(Root, "state", name + "-" + Guid.NewGuid().ToString("N"));
        return Activator.CreateInstance(BackendType, PrivateInstance, null, new object[] { config }, null)!;
    }

    private static Task<JsonElement> CallAsync(object backend, object? args = null)
    {
        return (Task<JsonElement>)CallMethod.Invoke(backend, new object?[] { "status", args, CancellationToken.None })!;
    }

    private static async Task VerifyStatusAsync(object backend, string name)
    {
        JsonElement result = await CallAsync(backend).WaitAsync(TimeSpan.FromSeconds(5));
        bool valid = result.GetProperty("native").GetBoolean() && !result.GetProperty("local_server").GetBoolean();
        Record(name, valid, new { native = result.GetProperty("native").GetBoolean(), localServer = result.GetProperty("local_server").GetBoolean(), version = result.GetProperty("version").GetString() });
        if (!valid) throw new InvalidOperationException("真实 bridge 状态不正确");
    }

    private static async Task VerifyExitAsync()
    {
        object backend = CreateBackend("exit");
        Process owned = (Process)ProcessField.GetValue(backend)!;
        int pid = owned.Id;
        try
        {
            await VerifyStatusAsync(backend, "real-bridge-status");
            // 只结束本测试刚创建并持有句柄的辅助进程。
            owned.Kill(entireProcessTree: true);
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Stopwatch eof = Stopwatch.StartNew();
            while (TerminalField.GetValue(backend) == null && eof.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(10);
            Record("stdout-eof-recorded", TerminalField.GetValue(backend) != null, new { pid, elapsedMs = eof.Elapsed.TotalMilliseconds });

            Stopwatch watch = Stopwatch.StartNew();
            Exception? observed = null;
            try { await CallAsync(backend).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) { observed = Unwrap(error); }
            bool passed = observed is IOException && watch.Elapsed < TimeSpan.FromSeconds(2);
            Record("call-after-exit-fails-immediately", passed, new { pid, elapsedMs = watch.Elapsed.TotalMilliseconds, exception = observed?.GetType().Name, pending = PendingCount(backend) });
        }
        finally
        {
            ((IDisposable)backend).Dispose();
            await EnsureExitedAsync(pid);
        }
    }

    private static async Task VerifyDisposeAsync(bool holdWriteLock)
    {
        string name = holdWriteLock ? "dispose-with-write-contention" : "dispose-during-status-requests";
        object backend = CreateBackend(name);
        int pid = ((Process)ProcessField.GetValue(backend)!).Id;
        SemaphoreSlim writer = (SemaphoreSlim)WriteLockField.GetValue(backend)!;
        bool held = false;
        Task<Exception?>[] requests = Array.Empty<Task<Exception?>>();
        try
        {
            await VerifyStatusAsync(backend, name + "-initial-status");
            if (holdWriteLock)
            {
                // 人为维持写锁竞争，验证退出不等待长达 70 秒的排队请求。
                await writer.WaitAsync();
                held = true;
            }

            string padding = new('x', 32 * 1024);
            requests = Enumerable.Range(0, 128).Select(_ => ObserveAsync(CallAsync(backend, new { padding }))).ToArray();
            int pendingBefore = PendingCount(backend);
            Stopwatch watch = Stopwatch.StartNew();
            await Task.Run(() => ((IDisposable)backend).Dispose()).WaitAsync(TimeSpan.FromSeconds(3));
            double elapsedMs = watch.Elapsed.TotalMilliseconds;
            if (held)
            {
                writer.Release();
                held = false;
            }

            Exception?[] completed = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(3));
            bool exited = await EnsureExitedAsync(pid);
            bool expectedErrors = completed.All(error => error == null || error is IOException || error is ObjectDisposedException);
            int pendingAfter = PendingCount(backend);
            Record(name, pendingBefore > 0 && elapsedMs < 3000 && exited && expectedErrors && pendingAfter == 0,
                new { pid, elapsedMs, requests = requests.Length, pendingBefore, pendingAfter, successful = completed.Count(error => error == null), failed = completed.Count(error => error != null), exited, expectedErrors });
        }
        finally
        {
            if (held) writer.Release();
            ((IDisposable)backend).Dispose();
            if (requests.Length > 0) await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(3));
            await EnsureExitedAsync(pid);
        }
    }

    private static async Task<Exception?> ObserveAsync(Task<JsonElement> request)
    {
        try { await request; return null; }
        catch (Exception error) { return Unwrap(error); }
    }

    private static int PendingCount(object backend)
    {
        return ((IDictionary)PendingField.GetValue(backend)!).Count;
    }

    private static async Task<bool> EnsureExitedAsync(int pid)
    {
        try
        {
            using Process owned = Process.GetProcessById(pid);
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (TimeoutException) { return false; }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
        return error;
    }

    private static void Record(string name, bool passed, object details)
    {
        Results.Add(new { name, passed, details });
        if (!passed) failures++;
        Console.WriteLine((passed ? "PASS " : "FAIL ") + name + " " + JsonSerializer.Serialize(details));
    }
}
