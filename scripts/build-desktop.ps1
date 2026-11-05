param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\windows-amd64'),
    [string]$Version = '',
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$executablePath = Join-Path $outputPath '红果桌面版.exe'

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    throw '构建需要安装 Go；使用发布的 EXE 无需安装 Go。'
}
if ($Version -and $Version -notmatch '^[A-Za-z0-9._+-]+$') {
    throw '版本号只允许字母、数字、点、下划线、加号和连字符。'
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$originalGoOS = $env:GOOS
$originalGoArch = $env:GOARCH
$originalCgo = $env:CGO_ENABLED
Push-Location $repositoryRoot
try {
    $env:GOOS = 'windows'
    $env:GOARCH = 'amd64'
    $env:CGO_ENABLED = '0'
    $linkerFlags = '-s -w -H=windowsgui'
    if ($Version) {
        $linkerFlags += " -X github.com/beilunyang/openhongguo/internal/desktop.Version=$Version"
    }

    # 图形子系统避免双击后出现命令行闪退，界面资源随 EXE 一起打包。
    & go build -trimpath -ldflags $linkerFlags -o $executablePath ./cmd/ohg-desktop
    if ($LASTEXITCODE -ne 0) {
        throw "桌面版构建失败，退出码：$LASTEXITCODE"
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\desktop.md') -Destination (Join-Path $outputPath '使用说明.md') -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $outputPath 'LICENSE') -Force
}
finally {
    $env:GOOS = $originalGoOS
    $env:GOARCH = $originalGoArch
    $env:CGO_ENABLED = $originalCgo
    Pop-Location
}

Get-Item -LiteralPath $executablePath | Select-Object FullName, Length
Get-FileHash -LiteralPath $executablePath -Algorithm SHA256
if (-not $NoZip) {
    $zipPath = Join-Path (Split-Path $outputPath -Parent) '红果桌面版-windows-amd64.zip'
    Compress-Archive -LiteralPath $executablePath, (Join-Path $outputPath '使用说明.md'), (Join-Path $outputPath 'LICENSE') -DestinationPath $zipPath -Force
    Get-Item -LiteralPath $zipPath | Select-Object FullName, Length
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
}
