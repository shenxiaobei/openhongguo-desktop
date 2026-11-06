# 搜索联想回归

复用真实 SDK `Suggest` 接口，经私有 stdio 桥返回显示文本；不用搜索第一页猜测联想，也不把直达内容 ID 当作剧名。

在本目录运行：

```powershell
dotnet run --project SearchSuggestionsSmoke.csproj -c Release -p:RuntimeFrameworkVersion=9.0.4 -p:GenerateSupportedTargetFrameworkAlias=false
```

夹具使用生产 MainWindow、实际 JSONL BackendClient 和独占临时目录中的假后端，不访问线上内容、不读取用户配置。测试有可见的自有窗口，需与其他前台测试串行。覆盖防抖、重复前缀缓存、旧词响应隔离、空词与输入法组合态、键盘与鼠标选词、未选词回车、空结果与错误、失焦/后台/尺寸变化/隐藏/关闭收起，以及窄窗下拉几何。结果写入 `results/search-suggestions-smoke.json`。界面截图不包含原生视频或桌面合成，不代表在线联想服务可用性或其他机器 DPI 验收。

生产联想在真实系统前台检查通过时才发起或显示。夹具对自己的窗口请求前台并严格确认，若系统拒绝或运行中其他程序成为前台，保留失败身份诊断而不去掉生产检查。输入法用例验证组合状态抑制分支，不代表已验证每种输入法软件的实际候选窗交互。鼠标和键盘用例经过生产路由事件，但不向操作系统注入输入。

初轮仅 WPF 激活状态下的 11 项通过是历史证据；随后加上生产真实前台检查的源码需独立验证。`search-suggestions-first.json`、`search-suggestions-native-diagnostic.json`、`search-suggestions-foreground-*.json` 与对应日志保留初始失败/前台身份观察，后续通过也不覆盖这些报告。旧报告缺少身份的失败仍保留原因未确认，不能按重跑结果推断。`search-suggestions-menu.png` 为最终测试渲染的下拉控件样式。
