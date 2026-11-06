# 画面模式定向验证

使用生产 MainWindow 菜单、真实双播放器和本地横竖屏样片，验证默认完整显示、铺满时保持比例并裁切边缘、切集重载、新建预加载实例、实际缓存播放器交接、恢复完整显示与窗口缩放。原生解码器属性和 OSD 几何作为证据，不使用 WPF 截图推断原生视频。

组合回归先从菜单选择裁切，再分别进入窗口内全屏、F11 和系统最大化，要求恢复完整显示并同步两个解码器。窗口内全屏严格保持原生外框、位置、标题栏与可缩放样式；退出 F11 或还原最大化也严格比较原生外框。125% DPI 下，WPF 的 254 DIP 请求值可能回写为同一像素外框的 254.4 DIP，因此逻辑尺寸只允许一物理像素以内的取整差，原生像素外框仍必须完全相同。

两种样片需带黄色四边框与底部两行白色烧录字幕。组合回归通过 libmpv `screenshot-to-file ... window` 导出**真实原生视频输出**，检查输出尺寸、四边标记和两行字幕像素均保留。该截图包括实际视频缩放和黑边，不含 WPF 浮条或桌面合成。样片均为单色本地生成，不包含线上内容。

从本目录执行：

```powershell
dotnet run --project VideoFramingSmoke.csproj -c Release
```

需要源码构建组件与 `../mpv-smoke/sample.mp4`（360×640）、`sample-landscape.mp4`（1280×720）。输出 `results/video-framing-smoke.json`。本地离屏窗口仅在本轮验证期间激活，不启动 SDK 后端、不读取线上内容、不创建观看记录；应与其他前台窗口验证串行运行。

642×373 缩窗回归从本目录执行：

```powershell
dotnet run --project VideoFramingSmoke.csproj -c Release -- --resize-only
```

该模式使用可见的本地测试窗口，覆盖普通播放与窗口内全屏、横竖屏、暂停与播放中的组合。通过 `SetWindowPos` 请求 1280×800、800×500、660×400、642×373、640×360、620×340、480×300、320×240、再次 642×373 和恢复 1000×650 的实际物理外框，并单独请求 642×373 DIP，报告同时记录系统 DPI 与实际外框。逐尺寸比较双解码器输出、视频子窗口客户区，以及全部 HWND 祖先客户区与 Region 的可见交集，确认完整画面实际处于窗口可见范围中。另单列从“铺满窗口（裁切）”缩至 642×373 的路径，要求缩窗恢复双实例完整显示。

输出为 `results/resize-smoke.json` 与 `resize-*.png`。四边黄色标记的几何与覆盖率断言始终生效；当样片字幕投影字高不足 10 像素时，两行文字分别使用抗锯齿亮度采样，避免将极小画面的亮度变化误判为裁切。该采样只证明两行文字仍进入输出，不保证极小字号的可读性。截图仍为原生视频输出，覆盖 UI 与桌面合成需另行检查。修复前有效基线保存在 `resize-smoke-before-fix.json/.log`；首轮固定白色像素阈值导致的极小字幕误报另存于 `resize-smoke-baseline-initial.json/.log`。

本轮修前 PNG 已被同名修后输出覆盖，未单独保留；修前结论依赖冻结 JSON/日志内的 `panscan`、边距与实际可见交集数值。现有 `resize-*.png` 均为最终修后输出，即使修前报告的 `artifact` 字段仍引用同名生成路径，也不代表该图片保留了修前状态。`--resize-only` 不执行预加载实例交接；原始报告通用 `scope` 中的交接说明属于默认完整模式。

样片可用本机 FFmpeg 创建：

```powershell
ffmpeg -f lavfi -i 'color=c=0x273448:s=360x640:r=24:d=8' -vf "drawbox=x=0:y=0:w=iw:h=ih:color=yellow:t=5,drawtext=fontfile='C\:/Windows/Fonts/msyh.ttc':text='字幕上行完整显示':fontcolor=white:fontsize=22:x=(w-tw)/2:y=h-72,drawtext=fontfile='C\:/Windows/Fonts/msyh.ttc':text='底部第二行文字':fontcolor=white:fontsize=22:x=(w-tw)/2:y=h-38" -c:v libx264 -pix_fmt yuv420p -y '../mpv-smoke/sample.mp4'
ffmpeg -f lavfi -i 'color=c=0x273448:s=1280x720:r=24:d=8' -vf "drawbox=x=0:y=0:w=iw:h=ih:color=yellow:t=8,drawtext=fontfile='C\:/Windows/Fonts/msyh.ttc':text='横屏字幕上行完整显示':fontcolor=white:fontsize=36:x=(w-tw)/2:y=h-100,drawtext=fontfile='C\:/Windows/Fonts/msyh.ttc':text='底部第二行文字':fontcolor=white:fontsize=36:x=(w-tw)/2:y=h-52" -c:v libx264 -pix_fmt yuv420p -y '../mpv-smoke/sample-landscape.mp4'
```
