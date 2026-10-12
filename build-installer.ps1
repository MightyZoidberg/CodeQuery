# 构建单 exe 安装/管理器：发布 CodeQuery → 打包 payload → 单文件发布管理器。
# 产物：C:\Tools\CodeQuery\dist\CodeQueryManager.exe（内嵌 payload，拷到任意 Windows x64 机器即可运行）
# 用法: powershell -ExecutionPolicy Bypass -File C:\Tools\CodeQuery\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

# 1) 发布 CodeQuery 到 publish\（脚本自带停实例逻辑；不发布源码，只发布产物）
& (Join-Path $root 'publish-mcp.ps1')

# 2) 版本标记：管理器读它显示版本，避免为取版本拉起进程
$ver = [regex]::Match((Get-Content (Join-Path $root 'Program.cs') -Raw), 'Version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $ver) { throw '从 Program.cs 解析版本号失败' }
Set-Content -Path (Join-Path $root 'publish\VERSION.txt') -Value $ver -NoNewline -Encoding ascii
Write-Output "payload 版本: v$ver"

# 3) 组装 payload zip（暂存目录保证 zip 内相对结构）：publish\ + README + 源码与脚本。
#    不含 default-sln.txt（那是各机器自己的多项目清单，机器状态不入包）
$stage = Join-Path $env:TEMP ('CQM-payload-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Copy-Item -Recurse (Join-Path $root 'publish') (Join-Path $stage 'publish')
    Copy-Item @(
        (Join-Path $root 'README.md'),
        (Join-Path $root 'Program.cs'),
        (Join-Path $root 'CodeQuery.csproj'),
        (Join-Path $root 'publish-mcp.ps1'),
        (Join-Path $root 'probe_mcp.ps1')
    ) -Destination $stage

    $zip = Join-Path $root 'Manager\CodeQueryPayload.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Output ("payload zip: {0} MB -> {1}" -f [math]::Round((Get-Item $zip).Length/1MB, 1), $zip)
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}

# 4) 发布管理器：self-contained 单文件（目标机无需 .NET 运行时；管理器无 BuildHost，可安全单文件发布）
#    EnableCompressionInSingleFile：不开约 120MB，开了约一半
& $dotnet publish (Join-Path $root 'Manager\CodeQueryManager.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o (Join-Path $root 'dist') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'manager publish failed' }

$out = Join-Path $root 'dist\CodeQueryManager.exe'
Write-Output ("manager: {0} ({1} MB)" -f $out, [math]::Round((Get-Item $out).Length/1MB, 1))
Write-Output '自检: dist\CodeQueryManager.exe --selftest   （cmd 里重定向到文件看输出）'
