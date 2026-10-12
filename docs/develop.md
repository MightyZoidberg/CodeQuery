# CodeQuery 二次开发指南

写给要改本工具代码的人。使用与部署看 [README](../README.md)；本文只讲四件事：
**架构地图、改动约定、如何加工具、如何构建发版自测**。

## 工程结构

| 路径 | 角色 |
|---|---|
| `Program.cs` | MCP 服务器全部逻辑（单文件约 1500 行，三个类，地图见下） |
| `CodeQuery.csproj` | 控制台工程，net10.0，framework-dependent 发布 |
| `Manager/` | WPF 安装/管理器（独立 csproj）：内嵌 CodeQuery payload，self-contained 单 exe 分发 |
| `publish-mcp.ps1` | 发布 CodeQuery 到 `publish\`（**禁止** `-p:PublishSingleFile`，见约定 6） |
| `build-installer.ps1` | 发布 → 打 payload zip → 构建管理器到 `dist\`；并从 `Program.cs` 抽版本写 `publish\VERSION.txt` |
| `probe_mcp.ps1` | MCP 协议级冒烟探针（initialize / tools/list / 调用），不依赖任何客户端 |
| `default-sln.txt.example` | 多项目清单模板（真身 `default-sln.txt` 是机器状态，不入 git、不进 payload） |
| `docs/develop.md` | 本文 |

## Program.cs 地图（三个类，自上而下）

### `Program`（静态类）
- `Main`：装 Job Object → MSBuild 定位注册（`DiscoverMsBuildPath`）→ cwd 切 TEMP → 起 `QueryServer.RunMcpAsync`
- `KillChildrenOnExit`：kill-on-close Job Object——客户端强杀本进程时，MSBuild BuildHost 等子进程一并被系统终止（否则孤儿进程曾锁住目录句柄）
- `Log`：`StdioProtocolMode` 下一律走 stderr

### `QueryServer`（引擎）
- `Version` 常量：**发版必 bump**（Manager 与 build 脚本都从这里读版本）
- 构造函数：解析 `--solution`；无参时读 default-sln.txt（单行自动采用，多行等待查询带 `solution=` 选择）
- `DiscoverMsBuildPath`：定位链 `CODEQUERY_MSBUILD` 环境变量 → VS 实例 → Rider 安装目录 → dotnet SDK；先补种 `DOTNET_ROOT`（AI 客户端拉起子进程会剥 PATH）
- `RunMcpAsync` / `StartEngine` / `LoadSolutionAsync`：生命周期。`_loadGeneration` 防切换竞态——被取代的加载任务静默退出，绝不触碰新工作区
- `StartWatchers`：FileSystemWatcher 只置脏标记（`_dirty`）；查询时懒刷新，5 秒防抖
- `QueryAsync`：**工具分发入口**。顺序：`solution=` 选择器（可能触发切换重载）→ 懒刷新检查 → 状态非 ready 时返回可重试提示（不阻塞协议循环）→ 拿 `_gate` 信号量 → 复查状态 → switch 分发到 7 个工具
- 7 个工具实现：`FindUsagesAsync` / `HierarchyAsync` / `SymbolsAsync` / `CallsAsync` / `FileProblemsAsync` / `LintFilesAsync` / `SearchTextAsync`
- `ResolveTargetsAsync`：符号定位。`file+line(+col)` 精确定位优先（供 AI 先文本搜索锁定再交给语义引擎）；否则 `symbol=` 名字，支持 `Ns.Type.Member` 点分链，容器不匹配时回退全部同名候选
- 辅助方法：`RelPath`（相对 solution 目录）、`ContainmentChain`（命名空间+嵌套类型+自身的全名链）、`FindDocument`（**不依赖进程 cwd** 解析文件路径）、`SourceLocs`

### `McpStdioServer`（协议层）
- newline-delimited JSON-RPC over stdin/stdout；只实现 `initialize` / `tools/list` / `tools/call` / `ping`
- **stdout 劫持**：`Console.SetOut(Console.Error)`——任何组件误写 Console.Out 都不会污染协议通道
- initialize 应答版本钳制到已知集（2024-11-05 / 2025-03-26 / 2025-06-18），未知版本回退最新已知
- `BuildToolDefinitions` + `MakeTool`：7 个工具的 name / 中文 description / inputSchema 全在这一处

## 改代码前必读的约定

1. **stdout 是协议通道**。任何输出必须走 `Program.Log`（stderr），调试打印也不例外。
2. **入参是扁平 k=v 字符串**。`tools/call` 的 arguments 对象被平铺进 `NameValueCollection`
   （非字符串值 `ToString()`）。新增参数记得同步 `BuildToolDefinitions` 里的 schema；
   description 是写给 AI 看的——写清用途、与其他工具的分工、传参方式（现有 7 个的行文是范例）。
3. **进程模型**：每客户端会话一个独立进程，无守护进程、无跨进程共享状态（这是刻意取舍，
   见 README「并发与资源模型」）。进程启动后 cwd 切到 TEMP——**代码里永远不要依赖 cwd**，
   路径一律以 `_cwd`（solution 目录）为基准经 `RelPath` / `FindDocument` 处理。
4. **错误约定**：参数问题 `throw new ArgumentException`（消息中文、给 AI 可读）；
   正常结果返回 `ok = true` 的匿名对象（JSON 序列化自动）。`QueryAsync` 对"未就绪 / 无
   solution"返回**可重试的引导对象**而非异常。
5. **Roslyn 已踩过的坑**（改动相关代码时勿回退）：
   - partial 类在多个项目 Compilation 中是**不同符号实例**——结果须按 `ContainmentChain|Kind` 分组去重
   - 扩展方法调用点绑定 reduced 形态符号，与声明形态不等——比对前先经 `ReducedFrom` 归一
     （`FindCallersCoreAsync`，v1.5.1 修的误杀 bug）
   - virtual/abstract 方法的 callers 要把全部 override 并入派发匹配集合（同上方法）
   - workspace 加载用 DesignTimeBuild 属性集（不跑编译目标，加速）；编译诊断走
     `GetCompilationAsync` 全项目内存编译后按文件过滤
6. **发布禁止 `-p:PublishSingleFile`**：会把 MSBuild BuildHost 吞进 bundle，运行时报
   "build host could not be found"（`publish-mcp.ps1` 已写死正确参数，别手改加）。
   Manager 例外：它无 BuildHost，可安全单文件发布。
7. **default-sln.txt 是机器状态**：不入 git、不进 payload；代码读它的查找顺序是
   exe 同目录 → exe 上级目录。

## 如何加一个新工具（以第 8 个为例）

1. `QueryServer` 里加实现方法：

   ```csharp
   private async Task<object> XxxAsync(NameValueCollection q)
   {
       var sw = Stopwatch.StartNew();
       // 参数校验：缺失时 throw new ArgumentException("xxx= required（…给 AI 可读的提示…）")
       // …逻辑（参考现有工具：并行/上限/去重的写法）…
       sw.Stop();
       return new { ok = true, /* … */ elapsedMs = sw.ElapsedMilliseconds };
   }
   ```

2. `QueryAsync` 的 switch 加 `"xxx" => await XxxAsync(q),`。
3. `BuildToolDefinitions()` 加 `MakeTool("xxx", "中文描述…", """{schema}""")`；
   schema 照抄现有工具，**必带 `solution` 参数说明**。
4. bump `Version` 常量；README「工具面」表补一行。
5. 按下节自测，过了再 commit。
6. 大结果集记得 `Truncate` + 条数上限 + `truncated` 标记（参考 `SearchTextAsync`），
   防止单次调用撑爆上下文。

## 构建与自测

```text
改 Program.cs   → publish-mcp.ps1          （会强杀本机所有 CodeQuery 实例，活跃会话断开属预期）
                → 冒烟：probe_mcp.ps1      （协议级：initialize / tools 列表 / 实际调用）
                  或 cmd 管道法（README 已知坑 #2 的写法）
改 Manager\     → build-installer.ps1
                → dist\CodeQueryManager.exe --selftest   （TEMP 沙盒自检，不碰真实配置）
```

发布后**客户端会话必须重开**才能拿到新工具面 / 新版本（stdio 进程在会话建立时拉起）。
ZCode 沙箱内别用 PowerShell `System.Diagnostics.Process` 拉起本 exe 冒烟（Job Object 与
沙箱作业层级冲突会连杀 PowerShell 自身），用 cmd 管道或直接走 MCP 工具。

## 发版流程

```text
1. bump Program.cs 的 Version 常量；README 版本历史补一行
2. 构建自测（上节）
3. git commit → 打 tag vN.N.N → push
4. build-installer.ps1 构建管理器
5. 挂 Release（普通用户永远从 Release 拿 exe，不从源码目录拷）：
   gh release create vN.N.N dist\CodeQueryManager.exe --title "vN.N.N" --notes "版本说明"
```

## Manager（安装器）要点

- **WPF**。net10 框架内的文件夹对话框在本机 Win11 构建必崩（ntdll fail-fast，README 已知坑 0）
  ——给 Manager 加任何"浏览文件夹"功能**勿直接用框架对话框类**，照抄 `MainWindow.cs` 的
  `FolderPicker`（借道 PowerShell 5.1 子进程）。
- 自包含单文件发布（无 BuildHost 可安全 PublishSingleFile）；CodeQuery payload 走
  framework-dependent，zip 后内嵌进 Manager。
- Manager 的「客户端注册」区目前只覆盖 ZCode / qoder；对 codex / VS Code 用户它是
  "安装器 + 环境体检器"，注册走 README 的手工模板（扩展它支持更多客户端是合适的练手任务）。
- `--selftest`：TEMP 沙盒里往返测试安装 / 移除逻辑（含 qoder 注册合并校验），不碰真实项目、
  真实 qoder 配置与守护进程。

## 排障速查

README 的「运行行为速查」与「已知坑」两章是第一手实测材料（qoder 守护进程扫描时机、
net10 对话框崩溃、沙箱 Job Object 冲突等），排障先读它。本工具日志全走 stderr，
客户端拉起方式下重定向到文件即可看到。
