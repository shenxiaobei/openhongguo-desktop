# 指定系列与集号的原生播放验证

验证程序使用生产窗口、JSONL 桥和 libmpv，检查真实首帧、视频尺寸、时长、播放进度与最高可用画质。系列 ID 和集号必须显式提供，防止同名不同季的测试对象混淆。

```powershell
dotnet run --project native/verification/episode12-live-smoke/Episode12LiveSmoke.csproj -c Release -p:GenerateSupportedTargetFrameworkAlias=false -- <series-id> <episode[,episode...]> <report.json> [--auto-next]
```

从仓库根目录执行，确保嵌入的 `native/Hongguo.Desktop/Resources/ohg-native-bridge.exe` 已由当前源码构建。`--auto-next` 会等待下一集预解码，跳到当前集结尾前 1.2 秒，再等待自然 EOF 触发生产自动连播逻辑。

程序仅复制现有匿名设备档案至临时目录，历史及偏好写入该临时目录，退出时自然关闭窗口并删除测试状态。JSON 报告只记录必要的系列与分集身份、播放质量、首帧和进度证据，以及密钥是否存在；不记录媒体地址、密钥值或完整远端响应。

窗口位于屏幕外，不执行系统输入。测试证明原生解码链路工作，不代表系统桌面合成或人工点击验收。
