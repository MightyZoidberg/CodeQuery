# 构建并发布 CodeQuery 到本目录 publish\ 子目录（机器级安装：源码与发布物同在本目录）。
# 用法: powershell -ExecutionPolicy Bypass -File C:\Tools\CodeQuery\publish-mcp.ps1
# 注意: 不要加 -p:PublishSingleFile —— 单文件打包会把 MSBuild BuildHost 辅助进程
#       吞进 bundle，运行时加载 solution 会报 "build host could not be found"。
$ErrorActionPreference = 'Stop'

$outDir = Join-Path $PSScriptRoot 'publish'

$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

# 正在运行的实例会锁住 CodeQuery.exe，先停掉（所有客户端的活跃会话都会被断开，
# 客户端下次调用/重开会话时自然重新拉起新版本）
Get-Process CodeQuery -ErrorAction SilentlyContinue | Stop-Process -Force

# 清理历史孤儿 BuildHost（v1.3.0 起 Job Object 已根治父死子不亡；此处兜底清存量，
# 新旧安装位置都扫）
Get-Process -Name 'Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$outDir*" -or $_.Path -like "$env:USERPROFILE\.codequery\*" } |
    Stop-Process -Force

& $dotnet publish "$PSScriptRoot\CodeQuery.csproj" -c Release -r win-x64 --self-contained false `
    -o $outDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Output "published: $outDir\CodeQuery.exe"
Write-Output "客户端注册 command: $outDir\CodeQuery.exe   args: mcp --solution <项目sln绝对路径>"
Write-Output "多项目清单(可选): $outDir\default-sln.txt（每行一个 sln 绝对路径，查询可带 solution= 选择器切换）"
