# 红果原生桌面版

**OpenHongguo Desktop · v0.3.8 · Windows 10 / 11 x64**

一个使用 WPF 原生窗口、内嵌视频播放器的 Windows 短剧客户端。支持搜索找剧、题材筛选、排行榜、连续推荐、选集播放和观看记录。

本项目基于并借鉴 [beilunyang/openhongguo](https://github.com/beilunyang/openhongguo) 二次开发，保留原作者版权与许可证；Windows 原生桌面功能在其 SDK 基础上扩展。

下载完整分发包并解压，双击其中的 `红果原生桌面版.exe` 即可使用。.NET 桌面运行时、SDK 桥和视频组件已内置，使用时无需另装 Edge、mpv、ffplay 或 ffmpeg。

当前 v0.3.8 修复了播放控制栏浮在其他应用上的问题：切换窗口或最小化时收起浮层，后台鼠标活动不再重新打开；沉浸控制栏移除灰色渐变底板。

## 功能

| 功能 | 当前实现 |
|---|---|
| 搜索与发现 | 按剧名或关键词搜索，支持「剧集 / 综合 / 视频」分类和分页；新搜索回到列表顶部 |
| 按题材找剧 | 组合选择平台提供的体裁、主题、人物设定、故事背景等条件 |
| 排行榜 | 剧目逐行展示名次、封面与热度；顶部横向切换榜单，支持题材筛选、加载更多和每日刷新 |
| 连续推荐 | 连续加载候选内容，在视频区域用滚轮或 ↑ / ↓ 切换另一部剧 |
| 播放控制 | 拖动进度、暂停、音量、0.5–3 倍速，以及当前分集实际提供的画质档位 |
| 自动连播 | 提前缓冲同一季的下一集；确认官方合集关系及下一季可播放时，支持同系列跨季连播 |
| 观看记录 | 带封面的并列卡片，保存集数与播放位置，继续上次观看 |
| 窗口适配 | 视频保持比例，支持竖屏和横屏；小窗口收拢侧栏与次要控制，应用不设置最小宽高 |
| 沉浸播放 | 窗口内全屏和屏幕全屏；透明控制栏，静止约 3 秒后隐藏控制栏与鼠标 |
| 封面与统计 | 解码平台真实海报；展示接口提供的播放、追剧、本集点赞和评论数量 |

画质选项随具体分集变化。评论正文、弹幕内容和收藏量尚未接入；接口缺失的统计值显示为 `—`，追剧数与收藏数分别处理。

## 下载与运行

在[本仓库 v0.3.8 Releases](https://github.com/shenxiaobei/openhongguo-desktop/releases/tag/v0.3.8) 页面选择 **Windows x64 完整分发包**，也可直接点击下表下载：

| 文件 | 用途 |
|---|---|
| [OpenHongguo-Desktop-v0.3.8-windows-x64.zip](https://github.com/shenxiaobei/openhongguo-desktop/releases/download/v0.3.8/OpenHongguo-Desktop-v0.3.8-windows-x64.zip) | 使用者下载的完整分发包，包含 EXE、说明和许可证 |
| [OpenHongguo-Desktop-v0.3.8-windows-x64.exe](https://github.com/shenxiaobei/openhongguo-desktop/releases/download/v0.3.8/OpenHongguo-Desktop-v0.3.8-windows-x64.exe) | 单独 EXE；建议下载完整包以保留说明和第三方许可证 |
| [openhongguo-native-runtime-v0.3.7-windows-x64.zip](https://github.com/shenxiaobei/openhongguo-desktop/releases/download/v0.3.7/openhongguo-native-runtime-v0.3.7-windows-x64.zip) | 开发者从源码构建时使用的原生运行库包 |

1. 解压完整分发包，双击 `红果原生桌面版.exe`。
2. 在「发现」「排行榜」或搜索结果中点击剧目，进入播放页。
3. 通过选集、进度条、倍速和画质菜单调整观看；小窗口下部分设置位于「更多」。
4. 退出时保存观看进度，下次从「观看记录」继续。

请保留分发包中的第三方许可证和组件来源说明。GitHub 附件采用英文文件名，完整包内的程序名称保持为 `红果原生桌面版.exe`。

首次启动会校验并释放内置运行组件，耗时可能比后续启动更长。成品以单 EXE 交付，运行时仍会创建组件、封面、配置和历史文件。

## 播放操作

| 操作 | 效果 |
|---|---|
| 空格 | 暂停 / 继续 |
| ← / → | 快退 / 快进 5 秒 |
| 播放器右下角全屏按钮 | 窗口内全屏，视频铺满当前窗口客户区，保留系统标题栏及窗口大小 |
| 标题栏最大化按钮 | 系统窗口最大化 / 还原 |
| F11 或双击视频 | 进入 / 退出屏幕全屏 |
| Esc | 先退出屏幕全屏，再退出窗口内全屏 |
| 视频区域滚轮或 ↑ / ↓ | 在「连续推荐」模式中切换另一部剧 |

两种沉浸模式下，移动鼠标或操作快捷键会重新显示控制栏；拖动进度、悬停控制区或打开菜单时保持可见。视频始终保持原始比例，空余区域保留黑边。窗口可继续缩小，但原生标题栏仍受 Windows 系统边界约束。

## 连播、推荐与排行榜

- **下一集预加载**：开启自动连播时，备用播放器提前静音缓冲并准备首帧，就绪后交接；播放地址失效或准备不足时走正常加载流程。切换其他剧目、关闭连播或退出时取消旧准备。
- **跨季连播**：依据平台返回的官方合集 ID、季序和分集信息确认下一季。当前季完结且末集播放结束后接续；相似剧名不作为续季依据。跨季目前预取关联信息，媒体在切季时加载。
- **推荐来源**：使用平台发现、热门目录和相关推荐接口，内容随匿名设备及服务端响应变化。推荐暂不可用时可显示同频道的热门目录，并注明来源。
- **每日榜单刷新**：应用运行期间每分钟检查本地日期，跨天刷新当前榜单；返回过期榜单也会更新。失败时保留旧列表，约 10 分钟后重试；支持手动刷新。此功能刷新榜单内容，软件版本更新需要另行下载。

## 本地数据与缓存

| 位置 | 内容 |
|---|---|
| `%APPDATA%\openhongguo` | 匿名设备配置、观看记录、目录缓存等 |
| `%LOCALAPPDATA%\OpenHongguo\covers` | 封面图片缓存 |
| `%LOCALAPPDATA%\OpenHongguo\native` | 从 EXE 释放的 SDK 桥和视频运行组件 |
| `%TEMP%\.net\红果原生桌面版` | .NET 单文件程序解包内容 |

视频预加载使用内存，明确关闭磁盘视频缓存。每个播放器的前向媒体缓存上限为 24 MiB，另有 4 MiB 回退缓存；约 12 秒为预读目标，解码帧、音频和 GPU 还会占用额外内存。停止或销毁播放器时释放对应媒体。

封面缓存的 24 小时有效期用于判断是否复用，目前没有统一的过期删除或容量限制；旧运行组件也可能保留。需要重置时先退出应用，再清理上述目录；删除 `%APPDATA%\openhongguo` 会同时清空观看记录和匿名设备配置。

## 技术实现

- **界面**：C# / .NET 9 / WPF，原生 Windows 窗口。
- **播放**：进程内加载 libmpv，将视频输出到原生窗口句柄；内置其所需的视频组件。
- **接口**：基于 OpenHongguo Go SDK，通过私有标准输入输出 JSONL 管道连接内置桥接进程。
- **海报**：Magick.NET 解码，包括平台返回的 HEIC / HEIF / AVIF 图片。
- **分发**：Windows x64、自包含、单文件 EXE；应用运行时不创建本地 HTTP 服务，也不启动浏览器。

当前网络请求沿用匿名设备机制。目录、榜单、画质和可播放状态取决于平台响应，服务端要求验证、限流或调整接口时可能出现相应提示。项目为非官方客户端，与红果官方无隶属关系。

## 从源码构建

构建环境：**Windows x64、Go 1.22+、.NET 9 SDK、PowerShell**，以及恢复 NuGet 依赖所需的网络连接。运行成品无需安装这些开发工具。

源码保留运行库的许可证和头文件，DLL 通过单独的运行库 ZIP 分发。先将 `openhongguo-native-runtime-v0.3.7-windows-x64.zip` 放入源码同级的 `Releases/`，然后在仓库根目录执行：

```powershell
Expand-Archive -LiteralPath '..\Releases\openhongguo-native-runtime-v0.3.7-windows-x64.zip' -DestinationPath '.' -Force
powershell -File scripts/build-native.ps1
```

脚本校验原生 DLL 的 SHA256，构建 Go 桥，再发布包含 .NET 运行时及原生组件的桌面程序。默认产物：

```text
dist/
├─ native-windows-amd64/
│  ├─ 红果原生桌面版.exe
│  ├─ 使用说明.md
│  ├─ 第三方组件来源.md
│  ├─ LICENSE
│  └─ 第三方许可证/
└─ 红果原生桌面版-windows-x64.zip
```

运行库来源、校验值和许可证见[原生桌面版说明](docs/native-desktop.md#源码与构建)及[第三方组件来源](docs/native-components.md)。旧浏览器入口源码与脚本保留供回溯，当前 Windows 桌面版使用 `scripts/build-native.ps1` 构建。

## 源码目录

```text
native/Hongguo.Desktop/    WPF 界面、播放器与桌面状态管理
native/runtime/            原生组件许可证、NOTICE 和 mpv 头文件
native/verification/       桌面功能验证代码
cmd/ohg-native-bridge/      Go SDK 桥入口
internal/nativebridge/     管道协议与 SDK 调用
sdk/                       SDK 与官方系列关联查询
adapters/                  匿名设备持久化等适配逻辑
scripts/                   构建及验证脚本
docs/                      使用、构建与开发文档
bindings/                  保留的多语言 SDK 绑定
```

## 文档

| 文档 | 内容 |
|---|---|
| [原生桌面版说明](docs/native-desktop.md) | 详细操作、预加载、数据行为及构建 |
| [第三方组件来源](docs/native-components.md) | 视频及图片组件版本、哈希与许可证 |
| [界面设计记录](docs/native-ui-design.md) | 桌面界面设计与交互说明 |
| [SDK 开发参考](docs/sdk.md) | 保留的 SDK 用法、方法表与错误表 |
| [SDK 类型参考](docs/types.md) | 查询参数与返回类型 |
| [CLI 参考](docs/cli.md) | 保留的 `ohg` 命令行入口 |

SDK 参考中的方法表和错误表由 `go run ./bindings/docgen` 维护；桌面项目首页单独编写。

## 来源与许可证

本项目基于 [beilunyang/openhongguo](https://github.com/beilunyang/openhongguo) 二次开发，感谢原作者提供 SDK、协议实现和多语言基础。当前仓库在此基础上增加 Windows 原生桌面界面、内嵌播放、预加载、跨季连播和桌面交互等功能。Go 模块路径沿用上游，以保持现有导入兼容。

仓库沿用 [Apache License 2.0](LICENSE)，保留上游版权声明。随程序分发的 libmpv、FFmpeg、Vulkan Loader、Magick.NET 等组件分别遵循各自许可证；其中当前 libmpv 构建按 GPLv2+ 分发，完整组件来源与许可见[第三方组件说明](docs/native-components.md)及分发包内的许可证文件。

短剧、海报等内容的权利归各自权利人所有。
