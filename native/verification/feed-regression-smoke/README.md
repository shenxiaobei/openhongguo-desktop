# 连续推荐与跨季回归

在此目录运行：

```powershell
dotnet run --project FeedRegressionSmoke.csproj -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4
```

该测试创建屏幕外的生产 WPF MainWindow；使用测试进程自己的 JSONL 后端子进程及现有 `../mpv-smoke/sample.mp4`，不访问线上内容、不修改用户配置或历史。测试通过反射替换 BackendClient 的自有管道进程，保留生产 JSONL 解析、异常码、取消与实际 libmpv 解码路径，未增加生产测试接口。

验证推荐候选不可用时的有界继续、播放信息错误分类、重复点击单一在途请求、导航取消、跨频道队列/游标清理、错误后的原页面状态与明确失败占位、错误码保留及数据来源提示。网络、验证、限流、设备错误不会自动跳过候选。

跨季验证通过真实本地样片自然 EOF，检查相邻季 ID、首个可播集、自动连播开关、未上线季与分集、错误不换无关推荐、取消和旧响应隔离，以及当前剧集/合集/季序不匹配时停止。合成季数据仅用于状态机回归；真实官方季关联可信度由 bridge 的独立协议与线上验证覆盖。

结果保存为 `results/feed-regression.json`，不记录媒体 URL 或密钥。每个用例使用独立的 WPF Dispatcher 进程，测试窗口禁用输入法，避免长批次重复创建窗口时触发宿主 TSF 初始化问题；每项原始结果单独保留为 `results/case-*.json`。
