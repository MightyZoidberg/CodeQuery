using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;

namespace CodeQueryManager;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
            return SelfTest.Run();

        var app = new System.Windows.Application
        {
            ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose,
        };
        app.Run(new MainWindow());
        return 0;
    }
}

/// <summary>
/// 无 GUI 自检（cmd 重定向运行：CodeQueryManager.exe --selftest > out.txt 2>&1）：
/// 环境体检、项目发现、内嵌 payload 校验、沙盒配置写入/移除往返。不触碰真实项目。
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        var ok = true;
        void Check(string name, bool pass, string detail)
        {
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {name}: {detail}");
            if (!pass) ok = false;
        }

        Console.WriteLine($"CodeQueryManager selftest @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var msb = Health.FindMsbuild();
        Check("MSBuild 源", msb != null, msb ?? "未检测到 dotnet SDK（目标机需 .NET SDK / VS / Rider 之一）");

        var cli = Health.FindUnityCli();
        Check("Unity CLI", cli != null, cli ?? "未找到（Unity MCP 条目将跳过）");

        var projects = Discovery.Discover();
        Check("项目发现", projects.Count > 0, $"{projects.Count} 个");
        foreach (var p in projects)
        {
            var sln = ConfigWriter.FindSolution(p.Path);
            Console.WriteLine($"    [{p.Source}] {p.Title} v{p.EditorVersion}  sln={(sln != null ? Path.GetFileName(sln) : "无")}  已配置={ConfigWriter.IsConfigured(p.Path)}");
        }

        var stream = typeof(Installer).Assembly.GetManifestResourceStream("Payload.zip");
        Check("内嵌 payload", stream != null, stream == null ? "缺失（构建未包含 zip）" : "存在");
        if (stream != null)
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            var verEntry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals("publish/VERSION.txt", StringComparison.OrdinalIgnoreCase));
            var ver = verEntry != null
                ? new StreamReader(verEntry.Open(), leaveOpen: false).ReadToEnd().Trim()
                : "?";
            var hasExe = entries.Any(e => e.Equals("publish/CodeQuery.exe", StringComparison.OrdinalIgnoreCase));
            Check("payload 内容", hasExe && verEntry != null, $"CodeQuery.exe={hasExe} 版本={ver} 条目数={entries.Count}");
            var hasSlnManifest = entries.Any(e => e.EndsWith("/default-sln.txt", StringComparison.OrdinalIgnoreCase));
            Check("payload 不含机器状态", !hasSlnManifest, hasSlnManifest ? "混入了 default-sln.txt！" : "无 default-sln.txt ✓");
        }

        // 沙盒往返：TEMP 里造一个假 Unity 项目（名字带空格），验证写入/保留无关条目/移除
        var sandbox = Path.Combine(Path.GetTempPath(), "CQM-SelfTest");
        try
        {
            var proj = Path.Combine(sandbox, "Fake Proj");
            Directory.CreateDirectory(Path.Combine(proj, "Assets"));
            Directory.CreateDirectory(Path.Combine(proj, "ProjectSettings"));
            File.WriteAllText(Path.Combine(proj, "Fake Proj.sln"), "stub");
            var cfgDir = Path.Combine(proj, ".zcode");
            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(Path.Combine(cfgDir, "config.json"),
                "{\"mcp\":{\"servers\":{\"keep-me\":{\"command\":\"x\"}}}}");
            var fakeCli = Path.Combine(sandbox, "unity.exe");
            File.WriteAllText(fakeCli, "stub");

            var r = ConfigWriter.Enable(proj, sandbox, fakeCli);
            var after = File.ReadAllText(r.ConfigPath);
            var flat = after.Replace("\r\n", " ").Replace("\n", " ");
            Check("写入 codequery 条目",
                after.Contains("\"codequery\"") && after.Contains("Fake Proj.sln"), flat);
            Check("unity 条目名映射", r.UnityServerName == "unity-fake-proj" && after.Contains("\"unity-fake-proj\""), r.UnityServerName);
            Check("保留无关条目", after.Contains("\"keep-me\""), "keep-me");

            var disabled = ConfigWriter.Disable(proj);
            var after2 = File.ReadAllText(r.ConfigPath);
            Check("移除仅本工具条目",
                !after2.Contains("\"codequery\"") && !after2.Contains("\"unity-") && after2.Contains("\"keep-me\""),
                string.Join("; ", disabled));

            // qoder 注册沙盒往返：临时 mcp.json 验证合并写入/正斜杠/保留无关条目/移除。
            // restartDaemon:false —— selftest 绝不杀真实 QoderCN（会打断用户活跃会话）。
            var qoderCfg = Path.Combine(sandbox, "mcp.json");
            File.WriteAllText(qoderCfg, "{\"mcpServers\":{\"other\":{\"url\":\"http://x\"}}}");
            var qr = QoderConfigWriter.Enable(sandbox, qoderCfg, restartDaemon: false);
            var qnode = JsonNode.Parse(File.ReadAllText(qoderCfg));
            var qcmd = qnode?["mcpServers"]?["codequery"]?["command"]?.GetValue<string>();
            Check("qoder 写入条目",
                qcmd == Path.Combine(sandbox, "publish", "CodeQuery.exe").Replace('\\', '/')
                    && qnode?["mcpServers"]?["codequery"]?["alwaysAllow"] != null,
                qcmd ?? "(missing)");
            Check("qoder 保留无关条目",
                File.ReadAllText(qoderCfg).Contains("\"other\""), "other");
            var qdisabled = QoderConfigWriter.Disable(qoderCfg, restartDaemon: false);
            var qafter2 = File.ReadAllText(qoderCfg);
            Check("qoder 移除仅本工具条目",
                !qafter2.Contains("\"codequery\"") && qafter2.Contains("\"other\""),
                string.Join("; ", qdisabled));
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }

        Console.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FAILED");
        return ok ? 0 : 1;
    }
}
