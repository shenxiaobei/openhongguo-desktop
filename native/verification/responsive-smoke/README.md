# 小窗口布局回归

在此目录运行：

```powershell
dotnet run --project ResponsiveSmoke.csproj -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4
```

直接创建生产 MainWindow 并移除其启动后端的 Loaded 事件，使用本地目录、榜单、题材、分集与画质数据验证真实 WPF 布局，不访问网络、不修改用户配置，不使用系统输入或 UIAutomation。每个尺寸使用独立 Dispatcher 进程，避免批量创建窗口的输入法 COM 状态残留。

覆盖 640×420、640×480、800×540、950×640、1380×900，检查窗口最小尺寸、图标导航及提示、搜索区域、逐行榜单与横向标签、每日更新提示、播放控制项边界、选集弹层、视频区可用面积和全屏恢复。PNG 为真实 WPF RenderTargetBitmap 布局截图，不包含原生视频 HWND 像素。

尺寸使用 WPF 逻辑像素。报告同时记录请求尺寸、实际尺寸与 Windows 最大窗口追踪值；例如当前 125% 缩放环境将请求的 900 高度限制为系统上限 881.6，这与应用的最小窗口限制分别检查。

汇总报告为 `results/responsive-smoke.json`，每个尺寸的报告与发现页、榜单、播放器、选集弹层截图保留在同目录。
