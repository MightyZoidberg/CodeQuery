using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using System.Text.RegularExpressions;

namespace CodeQuery;

public static class Program
{
    /// <summary>mcp 模式下 stdout 是协议通道，Log 等所有输出必须改走 stderr。</summary>
    internal static bool StdioProtocolMode;

    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        KillChildrenOnExit(); // Job Object：本进程无论如何退出（含客户端强杀），BuildHost 等子进程一并终止

        // 唯一模式：MCP stdio。`mcp` 子命令保留只为兼容既有客户端注册（等价于无参）。
        if (args.Length > 0 && args[0] == "mcp") args = args.Skip(1).ToArray();
        StdioProtocolMode = true;

        string msbuild;
        try
        {
            msbuild = QueryServer.DiscoverMsBuildPath();
            MSBuildLocator.RegisterMSBuildPath(msbuild);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[codequery] " + ex.Message);
            return 1;
        }
        Program.Log("msbuild: " + msbuild);

        try
        {
            var server = new QueryServer(args);
            // cwd 不再承载任何职责后立即切到 TEMP：本进程与 MSBuild 子进程都不以
            // 客户端工作目录为 cwd，避免目录句柄锁定客户端项目目录（曾致外部目录无法删除）
            try { Directory.SetCurrentDirectory(Path.GetTempPath()); } catch { }
            return server.RunMcpAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[codequery] fatal: " + ex);
            return 2;
        }
    }

    // ---------------- Job Object：父死子亡 ----------------

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpInfo, int cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>
    /// 客户端（qoder/ZCode）关闭会话时对 MCP 子进程用的是强杀，BuildHost 辅助进程不会随父退出，
    /// 孤儿进程曾持有文件/目录句柄导致目录无法删除。加入 kill-on-close 的 Job Object 后，
    /// 父进程句柄一关（任何退出方式），作业内子进程全部被系统终止。
    /// </summary>
    private static void KillChildrenOnExit()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if (SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ref info,
                    Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                AssignProcessToJobObject(job, GetCurrentProcess());
            // job 句柄故意不关闭：本进程退出时它随之关闭，触发 kill-on-close
        }
        catch
        {
            // Job Object 失败不影响主功能，只是退回到可能产生孤儿的旧行为
        }
    }

    internal static void Log(string message)
    {
        var line = "[codequery] " + message;
        if (StdioProtocolMode) Console.Error.WriteLine(line);
        else Console.WriteLine(line);
    }
}

/// <summary>
/// 本地语义查询工具：MCP stdio 服务器（JSON-RPC over stdin/stdout），供 qoder / ZCode /
/// CodeBuddy 等 AI 客户端挂载。工具集 7 个：find_usages / type_hierarchy / search_symbols /
/// analyze_calls / get_file_problems / lint_files / search_text。
/// 后四个原是 AGENTS.md 规划中依赖 Rider IDE 运行时的 "rider" 工具集，从未实现过
/// （2026-10-09 排查确认），现以纯 Roslyn 实现并入本服务器，不再依赖 Rider。
/// （HTTP 守护进程模式已于 2026-10-01 移除——与 MCP 双入口会让 AI 误用旧通道。）
///
/// MCP 模式下 stdout 是协议通道：所有日志一律走 stderr，杂散 Console 输出也被重定向到 stderr。
/// solution 发现：仅 --solution 参数 或 exe 同目录 default-sln.txt 清单
/// （单行自动加载；多行时查询传 solution=<路径或文件名子串> 切换）。**不做 cwd 发现**——
/// 客户端 cwd 任意，按 cwd 加载会锁定客户端目录；进程启动后 cwd 即切到 TEMP。
/// 源码/工程文件变化后采用懒刷新：下一次查询前自动重载，保证长会话数据新鲜。
/// </summary>
public sealed class QueryServer
{
    internal const string Version = "1.5.2";

    private string _solutionPath; // 非 readonly：MCP 多项目模式下可经 solution= 选择器切换
    private string _cwd;          // solution 所在目录，相对路径输出基准（切换 solution 时更新）
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MSBuildWorkspace _workspace;
    private volatile Status _status = new("starting", "", 0, Array.Empty<string>());
    private volatile bool _dirty;                    // 源码/工程文件变化标记（懒刷新）
    private DateTime _lastDirtyUtc = DateTime.MinValue;
    private FileSystemWatcher[] _watchers = Array.Empty<FileSystemWatcher>();
    private int _loadGeneration;                     // 每次 StartEngine 递增，用于废弃在途加载任务

    private sealed record Status(string State, string Solution, int Projects, string[] Errors);

    public QueryServer(string[] args)
    {
        string solution = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--solution" && i + 1 < args.Length)
            {
                solution = args[++i];
            }
        }

        if (string.IsNullOrEmpty(solution))
        {
            // 不做 cwd 发现：MCP 客户端 cwd 任意（其他项目的会话也会拉起本服务器），
            // 按 cwd 加载会把客户端目录变成 workspace/watch 目标甚至锁住目录。
            // solution 只来自 --solution 参数或 default-sln.txt 清单（单行自动采用，
            // 多行等待查询带 solution= 选择）。
            var candidates = LoadSolutionCandidates();
            if (candidates.Count == 1) solution = candidates[0];
            // 空清单：solution 保持 null，MCP 查询会返回 available 列表引导带 solution= 选择
        }

        _solutionPath = solution == null ? null : Path.GetFullPath(solution);
        _cwd = _solutionPath == null
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(_solutionPath) ?? Directory.GetCurrentDirectory();
    }

    // ---------------- MSBuild 定位（必须在任何 Microsoft.Build 交互之前调用） ----------------

    public static string DiscoverMsBuildPath()
    {
        // MSBuildLocator 的 dotnet SDK 发现依赖 DOTNET_ROOT/PATH 等变量；qoder 等 AI 客户端
        // 拉起本进程时会剥离 PATH，这里先把默认安装位置补种上（不覆盖已有值）。
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")) &&
            File.Exists(Path.Combine(programFiles, "dotnet", "dotnet.exe")))
            Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.Combine(programFiles, "dotnet"));

        var env = Environment.GetEnvironmentVariable("CODEQUERY_MSBUILD");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "Microsoft.Build.dll")))
            return env;

        List<VisualStudioInstance> vs;
        try
        {
            vs = MSBuildLocator.QueryVisualStudioInstances().ToList();
        }
        catch (Exception ex)
        {
            Program.Log("MSBuildLocator query failed, falling back to manual probing: " + ex.Message);
            vs = new List<VisualStudioInstance>();
        }

        var instance = vs
            .OrderByDescending(v => v.Version)
            .FirstOrDefault();
        if (instance != null)
            return instance.MSBuildPath;

        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JetBrains"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JetBrains", "Toolbox", "apps"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root, "*Rider*"))
            {
                var candidate = Path.Combine(dir, "tools", "MSBuild", "Current", "Bin");
                if (File.Exists(Path.Combine(candidate, "Microsoft.Build.dll")))
                    return candidate;
            }
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (string.IsNullOrEmpty(dotnetRoot) || !Directory.Exists(dotnetRoot))
            dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        var sdkRoot = Path.Combine(dotnetRoot, "sdk");
        if (Directory.Exists(sdkRoot))
        {
            var best = Directory.GetDirectories(sdkRoot)
                .Select(d => (Dir: d, Ver: TryVer(Path.GetFileName(d))))
                .OrderByDescending(x => x.Ver)
                .Select(x => x.Dir)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "Microsoft.Build.dll")));
            if (best != null)
                return best;
        }

        throw new InvalidOperationException(
            "no usable MSBuild found; set CODEQUERY_MSBUILD to a directory containing Microsoft.Build.dll");
    }

    private static Version TryVer(string s) => System.Version.TryParse(s, out var v) ? v : new Version(0, 0);

    // ---------------- 生命周期 ----------------

    /// <summary>MCP stdio 模式：stdout 为协议通道，日志全部走 stderr；stdin 关闭即退出。</summary>
    public async Task<int> RunMcpAsync()
    {
        Log($"CodeQuery v{Version} | solution: {_solutionPath ?? "(none yet; pass solution= selector on first call)"}");
        StartEngine();
        await new McpStdioServer(this).RunAsync();
        return 0;
    }

    private void StartEngine()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers = Array.Empty<FileSystemWatcher>();
        try { _workspace?.CloseSolution(); } catch { }
        try { (_workspace as IDisposable)?.Dispose(); } catch { }

        if (_solutionPath == null)
        {
            // MCP 多项目模式：未选定 solution，等待查询携带 solution= 选择器
            _status = new Status("no-solution", null, 0, Array.Empty<string>());
            return;
        }

        // 同步失效旧状态：切换/重启后立刻进入 loading，防止对未加载的新工作区派发查询
        _status = new Status("loading", RelPath(_solutionPath), 0, Array.Empty<string>());
        _workspace = CreateWorkspace();
        StartWatchers();
        var generation = ++_loadGeneration;
        _ = Task.Run(() => LoadSolutionAsync(generation));
    }

    /// <summary>
    /// 读取多项目清单（每行一个 sln 绝对路径，仅保留存在的文件）。
    /// 查找顺序：exe 同目录 → exe 上级目录。清单放上上级（C:\Tools\CodeQuery\）可免受
    /// "手工清空 publish\ 后丢失"的影响；放 exe 同目录亦可（republish 不删除多余文件）。
    /// 这是 v1.5.0 迁到机器级目录后的约定。
    /// </summary>
    public static List<string> LoadSolutionCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        var defaultFile = new[]
        {
            Path.Combine(baseDir, "default-sln.txt"),
            Path.Combine(baseDir, "..", "default-sln.txt"),
        }.FirstOrDefault(File.Exists);
        if (!File.Exists(defaultFile)) return new List<string>();
        return File.ReadAllLines(defaultFile)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && File.Exists(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>按子串匹配清单中的 sln（完整路径或文件名，忽略大小写）。</summary>
    private static string ResolveSolutionCandidate(string selector)
    {
        return LoadSolutionCandidates()
            .FirstOrDefault(c => c.IndexOf(selector, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>监视源码/工程文件变化并置脏标记；查询时懒刷新，避免长会话数据陈旧。</summary>
    private void StartWatchers()
    {
        var root = Path.GetDirectoryName(_solutionPath) ?? ".";
        var list = new List<FileSystemWatcher>();

        var rootWatcher = new FileSystemWatcher(root) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        rootWatcher.Created += OnSourceChanged;
        rootWatcher.Changed += OnSourceChanged;
        rootWatcher.Renamed += OnSourceRenamed;
        rootWatcher.Deleted += OnSourceChanged;
        rootWatcher.Error += OnWatchError; // 缓冲溢出等：宁可多 reload 不可漏事件
        rootWatcher.Filters.Add("*.sln");
        rootWatcher.Filters.Add("*.csproj");
        list.Add(rootWatcher);

        foreach (var (dir, filters) in new[]
                 {
                     (Path.Combine(root, "Assets"), new[] { "*.cs" }),
                     (Path.Combine(root, "Packages"), new[] { "*.cs", "manifest.json", "*.asmdef" }),
                 })
        {
            if (!Directory.Exists(dir)) continue;
            var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            w.Created += OnSourceChanged;
            w.Changed += OnSourceChanged;
            w.Renamed += OnSourceRenamed;
            w.Deleted += OnSourceChanged;
            w.Error += OnWatchError;
            foreach (var f in filters) w.Filters.Add(f);
            list.Add(w);
        }

        _watchers = list.ToArray();
        Log($"watching {list.Count} location(s) under {RelPath(root)} for lazy reload");
    }

    private void OnSourceChanged(object sender, FileSystemEventArgs e) => MarkDirty();
    private void OnSourceRenamed(object sender, RenamedEventArgs e) => MarkDirty();
    private void OnWatchError(object sender, ErrorEventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        _lastDirtyUtc = DateTime.UtcNow;
        _dirty = true;
    }

    /// <summary>MCP 工具分发入口。solution 未就绪时立即返回可读的 loading 提示（不阻塞协议循环）。</summary>
    public async Task<object> QueryAsync(string tool, NameValueCollection q)
    {
        if (string.IsNullOrEmpty(tool))
            throw new ArgumentException("tool name required");

        // 多项目：solution= 选择器（default-sln.txt 清单中路径或文件名的子串），切换即重载
        var selector = q["solution"];
        if (!string.IsNullOrEmpty(selector))
        {
            var resolved = ResolveSolutionCandidate(selector);
            if (resolved == null)
                return new
                {
                    ok = false,
                    error = "no configured solution matches '" + selector + "'",
                    available = LoadSolutionCandidates(),
                };
            if (!string.Equals(resolved, _solutionPath, StringComparison.OrdinalIgnoreCase))
            {
                Log("switching solution to " + resolved);
                _solutionPath = resolved;
                _dirty = false;
                StartEngine();
            }
        }

        if (_solutionPath == null)
            return new
            {
                ok = false,
                error = "no solution configured; pass solution= (substring of a path or file name listed in default-sln.txt next to CodeQuery.exe or its parent dir)",
                available = LoadSolutionCandidates(),
            };

        // 懒刷新：源码/工程文件变化后的首次查询先重载（加载失败时也允许通过重试恢复）
        if (_dirty && (_status.State == "ready" || _status.State == "error") &&
            (DateTime.UtcNow - _lastDirtyUtc).TotalSeconds >= 5)
        {
            _dirty = false;
            Log("source changes detected, reloading solution");
            await LoadSolutionAsync();
        }

        if (_status.State != "ready")
            return new
            {
                ok = false,
                error = "solution still loading, call this tool again shortly",
                state = _status.State,
                solution = _status.Solution,
            };

        await _gate.WaitAsync();
        try
        {
            // 进入 gate 后复查：等待期间可能发生 solution 切换（StartEngine 同步置 loading）
            if (_status.State != "ready")
                return new
                {
                    ok = false,
                    error = "solution still loading, call this tool again shortly",
                    state = _status.State,
                    solution = _status.Solution,
                };

            return tool switch
            {
                "find_usages" => await FindUsagesAsync(q),
                "type_hierarchy" => await HierarchyAsync(q),
                "search_symbols" => await SymbolsAsync(q),
                "analyze_calls" => await CallsAsync(q),
                "get_file_problems" => await FileProblemsAsync(q),
                "lint_files" => await LintFilesAsync(q),
                "search_text" => await SearchTextAsync(q),
                _ => throw new ArgumentException("unknown tool: " + tool),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private static MSBuildWorkspace CreateWorkspace()
    {
        var props = new Dictionary<string, string>
        {
            // 只做设计期评估，不跑编译目标，加速 csproj 加载
            { "DesignTimeBuild", "true" },
            { "BuildProjectReferences", "false" },
            { "SkipCompilerExecution", "true" },
            { "EnableNETAnalyzers", "false" },
        };
        return MSBuildWorkspace.Create(props);
    }

    /// <summary>
    /// 加载 solution。加载任务持有自己的 workspace/path 快照；被新的 StartEngine 取代
    /// （generation 过期）后静默退出，绝不触碰当前工作区或状态，避免切换竞态。
    /// </summary>
    private async Task LoadSolutionAsync(int generation = 0)
    {
        var ws = _workspace;
        var path = _solutionPath;
        if (ws == null || path == null) return;
        var sw = Stopwatch.StartNew();
        await _gate.WaitAsync();
        try
        {
            if (generation != 0 && generation != _loadGeneration) return; // 已被取代
            if (generation == 0 && path != _solutionPath) return;         // /reload 等旧调用，目标已变
            _dirty = false;
            _cwd = Path.GetDirectoryName(path) ?? _cwd;
            _status = new Status("loading", RelPath(path), 0, Array.Empty<string>());
            try
            {
                ws.CloseSolution();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var progress = new Progress<ProjectLoadProgress>(p =>
                {
                    if (seen.Add(p.FilePath))
                        Log("  project: " + RelPath(p.FilePath));
                });
                var solution = await ws.OpenSolutionAsync(path, progress);
                if (generation != 0 && generation != _loadGeneration) return; // 加载期间被取代
                var errors = ws.Diagnostics
                    .Where(d => d.Kind == WorkspaceDiagnosticKind.Failure)
                    .Select(d => Truncate(d.Message, 300))
                    .Take(20)
                    .ToArray();
                _status = new Status(
                    solution.Projects.Any() ? "ready" : "error",
                    RelPath(path),
                    solution.Projects.Count(),
                    errors);
                Log($"solution loaded: {solution.Projects.Count()} projects in {sw.Elapsed.TotalSeconds:F1}s (failures: {errors.Length})");
            }
            catch (Exception ex)
            {
                if (generation != 0 && generation != _loadGeneration) return;
                _status = new Status("error", RelPath(path), 0, new[] { Truncate(ex.Message, 300) });
                Log("solution load failed: " + ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------- 查询实现 ----------------

    private async Task<object> FindUsagesAsync(NameValueCollection q)
    {
        var sw = Stopwatch.StartNew();
        var targets = await ResolveTargetsAsync(q["symbol"], q["file"], ParseInt(q["line"]), ParseInt(q["col"]));
        var wantContainers = q["containers"] != "false";

        // 合并跨项目重复声明（partial 类在多个项目 Compilation 中是不同符号实例）
        var groups = targets
            .GroupBy(t => ContainmentChain(t) + "|" + t.Kind)
            .Select(g => g.First())
            .ToList();

        var solution = _workspace.CurrentSolution;
        var results = new List<object>();
        var total = 0;

        foreach (var sym in groups)
        {
            var refs = await SymbolFinder.FindReferencesAsync(sym, solution);
            var usages = new List<Dictionary<string, object>>();
            foreach (var rs in refs)
            {
                foreach (var loc in rs.Locations)
                {
                    var doc = loc.Document;
                    var text = await doc.GetTextAsync();
                    var linePos = loc.Location.GetLineSpan().StartLinePosition;

                    string container = null;
                    if (wantContainers && doc.SupportsSemanticModel)
                    {
                        var model = await doc.GetSemanticModelAsync();
                        var enclosing = model.GetEnclosingSymbol(loc.Location.SourceSpan.Start);
                        if (enclosing != null)
                            container = ContainmentChain(enclosing);
                    }

                    usages.Add(new Dictionary<string, object>
                    {
                        ["file"] = RelPath(doc.FilePath),
                        ["line"] = linePos.Line + 1,
                        ["col"] = linePos.Character + 1,
                        ["container"] = container,
                        ["text"] = Truncate(text.Lines[linePos.Line].ToString().Trim(), 200),
                    });
                }
            }

            var dedup = usages
                .GroupBy(u => (string)u["file"] + ":" + u["line"] + ":" + u["col"])
                .Select(g => g.First())
                .OrderBy(u => (string)u["file"])
                .ThenBy(u => (int)u["line"])
                .ToList();

            total += dedup.Count;
            results.Add(new
            {
                symbol = ContainmentChain(sym),
                kind = sym.Kind.ToString(),
                declaration = SourceLocs(sym),
                usageCount = dedup.Count,
                usages = dedup,
            });
        }

        sw.Stop();
        return new { ok = true, matched = groups.Count, totalUsages = total, elapsedMs = sw.ElapsedMilliseconds, results };
    }

    private async Task<object> HierarchyAsync(NameValueCollection q)
    {
        var sw = Stopwatch.StartNew();
        var mode = string.IsNullOrEmpty(q["mode"]) ? "derived" : q["mode"];
        var targets = await ResolveTargetsAsync(q["symbol"], q["file"], ParseInt(q["line"]), ParseInt(q["col"]));
        var type = targets.OfType<INamedTypeSymbol>().FirstOrDefault()
            ?? throw new InvalidOperationException("hierarchy queries need a named type (class/interface)");

        var solution = _workspace.CurrentSolution;
        IEnumerable<INamedTypeSymbol> result;
        if (mode == "derived")
        {
            result = await SymbolFinder.FindDerivedClassesAsync(type, solution);
        }
        else if (mode == "implementations")
        {
            result = await SymbolFinder.FindImplementationsAsync(type, solution);
        }
        else if (mode == "base")
        {
            var chain = new List<INamedTypeSymbol>();
            var b = type.BaseType;
            while (b != null)
            {
                chain.Add(b);
                b = b.BaseType;
            }
            chain.AddRange(type.AllInterfaces);
            result = chain;
        }
        else
        {
            throw new ArgumentException("mode must be one of: derived | implementations | base");
        }

        var list = result.ToList();
        sw.Stop();
        return new
        {
            ok = true,
            mode,
            symbol = ContainmentChain(type),
            count = list.Count,
            elapsedMs = sw.ElapsedMilliseconds,
            results = list.Select(t => new
            {
                name = ContainmentChain(t),
                kind = t.TypeKind.ToString(),
                declaration = SourceLocs(t),
            }),
        };
    }

    private async Task<object> SymbolsAsync(NameValueCollection q)
    {
        var query = q["query"];
        if (string.IsNullOrEmpty(query) || query.Length < 2)
            throw new ArgumentException("query= must be at least 2 characters");
        var limitRaw = ParseInt(q["limit"]);
        var limit = Math.Clamp(limitRaw > 0 ? limitRaw : 25, 1, 200);

        var sw = Stopwatch.StartNew();
        var solution = _workspace.CurrentSolution;
        var decls = await SymbolFinder.FindSourceDeclarationsAsync(
            solution,
            n => n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0,
            CancellationToken.None);

        var items = decls
            .GroupBy(d => ContainmentChain(d) + "|" + d.Kind)
            .Select(g => g.First())
            .Select(d => new
            {
                name = ContainmentChain(d),
                kind = d.Kind.ToString(),
                declaration = SourceLocs(d),
            })
            .OrderBy(i => i.name)
            .ThenBy(i => i.kind)
            .Take(limit)
            .ToList();

        sw.Stop();
        return new { ok = true, query, count = items.Count, elapsedMs = sw.ElapsedMilliseconds, results = (object)items };
    }

    /// <summary>
    /// 把查询解析为目标符号集合。
    /// 优先 file+line 精确定位（供 AI 先用文本搜索锁定声明处，再交给语义引擎）；
    /// 否则按符号名（支持 Ns.Type.Member 点分链，容器不匹配时退回同名全候选）。
    /// </summary>
    private async Task<List<ISymbol>> ResolveTargetsAsync(string symbolName, string file, int line, int col)
    {
        var solution = _workspace.CurrentSolution;

        if (!string.IsNullOrEmpty(file))
        {
            if (line <= 0)
                throw new ArgumentException("file given but line missing; pass line= (1-based)");
            var doc = FindDocument(solution, file)
                ?? throw new ArgumentException("document not part of solution: " + file);
            var text = await doc.GetTextAsync();
            if (line > text.Lines.Count)
                throw new ArgumentException($"line {line} out of range (1..{text.Lines.Count})");
            var textLine = text.Lines[line - 1];
            var lineStr = textLine.ToString();
            var firstOffset = 0;
            while (firstOffset < lineStr.Length && char.IsWhiteSpace(lineStr[firstOffset])) firstOffset++;
            var offset = col > 0
                ? Math.Min(col - 1, Math.Max(0, textLine.Span.Length - 1))
                : firstOffset;
            var position = textLine.Start + offset;

            var root = await doc.GetSyntaxRootAsync()
                ?? throw new InvalidOperationException("no syntax root for " + file);
            var model = await doc.GetSemanticModelAsync();

            // 无 col 时在整行内找声明符号（最内层命中）：
            // 行首 token 的祖先链会跳过字段/局部变量（声明符号在更深的 VariableDeclarator 上，
            // 而 FieldDeclaration 本身无声明符号），导致误命中外层类。
            // 优先取"完整落在行内"的声明中 span 最短者（如字段/局部变量），
            // 跨行声明（方法/类头）放宽为"与行相交"中最短者。
            if (col <= 0)
            {
                var lineSpan = textLine.Span;
                var nodes = root.DescendantNodes(lineSpan).ToList();
                ISymbol Pick(IEnumerable<SyntaxNode> ns) => ns
                    .Select(n => (Node: n, Sym: model.GetDeclaredSymbol(n)))
                    .Where(t => t.Sym is not null and not INamespaceSymbol)
                    .OrderBy(t => t.Node.Span.Length)
                    .Select(t => t.Sym)
                    .FirstOrDefault();
                var lineSymbol = Pick(nodes.Where(n => lineSpan.Contains(n.Span)))
                                ?? Pick(nodes.Where(n => n.Span.IntersectsWith(lineSpan)));
                if (lineSymbol != null)
                    return new List<ISymbol> { lineSymbol.OriginalDefinition };
            }

            for (var node = root.FindToken(position).Parent; node != null; node = node.Parent)
            {
                var s = model.GetDeclaredSymbol(node) ?? model.GetSymbolInfo(node).Symbol;
                if (s is INamespaceSymbol) continue;
                if (s != null)
                    return new List<ISymbol> { s.OriginalDefinition };
            }
            throw new InvalidOperationException($"no symbol found at {file}:{line}:{col}");
        }

        if (string.IsNullOrEmpty(symbolName))
            throw new ArgumentException("pass symbol=Name or symbol=Ns.Type.Member, or file=+line= for exact targeting");

        var parts = symbolName.Split('.');
        var leaf = parts[^1];
        var decls = (await SymbolFinder.FindSourceDeclarationsAsync(solution, leaf, false, CancellationToken.None)).ToList();
        if (decls.Count == 0)
            throw new InvalidOperationException("no source declaration named '" + leaf + "'");
        if (parts.Length == 1)
            return decls;

        var wanted = string.Join(".", parts);
        var filtered = decls.Where(d =>
        {
            var chain = ContainmentChain(d);
            return chain == wanted || chain.EndsWith("." + wanted, StringComparison.Ordinal);
        }).ToList();
        // 容器不匹配时退回全部同名候选，由结果分组自行区分
        return filtered.Count > 0 ? filtered : decls;
    }

    // ---------------- 调用层级 / 编译检查 / 文本搜索 ----------------
    // （原 AGENTS.md 规划的 rider 工具集，2026-10-09 以纯 Roslyn 实现并入，不再依赖 Rider IDE 运行）

    private sealed record CallSite(string File, int Line, int Col, string Caller, string Text);

    private sealed record SearchHit(string File, int Line, string Text);

    private sealed record ProblemsResult(
        string File, string Project, int ErrorCount, int WarningCount, int InfoCount,
        int Count, bool Truncated, List<object> Items);

    private async Task<object> CallsAsync(NameValueCollection q)
    {
        var sw = Stopwatch.StartNew();
        var direction = string.IsNullOrEmpty(q["direction"]) ? "both" : q["direction"];
        if (direction != "callers" && direction != "callees" && direction != "both")
            throw new ArgumentException("direction must be one of: callers | callees | both");

        var targets = await ResolveTargetsAsync(q["symbol"], q["file"], ParseInt(q["line"]), ParseInt(q["col"]));
        var methods = targets
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor)
            .GroupBy(m => ContainmentChain(m) + "|" + m.MethodKind)
            .Select(g => g.First())
            .ToList();
        if (methods.Count == 0)
            throw new InvalidOperationException(
                "analyze_calls 需要方法/构造器符号；属性与事件的引用查询用 find_usages，类型层级用 type_hierarchy");

        var solution = _workspace.CurrentSolution;
        var results = new List<object>();
        foreach (var m in methods)
        {
            object callers = null, callees = null;
            if (direction != "callees") callers = await FindCallersCoreAsync(m, solution);
            if (direction != "callers") callees = await FindCalleesCoreAsync(m, solution);
            results.Add(new
            {
                symbol = ContainmentChain(m),
                kind = m.MethodKind.ToString(),
                declaration = SourceLocs(m),
                callers,
                callees,
            });
        }

        sw.Stop();
        return new { ok = true, direction, matched = methods.Count, elapsedMs = sw.ElapsedMilliseconds, results };
    }

    /// <summary>
    /// callers：目标符号的全部引用位置里只保留真实调用表达式（调用/构造），
    /// nameof、方法组、声明等引用剔除；virtual/abstract 目标额外并入其全部 override
    /// 的派发约定（调用点绑定到 override 时同样命中）。
    /// </summary>
    private async Task<object> FindCallersCoreAsync(IMethodSymbol target, Solution solution)
    {
        var dispatch = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { target.OriginalDefinition };
        var isCtor = target.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor;
        if (!isCtor && target is { IsVirtual: true } or { IsOverride: true } or { IsAbstract: true })
        {
            foreach (var ov in await SymbolFinder.FindOverridesAsync(target, solution))
                dispatch.Add(ov.OriginalDefinition);
        }

        var refs = await SymbolFinder.FindReferencesAsync(target, solution);
        var sites = new List<(CallSite Site, string Caller)>();
        foreach (var r in refs)
        {
            foreach (var rl in r.Locations)
            {
                var doc = rl.Document;
                if (!doc.SupportsSemanticModel) continue;
                var model = await doc.GetSemanticModelAsync();
                var root = await doc.GetSyntaxRootAsync();
                if (root == null) continue;

                var callNode = FindEnclosingCallNode(root, rl.Location, isCtor);
                if (callNode == null) continue; // 非调用引用（声明/nameof/方法组）
                var info = model.GetSymbolInfo(callNode);
                var boundSym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                // 扩展方法调用点（x.Ext()）绑定到 reduced 形态符号，与声明形态（normal form）
                // 不相等——先经 ReducedFrom 归一回声明形态再比对，否则真实调用点会被误杀
                if (boundSym is IMethodSymbol { IsExtensionMethod: true, ReducedFrom: { } } reduced)
                    boundSym = reduced.ReducedFrom;
                var bound = boundSym?.OriginalDefinition;
                if (bound == null || !dispatch.Contains(bound)) continue; // 绑定到同名其他符号

                var text = await doc.GetTextAsync();
                var lp = rl.Location.GetLineSpan().StartLinePosition;
                // 调用点在 lambda/局部函数里时归到外层方法，调用方视图才聚合成一层
                var encl = model.GetEnclosingSymbol(rl.Location.SourceSpan.Start);
                while (encl is IMethodSymbol em && em.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction)
                    encl = em.ContainingSymbol;

                sites.Add((
                    new CallSite(RelPath(doc.FilePath), lp.Line + 1, lp.Character + 1, null,
                        Truncate(text.Lines[lp.Line].ToString().Trim(), 200)),
                    encl == null ? null : ContainmentChain(encl)));
            }
        }

        var dedup = sites
            .GroupBy(x => x.Site.File + ":" + x.Site.Line + ":" + x.Site.Col)
            .Select(g => g.First())
            .OrderBy(x => x.Site.File)
            .ThenBy(x => x.Site.Line)
            .ToList();
        var byCaller = dedup
            .GroupBy(x => x.Caller ?? "(unknown)")
            .Select(g => new { caller = g.Key, callSites = g.Count() })
            .OrderBy(x => x.caller)
            .ToList();

        return new
        {
            callerCount = byCaller.Count,
            callSiteCount = dedup.Count,
            callers = byCaller,
            callSites = dedup.Select(x => new
            {
                file = x.Site.File,
                line = x.Site.Line,
                col = x.Site.Col,
                caller = x.Caller,
                text = x.Site.Text,
            }),
        };
    }

    /// <summary>callees：方法/构造器体内全部直接调用点（方法调用 + 对象构造），按被调目标聚合。</summary>
    private async Task<object> FindCalleesCoreAsync(IMethodSymbol target, Solution solution)
    {
        if (target.DeclaringSyntaxReferences.Length == 0)
            return new { calleeCount = 0, callees = Array.Empty<object>(), note = "no source declaration" };

        var calls = new List<(ISymbol Sym, string Kind, CallSite Site)>();
        foreach (var decl in target.DeclaringSyntaxReferences)
        {
            var syntax = await decl.GetSyntaxAsync();
            var doc = solution.GetDocument(syntax.SyntaxTree);
            if (doc == null || !doc.SupportsSemanticModel) continue;
            var model = await doc.GetSemanticModelAsync();
            var text = await doc.GetTextAsync();

            foreach (var node in syntax.DescendantNodes())
            {
                string kind;
                if (node is InvocationExpressionSyntax) kind = "method";
                else if (node is ObjectCreationExpressionSyntax) kind = "constructor";
                else continue;
                var info = model.GetSymbolInfo(node);
                if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not IMethodSymbol sym) continue;

                var lp = node.GetLocation().GetLineSpan().StartLinePosition;
                calls.Add((sym.OriginalDefinition, kind,
                    new CallSite(RelPath(doc.FilePath), lp.Line + 1, lp.Character + 1, null,
                        Truncate(text.Lines[lp.Line].ToString().Trim(), 200))));
            }
        }

        var callees = calls
            .GroupBy(c => ContainmentChain(c.Sym) + "|" + c.Kind)
            .Select(g => new
            {
                callee = ContainmentChain(g.First().Sym),
                kind = g.Key[(g.Key.LastIndexOf('|') + 1)..],
                callSites = g.Count(),
                locations = g.Select(x => new { file = x.Site.File, line = x.Site.Line, col = x.Site.Col })
                    .OrderBy(x => x.file)
                    .ThenBy(x => x.line)
                    .ToList(),
            })
            .OrderBy(x => x.callee)
            .ToList();

        return new { calleeCount = callees.Count, callees };
    }

    /// <summary>引用位置向上找最内层的调用/构造节点；找不到即非调用引用。</summary>
    private static SyntaxNode FindEnclosingCallNode(SyntaxNode root, Location loc, bool isCtor)
    {
        var span = loc.SourceSpan;
        return root.DescendantNodes(span)
            .Where(n => n.Span.Contains(span))
            .Where(n => isCtor ? n is ObjectCreationExpressionSyntax : n is InvocationExpressionSyntax)
            .OrderBy(n => n.Span.Length)
            .FirstOrDefault();
    }

    private async Task<object> FileProblemsAsync(NameValueCollection q)
    {
        var sw = Stopwatch.StartNew();
        var file = q["file"];
        if (string.IsNullOrEmpty(file))
            throw new ArgumentException("file= required（其他工具输出的相对路径原样回传即可）");
        var doc = FindDocument(_workspace.CurrentSolution, file)
            ?? throw new ArgumentException("document not part of solution: " + file);
        var r = await ProblemsCoreAsync(doc, q["severity"]);
        sw.Stop();
        return new
        {
            ok = true,
            elapsedMs = sw.ElapsedMilliseconds,
            file = r.File,
            project = r.Project,
            errorCount = r.ErrorCount,
            warningCount = r.WarningCount,
            infoCount = r.InfoCount,
            count = r.Count,
            truncated = r.Truncated,
            problems = r.Items,
        };
    }

    private async Task<object> LintFilesAsync(NameValueCollection q)
    {
        var sw = Stopwatch.StartNew();
        var raw = q["files"] ?? q["file"];
        if (string.IsNullOrEmpty(raw))
            throw new ArgumentException("files= required（多文件用 ; 或 , 分隔）");
        var files = raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (files.Length == 0)
            throw new ArgumentException("files= required（多文件用 ; 或 , 分隔）");

        var results = new List<object>();
        var total = 0;
        var withProblems = 0;
        foreach (var f in files)
        {
            var doc = FindDocument(_workspace.CurrentSolution, f);
            if (doc == null)
            {
                results.Add(new { file = f, error = "document not part of solution" });
                continue;
            }
            var r = await ProblemsCoreAsync(doc, q["severity"]);
            total++;
            if (r.ErrorCount + r.WarningCount > 0) withProblems++;
            results.Add(new
            {
                file = r.File,
                project = r.Project,
                errorCount = r.ErrorCount,
                warningCount = r.WarningCount,
                infoCount = r.InfoCount,
                count = r.Count,
                problems = r.Items.Take(20).ToList(),
            });
        }

        sw.Stop();
        return new { ok = true, elapsedMs = sw.ElapsedMilliseconds, files = total, withProblems, results };
    }

    /// <summary>编译诊断：solution 加载走 DesignTimeBuild，此处按文件所属项目内存编译取真实诊断。</summary>
    private async Task<ProblemsResult> ProblemsCoreAsync(Document doc, string minSeverity)
    {
        var project = doc.Project;
        var compilation = await project.GetCompilationAsync()
            ?? throw new InvalidOperationException("no compilation for project " + project.Name);
        var tree = await doc.GetSyntaxTreeAsync()
            ?? throw new InvalidOperationException("no syntax tree for " + doc.FilePath);
        var text = await doc.GetTextAsync();

        var floor = minSeverity switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info,
        };

        var diags = compilation.GetDiagnostics(CancellationToken.None)
            .Where(d => d.Location.SourceTree == tree && d.Severity >= floor)
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .ToList();

        const int maxItems = 200;
        var items = diags.Take(maxItems)
            .Select(d =>
            {
                var lp = d.Location.GetLineSpan().StartLinePosition;
                var line = lp.Line < text.Lines.Count ? Truncate(text.Lines[lp.Line].ToString().Trim(), 200) : "";
                return (object)new Dictionary<string, object>
                {
                    ["severity"] = d.Severity.ToString().ToLowerInvariant(),
                    ["id"] = d.Id,
                    ["line"] = lp.Line + 1,
                    ["col"] = lp.Character + 1,
                    ["message"] = Truncate(d.GetMessage(), 300),
                    ["text"] = line,
                };
            })
            .ToList();

        return new ProblemsResult(
            RelPath(doc.FilePath),
            project.Name,
            diags.Count(d => d.Severity == DiagnosticSeverity.Error),
            diags.Count(d => d.Severity == DiagnosticSeverity.Warning),
            diags.Count(d => d.Severity == DiagnosticSeverity.Info),
            diags.Count,
            diags.Count > maxItems,
            items);
    }

    // 文本搜索目录黑名单（按目录名单段匹配）；Tools 不在其中，CodeQuery 自身源码可被搜到
    private static readonly string[] TextSearchSkipDirs =
    {
        ".git", ".vs", ".zcode", ".codequery", "Library", "Temp", "Logs", "Obj", "Obj64",
        "obj", "bin", "UserSettings", "MemoryCaptures", "Recordings", "Builds", "Build",
        "node_modules", "Perf",
    };

    private static readonly HashSet<string> TextSearchExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".meta", ".json", ".asmdef", ".asmref", ".xml", ".md", ".txt", ".yml", ".yaml", ".ini", ".config",
        ".shader", ".cginc", ".hlsl", ".compute", ".glsl", ".uxml", ".uss",
        ".mat", ".unity", ".prefab", ".asset", ".controller", ".anim", ".playable", ".signal",
        ".inputactions", ".sln", ".csproj", ".props", ".targets", ".rsp", ".html", ".css", ".js",
    };

    private async Task<object> SearchTextAsync(NameValueCollection q)
    {
        var sw = Stopwatch.StartNew();
        var pattern = q["pattern"];
        if (string.IsNullOrEmpty(pattern))
            throw new ArgumentException("pattern= required");
        var useRegex = q["regex"] == "true";
        var ignoreCase = q["ignorecase"] != "false";
        var limit = Math.Clamp(ParseInt(q["limit"]) > 0 ? ParseInt(q["limit"]) : 50, 1, 500);
        const long maxFileBytes = 5 * 1024 * 1024;

        var rootDir = _cwd;
        if (!string.IsNullOrEmpty(q["root"]))
        {
            rootDir = Path.GetFullPath(Path.Combine(_cwd, q["root"].Replace('/', Path.DirectorySeparatorChar)));
            if (!rootDir.StartsWith(_cwd, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(rootDir))
                throw new ArgumentException("root= not a directory under solution: " + q["root"]);
        }

        IEnumerable<string> exts = TextSearchExts;
        if (!string.IsNullOrEmpty(q["ext"]))
            exts = q["ext"].Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.StartsWith('.') ? e : "." + e);
        var extSet = new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);

        Regex rx = null;
        if (useRegex)
        {
            var options = RegexOptions.CultureInvariant | RegexOptions.Compiled
                | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            rx = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }

        var hits = new List<SearchHit>();
        var matchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;

        // Assets 全树数万文件，串行读盘要两分钟级；并行读 + 凑满 limit 即取消收尾
        var cancel = new CancellationTokenSource();
        var locker = new object();
        try
        {
            Parallel.ForEach(
                SafeWalk(rootDir, extSet),
                new ParallelOptions
                {
                    CancellationToken = cancel.Token,
                    MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1),
                },
                path =>
                {
                    if (cancel.IsCancellationRequested) return;
                    try
                    {
                        var fi = new FileInfo(path);
                        if (!fi.Exists || fi.Length > maxFileBytes) return;
                        Interlocked.Increment(ref scanned);

                        string content;
                        using (var reader = new StreamReader(path, new UTF8Encoding(false), true))
                            content = reader.ReadToEnd();

                        var lines = content.Split('\n');
                        var fileHits = new List<SearchHit>();
                        for (var i = 0; i < lines.Length && fileHits.Count < 50; i++)
                        {
                            var line = lines[i].TrimEnd('\r');
                            var hit = rx != null
                                ? rx.IsMatch(line)
                                : line.IndexOf(pattern, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0;
                            if (!hit) continue;
                            fileHits.Add(new SearchHit(RelPath(path), i + 1, Truncate(line.Trim(), 240)));
                        }
                        if (fileHits.Count == 0) return;
                        lock (locker)
                        {
                            hits.AddRange(fileHits);
                            matchedFiles.Add(path);
                            if (hits.Count >= limit) cancel.Cancel();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        // 单文件读取/解码失败不影响整体搜索
                    }
                });
        }
        catch (OperationCanceledException) { }

        hits.Sort((a, b) =>
        {
            var c = StringComparer.OrdinalIgnoreCase.Compare(a.File, b.File);
            return c != 0 ? c : a.Line.CompareTo(b.Line);
        });
        var truncated = hits.Count > limit;
        var matches = hits
            .Take(limit)
            .Select(h => (object)new { file = h.File, line = h.Line, text = h.Text })
            .ToList();
        cancel.Dispose();

        sw.Stop();
        return new
        {
            ok = true,
            pattern,
            regex = useRegex,
            scannedFiles = scanned,
            matchedFiles = matchedFiles.Count,
            count = matches.Count,
            truncated,
            elapsedMs = sw.ElapsedMilliseconds,
            results = matches,
        };
    }

    /// <summary>手工目录遍历：任一级目录命中黑名单即剪枝，权限错误跳过该目录继续。</summary>
    private static IEnumerable<string> SafeWalk(string root, HashSet<string> extSet)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string[] files, subDirs;
            var dir = pending.Pop();
            try
            {
                files = Directory.GetFiles(dir);
                subDirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var f in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                if (extSet.Contains(Path.GetExtension(f)))
                    yield return f;

            foreach (var d in subDirs.OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
                if (!TextSearchSkipDirs.Contains(Path.GetFileName(d)))
                    pending.Push(d);
        }
    }

    // ---------------- 工具方法 ----------------

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "...";

    private string RelPath(string p)
    {
        if (p == null) return null;
        try { return Path.GetRelativePath(_cwd, p); }
        catch { return p; }
    }

    private Document FindDocument(Solution solution, string file)
    {
        // 工具输出的是相对 solution 目录的路径，AI 原样回传时不能依赖进程 cwd 解析
        var candidates = Path.IsPathRooted(file)
            ? new[] { Path.GetFullPath(file) }
            : new[] { Path.GetFullPath(file), Path.GetFullPath(Path.Combine(_cwd, file)) };
        return solution.Projects
            .SelectMany(p => p.Documents)
            .FirstOrDefault(d => candidates.Any(c => string.Equals(d.FilePath, c, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>符号的命名空间 + 嵌套类型 + 自身名字链，如 Funbrew.FruitBomb.OnExplode。</summary>
    private static string ContainmentChain(ISymbol s)
    {
        var names = new List<string> { s.Name };
        var cur = s.ContainingSymbol;
        while (cur is INamedTypeSymbol t)
        {
            names.Add(t.Name);
            cur = cur.ContainingSymbol;
        }
        var ns = new List<string>();
        while (cur is INamespaceSymbol n && !n.IsGlobalNamespace)
        {
            ns.Add(n.Name);
            cur = n.ContainingNamespace;
        }
        ns.Reverse();
        names.Reverse();
        return string.Join(".", ns.Concat(names));
    }

    private IEnumerable<object> SourceLocs(ISymbol s) =>
        s.Locations
            .Where(l => l.IsInSource && l.SourceTree != null)
            .Select(l => new
            {
                file = RelPath(l.SourceTree.FilePath),
                line = l.GetLineSpan().StartLinePosition.Line + 1,
                col = l.GetLineSpan().StartLinePosition.Character + 1,
            })
            .ToList();

    private static void Log(string message) => Program.Log(message);
}

/// <summary>
/// 极简 MCP stdio 服务器：newline-delimited JSON-RPC，只实现 AI 查询所需的最小协议面
/// （initialize / tools/list / tools/call / ping）。工具集见 QueryServer 类注释。
/// </summary>
public sealed class McpStdioServer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly string ProtocolVersion = "2025-06-18";

    private readonly QueryServer _engine;

    /// <summary>协议输出专用 writer；Console.Out 整体被劫持到 stderr，杜绝杂散输出污染协议。</summary>
    private TextWriter _protocolOut;

    public McpStdioServer(QueryServer engine) => _engine = engine;

    public async Task RunAsync()
    {
        _protocolOut = Console.Out;
        Console.SetOut(Console.Error); // 任何组件误写 Console.WriteLine 都不会污染协议通道

        // 不走 Console.In：绕开 InputEncoding 重置等宿主差异，直接按 UTF-8 读原始 stdin 流
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false);
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Program.Log($"recv line len={line.Length}: {TruncateLine(line)}");
            try
            {
                await HandleAsync(line);
            }
            catch (Exception ex)
            {
                Program.Log("mcp message handling failed: " + ex.Message);
            }
        }
        Program.Log("stdin closed, exiting");
    }

    private async Task HandleAsync(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var methodEl))
            return;
        var method = methodEl.GetString();
        var hasId = root.TryGetProperty("id", out var idEl);

        object result = null;
        object error = null;

        switch (method)
        {
            case "initialize":
                var requested = ProtocolVersion;
                if (root.TryGetProperty("params", out var p) &&
                    p.TryGetProperty("protocolVersion", out var pv) &&
                    pv.ValueKind == JsonValueKind.String)
                    requested = pv.GetString();
                // MCP 规范：应答必须是本服务器支持的版本；原样回显未知版本会让客户端误判能力集
                if (requested != "2024-11-05" && requested != "2025-03-26" && requested != "2025-06-18")
                    requested = ProtocolVersion;
                result = new
                {
                    protocolVersion = requested,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "codequery", title = "CodeQuery (Roslyn 语义查询)", version = QueryServer.Version },
                };
                break;

            case "notifications/initialized":
                return; // 通知，不应答

            case "ping":
                result = new { };
                break;

            case "tools/list":
                result = new { tools = BuildToolDefinitions() };
                break;

            case "tools/call":
                result = await CallToolAsync(root);
                break;

            default:
                if (!hasId) return; // 未知通知，忽略
                error = new { code = -32601, message = "method not found: " + method };
                break;
        }

        if (!hasId) return;
        object message = error == null
            ? new { jsonrpc = "2.0", id = idEl.Clone(), result }
            : new { jsonrpc = "2.0", id = idEl.Clone(), error };
        _protocolOut.WriteLine(JsonSerializer.Serialize(message, JsonOpts));
        _protocolOut.Flush();
    }

    private async Task<object> CallToolAsync(JsonElement root)
    {
        string tool = null;
        try
        {
            var prms = root.GetProperty("params");
            tool = prms.GetProperty("name").GetString();
            var q = new NameValueCollection();
            if (prms.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Object)
                foreach (var prop in argsEl.EnumerateObject())
                    q[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.ToString();

            var payload = await _engine.QueryAsync(tool, q);
            return TextResult(JsonSerializer.Serialize(payload, JsonOpts), isError: false);
        }
        catch (Exception ex)
        {
            Program.Log($"tool '{tool}' failed: {ex.Message}");
            return TextResult($"tool '{tool}' failed: {ex.Message}", isError: true);
        }
    }

    private static object TextResult(string text, bool isError) => new
    {
        content = new object[] { new { type = "text", text } },
        isError,
    };

    private static string TruncateLine(string s) =>
        s.Length <= 80 ? s : s.Substring(0, 80) + "...(" + s.Length + ")";

    // ---------------- 工具定义 ----------------

    private static object[] BuildToolDefinitions() => new[]
    {
        MakeTool(
            "find_usages",
            "Roslyn 语义级 Find Usages：返回某符号在整个 solution 中的全部引用（file/line/col/container/所在行代码）。" +
            "支持字段、属性、方法、类型等任何符号，查字段（如 PlayerBehaviour.spawn）被谁使用必须用本工具；" +
            "方法调用点层级用 analyze_calls，类型层级用 type_hierarchy。" +
            "传 symbol=简单名或 Ns.Type.Member 点分链；或传 file+line(+col) 精确指向声明处。",
            """{"type":"object","properties":{"symbol":{"type":"string","description":"符号名，如 spawn 或 PlayerBehaviour.spawn"},"file":{"type":"string","description":"声明所在文件路径（与 line 搭配，优先于 symbol）"},"line":{"type":"integer","description":"1-based 行号"},"col":{"type":"integer","description":"1-based 列号，可省略"},"containers":{"type":"boolean","description":"是否附带每处引用的外层容器链，默认 true"},"solution":{"type":"string","description":"多项目清单(default-sln.txt)中按路径/文件名子串选择 solution，单项目可省略"}}}"""),
        MakeTool(
            "type_hierarchy",
            "Roslyn 类型层级：derived=全部子类，implementations=接口实现类，base=基类链+全部接口。补 Rider MCP 缺失的类型层级能力。" +
            "传 symbol=类/接口名（如 PlayerState），或 file+line 指向声明处；mode 默认 derived。",
            """{"type":"object","properties":{"symbol":{"type":"string","description":"类或接口名"},"file":{"type":"string"},"line":{"type":"integer"},"mode":{"type":"string","enum":["derived","implementations","base"],"description":"默认 derived"},"solution":{"type":"string","description":"多项目清单中按路径/文件名子串选择 solution，单项目可省略"}}}"""),
        MakeTool(
            "search_symbols",
            "按名称子串模糊搜索 solution 内全部符号声明（类/方法/字段等），返回符号全名、种类与声明位置。" +
            "适合先定位符号再用 find_usages 深挖。query 至少 2 个字符，limit 默认 25。",
            """{"type":"object","properties":{"query":{"type":"string","description":"名称子串，≥2 字符"},"limit":{"type":"integer","description":"默认 25，最大 200"},"solution":{"type":"string","description":"多项目清单中按路径/文件名子串选择 solution，单项目可省略"}},"required":["query"]}"""),
        MakeTool(
            "analyze_calls",
            "Roslyn 方法调用层级：direction=callers 返回整个 solution 中谁调用了它（每个调用点含 file/line 与调用方容器链，virtual/abstract 按派发匹配其 override），direction=callees 返回它方法体内调用了谁；默认 both。" +
            "仅方法/构造器；查字段属性引用用 find_usages，类型层级用 type_hierarchy。" +
            "传 symbol=Ns.Type.Member 点分链，或 file+line(+col) 指向方法声明或调用处。",
            """{"type":"object","properties":{"symbol":{"type":"string","description":"方法名或 Ns.Type.Member 点分链"},"file":{"type":"string","description":"方法声明（或调用处）所在文件，与 line 搭配，优先于 symbol"},"line":{"type":"integer","description":"1-based 行号"},"col":{"type":"integer","description":"1-based 列号，可省略"},"direction":{"type":"string","enum":["callers","callees","both"],"description":"默认 both"},"solution":{"type":"string","description":"多项目清单中按路径/文件名子串选择 solution，单项目可省略"}}}"""),
        MakeTool(
            "get_file_problems",
            "Roslyn 编译诊断：返回单个源文件的编译错误/警告/信息（含 id、行列、消息、所在行代码）。改接口/签名/引用后用它确认编译状态。severity=error|warning 过滤，默认 info 起全量。",
            """{"type":"object","properties":{"file":{"type":"string","description":"文件路径（其他工具输出的相对路径原样回传即可）"},"severity":{"type":"string","enum":["error","warning","info"],"description":"最低严重级，默认 info"},"solution":{"type":"string","description":"多项目清单中按路径/文件名子串选择 solution，单项目可省略"}},"required":["file"]}"""),
        MakeTool(
            "lint_files",
            "批量编译检查：files=a.cs;b.cs 传多个文件（; 或 , 分隔），每文件返回错误/警告/信息计数与前 20 条明细。文件多时先总览再对可疑文件用 get_file_problems 看全量。",
            """{"type":"object","properties":{"files":{"type":"string","description":"分号或逗号分隔的多个文件路径"},"severity":{"type":"string","enum":["error","warning","info"],"description":"最低严重级，默认 info"},"solution":{"type":"string","description":"多项目清单中按路径/文件名子串选择 solution，单项目可省略"}},"required":["files"]}"""),
        MakeTool(
            "search_text",
            "solution 目录内文本搜索（字符串字面量、注释、配置、资源路径、资产 GUID 等非语义内容）：默认忽略大小写字面匹配，regex=true 切正则（2 秒超时保护）；ext= 自定义扩展名白名单（默认含 cs/meta/json/unity/prefab/asset 等），root= 限定 solution 相对子目录，limit 默认 50。" +
            "查代码符号引用一律 find_usages，不要用本工具。",
            """{"type":"object","properties":{"pattern":{"type":"string","description":"匹配内容，≥1 字符"},"regex":{"type":"boolean","description":"true=按正则解释，默认 false 字面匹配"},"ignorecase":{"type":"boolean","description":"默认 true"},"ext":{"type":"string","description":"逗号分隔扩展名白名单，如 cs,meta,json；默认内置清单"},"root":{"type":"string","description":"限定搜索的 solution 相对子目录，如 Assets"},"limit":{"type":"integer","description":"结果上限，默认 50，最大 500"},"solution":{"type":"string","description":"多项目清单中按路径/文件名子串选择 solution，单项目可省略"}},"required":["pattern"]}"""),
    };

    private static object MakeTool(string name, string description, string inputSchemaJson)
    {
        using var doc = JsonDocument.Parse(inputSchemaJson);
        return new { name, description, inputSchema = doc.RootElement.Clone() };
    }
}
