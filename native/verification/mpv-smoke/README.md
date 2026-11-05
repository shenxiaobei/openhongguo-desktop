# 原生播放器实测

在此目录运行，要求开发环境具有 .NET 9 Windows Desktop SDK 和 FFmpeg。播放器运行库位于 `../../runtime/mpv`。

```powershell
ffmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=360x640:rate=24' -f lavfi -i 'sine=frequency=440:sample_rate=48000' -t 6 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -y sample.mp4
dotnet run --project MpvSmoke.csproj -- sample.mp4
```

验证既有解密参数和独立音轨传递时，使用本地生成的媒体及固定测试密钥：

```powershell
ffmpeg -hide_banner -loglevel error -i sample.mp4 -map 0:v:0 -c copy -encryption_scheme cenc-aes-ctr -encryption_key 00112233445566778899aabbccddeeff -encryption_kid 102132435465768798a9bacbdcedfe0f -y encrypted.mp4
ffmpeg -hide_banner -loglevel error -i sample.mp4 -map 0:a:0 -c copy -y audio.m4a
dotnet run --project MpvSmoke.csproj -- encrypted.mp4 00112233445566778899aabbccddeeff audio.m4a
```

成功时输出：

```text
PASS dimensions=360x640 duration pause seek speed=2 volume=35 EOF=1 Stop=0 replacement=0
```

该验证创建屏幕外测试窗口，不访问在线内容。它覆盖真实 libmpv 解码及播放状态行为；窗口布局与推荐接口由主应用的整体检查覆盖。
