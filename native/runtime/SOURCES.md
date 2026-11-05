# 内置播放器运行库来源

## libmpv

- 构建仓库：<https://github.com/shinchiro/mpv-winbuild-cmake>
- 发布版本：`20261105`
- 压缩包：<https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261105/mpv-dev-x86_64-20261105-git-3138fc8eea.7z>
- 压缩包 SHA256：`F463CC69A7A1610937688B837BE9F005CF163C26F2E35E50AEC8CC7BDE2894B4`
- `mpv/libmpv-2.dll` SHA256：`1D3C4067692048C6CAB7735954E2299664B94C098FCDB8F7BC700E6A6A133ED6`
- 文件大小：121,911,296 字节；PE32+，AMD64。
- 保留对应 `Copyright`、`LICENSE.GPL` 与 `include/mpv/client.h`。
- 上游默认构建按 GPLv2+ 分发；应用分发须保留许可证与相关来源信息。

## Vulkan loader

- 文件：`mpv/vulkan-1.dll`，版本 `1.4.341.0`，1,730,096 字节。
- 本次来源：本机 `C:\Windows\System32\vulkan-1.dll` 的签名有效副本。
- 验证签名：Microsoft Windows Hardware Compatibility Publisher。
- SHA256：`CD63989744D15FE13972511D3AFB7BBC171C7C72FE9C479B4909595A2F9D6EFB`。
- 上游：<https://github.com/KhronosGroup/Vulkan-Loader/tree/v1.4.341>
- 许可证：<https://raw.githubusercontent.com/KhronosGroup/Vulkan-Loader/v1.4.341/LICENSE.txt>，已保存为 `mpv/Vulkan-Loader-LICENSE.txt`。
- 该库是 libmpv 的装载依赖；播放器仍使用 D3D11 输出。

## 依赖与启动方式

PE 导入表核查：libmpv 除 Windows 系统库外依赖 Vulkan loader；loader 自身只依赖 `CFGMGR32.dll`、`KERNEL32.dll`、`ADVAPI32.dll`。

程序从嵌入资源校验并提取这两个 DLL，先加载 loader，再加载 libmpv。播放器使用传入的原生 HWND，不启用 HTTP 或 IPC 监听，也不启动外部播放器进程。

## 本次验证

`native/verification/mpv-smoke` 使用独立 WinForms 窗口验证实际解码。普通 MP4 以及本地生成的 CENC MP4（配独立 M4A 音轨）均通过：360×640 竖屏尺寸、时长、暂停、2 秒精确跳转、2 倍速、35% 音量、1 秒续播、自然 EOF 一次、Stop 与连续切集不触发 EOF。

验证时间：2026-11-05；Windows 10 x64；.NET SDK 9.0.203。测试媒体由本机 FFmpeg 生成，交付播放器不依赖本机 FFmpeg。
