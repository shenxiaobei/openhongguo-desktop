# 播放器随窗口适配验收

在此目录运行：

```powershell
dotnet run --project AdaptivePlayerSmoke.csproj -c Release -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4
```

使用生产 MainWindow 和 libmpv，移除启动后端的 Loaded 回调。自有窗口位于屏幕外且不激活，使用既有本地竖屏样片，不访问远端、不修改用户配置，不使用系统输入或 UIAutomation。

覆盖 320×240、480×240、620×250、640×260、784×400、950×640 和大窗。小窗验收直接检查视频、进度、播放、下一集和全屏入口位于当前视口内，禁止以整窗横纵滚动或 BringIntoView 代替适配。另查设置弹层、极小尺寸恢复和全屏来回。

真实解码检查读取 mpv 的输出尺寸、黑边和显示参数；原生 HWND 客户区需位于 VideoBorder 和当前视口中。WPF PNG 只展示界面布局，不包含原生视频像素，不把它当作视频画面已截图的证据。

结果位于 `results/adaptive-player-smoke.json`。极小的 80×80 是请求尺寸，仅确认仍可低于 320×240，实际值受 Windows 标题栏边界影响；这种尺寸不要求全部控件同时可见。

菜单快捷键及宽矮侧栏发生局部变更后可执行同一命令并追加 `-- --popup-only`，只复核小窗菜单、Esc/F11/换故事的父级恢复和 1380×300 宽矮窗口，保存 `results/popup-shortcuts.json`。

本阶段最终报告保留原始完整运行 `adaptive-primary.json` 的 9 项与后续定向运行的 3 项，按名称替换同名设置检查后为 11 项；`verificationRuns` 记录各次真实时间与来源。当前源码再次完整运行会直接执行全部 11 项。
