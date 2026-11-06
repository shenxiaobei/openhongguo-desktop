# 红果桌面版 1.0 · 原生播放器布局与控制栏回归

使用生产 `MainWindow`、WPF Popup 和 libmpv 验证窗口内全屏、屏幕全屏、顶部紧凑浮条、行式播放设置及后台浮层生命周期。测试不连接 SDK 后端，不读取或操作用户的其他应用。

## 运行

先按仓库构建说明准备原生组件。完整检查另需 `native/verification/mpv-smoke/sample.mp4`（360×640）和 `sample-landscape.mp4`（1280×720）；可使用底部带两行烧录字幕的本地样片。

在本目录运行：

```powershell
dotnet run --project WindowCinemaSmoke.csproj -c Release
```

仅检查显隐、接近唤醒及后台生命周期，不加载媒体：

```powershell
dotnet run --project WindowCinemaSmoke.csproj -c Release -- --overlay-only
```

退出码 0 表示本次检查通过，报告分别写入 `results/window-cinema-smoke.json` 与 `results/overlay-only-smoke.json`。失败后的兜底回收不计作自然关闭通过。

仅排查设置快捷键与父级恢复可使用 `--options-only`；使用 `--options-after-overlay` 则先运行浮层生命周期，再执行相同快捷键项，便于复现连续测试中的状态。两者均不加载媒体，结果写入 `results/options-only-smoke.json`。Esc 先确认设置关闭，再等待真实 `Popup.Closed` 和状态归还，最后严格断言影院与全屏模式；固定延时不能代表淡出生命周期完成。失败诊断记录真实前台、主窗口激活状态及生产前台判断。

人工视觉预览使用同一个生产窗口和本地字幕样片，暂停在首帧，不启动后端；关闭窗口结束预览，此模式不生成回归通过报告：

```powershell
dotnet run --project WindowCinemaSmoke.csproj -c Release -- --visual-preview
```

## 证据范围

- 真实媒体检查读取 libmpv 解码状态、原生窗口客户区、OSD 尺寸及黑边。沉浸视频填满客户区，底部布局行高度为零；顶部浮条不进入底部字幕区域，显隐时视频尺寸保持稳定。另加载 1280×720 横屏媒体，根据真实解码画面扣除黑边后验证底部字幕区域。普通和沉浸布局覆盖 320×240、480×320、784×517、1280×800 等窗口；普通底栏仍与视频分开。
- 设置检查通过实际行式菜单和选项按钮操作原有状态源；主栏/菜单音量及倍速核对真实播放器。原 ComboBox 始终隐藏，不打开旧下拉浮层；画质检查确认按钮选中原始档位，不冒充已验证在线重载或分辨率切换。
- 根菜单宽约 240 DIP、自然高度，普通底栏向上展开，顶部浮条下的菜单向下展开，均限定于客户区。根页、倍速页及画质页在同一 Popup 内切换；选择后收起，关闭和后台回收保留状态。
- 菜单状态回归替换原画质列表后再点击旧按钮，确认失效项不会覆盖新选择；直接调用生产 `PlayEpisodeAsync` 入口验证先收起旧菜单，此项不连接后端，不代表在线连续播放验收。主栏齿轮上的 Esc 仅收起菜单，保留沉浸模式。
- 浮层定位专项使用工作区内自有透明窗口并隐藏原生视频，避免系统对离屏 Popup 的位置钳制；显式暂开不可见 Popup 只用于测量，不计作生产唤醒。
- 后台回归在两个自有透明窗口间调用 Activate，验证真实激活/失活、最小化、隐藏、设置选项页回收和生产 DispatcherTimer 的约三秒隐藏。
- 顶部与底部接近唤醒通过生产指针事件及坐标转换链验证，中央活动仅恢复指针；键盘通过生产活动/路由链验证，没有向操作系统注入键鼠。交互保护检查提前过期活动时间，独立于完整等待的三秒检查。
- PNG 来自 WPF `RenderTargetBitmap`，包含实际控件布局和样式，不包含原生视频像素。`settings-menu-root.png`、`settings-menu-speed.png`、`settings-menu-quality.png` 与 `overlay-compact-controls.png` 用于视觉审查。透明浮条专项要求背景和外框画刷 alpha 均为 0，PNG 四角及中部空白采样 alpha 为 0，且超过半数像素完全透明；同时分别检查播放按钮、时间文字、进度轨道区域具有可见及不透明像素，避免整张空图误过。阴影仅来自控件内容，不允许大面积底板或阴影覆盖画面。字幕保护依据是实际几何与媒体状态，不能将这些 PNG 当作真实桌面视频合成截图。

测试覆盖当前机器和当前 DPI，不代替用户实际观感、其他显示缩放或多显示器验收。多个需要激活自有窗口的检查应顺序执行，避免互相改变激活状态。

2026-11-06 最终批次发现旧夹具仅等待 WPF `IsActive`，系统前台却仍属于另一个 HWND，导致菜单与浮层严格前台保护生效后出现 9 项前置条件失败。原始报告保存在 `Releases/verification-v1.0/resize-fit/cinema/cinema-before-native-foreground-fixture.json/.log`。现 `ActivateOwnWindow` 对夹具自有窗口调用 `SetForegroundWindow`，并要求 `GetAncestor(GetForegroundWindow(), GA_ROOTOWNER)` 与目标 HWND 一致，仍同时检查 WPF 激活及对照窗失活；没有放宽生产判断或原有行为断言。随后完整 20 项严格通过，0 Dispatcher 异常，证据为同目录 `cinema-native-foreground-final.json/.log`。初轮失败前台来源没有身份记录，不归因于用户输入。

透明浮条预览的新版证据另存于 `Releases/verification-v1.0/transparent-preview/`，不覆盖上述半透明浮条的历史报告。此预览仍严格验证顶部控件与底部字幕的几何分离、三秒隐藏、鼠标恢复和跨应用后台约束；结果以该目录实际生成报告为准。

透明预览另加 `--visible-foreground` 路线：主窗口与同进程对照窗口均放在工作区，`Opacity=1`、`ShowInTaskbar=true`，保留原有严格系统前台、三秒、后台与资源释放断言；不修改生产代码。结果另存 `results/visible-foreground-smoke.json`，不覆盖离屏透明夹具的前台失败。此模式只改变夹具主窗口前置，控制层测量仍暂开不可见 Popup，不能将其截图冒充桌面合成。
