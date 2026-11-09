# 原生弹幕设置验证

```powershell
dotnet run --project DanmakuSettingsSmoke.csproj -c Release
```

验证默认值与边界归一、只包含六个偏好字段的JSON、原子保存和重启恢复、损坏/过大配置回退、齿轮入口、所有选项的即时模型/摘要/保存反馈、短窄菜单滚动与最后选项可达、保存失败后本次生效及恢复提示。

窗口不调用`Show`，Popup始终关闭，不创建播放器或请求网络。配置目录为独立临时目录，正常关闭后清理。输出`results/danmaku-settings-smoke.json`以及240 DIP宽的上下两张离屏设置截图，不包含视频、桌面或系统菜单实际定位。
