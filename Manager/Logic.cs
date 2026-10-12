using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace CodeQueryManager;

/// <summary>环境体检：MSBuild 源（CodeQuery 运行前提）与 Unity Pipeline CLI 的定位。</summary>
public static class Health
{
    /// <summary>找 dotnet SDK（CodeQuery 的 MSBuild 源）。返回描述文本；找不到返回 null（VS/Rider 自带 MSBuild 亦可，但本检测只认 SDK）。</summary>
    public static string FindMsbuild()
    {
        var roots = new List<string>();
        var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env)) roots.Add(env);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var sdkRoot = Path.Combine(root, "sdk");
            if (!File.Exists(Path.Combine(root, "dotnet.exe")) || !Directory.Exists(sdkRoot)) continue;
            var best = Directory.GetDirectories(sdkRoot)
                .Select(Path.GetFileName)
                .Where(n => Version.TryParse(n, out _))
                .OrderByDescending(Version.Parse)
                .FirstOrDefault();
            if (best != null) return $"dotnet SDK {best}（{root}）";
        }
        return null;
    }

    /// <summary>找 Unity Pipeline CLI（unity.exe，WindowsApps 别名优先，PATH 兜底）。返回完整路径或 null。</summary>
    public static string FindUnityCli()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "unity.exe"),
        };
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.GetFullPath(Path.Combine(dir.Trim(), "unity.exe"))); }
            catch { }
        }
        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }
}

/// <summary>Unity 项目发现：Unity Hub 列表（主源，含编辑器版本）→ 编辑器注册表 MRU（兜底）。</summary>
public static class Discovery
{
    public sealed record UnityProject(string Path, string Title, string EditorVersion, string Source);

    public static List<UnityProject> Discover()
    {
        var byPath = new Dictionary<string, UnityProject>(StringComparer.OrdinalIgnoreCase);

        // 1) Unity Hub：%APPDATA%\UnityHub\projects-v1.json，格式 {"schema_version":"v1","data":{"<路径>":{path,title,version,...}}}
        try
        {
            var hubFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UnityHub", "projects-v1.json");
            if (File.Exists(hubFile))
            {
                if (JsonNode.Parse(File.ReadAllText(hubFile))?["data"] is JsonObject data)
                    foreach (var prop in data)
                    {
                        var p = prop.Value?["path"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(p)) continue;
                        byPath[p] = new UnityProject(
                            p,
                            prop.Value?["title"]?.GetValue<string>() ?? System.IO.Path.GetFileName(p.TrimEnd('\\')),
                            prop.Value?["version"]?.GetValue<string>() ?? "",
                            "Unity Hub");
                    }
            }
        }
        catch { /* Hub 存储格式变化时静默落兜底 */ }

        // 2) Unity 编辑器 MRU：HKCU\...\Unity Editor 5.x\RecentlyUsedProjectPath-*（正斜杠路径）
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Unity Technologies\Unity Editor 5.x");
            if (k != null)
                foreach (var name in k.GetValueNames())
                {
                    if (!name.StartsWith("RecentlyUsedProjectPath", StringComparison.OrdinalIgnoreCase)) continue;
                    if (k.GetValue(name) is not string p || string.IsNullOrEmpty(p)) continue;
                    p = p.Replace('/', '\\');
                    if (!byPath.ContainsKey(p))
                        byPath[p] = new UnityProject(p, Path.GetFileName(p.TrimEnd('\\')), "", "注册表 MRU");
                }
        }
        catch { }

        // 只保留仍然存在且确是 Unity 项目（有 Assets）的目录，避免 Hub/注册表里的已删项目
        return byPath.Values
            .Where(x => Directory.Exists(x.Path) && Directory.Exists(Path.Combine(x.Path, "Assets")))
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>安装/更新内嵌 payload（CodeQuery 发布目录 + 源码 + README）到本机目录。</summary>
public static class Installer
{
    public static bool IsInstalled(string installDir) =>
        File.Exists(Path.Combine(installDir, "publish", "CodeQuery.exe"));

    public static string InstalledVersion(string installDir)
    {
        try { return File.ReadAllText(Path.Combine(installDir, "publish", "VERSION.txt")).Trim(); }
        catch { return "?"; }
    }

    /// <summary>解压内嵌 payload 到 installDir（覆盖式）。会先停止运行中的 CodeQuery/BuildHost 实例（文件锁）。</summary>
    public static (int Files, string Version) ExtractPayload(string installDir, Action<string> log)
    {
        foreach (var name in new[] { "CodeQuery", "Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); log($"已停止运行中的 {name}（PID {p.Id}）——各客户端活跃查询会断开，重开会话即恢复"); }
                catch { }
            }

        using var stream = typeof(Installer).Assembly.GetManifestResourceStream("Payload.zip")
            ?? throw new InvalidOperationException("内嵌 payload 缺失（构建不完整，请用 build-installer.ps1 重新构建）");
        var installRoot = Path.GetFullPath(installDir);
        var count = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue; // 目录占位项
                var target = Path.GetFullPath(Path.Combine(installRoot, e.FullName));
                if (!target.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                    continue; // zip 路径穿越防护
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: true);
                count++;
            }
        return (count, InstalledVersion(installDir));
        // 注：default-sln.txt 不在 payload 内（机器状态），覆盖安装不会动它
    }
}

/// <summary>
/// 为 Unity 项目写入/移除 ZCode workspace 级 MCP 注册（.zcode/config.json）。
/// 只管理自己名下的键：codequery 与 unity-&lt;项目名&gt;；无关条目原样保留。
/// </summary>
public static class ConfigWriter
{
    public sealed record EnableResult(string ConfigPath, string SolutionPath, string UnityServerName, List<string> Actions);

    /// <summary>项目根目录找 .sln；优先与项目同名的，否则任取第一个；没有返回 null。</summary>
    public static string FindSolution(string projectDir)
    {
        var slns = Directory.GetFiles(projectDir, "*.sln");
        if (slns.Length == 0) return null;
        var prefer = Path.Combine(projectDir, Path.GetFileName(projectDir.TrimEnd('\\')) + ".sln");
        return slns.FirstOrDefault(s => string.Equals(s, prefer, StringComparison.OrdinalIgnoreCase)) ?? slns[0];
    }

    /// <summary>项目文件夹名 → unity 服务器名："On Any Journey"→unity-on-any-journey，"Project_Ex_Nihilo"→unity-project-ex-nihilo。</summary>
    public static string UnityServerName(string projectDir)
    {
        var name = Path.GetFileName(projectDir.TrimEnd('\\')).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        return "unity-" + sb.ToString().Trim('-');
    }

    private static string ConfigPath(string projectDir) =>
        Path.Combine(projectDir, ".zcode", "config.json");

    /// <summary>写入/更新两个 MCP 条目。返回落盘信息与动作日志；解析失败的旧 config 会先备份再重建。</summary>
    public static EnableResult Enable(string projectDir, string installDir, string unityCli)
    {
        var file = ConfigPath(projectDir);
        var actions = new List<string>();
        JsonObject root;
        if (File.Exists(file))
        {
            try { root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject(); }
            catch
            {
                var backup = file + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(file, backup);
                actions.Add($"原 config.json 解析失败，已备份为 {Path.GetFileName(backup)} 并重建");
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        var mcp = root["mcp"] as JsonObject ?? new JsonObject();
        root["mcp"] = mcp;
        var servers = mcp["servers"] as JsonObject ?? new JsonObject();
        mcp["servers"] = servers;

        var sln = FindSolution(projectDir);
        var slnForConfig = sln ?? Path.Combine(projectDir, Path.GetFileName(projectDir.TrimEnd('\\')) + ".sln");
        if (sln == null)
            actions.Add("警告：项目根目录没有 .sln（需用 Unity 打开过项目让其生成）；已按约定名写入，生成前 codequery 查询会报加载失败");

        servers["codequery"] = new JsonObject
        {
            ["command"] = Path.Combine(installDir, "publish", "CodeQuery.exe"),
            ["args"] = new JsonArray("mcp", "--solution", slnForConfig),
        };

        var unityName = UnityServerName(projectDir);
        // workspace 级的 unity-* 条目只该指向本项目：项目改名/旧条目一律清掉只留当前
        var stale = servers
            .Where(kv => kv.Key.StartsWith("unity-", StringComparison.OrdinalIgnoreCase) && kv.Key != unityName)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var s in stale)
        {
            servers.Remove(s);
            actions.Add($"移除旧 Unity 条目 {s}（当前应为 {unityName}）");
        }

        if (!string.IsNullOrEmpty(unityCli))
        {
            servers[unityName] = new JsonObject
            {
                ["command"] = unityCli,
                ["args"] = new JsonArray("mcp", "--project-path", projectDir),
            };
        }
        else
        {
            actions.Add("警告：未检测到 unity.exe（Unity Pipeline CLI），Unity MCP 条目未写入；可先装 Unity 6000.x 后重试");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        actions.Add("已写入 " + file);
        return new EnableResult(file, slnForConfig, unityName, actions);
    }

    /// <summary>移除本工具写入的全部条目（codequery + unity-*），保留其余内容。</summary>
    public static List<string> Disable(string projectDir)
    {
        var file = ConfigPath(projectDir);
        if (!File.Exists(file)) return new List<string> { "无 .zcode/config.json，无需清理" };
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
        catch { return new List<string> { "config.json 解析失败，未改动" }; }
        if (root == null) return new List<string> { "config.json 非对象，未改动" };
        if (root["mcp"]?["servers"] is not JsonObject servers)
            return new List<string> { "无 mcp.servers，无需清理" };

        var removed = new List<string>();
        foreach (var key in servers.Select(kv => kv.Key).ToList())
            if (key == "codequery" || key.StartsWith("unity-", StringComparison.OrdinalIgnoreCase))
            {
                servers.Remove(key);
                removed.Add(key);
            }
        if (removed.Count == 0) return new List<string> { "未发现本工具写入的条目" };

        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return new List<string> { "已移除：" + string.Join(", ", removed) };
    }

    /// <summary>该项目是否已配置（存在 codequery 条目即视为已配置）。</summary>
    public static bool IsConfigured(string projectDir)
    {
        try
        {
            var file = ConfigPath(projectDir);
            if (!File.Exists(file)) return false;
            return JsonNode.Parse(File.ReadAllText(file))?["mcp"]?["servers"]?["codequery"] is not null;
        }
        catch { return false; }
    }
}

/// <summary>管理器自身设置（安装目录记忆），存 %APPDATA%\CodeQueryManager\settings.json。</summary>
public static class Settings
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodeQueryManager", "settings.json");

    public static string LoadInstallDir()
    {
        try { var v = File.ReadAllText(FilePath).Trim(); return v.Length > 0 ? v : null; }
        catch { return null; }
    }

    public static void SaveInstallDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, dir);
        }
        catch { }
    }
}

/// <summary>
/// qoder（Rider 插件）注册：写入/移除用户级 %USERPROFILE%\.qoder-cn\shared_client\mcp.json 的 codequery 条目。
/// 只管理自己名下的键，无关服务器条目原样保留。2026-10-10 实测结论（详见 CodeQuery README）：
/// qoder 守护进程（QoderCN.exe，跨 Rider 重启存活）只读此文件（项目级 .qoder/settings.json 不生效），
/// 且仅在自身启动时扫描一次——/mcp reload、新会话、Rider 重启都不会重扫，写入后必须重启守护进程。
/// </summary>
public static class QoderConfigWriter
{
    public sealed record EnableResult(string ConfigPath, List<string> Actions);

    // 与 CodeQuery 本体的 JSON 输出策略一致：中文等非 ASCII 原样输出，文件保持可读
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string ConfigPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".qoder-cn", "shared_client", "mcp.json");

    /// <summary>qoder 守护进程是否在运行。</summary>
    public static (bool Running, int Pid) DaemonStatus()
    {
        var p = Process.GetProcessesByName("QoderCN").FirstOrDefault();
        return (p != null, p?.Id ?? 0);
    }

    /// <summary>杀掉 QoderCN（Rider 插件会自动重新拉起并重读 mcp.json）。未运行则记一行说明。</summary>
    public static List<string> RestartDaemon()
    {
        var lines = new List<string>();
        foreach (var p in Process.GetProcessesByName("QoderCN"))
        {
            try
            {
                p.Kill();
                lines.Add($"已重启 qoder 守护进程（PID {p.Id}）——Rider 插件会自动重新拉起，新开会话生效");
            }
            catch (Exception ex)
            {
                lines.Add($"重启 QoderCN（PID {p.Id}）失败：{ex.Message}（可手工 taskkill /IM QoderCN.exe /F）");
            }
        }
        if (lines.Count == 0) lines.Add("qoder 守护进程未在运行——下次启动时自动读取新配置");
        return lines;
    }

    /// <summary>写入/更新 codequery 条目（多项目模式：不带 --solution，依赖 default-sln.txt + 查询 solution= 选择器）。</summary>
    public static EnableResult Enable(string installDir) => Enable(installDir, ConfigPath(), restartDaemon: true);

    /// <summary>核心实现；selftest 传临时文件与 restartDaemon:false，不碰真实配置与守护进程。</summary>
    internal static EnableResult Enable(string installDir, string configFile, bool restartDaemon)
    {
        var actions = new List<string>();
        JsonObject root;
        if (File.Exists(configFile))
        {
            try { root = JsonNode.Parse(File.ReadAllText(configFile)) as JsonObject ?? new JsonObject(); }
            catch
            {
                var backup = configFile + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Move(configFile, backup);
                actions.Add($"原 mcp.json 解析失败，已备份为 {Path.GetFileName(backup)} 并重建");
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        root["mcpServers"] = servers;

        servers["codequery"] = new JsonObject
        {
            // qoder 的命令行解析器吃反斜杠（README 2026-10-01 实测老坑），命令路径一律正斜杠
            ["command"] = Path.Combine(installDir, "publish", "CodeQuery.exe").Replace('\\', '/'),
            ["args"] = new JsonArray("mcp"),
            ["description"] = "Roslyn 语义查询：find_usages / type_hierarchy / analyze_calls / search_symbols / get_file_problems / lint_files / search_text。多项目：查询带 solution= 选择器（default-sln.txt 清单子串）",
            ["timeout"] = 180000,
            ["alwaysAllow"] = true,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);
        File.WriteAllText(configFile, root.ToJsonString(WriteOpts));
        actions.Add("已写入 " + configFile);

        if (restartDaemon) actions.AddRange(RestartDaemon());
        return new EnableResult(configFile, actions);
    }

    /// <summary>移除 codequery 条目，保留其他服务器。</summary>
    public static List<string> Disable() => Disable(ConfigPath(), restartDaemon: true);

    internal static List<string> Disable(string configFile, bool restartDaemon)
    {
        if (!File.Exists(configFile)) return new List<string> { "无 " + configFile + "，无需清理" };
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(configFile)) as JsonObject; }
        catch { return new List<string> { "mcp.json 解析失败，未改动" }; }
        if (root == null) return new List<string> { "mcp.json 非对象，未改动" };
        if (root["mcpServers"] is not JsonObject servers)
            return new List<string> { "无 mcpServers，无需清理" };
        if (servers["codequery"] is null)
            return new List<string> { "未发现 codequery 条目" };

        servers.Remove("codequery");
        File.WriteAllText(configFile, root.ToJsonString(WriteOpts));
        var actions = new List<string> { "已移除 codequery 条目（其余服务器保留）→ " + configFile };
        if (restartDaemon) actions.AddRange(RestartDaemon());
        return actions;
    }

    /// <summary>是否已注册；command 输出其命令路径（斜杠归一，用于显示与指向判断）。</summary>
    public static bool IsConfigured(out string command)
    {
        command = null;
        try
        {
            var file = ConfigPath();
            if (!File.Exists(file)) return false;
            if (JsonNode.Parse(File.ReadAllText(file))?["mcpServers"]?["codequery"] is not JsonObject node)
                return false;
            command = node["command"]?.GetValue<string>()?.Replace('\\', '/');
            return true;
        }
        catch { return false; }
    }

    /// <summary>注册的 command 是否指向给定安装目录（升级后据此判断要不要重启守护进程）。</summary>
    public static bool ConfiguredFor(string installDir)
    {
        if (!IsConfigured(out var command)) return false;
        var expected = Path.Combine(Path.GetFullPath(installDir), "publish", "CodeQuery.exe").Replace('\\', '/');
        return string.Equals(command, expected, StringComparison.OrdinalIgnoreCase);
    }
}
