# 双播放器媒体预加载验证

在此目录运行：

```powershell
dotnet run --project MediaBufferSmoke.csproj -p:GenerateSupportedTargetFrameworkAlias=false
```

复用 `../mpv-smoke/` 中已有的本地样片、加密样片及独立音轨。测试宿主直接编译生产 `MpvPlayer.cs` 和 `NativeVideoHost.cs`，在屏幕外创建两个重叠的原生视频窗口。

检查备用窗口被遮挡时的真实媒体缓存、首帧准备、暂停和静音、窗口交换、事件隔离、停止清理和局部透明鼠标。结果写入 `results/media-buffer.json`。本地窗口交换时间仅代表本地媒体准备后的交换操作，线上完整切集时间见 `../desktop-buffer-smoke/`。
