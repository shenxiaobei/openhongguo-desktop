# 窗口内全屏与屏幕全屏分工验收

在此目录运行：

```powershell
dotnet run --project WindowCinemaSmoke.csproj -c Release -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4
```

使用生产 MainWindow 与 libmpv，移除启动后端的 Loaded 处理器，使用既有本地 360×640 样片。没有访问远端、修改用户配置、发送系统输入或使用 UIAutomation。

当前结果为 `results/window-cinema-smoke.json`，9 个检查全部通过，0 Dispatcher 异常：

- 对 640×360、950×640 实际触发右下角按钮的 Click 事件，核对 WindowState、WindowStyle、ResizeMode、位置、请求/实际尺寸以及 Win32 外框/客户区在进入和退出前后完全相同。视频占满当前客户区，标题与侧栏不占空间，控制栏进入透明独立浮层。
- 真实本地媒体解码就绪，libmpv OSD 与实际视频 HWND 客户区同步；保持原始 360:640 比例和黑边，keepaspect=yes、panscan=0。
- 窗口内模式下拖动对应的 Left/Top 变化、缩放、WindowState.Maximized 与还原后，实际 Popup 子控件屏幕矩形始终贴合 RootLayout 底部，标题栏与可缩放属性保留。最大化通过自有窗口属性触发相同系统状态变化，没有点击用户桌面。
- F11 进入真正的无边框屏幕全屏；Esc 从屏幕全屏恢复之前的普通或窗口内模式，再次 Esc 退出窗口内模式。原生宿主双击/F11 和 Esc 共用事件的生产订阅链也有验证；没有声称实际发送鼠标双击。
- 更多设置中的 F11、导航离开播放器会关闭浮层、归还设置与播放控件父级；窗口内模式中一次外部 Close 自然触发 Closed、停止计时器、关闭浮层并释放真实解码器。

## 定位夹具与验证边界

主要窗口/媒体检查使用非激活、屏幕外的自有窗口。WPF Popup 会把离屏目标钳到真实显示器边界，因此浮层严格定位检查单独将自有窗口置于正常工作区，主窗口保持 `Opacity=0`、`ShowActivated=false`，原生 VideoHost 设为 Hidden，独立 Popup 中的控制层另设 `Opacity=0`。在该不可见夹具中比较实际 PointToScreen 坐标，没有放宽贴底断言；真实视频输出另在屏幕外窗口验证。

显隐测试显式改变浮层和局部鼠标状态，核对视频布局不被挤压、操作和退出可恢复鼠标；没有在非激活窗口中伪造 IsActive，也没有把该检查描述成实际活动窗口的三秒闲置计时。

`window-cinema-controls-*.png` 已实际查看，仅展示 WPF 控件透明底板和布局，不包含原生视频 HWND 像素。主视频 PNG 的黑色区域不作为媒体解码证据，真实画面适配由独立播放器参数证明。

首轮离屏 Popup 坐标钳制的环境观察另存 `offscreen-popup-clamp-observation.json`，它不是本阶段最终产品验收报告，也不是生产定位失败结论。
