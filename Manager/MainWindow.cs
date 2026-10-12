using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CodeQueryManager;

/// <summary>
/// 单窗口 UI（WPF）：安装/更新 payload、环境体检、qoder 注册、Unity 项目列表与 ZCode workspace 配置写入/移除。
/// 目录选择不走框架内 IFileDialog——net10 的对话框包装在本机 Windows 构建上打开即 fail-fast
/// （2026-10-10 实测），改由 <see cref="FolderPicker"/> 借道 PowerShell 5.1 子进程。
/// </summary>
public sealed class MainWindow : Window
{
    private readonly TextBox _installDir = new();
    private readonly TextBlock _installStatus = new();
    private readonly TextBlock _msbStatus = new();
    private readonly TextBlock _cliStatus = new();
    private readonly TextBlock _qoderStatus = new();
    private readonly TextBlock _qoderDaemon = new();
    private readonly ListView _list = new();
    private readonly TextBox _log = new();
    private List<Discovery.UnityProject> _projects = new();

    /// <summary>项目列表行（RefreshItemStates 整体重建 ItemsSource，无需通知机制）。</summary>
    private sealed class Row
    {
        public string Status = "";
        public string Title = "";
        public string Version = "";
        public string Sln = "";
        public string Configured = "";
        public string Path = "";
        public Discovery.UnityProject Project;

        public string StatusText => Status;
        public string TitleText => Title;
        public string VersionText => Version;
        public string SlnText => Sln;
        public string ConfiguredText => Configured;
        public string PathText => Path;
    }

    public MainWindow()
    {
        Title = "CodeQuery 安装与项目管理器";
        Width = 1000;
        Height = 720;
        MinWidth = 820;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        // ---- 程序安装（Top）----
        var installGrid = new Grid();
        installGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        installGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        installGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        installGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        installGrid.RowDefinitions.Add(new RowDefinition());
        installGrid.RowDefinitions.Add(new RowDefinition());
        var dirLbl = new TextBlock { Text = "安装目录：", Margin = new Thickness(6, 8, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(dirLbl, 0); Grid.SetColumn(dirLbl, 0);
        _installDir.Margin = new Thickness(0, 6, 6, 0); Grid.SetRow(_installDir, 0); Grid.SetColumn(_installDir, 1);
        var browse = new Button { Content = "浏览…", Width = 70, Margin = new Thickness(0, 6, 6, 0) };
        Grid.SetRow(browse, 0); Grid.SetColumn(browse, 2);
        var installBtn = new Button { Content = "安装 / 更新", Width = 120, Margin = new Thickness(0, 6, 10, 0) };
        Grid.SetRow(installBtn, 0); Grid.SetColumn(installBtn, 3);
        _installStatus.TextWrapping = TextWrapping.Wrap; _installStatus.Margin = new Thickness(6, 6, 10, 8);
        Grid.SetRow(_installStatus, 1); Grid.SetColumnSpan(_installStatus, 4);
        installGrid.Children.Add(dirLbl);
        installGrid.Children.Add(_installDir);
        installGrid.Children.Add(browse);
        installGrid.Children.Add(installBtn);
        installGrid.Children.Add(_installStatus);
        var installG = new GroupBox { Header = "程序文件（CodeQuery 本体）", Content = installGrid };
        DockPanel.SetDock(installG, Dock.Top);
        browse.Click += (s, e) =>
        {
            var picked = FolderPicker.Pick("选择 CodeQuery 安装目录");
            if (picked == null) return;
            _installDir.Text = picked;
            RefreshInstallStatus();
            RefreshQoderStatus();
        };
        installBtn.Click += (s, e) => DoInstall();

        // ---- 环境体检（Top）----
        var envStack = new StackPanel { Margin = new Thickness(6, 6, 10, 8) };
        envStack.Children.Add(_msbStatus);
        envStack.Children.Add(_cliStatus);
        var envG = new GroupBox { Header = "环境体检（CodeQuery 运行前提）", Content = envStack };
        DockPanel.SetDock(envG, Dock.Top);

        // ---- qoder 注册（Top）----
        var qoderGrid = new Grid();
        qoderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        qoderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        qoderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var qoderStack = new StackPanel { Margin = new Thickness(6, 6, 6, 8) };
        qoderStack.Children.Add(_qoderStatus);
        qoderStack.Children.Add(_qoderDaemon);
        Grid.SetColumn(qoderStack, 0);
        var btnQoderEnable = new Button { Content = "写入 / 修复 qoder 注册", Width = 150, Margin = new Thickness(0, 10, 6, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(btnQoderEnable, 1);
        var btnQoderDisable = new Button { Content = "移除 qoder 注册", Width = 116, Margin = new Thickness(0, 10, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(btnQoderDisable, 2);
        qoderGrid.Children.Add(qoderStack);
        qoderGrid.Children.Add(btnQoderEnable);
        qoderGrid.Children.Add(btnQoderDisable);
        var qoderG = new GroupBox { Header = "qoder（Rider 插件）注册", Content = qoderGrid };
        DockPanel.SetDock(qoderG, Dock.Top);
        btnQoderEnable.Click += (s, e) => DoQoderEnable();
        btnQoderDisable.Click += (s, e) => DoQoderDisable();

        // ---- 日志（Bottom）----
        _log.IsReadOnly = true;
        _log.TextWrapping = TextWrapping.NoWrap;
        _log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _log.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _log.FontFamily = new FontFamily("Consolas");
        _log.Background = Brushes.White;
        _log.Margin = new Thickness(6, 6, 10, 8);
        var logG = new GroupBox { Header = "日志", Content = _log };
        DockPanel.SetDock(logG, Dock.Bottom);

        // ---- 项目列表（Fill）+ 按钮行（Bottom，在组内）----
        var center = new DockPanel();
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        var btnRefresh = new Button { Content = "刷新列表", Width = 90, Margin = new Thickness(0, 0, 6, 0) };
        var btnAdd = new Button { Content = "手动添加项目…", Width = 110, Margin = new Thickness(0, 0, 6, 0) };
        var btnEnable = new Button { Content = "写入 / 更新选中项目的 ZCode 配置", Width = 190, Margin = new Thickness(0, 0, 6, 0) };
        var btnDisable = new Button { Content = "移除选中项目的本工具配置", Width = 170 };
        btnRow.Children.Add(btnRefresh);
        btnRow.Children.Add(btnAdd);
        btnRow.Children.Add(btnEnable);
        btnRow.Children.Add(btnDisable);
        DockPanel.SetDock(btnRow, Dock.Bottom);
        center.Children.Add(btnRow);

        _list.Background = Brushes.White;
        var gv = new GridView();
        var statusTemplate = MakeStatusCellTemplate();
        gv.Columns.Add(new GridViewColumn { Header = "状态", Width = 76, CellTemplate = statusTemplate });
        gv.Columns.Add(MakeColumn("项目", "TitleText", 170));
        gv.Columns.Add(MakeColumn("Unity 版本", "VersionText", 96));
        gv.Columns.Add(MakeColumn(".sln", "SlnText", 60));
        gv.Columns.Add(MakeColumn("ZCode 配置", "ConfiguredText", 84));
        gv.Columns.Add(MakeColumn("路径", "PathText", 460));
        _list.View = gv;
        center.Children.Add(_list);

        var root = new DockPanel();
        root.Children.Add(installG);
        root.Children.Add(envG);
        root.Children.Add(qoderG);
        root.Children.Add(logG);
        root.Children.Add(center); // 最后加入的占满剩余空间
        Content = root;

        btnRefresh.Click += (s, e) => RefreshProjects();
        btnAdd.Click += (s, e) => ManualAdd();
        btnEnable.Click += (s, e) => DoEnable();
        btnDisable.Click += (s, e) => DoDisable();

        _installDir.Text = Settings.LoadInstallDir() ?? @"C:\Tools\CodeQuery";

        ContentRendered += (s, e) =>
        {
            Log($"内嵌 CodeQuery v{EmbeddedPayloadVersion()}");
            RefreshHealth();
            RefreshInstallStatus();
            RefreshQoderStatus();
            RefreshProjects();
        };
    }

    private static GridViewColumn MakeColumn(string header, string binding, double width) => new()
    {
        Header = header,
        Width = width,
        DisplayMemberBinding = new Binding(binding),
    };

    /// <summary>状态列：就绪→绿，未配置→灰，其余（空/警告）→红。</summary>
    private static DataTemplate MakeStatusCellTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new Binding("StatusText"));
        var style = new Style(typeof(TextBlock));
        style.Triggers.Add(new DataTrigger { Binding = new Binding("StatusText"), Value = "就绪", Setters = { new Setter(TextBlock.ForegroundProperty, Brushes.Green) } });
        style.Triggers.Add(new DataTrigger { Binding = new Binding("StatusText"), Value = "未配置", Setters = { new Setter(TextBlock.ForegroundProperty, Brushes.Gray) } });
        factory.SetValue(TextBlock.StyleProperty, style);
        return new DataTemplate { VisualTree = factory };
    }

    // ---- 动作 ----

    private void DoInstall()
    {
        var dir = _installDir.Text.Trim();
        if (dir.Length == 0) { Show("请先填写安装目录。"); return; }
        try
        {
            if (Installer.IsInstalled(dir) &&
                Show($"目标已安装 v{Installer.InstalledVersion(dir)}。更新会覆盖程序文件并停止运行中的 CodeQuery 实例（各客户端活跃查询断开，重开会话即恢复），继续？",
                    "确认更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                var (files, version) = Installer.ExtractPayload(dir, Log);
                Log($"安装完成：{files} 个文件 → {dir}\\publish\\（CodeQuery v{version}）");
                Settings.SaveInstallDir(dir);
                // 升级过程杀掉了运行中的 CodeQuery；qoder 守护进程挂载的实例不会自行重生，须重启守护进程
                if (QoderConfigWriter.ConfiguredFor(dir))
                    foreach (var a in QoderConfigWriter.RestartDaemon()) Log("[qoder] " + a);
            }
            finally { Mouse.OverrideCursor = null; }
            RefreshInstallStatus();
            RefreshQoderStatus();
            RefreshProjects();
        }
        catch (Exception ex)
        {
            Log("安装失败：" + ex.Message);
            Show(ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ManualAdd()
    {
        var picked = FolderPicker.Pick("选择 Unity 项目根目录（须含 Assets 文件夹）");
        if (picked == null) return;
        if (!Directory.Exists(Path.Combine(picked, "Assets")))
            if (Show("所选目录没有 Assets 子目录，不像 Unity 项目。仍添加？",
                    "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        if (_projects.Any(p => string.Equals(p.Path, picked, StringComparison.OrdinalIgnoreCase)))
        {
            Log("已在列表中：" + picked);
            return;
        }
        _projects.Add(new Discovery.UnityProject(picked, Path.GetFileName(picked.TrimEnd('\\')), "", "手动添加"));
        RefreshItemStates();
        Log("已添加：" + picked);
    }

    private void DoEnable()
    {
        if (_list.SelectedItem is not Row row) { Show("请先在列表中选中一个项目。"); return; }
        var p = row.Project;
        var dir = _installDir.Text.Trim();
        if (!Installer.IsInstalled(dir))
            if (Show($"安装目录 {dir} 下还没有 CodeQuery（未安装或路径不对）。仍按此路径写入配置？",
                    "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

        try
        {
            var r = ConfigWriter.Enable(p.Path, dir, Health.FindUnityCli());
            foreach (var a in r.Actions) Log($"[{p.Title}] {a}");
            RefreshItemStates();
        }
        catch (Exception ex)
        {
            Log($"[{p.Title}] 写入失败：" + ex.Message);
            Show(ex.Message, "写入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DoDisable()
    {
        if (_list.SelectedItem is not Row row) { Show("请先在列表中选中一个项目。"); return; }
        var p = row.Project;
        if (Show($"移除 {p.Path} 的 .zcode/config.json 中本工具写入的条目（codequery 与 unity-*）？其余内容保留。",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        foreach (var a in ConfigWriter.Disable(p.Path)) Log($"[{p.Title}] {a}");
        RefreshItemStates();
    }

    private void DoQoderEnable()
    {
        var dir = _installDir.Text.Trim();
        if (!Installer.IsInstalled(dir))
            if (Show($"安装目录 {dir} 下还没有 CodeQuery（未安装或路径不对）。仍按此路径写入 qoder 注册？",
                    "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

        var (running, _) = QoderConfigWriter.DaemonStatus();
        if (running && Show(
                "写入后将重启 qoder 守护进程（进行中的 qoder 对话会中断，Rider 插件会自动重新拉起，新开会话生效）。继续？",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var r = QoderConfigWriter.Enable(dir);
            foreach (var a in r.Actions) Log("[qoder] " + a);
        }
        catch (Exception ex)
        {
            Log("[qoder] 写入失败：" + ex.Message);
            Show(ex.Message, "写入失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshQoderStatus();
    }

    private void DoQoderDisable()
    {
        if (Show("移除用户级 mcp.json（~/.qoder-cn/shared_client/）中的 codequery 条目？其他 qoder 服务器保留。",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try { foreach (var a in QoderConfigWriter.Disable()) Log("[qoder] " + a); }
        catch (Exception ex) { Log("[qoder] 移除失败：" + ex.Message); }
        RefreshQoderStatus();
    }

    // ---- 刷新 ----

    private void RefreshHealth()
    {
        var msb = Health.FindMsbuild();
        _msbStatus.Text = "MSBuild：    " + (msb ?? "未检测到 dotnet SDK —— CodeQuery 需要 .NET SDK / VS / Rider 之一（推荐：winget install Microsoft.DotNet.SDK.10）");
        _msbStatus.Foreground = msb != null ? Brushes.Black : Brushes.Firebrick;
        var cli = Health.FindUnityCli();
        _cliStatus.Text = "Unity CLI：" + (cli ?? "未找到 unity.exe（Unity Pipeline CLI）—— Unity MCP 条目将不写入");
        _cliStatus.Foreground = cli != null ? Brushes.Black : Brushes.Firebrick;
    }

    private void RefreshInstallStatus()
    {
        var dir = _installDir.Text.Trim();
        if (Installer.IsInstalled(dir))
        {
            _installStatus.Text = $"已安装 CodeQuery v{Installer.InstalledVersion(dir)} @ {dir}（重开会话后生效）";
            _installStatus.Foreground = Brushes.Green;
        }
        else
        {
            _installStatus.Text = $"未安装（{dir} 下无 publish\\CodeQuery.exe）—— 点击\"安装 / 更新\"解压内嵌程序文件";
            _installStatus.Foreground = Brushes.Firebrick;
        }
    }

    private void RefreshQoderStatus()
    {
        var configured = QoderConfigWriter.IsConfigured(out var command);
        _qoderStatus.Text = "qoder 注册：" + (configured
            ? $"已配置（{(QoderConfigWriter.ConfiguredFor(_installDir.Text.Trim()) ? "指向当前安装目录" : command)}）"
            : "未配置 —— 点击\"写入 / 修复 qoder 注册\"（写入用户级 mcp.json 并重启 qoder 守护进程）");
        _qoderStatus.Foreground = configured ? Brushes.Green : Brushes.Firebrick;
        var (running, pid) = QoderConfigWriter.DaemonStatus();
        _qoderDaemon.Text = "守护进程：" + (running
            ? $"QoderCN.exe 运行中（PID {pid}）——配置变更后须重启它才生效（本工具写入/移除时自动处理）"
            : "未运行（下次启动自动读取配置）");
        _qoderDaemon.Foreground = running ? Brushes.Black : Brushes.Gray;
    }

    private void RefreshProjects()
    {
        _projects = Discovery.Discover();
        RefreshItemStates();
        Log($"发现 {_projects.Count} 个 Unity 项目（Unity Hub 列表 + 注册表 MRU）");
        RefreshHealth();
    }

    private void RefreshItemStates()
    {
        var selectedPath = (_list.SelectedItem as Row)?.Project?.Path;
        var rows = _projects.Select(p =>
        {
            var sln = ConfigWriter.FindSolution(p.Path);
            var configured = ConfigWriter.IsConfigured(p.Path);
            return new Row
            {
                Status = configured ? "就绪" : "未配置",
                Title = p.Title,
                Version = p.EditorVersion,
                Sln = sln != null ? "有" : "无",
                Configured = configured ? "已写入" : "—",
                Path = p.Path,
                Project = p,
            };
        }).ToList();
        _list.ItemsSource = rows;
        if (selectedPath != null)
            _list.SelectedItem = rows.FirstOrDefault(r =>
                string.Equals(r.Project.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
    }

    // ---- 杂项 ----

    private static string EmbeddedPayloadVersion()
    {
        var stream = typeof(Installer).Assembly.GetManifestResourceStream("Payload.zip");
        if (stream == null) return "(payload 未内嵌)";
        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals("publish/VERSION.txt", StringComparison.OrdinalIgnoreCase));
        return entry != null ? new StreamReader(entry.Open()).ReadToEnd().Trim() : "?";
    }

    private MessageBoxResult Show(string text, string caption = "CodeQueryManager",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None) =>
        MessageBox.Show(this, text, caption, buttons, icon);

    private void Log(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        _log.ScrollToEnd();
    }
}

/// <summary>
/// 系统目录选择对话框，借道 Windows PowerShell 5.1（.NET Framework 4.8 独立运行时）子进程：
/// .NET 10 的框架内 IFileDialog 包装（WinForms/WPF 皆是）在本机 Windows 构建上打开即 fail-fast
/// 崩溃（2026-10-10 实测），PS 5.1 不受影响。stdout 返回所选路径，取消返回 null。
/// 用 -EncodedCommand 传输脚本，规避一切命令行引号问题。
/// </summary>
internal static class FolderPicker
{
    public static string Pick(string description)
    {
        var script =
            """
            Add-Type -AssemblyName System.Windows.Forms
            $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
            $dlg.Description = 'DESC'
            if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
                [Console]::Out.Write($dlg.SelectedPath)
            }
            """;
        script = script.Replace("DESC", description.Replace("'", "''"));
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -STA -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 powershell.exe（目录选择对话框）");
        var path = (p.StandardOutput.ReadToEnd() ?? "").Trim();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && err.Length > 0)
            throw new InvalidOperationException("目录选择子进程失败：" + err.Trim());
        return path.Length > 0 ? path : null;
    }
}
