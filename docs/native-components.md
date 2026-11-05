# 内置播放器运行库来源

## 图片解码（0.3.1 新增）

- 用途：在程序内解码服务端真实 HEIC 海报，转成保持比例的 PNG 缓存；无需系统 HEIF 扩展或外部 ImageMagick 程序。
- NuGet：`Magick.NET-Q8-x64 15.0.0`、`Magick.NET.Core 15.0.0`。
- 来源：[Magick.NET 15.0.0](https://github.com/dlemstra/Magick.NET/releases/tag/15.0.0)，提交 `79bb8700d7fe3a564f62926ab299fc2520cb2269`；包含 ImageMagick 7.1.2-33。
- Q8-x64 包 SHA256：`96BF50FCBC586E8A149B467A97FB3D1BF09C0362B5D8FF828A9936B23FDC5374`；Core 包：`9936D4EA3ED1991A6672056E8DF47B75B43C00AFA884BFF78E136CD2141798B3`。
- 本机历史检查显示，Windows 原生库仅导入 Windows 系统库；PE 直接和延迟导入均未发现 VC++/OpenMP 独立运行时。检查输出已在上传前清理，仓库不包含该报告。
- 保留包内完整 NOTICE 与 Apache-2.0 许可证于 `native/runtime/imaging/`，随发布包保留并嵌入 EXE。

## libmpv

- 构建仓库：<https://github.com/shinchiro/mpv-winbuild-cmake>
- 发布版本：`20261105`
- 压缩包：<https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261105/mpv-dev-x86_64-20261105-git-3138fc8eea.7z>
- 压缩包 SHA256：`F463CC69A7A1610937688B837BE9F005CF163C26F2E35E50AEC8CC7BDE2894B4`
- `native/runtime/mpv/libmpv-2.dll` SHA256：`1D3C4067692048C6CAB7735954E2299664B94C098FCDB8F7BC700E6A6A133ED6`
- 文件大小：121,911,296 字节；PE32+，AMD64。
- 保留对应 `Copyright`、`LICENSE.GPL` 与 `include/mpv/client.h`。
- 上游默认构建按 GPLv2+ 分发；应用分发须保留许可证与相关来源信息。

## Vulkan loader

- 文件：`native/runtime/mpv/vulkan-1.dll`，版本 `1.4.341.0`，1,730,096 字节。
- 本次来源：本机 `C:\Windows\System32\vulkan-1.dll` 的签名有效副本。
- 验证签名：Microsoft Windows Hardware Compatibility Publisher。
- SHA256：`CD63989744D15FE13972511D3AFB7BBC171C7C72FE9C479B4909595A2F9D6EFB`。
- 上游：<https://github.com/KhronosGroup/Vulkan-Loader/tree/v1.4.341>
- 许可证：<https://raw.githubusercontent.com/KhronosGroup/Vulkan-Loader/v1.4.341/LICENSE.txt>，已保存为 `native/runtime/mpv/Vulkan-Loader-LICENSE.txt`。
- 该库是 libmpv 的装载依赖；播放器仍使用 D3D11 输出。

## 依赖与启动方式

PE 导入表核查：libmpv 除 Windows 系统库外依赖 Vulkan loader；loader 自身只依赖 `CFGMGR32.dll`、`KERNEL32.dll`、`ADVAPI32.dll`。

程序从嵌入资源校验并提取这两个 DLL，先加载 loader，再加载 libmpv。播放器使用传入的原生 HWND，不启用 HTTP 或 IPC 监听，也不启动外部播放器进程。

## 本次验证

`native/verification/mpv-smoke` 使用独立 WinForms 窗口验证实际解码。普通 MP4 以及本地生成的 CENC MP4（配独立 M4A 音轨）均通过：360×640 竖屏尺寸、时长、暂停、2 秒精确跳转、2 倍速、35% 音量、1 秒续播、自然 EOF 一次、Stop 与连续切集不触发 EOF。

验证时间：2026-11-05；Windows 10 x64；.NET SDK 9.0.203。测试媒体由本机 FFmpeg 生成，交付播放器不依赖本机 FFmpeg。
