# 首次关闭与资源释放验收

在此目录执行：

```powershell
dotnet run --project ClosingSmoke.csproj -c Release -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4
```

使用生产 MainWindow 的屏幕外、非激活窗口，移除启动目录请求的 Loaded 事件。没有系统输入或 UIAutomation，不操作用户窗口；桥接配置写入本验收目录的独立夹具文件夹，媒体使用本地样片，不请求远端内容。

选集和播放设置弹层的关闭用例改用工作区内的可见自有窗口，通过 Activate 激活，并严格核对真实系统前台的根 owner 与夹具 HWND 一致后再打开菜单。生产菜单受前台约束，离屏且非激活的旧夹具会在发出 Close 前失败；该次 6/7 报告另存为 `closing-before-foreground-fixture.json/.log`，不计为关闭行为失败，也不覆盖原始证据。菜单用例仍严格要求弹层已打开、一次外部 Close 自然完成、无 Dispatcher 异常和无残余资源。

`results/closing-baseline.json` 保存修复前证据：一次 Close 后窗口未触发 Closed，Dispatcher 捕获 `InvalidOperationException`，堆栈为 `Window.VerifyNotClosing → InternalClose → MainWindow.Window_Closing`。当历史保存同步完成时，旧关闭处理在同一轮 Closing 里重新调用 Close，触发 WPF 重入限制。

修复后的 `results/closing-smoke.json` 分别覆盖无后端空闲关闭、失效后端的同步历史返回、真实双解码器与异步观看记录、全屏、选集弹层、播放设置弹层，以及历史写锁占用时再次关闭。

每个正常场景只发起一次外部 Close，并等待真实 Closed 事件；要求 Dispatcher 无异常、计时器停止、相应播放器释放和桥进程退出。历史写锁场景另验证再次 Close 在清理完成前继续取消，松锁后保存本次独立历史文件并自然关闭。报告不以进程强杀或第二次用户点击代替首次关闭成功。
