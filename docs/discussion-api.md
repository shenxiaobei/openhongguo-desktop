# 评论与滚动弹幕

这两个读取方法是本项目在现有 Go SDK 上新增的封装，并非红果发布的官方 SDK。桌面界面通过内嵌 JSONL 桥调用，无需浏览器或本地 HTTP 服务。

| 内容 | 来源与方法 | 范围 |
|---|---|---|
| 评论 | `GET https://hongguoduanju.com/player/{seriesID}/{episodeID}`；`Client.WebEpisodeComments` | 解析官方网页 `_ROUTER_DATA` 中匹配剧集与分集的 `episodeSocialInfo.reviews`；仅公开预览，无完整分页 |
| 弹幕 | `POST /novel/commentapi/comment/list/{episodeID}/v1/`；`Client.Danmaku` | 沿用 SDK 默认 API 主机、游客身份和 POST 签名；真实正文及毫秒时间轴，每次最多请求 90 条 |

评论网页不附加 App Cookie 或账号凭据。弹幕请求类型为 `comment_source=601`、`server_channel=1000`、`group_type=30`、`comment_type=20`；业务参数携带剧集 ID、实际视频时长及时间段起点。弹幕不是 `barrage_mask` 遮罩数据。

`DiscussionPage` 返回 `items`、可未知的 `total`、`cursor`、`has_more`、`source_limited`、`source` 与说明。弹幕 `position_ms` 为服务端毫秒时间；公开评论保留作者、正文、时间和可用点赞量。未知计数为 `null`，来源明确返回的零保留为零；HTTP、业务及解析失败作为错误返回。

弹幕 cursor 对外为下一段起点的数字毫秒字符串。显式 `has_more=false` 或下一段起点达到时长边界时结束分页；其他情况下要求游标向前推进。缺失或异常的分页信息保留当前页并说明来源受限，避免把未知状态显示为全部已加载。SDK 单次读取一页，桌面滚动层随播放位置自动获取后续时间段；拖动进度后按新的时间位置继续获取，不要求打开右侧讨论区。弹幕跨页按时间和正文去重，换集、关闭滚动弹幕与退出取消相应的旧请求，迟到响应还需通过代次和剧集身份检查。

右侧评论预览和弹幕时间轴列表继续提供浏览与点击定位。讨论区关闭或换标签只取消其列表请求，滚动弹幕有独立的开关和加载状态。官网预览总数不覆盖分集统计接口的评论数。

滚动层按播放位置回看约 18 秒、预读约 12 秒，按来源游标获取覆盖这些时间的分段。内存最多保留距当前播放位置较近的 12 段，远距离回看时重新获取。播放位置与在途请求相差超过 30 秒时，取消旧位置请求并优先读取目标附近，旧位置的退避不阻塞新位置。读取失败保留当前正文，依次等待约 5、15、60 秒后重试；关闭开关或切集会取消旧请求。

## 显示与同步

播放设置中的「弹幕」页提供：

| 设置 | 范围与默认值 |
|---|---|
| 显示滚动弹幕 | 默认开启；关闭后清除当前弹幕 |
| 显示范围 | 顶部 1/4、半屏、全屏；默认半屏，全屏范围仍为底部字幕预留空间 |
| 字号 | 小、中、大；默认中号 |
| 不透明度 | 20%–100%；默认 85%，数值越低越透明 |
| 滚动速度 | 慢、标准、快；默认标准 |
| 弹幕密度 | 稀疏、标准、密集；默认标准 |

滚动文字由本项目生成 ASS 时间轴，经 mpv `memory://` 字幕轨在原生视频表面内渲染。暂停时冻结，恢复后继续；拖动进度和切换视频倍速由播放器的媒体时间统一同步。弹幕速度设置另行控制文字穿过画面的时长。区域、字号和透明度应用到当前轨道；行道占满时跳过该条，不把过期弹幕积压到后面补播。

弹幕字面使用中性灰 `#D5D9DC`，细描边及轻阴影分别按所选不透明度的 50% 和 25% 衬托，边缘轻微柔化。每条文字按媒体时间渐入 220 ms、淡出 300 ms，暂停、跳转及倍速沿用同一时间轴。用户保存的不透明度及其他显示设置保持原样。

中号字按视频实际显示高度调整：达到 540 像素时使用 ASS 字号 26，否则使用 29；这些数值属于 720 高的 ASS 画布，不是屏幕像素字号。中号行道间距始终按 29 计算，小号与大号保持原值。标准档相同文字停留时间较首版增加约 50%；慢、标准、快三档最长驻留时间分别为 16、12、8 秒，短文字会更早完全离开画面。移动速度仍在同档中保持一致，避免后发长文字追上前条。

当前滚动弹幕临时占用 mpv 的主软字幕轨：片源另带可选软字幕时，开启弹幕会暂时切走该轨，关闭后恢复原选中轨及字幕可见性、样式覆盖、延迟和速度参数。烧录在视频中的字幕不受轨道切换影响。当前实现未同时显示主软字幕与滚动弹幕。

正文、时间轴及生成的 ASS 只保留在内存中，退出后释放，不另建评论数据库或磁盘弹幕文件。关闭显示开关时清除当前轨道并保留本集已读时间段，重新开启时复用；换集清空本集内存内容。显示偏好保存在 `%APPDATA%\openhongguo\danmaku-settings.json`，不包含正文和用户资料。视频内弹幕不建立桌面置顶窗口，随视频宿主一起隐藏、缩放和切换全屏。

## 桥接参数

- `comments`：`series_id`、`episode_id`；网页预览没有分页参数。
- `danmaku`：`series_id`、`episode_id`、`duration_ms`，以及可选 `cursor`。时长为 1–86,400,000 毫秒；游标最多 19 位非负十进制数字，允许前导零，且严格小于时长；空字符串从零开始。
- 播放信息同时返回 `duration_ms` 和兼容的整数 `duration_seconds`，弹幕优先使用毫秒字段，避免丢掉最后不足一秒的时间段。

## 播放交互参考

开关、透明度、播放与暂停状态、跳转后重定位和有限行道的交互参考 [DPlayer 文档](https://dplayer.diygod.dev/guide.html#api)及其[弹幕源码](https://github.com/DIYgod/DPlayer/blob/3d9940adc7dd3c085ee1d1ed1c20e034bc52765d/src/js/danmaku.js)。本项目的原生渲染器独立实现，未引入 DPlayer 的浏览器运行时。

字幕轨接入依据随包 mpv 对应版本的官方 [`sub-add` 文档](https://github.com/mpv-player/mpv/blob/3138fc8eea/DOCS/man/input.rst#L687)和 [`memory://` 协议](https://github.com/mpv-player/mpv/blob/3138fc8eea/DOCS/man/mpv.rst#L1533)。ASS 滚动事件随播放器时间运行，不以另一个桌面窗口叠加。

## 协议调查来源

通过下列公开项目的接口文档与行为定位线索，然后使用当前项目的签名与真实响应独立验证、实现。没有引入它们的签名库或复制 GPL 实现。

- [beilunyang/openhongguo](https://github.com/beilunyang/openhongguo)：本项目原始 SDK 与协议基础。
- [jackotom/hongguo-drama](https://github.com/jackotom/hongguo-drama/tree/affc81c7f885bcdbcbc06e4c205b820c61f9c0c4)：`docs/EPISODE_COMMENTS.md`、`src/native/public-details.js`、`src/native/episode-comments.js` 和 `src/native/danmaku.js` 提供公开网页及弹幕线索。
- [Junle90/hongguo-next](https://github.com/Junle90/hongguo-next/blob/6b19c9451758f8cab535720525d5ac70f31bd54c/core/ui_playback_danmaku.go)：时间轴弹幕端点与字段线索。

接口可能随平台变化。公开网页预览条数与弹幕返回数量因分集、时间段而异；单次抽样成功不代表全库长期可用。
