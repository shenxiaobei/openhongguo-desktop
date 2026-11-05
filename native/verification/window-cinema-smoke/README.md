# 窗口内全屏、透明控制层与后台浮层回归

在此目录运行完整检查：

```powershell
dotnet run --project WindowCinemaSmoke.csproj -c Release -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4
```

完整模式使用生产 MainWindow 与 libmpv，读取 `../mpv-smoke/sample.mp4` 本地 360×640 样片，输出 `results/window-cinema-smoke.json`。结果以本次 JSON 的 `failures` 和逐项 `passed` 为准。

只检查控制层透明度、真实激活状态和三秒显隐时，无需样片或播放器：

```powershell
dotnet run --project WindowCinemaSmoke.csproj -c Release -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4 -- --overlay-only
```

独立模式输出 `results/overlay-only-smoke.json` 与 `results/overlay-transparent-controls.png`。两种模式均移除启动 SDK 后端的 Loaded 处理器，不访问远端或修改用户配置，不发送系统键鼠输入，不使用 UIAutomation，也不读取用户的其他窗口。

## 透明控制层与显隐检查

- 背景必须为完全透明的画刷；只保留控件轮廓阴影。真实 WPF 控制层经 RenderTargetBitmap 渲染，检查三个空白位置的 alpha 为 0，同时要求存在实际绘制的控件像素，避免全空图片误通过。退出影院后原有 Effect 恢复。
- 两个自有、屏幕外、`Opacity=0` 窗口通过 `Window.Activate()` 切换真实激活状态，不伪造 IsActive。非激活进入影院时 Popup 保持关闭；模拟旧浮层开启状态后，生产更新必须关闭它；调用原生宿主 UserActivity 的真实订阅链也不得在后台重开或抢走激活状态。
- 主窗口实际激活时，生产 Activated 事件恢复浮层；不修改最后活动时间，等待生产 DispatcherTimer 的三秒阈值，检查控制栏与鼠标一起隐藏、原生视频活动恢复，以及显隐均不挤占视频布局。
- 对照窗口激活会实际触发主窗口 Deactivated，必须收起浮层。最小化、Hide 后即使发生后台活动也保持关闭；还原和重新激活后恢复。最后退出影院应归还控件父级、标题栏及鼠标状态。

自有不可见控制层始终保持 `Opacity=0`。激活专项另外关闭该控制层的命中测试，避免被系统钳到屏幕内的不可见 Popup 拦截用户鼠标，并保证实际闲置检查不被鼠标恰好悬停干扰；它验证计时器及状态变化，未覆盖用户真实悬停控件时暂停隐藏的体验。

## 完整模式额外检查

- 640×360、950×640 窗口触发右下角按钮的 Click 事件后，外部位置、尺寸、WindowState、WindowStyle、ResizeMode 及 Win32 外框/客户区保持一致；视频使用释放的客户区，标题与侧栏收起。
- 本地媒体实际解码就绪，libmpv OSD 与视频 HWND 客户区同步，保持 360:640 源比例与黑边，keepaspect=yes、panscan=0。
- 通过自有窗口的位置、尺寸和 WindowState 属性检查移动、缩放、最大化与还原后的浮层贴底坐标。
- F11/Esc 路由、原生宿主共用的全屏订阅链、更多菜单快捷键、导航退出和首次 Close 的布局与资源释放。

## 定位与图片边界

WPF Popup 会把离屏目标钳到真实显示器边界。严格定位专项将自有窗口暂放工作区，保持主窗口 `Opacity=0`、视频 Hidden、控制层 `Opacity=0`；生产计时器暂停期间，夹具显式暂开这个不可见 Popup 测量 PointToScreen，然后恢复原状态。这只证明定位，不作为生产后台应显示浮层的证据。

所有 PNG 都来自 WPF 离屏渲染，只展示真实控件和透明像素，不包含原生视频 HWND 或其他窗口像素，没有把媒体图像拼接到控制层。窗口 PNG 的黑色区域也不作为真实媒体画面证据；媒体解码及画面比例依据 libmpv 状态和实际客户区参数。GPU、桌面合成及真实视频上的观看效果仍应结合成品人工检查。
