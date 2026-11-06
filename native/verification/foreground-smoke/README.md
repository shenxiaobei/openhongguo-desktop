# 跨进程前台与浮层原生层级验证

此夹具补充 `window-cinema-smoke` 的同进程窗口切换测试，验证生产 `MainWindow` 的播放浮层与设置菜单不会压在其他前台进程上。

```powershell
dotnet build .\ForegroundSmoke.csproj -c Release --ignore-failed-sources -p:RuntimeFrameworkVersion=9.0.4
.\bin\Release\net9.0-windows\win-x64\ForegroundSmoke.exe
```

编译前需按原项目构建说明生成 `native/Hongguo.Desktop/Resources/ohg-native-bridge.exe`。本夹具移除生产窗口的 `Loaded` 后端订阅，不启动 SDK、网络请求或媒体解码器，不修改观看历史。运行时会短暂显示两个自有普通窗口，其中对照窗口由同一验证程序的 `--helper` 模式在另一个进程创建；测试后自动关闭。

验证内容：

1. 使用实际 `HwndSource` 检查播放浮层和设置菜单的 HWND owner 为主窗口，扩展样式无 `WS_EX_TOPMOST`。
2. 通过受控标准输入输出请求对照进程激活，验证实际 `GetForegroundWindow`、浮层及菜单关闭、后台活动不重开及局部鼠标恢复。
3. 将主窗口 Dispatcher 短暂阻塞 500ms，同时由独立对照进程取得真实前台并采样原生 Z 序。此时即便主线程尚未处理失活消息，仍要求浮层已隐藏，或为非置顶并在前台窗口之后。
4. 回到播放器前台后，验证控制条、设置菜单及键盘焦点仍可操作，拥有的菜单不会被误判为其他应用。

结果写入 `results/foreground-smoke.json`。首次修正前的实际失败记录另存 `foreground-smoke-before-native-scope-fix.json`，不覆盖或视为通过。原有同线程透明窗口检查不足以覆盖原生置顶窗口在跨进程消息延迟期间的系统层级。

范围限制：此测试检查 HWND 可见性、owner、扩展样式、真实前台与 Z 序，不截取桌面、不操作用户其他应用，也不包含视频画面合成、在线内容或其他电脑的验证。

## 前台变化诊断

报告的 `observations` 保留主窗口激活/失活、原生前台变化，以及后台 Reveal、指针与定时更新前后的时间和状态。只记录 HWND、PID、主/对照/其他进程角色、根 owner、WPF 激活、菜单注册与可见状态及原生浮层信息，不读取其他应用窗口标题。失败条目也保留当时同一状态，便于区分生产抢焦点、外部前台变化和菜单生命周期。

2026-11-06 窗口完整显示修订的一次首轮出现两项前台/菜单失败，原报告保留为 `cinema-fit-initial-foreground-observation.json`。旧报告缺少失败时的前台身份，原因尚未确认，不能据此断言是外部输入或生产焦点竞争。随后第一次带身份诊断的严格 4 项全部通过，记录为 `cinema-fit-foreground-diagnostic.json`；事件链仅含主进程与对照进程。诊断未修改生产代码或放宽原断言，不以重复运行结果替代首轮缺失的证据。
