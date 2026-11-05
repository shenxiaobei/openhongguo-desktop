# 生产桌面应用切集验证

在此目录运行：

```powershell
dotnet run --project DesktopBufferSmoke.csproj -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4
```

测试直接创建生产 MainWindow，使用独立的本地状态目录及只读复制的匿名设备配置，不覆盖用户观看记录。访问现有后端读取短剧、播放信息及媒体。

验证下一集真正缓冲媒体并完成输出首帧准备，随后通过自然 EOF 切换到备用解码器，记录 native EOF 事件至已准备前台帧、开始播放进度的耗时；同时验证手动切集、关闭连播、切画质保留暂停进度、备用实例静音、过期回退及导航清理。

结果位于 `results/desktop-buffer.json`。不记录媒体 URL 或密钥。首帧证据来自 libmpv 的 playback-restart、vo-configured、视频尺寸和真实缓存；计时不包含物理屏幕扫描输出，也不是网络条件变化下的固定性能保证。
