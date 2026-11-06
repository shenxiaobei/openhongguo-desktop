# 连续推荐与跨季回归

在此目录运行：

```powershell
dotnet run --project FeedRegressionSmoke.csproj -p:GenerateSupportedTargetFrameworkAlias=false -p:RuntimeFrameworkVersion=9.0.4
```

该测试创建屏幕外的生产 WPF MainWindow；使用测试进程自己的 JSONL 后端子进程及现有 `../mpv-smoke/sample.mp4`，不访问线上内容、不修改用户配置或历史。测试通过反射替换 BackendClient 的自有管道进程，保留生产 JSONL 解析、异常码、取消与实际 libmpv 解码路径，未增加生产测试接口。

验证推荐候选不可用时的有界继续、播放信息错误分类、重复点击单一在途请求、导航取消、跨频道队列/游标清理、错误后的原页面状态与明确失败占位、错误码保留及数据来源提示。网络、验证、限流、设备错误不会自动跳过候选。

`manual_next_decodes_before_delayed_history_reply` 关闭自动连播并禁用备用预加载，手动切到第二集；假后端先确认旧集历史请求已进入等待，再检查新集能否在历史响应释放前请求媒体并解码首帧。观察结束后始终释放历史请求，同时验证新旧两集记录均保留。每个窗口的配置目录与后端工作目录都指向该用例独占的临时目录，结束时清理。

`quality_switch_keeps_pause_and_position_before_delayed_history_reply` 使用同一延迟机制，在暂停并定位到第 2 秒后切换画质，检查历史响应释放前已重新解码、暂停状态与播放位置保持。

`rapid_next_history_writes_remain_ordered` 只卡住最早的一条旧集历史响应，依次播放第二、三集并暂停首帧，媒体加载不等待历史完成；释放旧请求后要求最后持久化的集数仍为第三集。该假后端允许后发请求先完成，因此能暴露仅在单次播放调用内等待、却没有全局历史顺序的遗漏。

`close_after_navigation_waits_for_queued_history` 在导航停止已清空当前剧集后关闭窗口；独立后端延迟释放此前的历史写入，验证窗口在记录排空前保持存活，并在自然关闭后检查夹具临时目录中的持久化记录。文件仅保存本地合成集数，不读取用户数据。

跨季验证通过真实本地样片自然 EOF，检查相邻季 ID、首个可播集、自动连播开关、未上线季与分集、错误不换无关推荐、取消和旧响应隔离，以及当前剧集/合集/季序不匹配时停止。合成季数据仅用于状态机回归；真实官方季关联可信度由 bridge 的独立协议与线上验证覆盖。

结果保存为 `results/feed-regression.json`，不记录媒体 URL 或密钥。每个用例使用独立的 WPF Dispatcher 进程，测试窗口禁用输入法，避免长批次重复创建窗口时触发宿主 TSF 初始化问题；每项原始结果单独保留为 `results/case-*.json`。
