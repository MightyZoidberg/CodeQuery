# CodeQuery — Roslyn 语义查询 MCP 服务器（机器级安装）

自研的 MCP stdio 服务器（JSON-RPC over stdin/stdout），为**任意支持 stdio MCP 的 AI 客户端**
（ZCode、qoder、codex、VS Code/Copilot 等）提供 C# solution 的语义查询——协议层客户端无关，
换客户端只改注册配置，不改代码。

**分发（GitHub 私有仓库，项目组内部）**：普通用户从仓库 Releases 下载 `CodeQueryManager.exe`；
二次开发者 clone 仓库，流程与架构见 `docs\develop.md`。**本目录在开发机上同时是机器级安装**：
源码、发布脚本、发布物（`publish\`）、单 exe 安装/管理器（`dist\`）同在此处，
与任何 Unity/非 Unity 项目目录解耦。

当前版本：**v1.5.2**（7 个工具）。版本历史：v1.3.0 基础三工具 → v1.4.0 并入原 rider 规划的四工具 →
v1.5.0 迁出项目目录、多项目清单改为 exe 同目录 → v1.5.1 修复扩展方法 callers 调用点被误杀的
符号形态比对 bug（调用点绑定 reduced 形态，需经 ReducedFrom 归一回声明形态再比对）→
v1.5.2 小加固：search_text 白名单补 .asmref；MCP initialize 版本钳制到已知集（规范要求应答
支持的版本）；default-sln.txt 查找改为 exe 同目录→上级目录两级回退（清单放上级可免受手工
清空 publish\ 影响）。2026-10-12 仓库化交付（GitHub 私有仓库 + Releases 发版）：引入 git
版本管理，补 codex / VS Code 注册文档与二次开发指南 `docs\develop.md`；工具本体未变。

## 工具面（7 个）

| 工具 | 用途 |
|---|---|
| `find_usages` | 任何符号（字段/属性/方法/类）的全 solution 引用，含 file:line/容器链 |
| `type_hierarchy` | 类型层级：derived / implementations / base |
| `search_symbols` | 符号名子串模糊搜索声明 |
| `analyze_calls` | 方法调用层级（callers/callees；virtual 按 override 派发匹配） |
| `get_file_problems` | 单文件 Roslyn 编译诊断（错误/警告/信息） |
| `lint_files` | 多文件批量编译诊断概览 |
| `search_text` | solution 目录内文本搜索（字面量/注释/配置/资产 GUID；并行扫描） |

## 部署条件（新电脑）

1. **操作系统**：Windows 10/11 x64。
2. **运行 .NET 10**（发布物是 framework-dependent）：装 [.NET Runtime 10](https://dotnet.microsoft.com/download)（桌面/控制台即可，无需 ASP.NET）。
3. **有可用的 MSBuild**（Roslyn 加载 solution 必需），三选一即可：
   - .NET SDK 10 装在 `C:\Program Files\dotnet`（最常见）；
   - 或 Visual Studio；
   - 或 Rider（自带 MSBuild，自动探测）。
   - 都不在默认位置时设环境变量 `CODEQUERY_MSBUILD` 指向含 `Microsoft.Build.dll` 的目录。
4. **目标 Unity 项目前提**：Unity 编辑器打开过该项目（已生成 `.sln` 和全部 `.csproj`——
   Project Settings → External Tools → "Generate all .csproj files" 按需勾选）。
   **只查得到 csproj 里包含的代码**；Packages 内嵌包随Assembly 加载，纯 Assets 下未入 csproj 的散脚本查不到。

## 部署步骤

### 方式 A（推荐）：CodeQueryManager 单 exe 安装/管理器

获取（普通成员机器）：从 GitHub 仓库 **Releases** 下载 `CodeQueryManager.exe`（约 58MB，
self-contained 单文件：CodeQuery payload 内嵌其中）。
开发机自构建：`powershell -ExecutionPolicy Bypass -File C:\Tools\CodeQuery\build-installer.ps1`
→ 产物 `dist\`。

分发到其他电脑：**只拷这一个 exe**，双击运行（未签名，SmartScreen 警告选"仍要运行"）：

1. 「程序文件」区：安装目录默认 `C:\Tools\CodeQuery`，点"安装 / 更新"解压内嵌 payload；
2. 「环境体检」区：看 MSBuild 源与 unity.exe（Unity Pipeline CLI）是否检测到，缺 SDK 时按提示
   `winget install Microsoft.DotNet.SDK.10`；
3. 「Unity 项目」区：自动列出 Unity Hub 项目列表（`%APPDATA%\UnityHub\projects-v1.json`，兜底
   编辑器注册表 MRU），也可"手动添加项目…"；选中 → "写入 / 更新选中项目的 ZCode 配置"
   → 生成该项目 workspace 级 `.zcode\config.json`（codequery + `unity-<项目名>` 两个条目，
   无关条目保留；"移除选中项目的本工具配置"可逆）；
4. 「qoder（Rider 插件）注册」区：一键写入/修复用户级 `~/.qoder-cn/shared_client/mcp.json` 的
   codequery 条目（合并写入，无关服务器保留），并自动重启 QoderCN 守护进程使配置即时生效
   （Rider 插件会自动重新拉起守护进程）；"移除"可逆。"安装 / 更新"后若检测到 qoder 注册
   指向本安装目录，也会自动重启守护进程（升级杀掉的 CodeQuery 实例不会自行重生）；
5. 重开该项目的 ZCode 会话 / qoder 新开会话。

管理器源码在 `Manager\`（**WPF**，源文件 3 个）。无 GUI 自检：
`dist\CodeQueryManager.exe --selftest > out.txt 2>&1`（cmd 重定向运行；含 TEMP 沙盒写入/移除
往返测试——含 qoder 注册的合并/正斜杠/保留无关条目校验，不碰真实项目与真实 qoder 配置/守护进程）。

### 方式 B：手工部署

```text
1. 整个目录复制到目标机，例如 C:\Tools\CodeQuery（路径可换，下同）。
2. 发布：powershell -ExecutionPolicy Bypass -File C:\Tools\CodeQuery\publish-mcp.ps1
   → 产物在 C:\Tools\CodeQuery\publish\（含 CodeQuery.exe）。
   ⚠ 发布会强杀本机所有正在运行的 CodeQuery 实例（各客户端的活跃查询会断开）。
3. 按需编辑 default-sln.txt（**建议放上上级目录** C:\Tools\CodeQuery\default-sln.txt，v1.5.2 起
   查找顺序为 exe 同目录 → 上级目录；每行一个 sln 绝对路径，多项目 fallback 清单，可留空/不加）。
4. 按下节注册客户端，然后重开客户端会话（MCP 工具面在会话建立时握手）。
```

升级用 CodeQueryManager 的"安装 / 更新"（覆盖解压；default-sln.txt 是机器状态不在 payload 内，
不受影响）或重跑方式 B 的发布脚本。

## 客户端注册

### ZCode（首选：用 CodeQueryManager 自动写入；手工模板如下）

```json
{
  "mcp": {
    "servers": {
      "codequery": {
        "command": "C:\\Tools\\CodeQuery\\publish\\CodeQuery.exe",
        "args": ["mcp", "--solution", "<项目sln绝对路径>"]
      }
    }
  }
}
```

**必须用 `--solution` 显式指向本项目 sln**（项目隔离：A 项目的会话不得依赖/切换到 B 项目，
不要图省事放用户级共享注册）。`default-sln.txt` 只是没传 `--solution` 时的兜底。

### qoder 等其它 stdio MCP 客户端

任意支持标准 stdio MCP 的客户端，command 填 `C:\Tools\CodeQuery\publish\CodeQuery.exe`、
args 填 `mcp --solution <sln绝对路径>`（或 `mcp` + 依赖 default-sln.txt + 查询带 `solution=` 选择器）。
qoder CN（Rider 内插件）同理：在其 MCP 配置里把命令指向本 exe。
**qoder 坑（2026-10-01 实测）**：qoder 的命令行解析器会吃反斜杠，且常驻 agent 可能缺 PATH——
命令必须**全绝对路径 + 正斜杠**（如 `C:/Tools/CodeQuery/publish/CodeQuery.exe mcp --solution <sln>`）；
"远端 MCP"表单只有 URL 必填时，URL 填占位、命令整行替换。配置落盘 `~/.qoder-cn/shared_client/mcp.json`，agent 热加载。

**qoder 推荐注册方式（2026-10-10 实测）**：Rider 插件的 qoder 守护进程（QoderCN.exe，跨 IDE
重启存活）**只读用户级** `~/.qoder-cn/shared_client/mcp.json`；项目级 `.qoder/settings.json`
实测不生效（守护进程扫描 `count:0`，官方文档的分级配置只适用于 qoder 独立 IDE/CLI）：

```json
{
  "mcpServers": {
    "codequery": {
      "command": "C:/Tools/CodeQuery/publish/CodeQuery.exe",
      "args": ["mcp"],
      "timeout": 180000,
      "alwaysAllow": true
    }
  }
}
```

多项目用 default-sln.txt 清单 + 查询带 `solution=` 选择器（首个查询带一次即可，会话内记住；
漏带时工具返回 available 清单引导）。**挂载只发生在守护进程启动时**：改完配置必须重启守护进程——
`taskkill /IM QoderCN.exe /F`（Rider 插件会自动重新拉起）或重启 Rider 里 qoder 面板；会话内
`/mcp reload` 只重载已知服务器的工具面，**不会**重新扫描配置文件。验证：
`~/.qoder-cn/shared_client/logs/qoder.log` 里 `mcp server end {"serverCount": 1}`、
`save mcp tools end {... "toolCount": 7}`、`mcps\codequery\` 目录生成，且 CodeQuery.exe 进程
以 QoderCN 为父进程运行。2026-10-10 按此法挂载成功。**以上操作已产品化进 CodeQueryManager
（「qoder 注册」区一键完成，含守护进程自动重启）；手工步骤仅排障用。**

### codex（OpenAI Codex CLI）

配置文件 `%USERPROFILE%\.codex\config.toml`（TOML；项目级 `<项目>\.codex\config.toml`
优先级更高，配置层级见 codex 官方文档）：

```toml
[mcp_servers.codequery]
command = 'C:\Tools\CodeQuery\publish\CodeQuery.exe'
args = ['mcp', '--solution', 'C:\Path\To\Project\Project.sln']
```

要点：

- TOML 字符串用**单引号字面量**（不转义反斜杠）；`command` 必须绝对路径——codex 拉起
  子进程时 PATH 不可靠（与 qoder 同款坑）。
- 项目隔离：多项目建议每项目放一份 `.codex\config.toml`（条目名可带项目后缀，如
  `codequery-mygame`）；或用户级单条不带 `--solution`，走 default-sln.txt 清单 + 首个查询
  带 `solution=<路径或文件名子串>` 选择器（会话内记住）。
- 改完配置重开会话；冷启动期间工具返回 "solution still loading"，原样重试即可。

### VS Code（GitHub Copilot 原生 MCP）

项目里放 `.vscode\mcp.json`（workspace 级，天然满足项目隔离——每项目一份、指向各自 sln）：

```json
{
  "servers": {
    "codequery": {
      "type": "stdio",
      "command": "C:\\Tools\\CodeQuery\\publish\\CodeQuery.exe",
      "args": ["mcp", "--solution", "C:\\Path\\To\\Project\\Project.sln"]
    }
  }
}
```

要点：

- 需内置 MCP 支持的 VS Code 版本（Copilot Agent 模式）。打开该文件时编辑器内联提供
  Start/Stop，也可用命令面板 "MCP: List Servers"；首次运行会请求工作区批准，同意即可。
- **不要**挪到用户级全局设置——会把 A 项目的会话引到 B 项目的 solution，违背项目隔离原则。
- 用 Cline / Roo Code 插件的成员：插件自带 MCP 设置（`cline_mcp_settings.json`），
  同样 command/args 指向本 exe。

## 并发与资源模型（为什么没有守护进程）

- **每个客户端会话各自拉起一个独立 CodeQuery.exe 进程**（stdio MCP 标准模型）。
  进程间零共享状态 → 多工具/多项目**并发调用天然安全**，无需加锁、无需中转。
- **没有任何客户端连接 = 没有进程 = 零常驻资源**：stdio 断开即退出；Kill-on-close Job Object
  保证 MSBuild BuildHost 辅助子进程随父陪葬，不留孤儿（发布脚本另有兜底清理）。
- 代价：N 个客户端同时开同一大 solution 时各持一份 Roslyn 工作区内存（几十到几百 MB 级/份）。
  这是刻意取舍——共享守护进程需要端口管理、空闲看门狗、启动编排，坑多且违背"空闲零占用"。

## 运行行为速查（给 AI 与排障用）

- 冷启动加载 solution 需数秒（31 项目约 3.5s，机器忙时更久）；期间工具返回
  `"solution still loading, call this tool again shortly"` —— **原样重试即可**。
- 源码/.csproj/.sln 变化后懒刷新：下一次查询前自动重载（5 秒防抖）。
- 多行 default-sln.txt 且未传 `--solution` 时：不预载，首个查询须带 `solution=<路径或文件名子串>`，
  否则返回 `available` 清单引导。
- 日志全走 stderr；stdout 是协议通道。
- `get_file_problems` 是**整项目编译后的单文件过滤诊断**：若同项目其他文件本身有错，可能在被查
  文件里产生 Unity 编辑器里不存在的级联错误（如依赖类型解析失败）——见到可疑级联时结合
  lint_files 看邻文件是否为源头。

## 已知坑（务必读）

0. **.NET 10 的框架内文件夹对话框在本机 Windows 构建上必崩**（2026-10-10 实测，Win11 24H2
   26100.9444）：`FolderBrowserDialog`（WinForms）与 `OpenFolderDialog`（WPF）打开即 ntdll
   fail-fast（0xc0000409，偏移 0x123fb6），与业务代码无关（最小复现验证）；net8.0 同代码正常。
   管理器因此用 **WPF UI + 目录选择借道 PowerShell 5.1 子进程**（.NET Framework 4.8 独立运行时，
   实测正常，用户已人工验证），见 `MainWindow.cs` 的 `FolderPicker`。若微软后续修复 net10 的
   IFileDialog 互操作，可改回框架内对话框。**给 Manager 加任何"浏览文件夹"功能时勿直接用
   框架对话框类。**
1. **发布禁止 `-p:PublishSingleFile`**：会把 MSBuild BuildHost 吞进 bundle，运行时报
   "build host could not be found"（publish-mcp.ps1 已按正确参数写死，别手改加）。
2. **ZCode 沙箱内别用 PowerShell `System.Diagnostics.Process` 拉起本 exe 做冒烟测试**
   （Kill-on-close Job Object 与沙箱作业层级冲突会连杀 PowerShell 自身）。用 cmd 管道：
   `(type req.txt) | CodeQuery.exe mcp > out.txt 2> err.txt`，或直接走 MCP 工具。
3. 探针脚本 `probe_mcp.ps1` 是未挂载 MCP 工具面时的兜底通道（走 cmd/PowerShell 均可，
   它在子进程里异步排空 stderr 防管道死锁）。
4. 发布后**客户端会话必须重开**才能拿到新工具面/新版本（stdio 进程在会话建立时拉起）。

## 升级 / 修改

- **使用者升级**：从仓库 Releases 下载新版 `CodeQueryManager.exe`，覆盖运行"安装 / 更新"；
  或在装有源码的机器上重跑方式 B 发布脚本。
- **二次开发**（加工具 / 改行为）：架构地图、约定与完整流程见 `docs\develop.md`。
  最短路径：改 `Program.cs`（单文件全部逻辑）→ 跑 `publish-mcp.ps1` → 重开各客户端会话。
- **发版纪律**：bump `Program.cs` 的 `Version` 常量 → 构建自测 → git commit + tag `vN.N.N` →
  `build-installer.ps1` → `dist\CodeQueryManager.exe` 挂到 GitHub Release（附版本说明）。
  普通用户永远从 Release 拿 exe，不从源码目录拷。
