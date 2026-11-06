# 播放控制栏与字幕避让调研

核对日期：2026-11-05（Asia/Taipei）。本轮只调研及记录，不修改播放器代码、不构建新版本、不上传 GitHub、不删除 v0.3.7。

后续实施状态：v0.3.9 曾采用稳定预留 60 DIP 画面外控制带，并完成当时的 17 项回归与单 EXE 独立启动检查；用户实际反馈认为设置面板过大、隐藏后底部留白影响观感。当前已构建 1.0 本地版，尚未上传 GitHub：沉浸视频宿主占全部客户区，主控使用顶部居中短浮条，显隐不改变视频尺寸；设置改为约 240 DIP 宽、按内容自然测量的行式菜单，同面板选择倍速与画质。1.0 完整播放器回归 20/20 通过、0 异常；最终 EXE 在单独复制、缩减 PATH、外部 DOTNET_ROOT 不存在的条件下正常启动，无本地监听、仅内置桥子进程，一次关闭自然 exit0、无强制清理且相关进程清零。上述验证不代表已验证在线内容或其他电脑，证据位于源码同级 `Releases/verification-v1.0/`，不沿用 v0.3.9 的通过结论。详细交互见[当前源码使用说明](native-desktop.md)。本文件下文保留调研阶段的事实、建议和当时边界；GitHub 更新及 v0.3.7 删除继续暂停。

## 结论

底板透明只能去掉底色，按钮、文字和进度线依然占据画面像素。成熟播放器主要采用精简控件、自动或局部唤醒、独立字幕层避让，以及可选的画面外控制区。没有证据支持将“每次唤出控件就缩放视频”当作这些播放器的共同默认行为。

用户截图中的字幕看起来已烧录进视频；本轮没有读取该片源的字幕轨道，不能仅凭截图确认媒体封装。对于烧录字幕，字幕位置选项不改变视频像素中的文字；操作时也要求完全不遮挡，应让控件矩形与视频显示矩形不相交。

## 核实到的实现

| 产品及核对版本 | 控件与字幕处理 | 可借鉴之处及边界 |
|---|---|---|
| mpv OSC，本项目打包构建对应的 `3138fc8eea22f7898f91208bb6ea53514db29ba5` | `deadzonesize=0.75` 定义鼠标唤醒死区；`boxvideo=yes` 才让视频避开控制区域；`dynamic_margins` 可随显隐改变边距，默认关闭；`sub_margins` 调整独立字幕边距 | 局部唤醒、稳定的画面外控制区可分别选择。动态缩放属于选项。当前项目 `osc=no`、`load-scripts=no`，这些配置不会直接控制 WPF 控件 |
| uosc，`074b0869c7d758306d7b29d13f59801b73caaee2` | 控件按鼠标接近距离分别淡入淡出，默认接近 40 px 内完全显示、120 px 外淡出；最小进度线 2 px；控制按钮 32 px；动画 100 ms | 中间区域移动不必唤出整排控件。其背景透明度不影响文字，透明背景仍可能压住烧录字幕；并非统一三秒自动隐藏 |
| VLC 3.0.21 Qt | 普通窗口把视频与输入/控制区放在主布局的不同位置；全屏控制器独立浮动，支持位置记录、拖动、整行贴底、计时隐藏及失活收起 | 区分窗口与全屏交互，允许浮条移位。不能由此宣称全屏浮条会自动避开视频里的烧录字幕 |
| Video.js v8.23.4 | 控制栏在视频底部绝对定位；独立字幕显示层随控件状态改变底部空间，从 `1em` 到 `3em`；控件隐藏时关闭指针命中 | 移动独立字幕而非反复改变视频尺寸。其字幕层规则不搬动烧录字幕 |
| Plyr v3.7.8（历史实现） | 控件出现时独立字幕上移；默认将画质与倍速归入 Settings；隐藏时淡出且关闭命中 | 精简当前两块常驻大下拉框。Plyr 网站现提示旧项目仅维护安全更新并推荐 Video.js 10，旧版本作为交互实现参考 |

## 网页现场观察及限制

- 浏览器访问 [Plyr 当前首页](https://plyr.io/)，现场页面说明已转向 Video.js 10。示例视频实际播放，`readyState=4`，随后已暂停。
- 在该示例中观察到控件淡出到 `opacity=0`、`pointer-events=none`；鼠标返回播放器后控件出现，视频显示尺寸仍为 480×270。打开设置可见 Quality、Speed、Captions 三个菜单项，没有两个常驻的画质和倍速下拉框。
- 浏览器打开设置时发生页面滚动，视频屏幕 Y 坐标改变；宽高不变。不能将页面滚动误作视频缩放。现场没有对比字幕位置或进入全屏，字幕避让结论分别来自固定版本源码。
- YouTube 播放页面加载超时，没有把它记为 UI 实测。YouTube、Netflix 官方帮助可确认字幕样式属于独立字幕功能，但没有据此推断当前桌面端的自动避让行为，也没有登录 Netflix 播放。
- mpv、uosc、VLC 的结论为官方文档或源码核对，本轮没有运行它们的 GUI。没有操作用户的微信或读取用户账户。

观察截图保存在源码同级 `Releases/player-controls-research/`：`videojs-demo-idle.png` 与 `videojs-demo-settings-open.png`。它们是网页示例截图，不是本项目改版预览。

## 对当前项目的建议

1. 主栏精简为播放/暂停、上一集/下一集、时间、细进度、音量、设置和全屏。倍速、画质、自动连播收进设置，保留快捷操作入口。
2. 沉浸播放采用 uosc 式局部唤醒：鼠标接近底部控制区域才显示操作栏，视频中央移动不再弹出整排按钮；显示动画与隐藏命中状态同步。键盘焦点、拖动进度和菜单打开时保持可操作。
3. 将“字幕保护”与“浮动控制”明确区分。字幕保护布局优先利用画面外黑边；空间不足时预留稳定的底部控制带，控件显隐不改变视频尺寸。视频保持比例且不裁切，字幕和画面保持完整。
4. 偏好最大画面时可用紧凑浮条，并借鉴 VLC 允许拖动到合适位置、记忆位置。它降低遮挡概率，但不保证任何位置的烧录文字都无遮挡。动态缩放可后续作为明确选项，不作为默认鼠标行为。
5. 保留 v0.3.8 的前台范围限制，切走、最小化和隐藏时收起所有控制浮层；不能因换布局丢掉此前修复。

下一步应先做本地预览，重点用最底部烧录两行字幕的样片验证：控件矩形与实际视频显示矩形不相交；控件显隐前后视频尺寸稳定；菜单、缩放、窗口内全屏、F11/Esc 及后台收回仍正确。本轮尚未实现或验证新布局。

## 来源

- [mpv OSC 唤醒死区](https://github.com/mpv-player/mpv/blob/3138fc8eea22f7898f91208bb6ea53514db29ba5/DOCS/man/osc.rst#L231)
- [mpv boxvideo、dynamic_margins 与 sub_margins](https://github.com/mpv-player/mpv/blob/3138fc8eea22f7898f91208bb6ea53514db29ba5/DOCS/man/osc.rst#L389)
- [mpv OSC 实际边距计算](https://github.com/mpv-player/mpv/blob/3138fc8eea22f7898f91208bb6ea53514db29ba5/player/lua/osc.lua#L650)
- [uosc 控件及时间轴配置](https://github.com/tomasklaen/uosc/blob/074b0869c7d758306d7b29d13f59801b73caaee2/src/uosc.conf#L1)
- [uosc 接近距离与透明度说明](https://github.com/tomasklaen/uosc/blob/074b0869c7d758306d7b29d13f59801b73caaee2/src/uosc.conf#L155)
- [VLC 普通窗口布局](https://github.com/videolan/vlc/blob/3.0.21/modules/gui/qt/main_interface.cpp#L484-L505)
- [VLC 全屏控制器](https://github.com/videolan/vlc/blob/3.0.21/modules/gui/qt/components/controller.cpp#L792-L958)
- [VLC 失活收起](https://github.com/videolan/vlc/blob/3.0.21/modules/gui/qt/components/controller.cpp#L1084-L1096)
- [Video.js 字幕显示层](https://github.com/videojs/video.js/blob/v8.23.4/src/css/components/_text-track.scss#L1-L20)
- [Video.js 控制条](https://github.com/videojs/video.js/blob/v8.23.4/src/css/components/_control-bar.scss#L1-L46)
- [Plyr 历史字幕上移实现](https://github.com/sampotts/plyr/blob/v3.7.8/src/sass/components/captions.scss#L36-L42)
- [Plyr 历史默认控件和设置项](https://github.com/sampotts/plyr/blob/v3.7.8/src/js/config/defaults.js#L128-L147)
- [当前 Video.js 示例入口及迁移说明](https://plyr.io/)
- [YouTube 官方字幕设置](https://support.google.com/youtube/answer/100078?hl=en)
- [Netflix 官方字幕样式设置](https://help.netflix.com/en/node/100267)
