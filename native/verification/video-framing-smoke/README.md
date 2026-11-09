# 画面模式定向验证

使用生产 MainWindow 菜单、真实双播放器和本地横竖屏样片，验证默认完整显示、用户选择的拉伸铺满、切集重载、新建预加载实例、实际缓存播放器交接、恢复完整显示与窗口缩放。完整显示要求 `keepaspect=yes`、`panscan=0`，保留片源比例和全画面；拉伸铺满要求 `keepaspect=no`、`panscan=0`，按视频客户区宽高分别缩放，四边和烧录字幕均保留，画面比例可随窗口变化。原生解码器属性和 OSD 几何作为证据，不使用 WPF 截图推断原生视频。

组合回归先从菜单选择拉伸铺满，再分别进入窗口内全屏、F11 和系统最大化，要求保留用户选择并同步两个解码器，不再自动切回完整显示。窗口内全屏严格保持原生外框、位置、标题栏与可缩放样式；退出 F11 或还原最大化也严格比较原生外框。125% DPI 下，WPF 的 254 DIP 请求值可能回写为同一像素外框的 254.4 DIP，因此逻辑尺寸只允许一物理像素以内的取整差，原生像素外框仍必须完全相同。

本轮增加用户截图比例 1920×1001 的定向路径：按原生视频客户区与外框差值，请求 960×501 的较小视频视口，记录实际客户区和外框，不强制创建超出显示器的 1920 像素宽外窗。随后请求 642×373 物理外框。横竖屏、主用与备用解码器均检查拉伸后四边、两行字幕及零裁切边距。

两种样片需带黄色四边框与底部两行白色烧录字幕。组合回归通过 libmpv `screenshot-to-file ... window` 导出**真实原生视频输出**，检查输出尺寸、四边标记和两行字幕像素均保留。该截图包括实际视频缩放和黑边，不含 WPF 浮条或桌面合成。样片均为单色本地生成，不包含线上内容。

从本目录执行：

```powershell
dotnet run --project VideoFramingSmoke.csproj -c Release
```

需要源码构建组件与 `../mpv-smoke/sample.mp4`（360×640）、`sample-landscape.mp4`（1280×720）。样片建议至少 60 秒，确保播放中的多尺寸双实例检查未提前结束。输出 `results/video-framing-smoke.json`。本地窗口可见，菜单前置条件同时检查 WPF 激活与原生前台根所有者；不启动 SDK 后端、不读取线上内容、不创建观看记录，应与其他前台窗口验证串行运行。

当前台条件受执行环境限制时，可显式运行直接调用模式，独立验证生产画面切换、双实例和原生渲染链：

```powershell
dotnet run --project VideoFramingSmoke.csproj -c Release -- --direct-framing
dotnet run --project VideoFramingSmoke.csproj -c Release -- --resize-only --direct-framing
```

`--direct-framing` 让 `SelectFraming` 直接调用生产 `SetVideoFillMode(bool)` 并等待布局稳定，跳过菜单与前台检查。所有用例名添加 `direct_` 前缀，原有 `menu_` 前缀会被移除；报告明确记录 `interactionMode=direct-production-call`，分别写入 `results/video-framing-direct-smoke.json`、`results/resize-direct-smoke.json`，截图使用 `direct-` 文件名前缀。它的通过只证明直接调用后的状态、几何与视频输出，不证明菜单点击、前台浮层或桌面合成通过。未加该参数时继续使用原 GUI 菜单路径和原报告文件，供前台条件恢复后重测；两种模式证据分开保留。

642×373 缩窗回归从本目录执行：

```powershell
dotnet run --project VideoFramingSmoke.csproj -c Release -- --resize-only
```

该模式使用可见的本地测试窗口，覆盖完整显示与拉伸铺满、普通播放与窗口内全屏、横竖屏、暂停与播放中的 16 组组合。通过 `SetWindowPos` 请求 1280×800、800×500、660×400、642×373、640×360、620×340、480×300、320×240、再次 642×373 和恢复 1000×650 的实际物理外框，并单独请求 642×373 DIP，报告同时记录系统 DPI 与实际外框。逐尺寸比较双解码器输出、视频子窗口客户区，以及全部 HWND 祖先客户区与 Region 的可见交集，确认完整画面实际处于窗口可见范围中。另单列从菜单选择拉伸铺满后缩至 642×373 的路径，要求缩窗后双实例继续拉伸铺满且保留四边和字幕。

输出为 `results/resize-smoke.json` 与 `resize-*.png`。完整显示和拉伸铺满均对主用、备用实例执行四边黄色标记的几何与覆盖率断言；当样片字幕投影字高不足 10 像素时，两行文字分别使用抗锯齿亮度采样，避免将极小画面的亮度变化误判为裁切。该采样只证明两行文字仍进入输出，不保证极小字号的可读性。截图仍为原生视频输出，覆盖 UI 与桌面合成需另行检查。

历史缩窗修复的基线及其图片覆盖限制记录在项目证据归档内，不能当作本轮拉伸铺满的通过结果。`--resize-only` 不执行预加载实例交接；交接由默认运行模式验证。本轮是否通过，以重新运行生成的 JSON、日志与 PNG 为准。

样片可用本机 FFmpeg 创建：

```powershell
ffmpeg -f lavfi -i 'color=c=0x273448:s=360x640:r=24:d=60' -vf "drawbox=x=0:y=0:w=iw:h=ih:color=yellow:t=5,drawtext=fontfile='C\:/Windows/Fonts/msyh.ttc':text='字幕上行完整显示':fontcolor=white:fontsize=22:x=(w-tw)/2:y=h-72,drawtext=fontfile='C\:/Windows/Fonts/msyh.ttc':text='底部第二行文字':fontcolor=white:fontsize=22:x=(w-tw)/2:y=h-38" -c:v libx264 -pix_fmt yuv420p -y '../mpv-smoke/sample.mp4'
ffmpeg -f lavfi -i 'color=c=0x273448:s=1280x720:r=24:d=60' -vf "drawbox=x=0:y=0:w=iw:h=ih:color=yellow:t=8,drawtext=fontfile='C\:/Windows/Fonts/msyh.ttc':text='横屏字幕上行完整显示':fontcolor=white:fontsize=36:x=(w-tw)/2:y=h-100,drawtext=fontfile='C\:/Windows/Fonts/msyh.ttc':text='底部第二行文字':fontcolor=white:fontsize=36:x=(w-tw)/2:y=h-52" -c:v libx264 -pix_fmt yuv420p -y '../mpv-smoke/sample-landscape.mp4'
```
