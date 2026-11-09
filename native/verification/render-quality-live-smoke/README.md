# 真实片源同帧缩放与软硬解码核对

从生产 `MainWindow` 和 JSONL 桥取得指定系列、分集的最高画质模型；只复制匿名设备档案到临时目录，测试历史及偏好不写入用户目录。随后用生产 `MpvPlayer` 在屏幕外、不激活的 `NativeVideoHost` 播放同一地址，暂停在指定时间，核对物理输出尺寸、截图前后播放位置和暂停状态。

```powershell
dotnet run --project native/verification/render-quality-live-smoke/RenderQualityLiveSmoke.csproj -c Release -- <series-id> <episode> <seconds> <output-directory> [candidate-dscale] [candidate-antiring]
```

候选默认 `ewa_lanczossharp` / `1`。新播放器构造后的配置必须与候选相同；旧基线明确覆盖为 `hermite` / `0`，避免生产默认变化污染基线。依次检查自动硬件解码和软件解码，在 1920×1080、1658×933 两个物理尺寸下保存原尺寸 `video` 与原生渲染 `window` PNG；没有额外图片锐化。

报告仅保存系列/分集身份、档位、码率、尺寸、密钥是否存在、暂停时间及播放器白名单属性。完整媒体地址、密钥值及匿名配置均不写报告；测试不保存完整视频文件。`video-pts` 在当前随包版本返回空值，报告保留这一事实，实际稳定性断言依据非空 `time-pos`、暂停状态和精确定位，后续逐像素比较可进一步检查软硬解码相同帧。

测试属于原生离屏输出验证，不是系统桌面合成、iPhone 同源验证或动态性能测试。退出时正常关闭测试窗口、销毁播放器并删除临时配置。夹具内部一次性修改的渲染参数只影响测试实例。
