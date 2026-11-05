param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\native-windows-amd64'),
    [string]$PackageName = '红果原生桌面版-windows-x64.zip',
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$project = Join-Path $repositoryRoot 'native\Hongguo.Desktop\Hongguo.Desktop.csproj'
$runtime = Join-Path $repositoryRoot 'native\runtime\mpv'

foreach ($command in @('go', 'dotnet')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "源码构建需要 $command；成品用户直接运行 EXE。"
    }
}
$components = @{
    'libmpv-2.dll' = '1D3C4067692048C6CAB7735954E2299664B94C098FCDB8F7BC700E6A6A133ED6'
    'vulkan-1.dll' = 'CD63989744D15FE13972511D3AFB7BBC171C7C72FE9C479B4909595A2F9D6EFB'
}
foreach ($name in $components.Keys) {
    $path = Join-Path $runtime $name
    if (-not (Test-Path -LiteralPath $path)) { throw "缺少构建组件 $path，来源见 docs/native-desktop.md。" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $components[$name]) {
        throw "构建组件校验失败：$name"
    }
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$previousGoOS = $env:GOOS
$previousGoArch = $env:GOARCH
$previousCgo = $env:CGO_ENABLED
Push-Location $repositoryRoot
try {
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'
    $env:CGO_ENABLED = '0'
    & go build -trimpath -ldflags '-s -w' -o native/Hongguo.Desktop/Resources/ohg-native-bridge.exe ./cmd/ohg-native-bridge
    if ($LASTEXITCODE -ne 0) { throw 'SDK 组件构建失败。' }

    # 包含桌面运行时和所有原生组件，发布目录中的 EXE 可单独复制运行。
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:RuntimeFrameworkVersion=9.0.4 -p:DebugType=None -p:DebugSymbols=false -p:GenerateSupportedTargetFrameworkAlias=false --ignore-failed-sources -o $outputPath
    if ($LASTEXITCODE -ne 0) { throw '原生桌面程序发布失败。' }

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\native-desktop.md') -Destination (Join-Path $outputPath '使用说明.md') -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\native-components.md') -Destination (Join-Path $outputPath '第三方组件来源.md') -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $outputPath 'LICENSE') -Force
    $licenseFolder = Join-Path $outputPath '第三方许可证'
    New-Item -ItemType Directory -Path $licenseFolder -Force | Out-Null
    foreach ($name in @('Copyright', 'LICENSE.GPL', 'Vulkan-Loader-LICENSE.txt')) {
        Copy-Item -LiteralPath (Join-Path $runtime $name) -Destination $licenseFolder -Force
    }
    $imagingLicenses = Join-Path $repositoryRoot 'native\runtime\imaging'
    if (Test-Path -LiteralPath $imagingLicenses) {
        Get-ChildItem -LiteralPath $imagingLicenses -File -Filter '*.txt' | Copy-Item -Destination $licenseFolder -Force
    }
}
finally {
    $env:GOOS = $previousGoOS
    $env:GOARCH = $previousGoArch
    $env:CGO_ENABLED = $previousCgo
    Pop-Location
}

$executable = Join-Path $outputPath '红果原生桌面版.exe'
Get-Item -LiteralPath $executable | Select-Object FullName, Length
Get-FileHash -LiteralPath $executable -Algorithm SHA256
if (-not $NoZip) {
    $zipPath = Join-Path (Split-Path $outputPath -Parent) $PackageName
    Compress-Archive -LiteralPath $executable, (Join-Path $outputPath '使用说明.md'), (Join-Path $outputPath '第三方组件来源.md'), (Join-Path $outputPath 'LICENSE'), (Join-Path $outputPath '第三方许可证') -DestinationPath $zipPath -Force
    Get-Item -LiteralPath $zipPath | Select-Object FullName, Length
}
