# 探针脚本：以 stdio JSON-RPC 驱动 CodeQuery.exe mcp（未挂载到 agent 工具面板时的兜底通道）
# 用法：
#   仅握手（列工具）： & .\probe_mcp.ps1
#   带查询：& .\probe_mcp.ps1 -Calls @('{"name":"find_usages","arguments":{"symbol":"xxx","solution":"on any"}}')
param(
    [string[]] $Calls = @()
)
$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot 'publish\CodeQuery.exe'
$outFile = Join-Path $PSScriptRoot 'probe_out.txt'
if (Test-Path $outFile) { Remove-Item $outFile -Force -ErrorAction SilentlyContinue }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = 'mcp'
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)

# 关键1：异步排空 stderr（不注册 handler）。solution 加载日志走 stderr，
# 管道缓冲（约4KB）写满且无人读时子进程会死锁，协议响应永远出不来。
$p.BeginErrorReadLine()

function Write-Log([string] $text) { Add-Content -Path $outFile -Value $text -Encoding UTF8 }

function Send-Line([string] $line) {
    $p.StandardInput.WriteLine($line)
    $p.StandardInput.Flush()
}

# 关键2：stdout 首批是 "[codequery] ..." 启动日志（stderr 重定向之前写入），须跳过直至匹配目标 id
function Read-Response([int] $id) {
    while ($true) {
        $line = $p.StandardOutput.ReadLine()
        if ($null -eq $line) { throw 'stdout closed' }
        $pattern = '"id":\s*' + $id + '\s*[,}]'
        if ($line -match $pattern) { return $line }
        Write-Log ('[noise] ' + $line)
    }
}

try {
    Send-Line '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}'
    $initResp = Read-Response 1
    Write-Log '=== initialize ==='
    Write-Log $initResp

    Send-Line '{"jsonrpc":"2.0","method":"notifications/initialized"}'

    Send-Line '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
    $listResp = Read-Response 2
    Write-Log '=== tools/list ==='
    Write-Log $listResp

    $nextId = 3
    foreach ($c in $Calls) {
        $req = '{"jsonrpc":"2.0","id":' + $nextId + ',"method":"tools/call","params":' + $c + '}'
        $resp = $null
        # solution 冷加载期间返回 "solution still loading"，重发直至成功（大 sln 首载可达数分钟，上限 ~12 分钟）
        for ($attempt = 0; $attempt -lt 240; $attempt++) {
            Send-Line $req
            $resp = Read-Response $nextId
            if ($resp -match 'still loading') { Start-Sleep -Seconds 3; continue }
            break
        }
        Write-Log ('=== tools/call id=' + $nextId + ' ===')
        Write-Log $resp
        $nextId++
    }

    Write-Log '=== done ==='
}
finally {
    try { $p.Kill() } catch {}
}
