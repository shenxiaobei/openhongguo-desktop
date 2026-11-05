# 不设应用最小窗口与内存缓冲验收

在此目录执行：

```powershell
dotnet run --project UnrestrictedWindowSmoke.csproj -c Release -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4
```

使用生产 MainWindow，移除其启动后端的 Loaded 处理器，窗口位于屏幕外且不激活。纯布局数据与既有本地竖屏视频用于验证，不修改用户配置，不使用系统输入或 UIAutomation。

验证 320×240、480×320、640×420 窗口实际尺寸与 MinWidth/MinHeight=0；通过滚动访问导航、控制栏和选集；进一步请求 80×80，确认实际尺寸可低于 320×240 后再恢复，真实本地样片保持解码就绪。极小尺寸仍受系统标题栏边界影响；全屏退出恢复极小尺寸，放大后恢复完整布局并关闭外层滚动。

缓存检查读取生产 libmpv 的实际 `options/cache-on-disk`，记录真实缓存字节，并等待 Stop 后 Loaded=false 和 CacheBytes=0。通过原生 HWND 窗口及祖先客户区/Region 的有效交集检查视频裁剪，不以 WPF 截图推断视频像素。

结果为 `results/unrestricted-window-smoke.json`，PNG 只包含 WPF 布局，不含原生视频 HWND 像素。此检查证明应用没有设定原先的最小尺寸，不代表移除了 Windows 标题栏自身的系统边界。
