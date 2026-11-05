# 后端进程退出验证

这是控制台验证宿主，通过反射调用主工程中的 `BackendClient`，运行真实内置 bridge；只调用 `status`，不访问业务网络，也不创建图形窗口。

验证项目：正常握手、主动结束本宿主创建的辅助进程后快速失败、并发请求下有界退出、写锁竞争下退出及未完成请求清理。配置写入当前目录的 `state/`，验证结果写入 `results/report.json`。

在本目录执行：

```powershell
dotnet restore BackendSmoke.csproj --configfile NuGet.Config -p:RuntimeFrameworkVersion=9.0.4 -p:GenerateSupportedTargetFrameworkAlias=false
dotnet build BackendSmoke.csproj --no-restore -p:RuntimeFrameworkVersion=9.0.4 -p:GenerateSupportedTargetFrameworkAlias=false
& '.\bin\Debug\net9.0-windows\win-x64\BackendSmoke.exe'
```
