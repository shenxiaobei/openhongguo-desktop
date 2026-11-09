# Windows 桌面版发布

本流程用于 [shenxiaobei/openhongguo-desktop](https://github.com/shenxiaobei/openhongguo-desktop) 的 Windows 原生桌面版。项目基于并借鉴 [beilunyang/openhongguo](https://github.com/beilunyang/openhongguo)，保留其 Go SDK、模块路径和多语言绑定；这些继承源码不表示本仓库同时发布 npm、Maven Central、pub.dev 或移动端 SDK。

2026-11-09 的发布整理沿用产品版本 **1.0** 和现有 **v1.0** 发行入口，更新当前源码、使用说明与 Windows 附件。版本号相同的构建通过发行日期及 SHA256 区分。本次复用 2026-11-08 已验证的 EXE，打包时更新配套文档，不因文档变化重新生成程序。

## 发行文件

| GitHub 附件 | 内容 |
|---|---|
| `OpenHongguo-Desktop-v1.0-windows-x64.exe` | 自包含 Windows x64 单文件桌面程序 |
| `OpenHongguo-Desktop-v1.0-windows-x64.zip` | 当前 EXE、使用说明、组件来源、LICENSE 和第三方许可证 |
| `openhongguo-native-runtime-v1.0-windows-x64.zip` | 源码构建所需的原生运行组件，普通运行无需下载 |
| `OpenHongguo-Desktop-v1.0-SHA256SUMS.txt` | 上述三个附件的 SHA256，文件名与实际下载名一致 |

GitHub 自动生成的源码压缩包由 v1.0 标签生成，标签应对应本次发布的源码提交。GitHub 的英文 EXE 文件名和完整 ZIP 内的 `红果原生桌面版.exe` 内容相同。

本地交付保留 `Releases/v1.0/红果原生桌面版.exe` 及配套文件、一份当前完整 ZIP、构建运行库 ZIP 和校验清单。需要上传的英文名 EXE 可放在临时发布目录，上传核对后清理，避免本地出现多个运行入口。观看记录、设备配置、封面缓存、媒体地址和测试输出不放入分发包。

## 准备源码与程序

1. 核对工作树差异、远端仓库和当前分支；保留已完成的功能改动及二开来源说明，避免带入临时工具、个人配置或未验证的成品。
2. 根据本次改动执行对应检查。播放协议涉及 `internal/protocol`、`sdk`、`sdk/model`、`internal/nativebridge`；桌面调整使用 `native/verification/` 中对应的原生验证。报告区分离线、真实接口、离屏输出和前台交互的范围。
3. 对已有验证证据的 EXE，先核对 SHA256 与证据，再复用该文件。若代码发生变化，则重新构建、验证最终程序，并更新摘要。桌面版本位于 `native/Hongguo.Desktop/Hongguo.Desktop.csproj`；本次产品版本为 `1.0`、文件版本为 `1.0.0.0`。
4. 更新 README、`docs/native-desktop.md`、发行说明及下载链接；不要让“仅本地”的旧阶段文字作为当前发布状态。历史验证保留日期与当时结论。

需要构建时，使用 Windows x64、Go、.NET 9 SDK 和 **PowerShell 7（pwsh）**。从当前发行附件恢复构建运行库后，在仓库根目录执行：

```powershell
Expand-Archive -LiteralPath '..\Releases\openhongguo-native-runtime-v1.0-windows-x64.zip' -DestinationPath '.' -Force
pwsh -File scripts/build-native.ps1 -OutputDirectory '..\Releases\v1.0' -NoZip
```

构建脚本核对 DLL 摘要，生成内嵌 Go 桥，再发布带 .NET 桌面运行时和原生组件的单 EXE。运行成品无需安装上述开发工具。若旧程序正在运行，先正常退出，保存观看进度后再替换原路径；用户配置目录与程序文件分开保留。

## 打包与核对

将当前 `docs/native-desktop.md` 复制为成品目录的 `使用说明.md`，组件来源及许可证从对应源码文件复制。完整 ZIP 应包含：

```text
红果原生桌面版.exe
使用说明.md
第三方组件来源.md
LICENSE
第三方许可证/
```

在 PowerShell 7 中可打包当前成品：

```powershell
Compress-Archive -LiteralPath '..\Releases\v1.0\红果原生桌面版.exe', '..\Releases\v1.0\使用说明.md', '..\Releases\v1.0\第三方组件来源.md', '..\Releases\v1.0\LICENSE', '..\Releases\v1.0\第三方许可证' -DestinationPath '..\Releases\OpenHongguo-Desktop-v1.0-windows-x64.zip' -Force
Get-FileHash -Algorithm SHA256 -LiteralPath '..\Releases\v1.0\红果原生桌面版.exe', '..\Releases\OpenHongguo-Desktop-v1.0-windows-x64.zip', '..\Releases\openhongguo-native-runtime-v1.0-windows-x64.zip'
```

校验清单使用 `64位十六进制摘要`、两个空格、`下载文件名` 的格式。上传前检查 ZIP 内 EXE 与独立 EXE 的摘要一致，说明和许可文件与当前源码一致；ZIP 不应包含用户状态、调试输出或旧版程序。

## 同步 GitHub

先提交并推送经过核对的源码，随后更新 v1.0 发行附件和正文。发布说明应包含本次实际变化、验证范围、二开来源、运行要求及文件说明。

本次使用已有 v1.0，先读取并记录远端标签提交、发行正文和附件列表，再按用户要求将当前发行对应到本次源码。只更新目标标签及发行，不改写主分支历史。原附件同名替换前保留当前发布元数据，避免上传中断后失去核对依据；新文件核验后再清理多余旧资产。

可使用已登录的 GitHub CLI 或 GitHub API 完成附件上传；CLI 示例仅演示已有发行的附件操作：

```powershell
gh release upload v1.0 <当前EXE路径> <当前完整ZIP路径> <运行库ZIP路径> <SHA256清单路径> --repo shenxiaobei/openhongguo-desktop --clobber
gh release edit v1.0 --repo shenxiaobei/openhongguo-desktop --title '红果原生桌面版 1.0' --notes-file <发行说明文件路径>
```

发行说明使用 UTF-8 文本文件，通过 `--notes-file` 传入，保留实际换行。凭据只交给 GitHub 客户端或凭据管理器，不写入仓库、命令输出或发布说明。上传桌面附件时不触发继承自上游的多平台 CLI/SDK 发布任务。

## 发布完成检查

- 远端分支和 v1.0 标签对应预期源码提交，首页正确显示二开来源、当前功能及下载入口。
- 发行页只保留目标最新版及当前附件，EXE、完整 ZIP、运行库 ZIP、校验清单名称正确。
- GitHub 返回的资产摘要或重新下载文件的 SHA256 与本地一致；完整 ZIP 内 EXE 与独立 EXE 一致。
- 下载链接可访问，使用说明、组件来源及所有第三方许可证完整。
- 本地 `Releases/` 只保留当前交付，临时上传副本和构建中间产物清理；源码、Git 历史、验证记录和用户数据保留。

以这些检查的实际结果记录发布完成，不用上传命令退出成功替代附件内容核对。
