using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace Hongguo.Desktop;

internal static class BundledFiles
{
    private static readonly ConcurrentDictionary<string, Lazy<string>> Files = new();

    internal static string Extract(string filename)
    {
        if (Path.GetFileName(filename) != filename)
            throw new ArgumentException("组件名称不正确", nameof(filename));
        return Files.GetOrAdd(filename, name => new Lazy<string>(() => ExtractResource(name))).Value;
    }

    private static string ExtractResource(string filename)
    {
        Assembly assembly = typeof(BundledFiles).Assembly;
        string resourceName = "Hongguo.Desktop.Resources." + filename;
        using Stream resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("程序中缺少内置组件：" + filename);
        byte[] expectedHash = SHA256.HashData(resource);
        string version = assembly.ManifestModule.ModuleVersionId.ToString("N");
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenHongguo", "native", version);
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, filename);
        if (Matches(destination, expectedHash)) return destination;

        // 先写入独立临时文件，避免两个窗口同时启动时读到不完整组件。
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            resource.Position = 0;
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(output);
                output.Flush(true);
            }
            try
            {
                File.Move(temporary, destination, true);
            }
            catch (IOException) when (Matches(destination, expectedHash))
            {
                // 另一个实例已完成同版本组件的释放，可以直接共用。
            }
            if (!Matches(destination, expectedHash)) throw new IOException("内置组件校验失败：" + filename);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool Matches(string path, byte[] expectedHash)
    {
        if (!File.Exists(path)) return false;
        using FileStream stream = File.OpenRead(path);
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), expectedHash);
    }
}
