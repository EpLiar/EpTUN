using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.Json;

namespace EpTUN;

internal sealed class MainForm : Form
{
    private readonly Localizer _i18n;
    private readonly TextBox _configPathTextBox = new();
    private readonly Button _browseConfigButton = new();
    private readonly Button _openConfigButton = new();
    private readonly CheckBox _bypassCnCheckBox = new();
    private readonly Button _editConfigButton = new();
    private readonly Button _startStopButton = new();
    private readonly Button _reloadConfigButton = new();
    private readonly Button _restartButton = new();
    private readonly Button _clearLogButton = new();
    private readonly CheckBox _wrapLogsCheckBox = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _logTextBox = new();

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _trayShowWindowItem;
    private readonly ToolStripMenuItem _trayStartItem;
    private readonly ToolStripMenuItem _trayRestartItem;
    private readonly ToolStripMenuItem _trayStopItem;
    private readonly ToolStripMenuItem _trayExitItem;

    private readonly Icon _appIcon;
    private LogLevelSetting _windowLogLevel;
    private LogLevelSetting _fileLogLevel;
    private readonly string[] _logLevelLoadWarnings;
    private FileLogSink? _fileLogSink;
    private readonly UiLogWriter _logWriter;
    private readonly UiLogWriter _errorWriter;

    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private CancellationTokenSource? _trafficCts;
    private Task? _trafficTask;
    private bool _isRunning;
    private bool _exitRequested;
    private bool _trayHintShown;
    private string _baseStatusText = string.Empty;
    private string _trafficStatusText = string.Empty;

    public MainForm(string configPath)
    {
        _i18n = new Localizer(UiLanguageResolver.ResolveFromConfigPath(configPath));
        _baseStatusText = T("Status: idle", "状态：空闲");

        _appIcon = IconLoader.LoadFromPngCandidates();
        Icon = _appIcon;

        (_windowLogLevel, _fileLogLevel, _logLevelLoadWarnings) = ResolveLogLevelSettings(configPath);

        string? fileLogInitError = null;
        if (_fileLogLevel != LogLevelSetting.Off)
        {
            try
            {
                _fileLogSink = FileLogSink.Create(configPath);
            }
            catch (Exception ex)
            {
                fileLogInitError = ex.Message;
            }
        }

        _logWriter = new UiLogWriter(line => AppendLog("INFO", line));
        _errorWriter = new UiLogWriter(line => AppendLog("ERROR", line));

        _trayShowWindowItem = new ToolStripMenuItem(T("Show Window", "显示窗口"));
        _trayStartItem = new ToolStripMenuItem(T("Start VPN", "启动 VPN"));
        _trayRestartItem = new ToolStripMenuItem(T("Restart VPN", "重启 VPN"));
        _trayStopItem = new ToolStripMenuItem(T("Stop VPN", "停止 VPN"));
        _trayExitItem = new ToolStripMenuItem(T("Exit", "退出"));

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "EpTUN",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };

        _notifyIcon.ContextMenuStrip.Items.AddRange(
        [
            _trayShowWindowItem,
            _trayStartItem,
            _trayRestartItem,
            _trayStopItem,
            new ToolStripSeparator(),
            _trayExitItem
        ]);

        InitializeLayout(configPath);
        HookEvents();
        TryLoadBypassCnSetting(configPath, logErrors: false);

        AppendLog("INFO", T("UI ready.", "界面已就绪。"));
        AppendLog("INFO",
            T(
                $"Log levels: window>={LoggingConfig.ToText(_windowLogLevel)}, file>={LoggingConfig.ToText(_fileLogLevel)}",
                $"日志级别：窗口>={LoggingConfig.ToText(_windowLogLevel)}，文件>={LoggingConfig.ToText(_fileLogLevel)}"));
        if (_fileLogSink is not null)
        {
            AppendLog("INFO", T($"Local log file: {_fileLogSink.FilePath}", $"本地日志文件：{_fileLogSink.FilePath}"));
        }
        else if (_fileLogLevel == LogLevelSetting.Off)
        {
            AppendLog("INFO", T("Local file logging is disabled by logging.fileLevel.", "logging.fileLevel 已禁用本地文件日志。"));
        }
        else if (!string.IsNullOrWhiteSpace(fileLogInitError))
        {
            AppendLog("WARN", T($"Local file logging disabled: {fileLogInitError}", $"本地文件日志不可用：{fileLogInitError}"));
        }

        foreach (var warning in _logLevelLoadWarnings)
        {
            AppendLog("WARN", warning);
        }

        if (!IsAdministrator())
        {
            AppendLog("ERROR", T("Administrator privileges are required. Restart app and approve UAC.", "需要管理员权限。请重启程序并通过 UAC 授权。"));
            SetStatusText(T("Status: not elevated", "状态：未提权"));
        }

        UpdateUiState();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopTrafficMonitor();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _appIcon.Dispose();
            _logWriter.Dispose();
            _errorWriter.Dispose();
            _fileLogSink?.Dispose();
            _sessionCts?.Dispose();
            _trafficCts?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeLayout(string configPath)
    {
        Text = "EpTUN";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1020;
        Height = 640;
        MinimumSize = new Size(840, 520);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var configRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 5,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0)
        };
        configRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        configRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        configRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var configLabel = new Label
        {
            Text = T("Config:", "配置："),
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0)
        };

        _configPathTextBox.Dock = DockStyle.None;
        _configPathTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _configPathTextBox.Text = configPath;
        _configPathTextBox.Margin = new Padding(0);
        _configPathTextBox.MinimumSize = new Size(120, 26);
        _configPathTextBox.Font = Font;

        _browseConfigButton.Text = T("Browse...", "浏览...");
        _browseConfigButton.AutoSize = true;
        _browseConfigButton.Margin = new Padding(8, 0, 0, 0);
        _browseConfigButton.Anchor = AnchorStyles.Left;

        _openConfigButton.Text = T("Open", "打开");
        _openConfigButton.AutoSize = true;
        _openConfigButton.Margin = new Padding(8, 0, 0, 0);
        _openConfigButton.Anchor = AnchorStyles.Left;

        _bypassCnCheckBox.Text = T("Bypass CN", "绕过 CN");
        _bypassCnCheckBox.AutoSize = true;
        _bypassCnCheckBox.Anchor = AnchorStyles.Left;
        _bypassCnCheckBox.Margin = new Padding(12, 0, 0, 0);

        configRow.Controls.Add(configLabel, 0, 0);
        configRow.Controls.Add(_configPathTextBox, 1, 0);
        configRow.Controls.Add(_browseConfigButton, 2, 0);
        configRow.Controls.Add(_openConfigButton, 3, 0);
        configRow.Controls.Add(_bypassCnCheckBox, 4, 0);

        var controlRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 8, 0, 0)
        };

        _editConfigButton.Text = T("Config Editor", "配置编辑器");
        _editConfigButton.AutoSize = true;

        _startStopButton.Text = T("Start VPN", "启动 VPN");
        _startStopButton.AutoSize = true;

        _reloadConfigButton.Text = T("Reload Config", "重新加载配置");
        _reloadConfigButton.AutoSize = true;

        _restartButton.Text = T("Restart VPN", "重启 VPN");
        _restartButton.AutoSize = true;

        _clearLogButton.Text = T("Clear Logs", "清空日志");
        _clearLogButton.AutoSize = true;

        _wrapLogsCheckBox.Text = T("Wrap Logs", "日志换行");
        _wrapLogsCheckBox.AutoSize = true;
        _wrapLogsCheckBox.Checked = true;
        _wrapLogsCheckBox.Margin = new Padding(8, 6, 0, 0);

        controlRow.Controls.Add(_editConfigButton);
        controlRow.Controls.Add(_startStopButton);
        controlRow.Controls.Add(_reloadConfigButton);
        controlRow.Controls.Add(_restartButton);
        controlRow.Controls.Add(_clearLogButton);
        controlRow.Controls.Add(_wrapLogsCheckBox);

        _statusLabel.AutoSize = true;
        _statusLabel.Text = _baseStatusText;
        _statusLabel.Margin = new Padding(0, 8, 0, 8);

        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ReadOnly = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.WordWrap = true;
        _logTextBox.Font = CreateLogTextFont();

        root.Controls.Add(configRow, 0, 0);
        root.Controls.Add(controlRow, 0, 1);
        root.Controls.Add(_statusLabel, 0, 2);
        root.Controls.Add(_logTextBox, 0, 3);

        Controls.Add(root);
        ApplyLogWrapSetting(_wrapLogsCheckBox.Checked);
    }

    private void HookEvents()
    {
        _browseConfigButton.Click += OnBrowseConfigClicked;
        _openConfigButton.Click += OnOpenConfigClicked;
        _editConfigButton.Click += OnEditConfigClicked;
        _startStopButton.Click += async (_, _) => await ToggleVpnAsync();
        _reloadConfigButton.Click += async (_, _) => await ReloadConfigAsync();
        _restartButton.Click += async (_, _) => await RestartVpnAsync();
        _clearLogButton.Click += (_, _) => ClearLogs();
        _wrapLogsCheckBox.CheckedChanged += (_, _) => ApplyLogWrapSetting(_wrapLogsCheckBox.Checked);

        _trayShowWindowItem.Click += (_, _) => ShowWindow();
        _trayStartItem.Click += async (_, _) => await StartVpnAsync();
        _trayRestartItem.Click += async (_, _) => await RestartVpnAsync();
        _trayStopItem.Click += (_, _) => StopVpn();
        _trayExitItem.Click += async (_, _) => await ExitApplicationAsync();
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        FormClosing += OnFormClosing;
    }

    private async Task ToggleVpnAsync()
    {
        if (_isRunning)
        {
            StopVpn();
            return;
        }

        await StartVpnAsync();
    }

    private async Task ReloadConfigAsync()
    {
        var configPath = _configPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(configPath))
        {
            MessageBox.Show(
                this,
                T("Please select appsettings.json.", "请选择 appsettings.json。"),
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        configPath = Path.GetFullPath(configPath);
        _configPathTextBox.Text = configPath;

        if (!File.Exists(configPath))
        {
            MessageBox.Show(
                this,
                T($"Config file not found:\n{configPath}", $"未找到配置文件：\n{configPath}"),
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        AppConfig config;
        try
        {
            config = await LoadConfigAsync(configPath);
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", T($"Config reload failed: {ex.Message}", $"配置重载失败：{ex.Message}"));
            MessageBox.Show(
                this,
                ex.Message,
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        ApplyReloadableConfig(configPath, config);
    }

    private void ApplyReloadableConfig(string configPath, AppConfig config)
    {
        ApplyLoggingConfig(configPath, config.Logging);

        if (_isRunning)
        {
            StartTrafficMonitor(config);
            AppendLog("INFO",
                T(
                    "Config reload applied (runtime): logging.windowLevel, logging.fileLevel, logging.trafficSampleMilliseconds.",
                    "配置热重载已应用（运行时）：logging.windowLevel、logging.fileLevel、logging.trafficSampleMilliseconds。"));
            AppendLog("INFO",
                T(
                    "Restart VPN required for other fields (proxy.*, tun2Socks.*, vpn routing, v2rayA.*).",
                    "其他字段（proxy.*、tun2Socks.*、vpn 路由、v2rayA.*）需要重启 VPN 才会生效。"));
            return;
        }

        TryLoadBypassCnSetting(configPath, logErrors: true);
        AppendLog("INFO", T("Config reloaded.", "配置已重载。"));
    }

    private void ApplyLoggingConfig(string configPath, LoggingConfig logging)
    {
        var nextWindowLevel = LoggingConfig.ParseLevelOrDefault(logging.WindowLevel);
        var nextFileLevel = LoggingConfig.ParseLevelOrDefault(logging.FileLevel);

        _windowLogLevel = nextWindowLevel;

        var currentSink = _fileLogSink;
        if (nextFileLevel == LogLevelSetting.Off)
        {
            _fileLogSink = null;
            _fileLogLevel = nextFileLevel;
            currentSink?.Dispose();
            AppendLog("INFO",
                T(
                    $"Log levels updated: window>={LoggingConfig.ToText(_windowLogLevel)}, file>=OFF",
                    $"日志级别已更新：窗口>={LoggingConfig.ToText(_windowLogLevel)}，文件>=OFF"));
            return;
        }

        try
        {
            var nextSink = FileLogSink.Create(configPath);
            _fileLogSink = nextSink;
            _fileLogLevel = nextFileLevel;
            currentSink?.Dispose();
            AppendLog("INFO",
                T(
                    $"Log levels updated: window>={LoggingConfig.ToText(_windowLogLevel)}, file>={LoggingConfig.ToText(_fileLogLevel)}",
                    $"日志级别已更新：窗口>={LoggingConfig.ToText(_windowLogLevel)}，文件>={LoggingConfig.ToText(_fileLogLevel)}"));
            AppendLog("INFO", T($"Local log file: {_fileLogSink.FilePath}", $"本地日志文件：{_fileLogSink.FilePath}"));
        }
        catch (Exception ex)
        {
            _fileLogSink = currentSink;
            _fileLogLevel = nextFileLevel;
            AppendLog("WARN",
                T(
                    $"File logging sink update failed, keeping previous sink: {ex.Message}",
                    $"文件日志写入器更新失败，保留旧写入器：{ex.Message}"));
            AppendLog("INFO",
                T(
                    $"Log levels updated: window>={LoggingConfig.ToText(_windowLogLevel)}, file>={LoggingConfig.ToText(_fileLogLevel)}",
                    $"日志级别已更新：窗口>={LoggingConfig.ToText(_windowLogLevel)}，文件>={LoggingConfig.ToText(_fileLogLevel)}"));
        }
    }

    private async Task StartVpnAsync()
    {
        if (_isRunning)
        {
            return;
        }

        if (!IsAdministrator())
        {
            MessageBox.Show(
                this,
                T("Administrator privileges are required.", "需要管理员权限。"),
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var configPath = _configPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(configPath))
        {
            MessageBox.Show(
                this,
                T("Please select appsettings.json.", "请选择 appsettings.json。"),
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        configPath = Path.GetFullPath(configPath);
        _configPathTextBox.Text = configPath;

        if (!File.Exists(configPath))
        {
            MessageBox.Show(
                this,
                T($"Config file not found:\n{configPath}", $"未找到配置文件：\n{configPath}"),
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        AppConfig config;
        try
        {
            config = await LoadConfigAsync(configPath);
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", ex.Message);
            MessageBox.Show(
                this,
                ex.Message,
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var bypassCnEnabled = _bypassCnCheckBox.Checked;
        if (bypassCnEnabled)
        {
            AppendLog("INFO", T("Bypass CN is enabled.", "Bypass CN 已启用。"));
        }

        _sessionCts = new CancellationTokenSource();
        var session = new VpnSession(config, configPath, _logWriter, _errorWriter, bypassCnEnabled);

        _isRunning = true;
        SetStatusText(T("Status: running", "状态：运行中"));
        SetTrafficStatusText(string.Empty);
        StartTrafficMonitor(config);
        UpdateUiState();

        _sessionTask = Task.Run(async () => await session.RunAsync(_sessionCts.Token));
        _ = ObserveSessionAsync(_sessionTask);

        AppendLog("INFO", T("VPN session started.", "VPN 会话已启动。"));
    }

    private void StopVpn()
    {
        if (!_isRunning)
        {
            return;
        }

        SetStatusText(T("Status: stopping", "状态：停止中"));
        _sessionCts?.Cancel();
        StopTrafficMonitor();
        UpdateUiState();
        AppendLog("INFO", T("Stop signal sent.", "已发送停止信号。"));
    }

    private async Task RestartVpnAsync()
    {
        if (!_isRunning)
        {
            await StartVpnAsync();
            return;
        }

        StopVpn();

        if (_sessionTask is not null)
        {
            await Task.WhenAny(_sessionTask, Task.Delay(8000));
        }

        if (_isRunning)
        {
            AppendLog("ERROR", T("Restart timed out: previous VPN session is still stopping.", "重启超时：上一个 VPN 会话仍在停止中。"));
            return;
        }

        await StartVpnAsync();
    }

    private async Task ObserveSessionAsync(Task task)
    {
        try
        {
            await task;
            AppendLog("INFO", T("VPN session stopped.", "VPN 会话已停止。"));
        }
        catch (OperationCanceledException)
        {
            AppendLog("INFO", T("VPN session canceled.", "VPN 会话已取消。"));
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", T($"VPN session failed: {ex.Message}", $"VPN 会话失败：{ex.Message}"));
        }
        finally
        {
            _sessionCts?.Dispose();
            _sessionCts = null;
            _sessionTask = null;
            _isRunning = false;
            StopTrafficMonitor();

            if (!IsDisposed)
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() =>
                    {
                        SetStatusText(T("Status: stopped", "状态：已停止"));
                        UpdateUiState();
                    }));
                }
                else
                {
                    SetStatusText(T("Status: stopped", "状态：已停止"));
                    UpdateUiState();
                }
            }
        }
    }

    private void UpdateUiState()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(UpdateUiState));
            return;
        }

        var elevated = IsAdministrator();
        _startStopButton.Enabled = _isRunning || elevated;
        _startStopButton.Text = _isRunning
            ? T("Stop VPN", "停止 VPN")
            : T("Start VPN", "启动 VPN");
        _editConfigButton.Enabled = true;
        _reloadConfigButton.Enabled = _isRunning;
        _restartButton.Enabled = _isRunning && elevated;
        _trayStartItem.Enabled = !_isRunning && elevated;
        _trayRestartItem.Enabled = _restartButton.Enabled;
        _trayStopItem.Enabled = _isRunning;
        _bypassCnCheckBox.Enabled = !_isRunning;
    }

    private void SetStatusText(string text)
    {
        _baseStatusText = text;
        RefreshStatusLabel();
    }

    private void SetTrafficStatusText(string text)
    {
        _trafficStatusText = text;
        RefreshStatusLabel();
    }

    private void RefreshStatusLabel()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshStatusLabel));
            return;
        }

        _statusLabel.Text = string.IsNullOrWhiteSpace(_trafficStatusText)
            ? _baseStatusText
            : $"{_baseStatusText} | {_trafficStatusText}";
    }

    private void StartTrafficMonitor(AppConfig config)
    {
        StopTrafficMonitor();

        var sampleIntervalMs = Math.Clamp(config.Logging.TrafficSampleMilliseconds, 100, 3600000);
        _trafficCts = new CancellationTokenSource();
        _trafficTask = Task.Run(async () =>
            await MonitorTrafficAsync(config.Vpn.InterfaceName, sampleIntervalMs, _trafficCts.Token));
        _ = ObserveTrafficMonitorAsync(_trafficTask);

        AppendLog("INFO",
            T(
                $"Traffic monitor started: interface={config.Vpn.InterfaceName}, interval={sampleIntervalMs}ms.",
                $"流量监控已启动：接口={config.Vpn.InterfaceName}，采样间隔={sampleIntervalMs}ms。"));
    }

    private void StopTrafficMonitor()
    {
        try
        {
            _trafficCts?.Cancel();
        }
        catch
        {
            // Ignore cancellation race during shutdown.
        }

        _trafficCts?.Dispose();
        _trafficCts = null;
        _trafficTask = null;
        SetTrafficStatusText(string.Empty);
    }

    private async Task ObserveTrafficMonitorAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping the session.
        }
        catch (Exception ex)
        {
            AppendLog("WARN", T($"Traffic monitor failed: {ex.Message}", $"流量监控失败：{ex.Message}"));
        }
    }

    private async Task MonitorTrafficAsync(string interfaceName, int sampleIntervalMs, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(sampleIntervalMs);
        NetworkInterface? networkInterface = null;
        TrafficSnapshot? lastSnapshot = null;
        ulong totalBytesReceived = 0;
        ulong totalBytesSent = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            networkInterface ??= FindInterface(interfaceName);
            if (networkInterface is null)
            {
                SetTrafficStatusText(T("Traffic: waiting for Wintun interface...", "流量：等待 Wintun 接口..."));
                await Task.Delay(interval, cancellationToken);
                continue;
            }

            if (!TryReadInterfaceBytes(networkInterface, out var bytesReceived, out var bytesSent))
            {
                networkInterface = null;
                lastSnapshot = null;
                await Task.Delay(interval, cancellationToken);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            if (lastSnapshot is not null)
            {
                var elapsedSeconds = (now - lastSnapshot.Timestamp).TotalSeconds;
                if (elapsedSeconds > 0)
                {
                    var deltaReceived = ComputeCounterDelta(lastSnapshot.BytesReceived, bytesReceived);
                    var deltaSent = ComputeCounterDelta(lastSnapshot.BytesSent, bytesSent);
                    totalBytesReceived += deltaReceived;
                    totalBytesSent += deltaSent;

                    var downRate = deltaReceived / elapsedSeconds;
                    var upRate = deltaSent / elapsedSeconds;
                    SetTrafficStatusText(
                        T(
                            $"Down {FormatRate(downRate)} | Up {FormatRate(upRate)} | " +
                            $"Total Down {FormatBytes(totalBytesReceived)} | Total Up {FormatBytes(totalBytesSent)}",
                            $"下行 {FormatRate(downRate)} | 上行 {FormatRate(upRate)} | " +
                            $"累计下行 {FormatBytes(totalBytesReceived)} | 累计上行 {FormatBytes(totalBytesSent)}"));
                }
            }

            lastSnapshot = new TrafficSnapshot(now, bytesReceived, bytesSent);
            await Task.Delay(interval, cancellationToken);
        }
    }

    private static NetworkInterface? FindInterface(string interfaceName)
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => string.Equals(ni.Name, interfaceName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(ni => ni.OperationalStatus == OperationalStatus.Up)
            .FirstOrDefault();
    }

    private static bool TryReadInterfaceBytes(NetworkInterface networkInterface, out ulong bytesReceived, out ulong bytesSent)
    {
        try
        {
            var stats = networkInterface.GetIPStatistics();
            bytesReceived = (ulong)Math.Max(0, stats.BytesReceived);
            bytesSent = (ulong)Math.Max(0, stats.BytesSent);
            return true;
        }
        catch
        {
            bytesReceived = 0;
            bytesSent = 0;
            return false;
        }
    }

    private static ulong ComputeCounterDelta(ulong previous, ulong current)
    {
        return current >= previous ? current - previous : 0;
    }

    private static string FormatRate(double bytesPerSecond)
    {
        return $"{FormatTrafficValue(bytesPerSecond)}/s";
    }

    private static string FormatBytes(ulong bytes)
    {
        return FormatTrafficValue(bytes);
    }

    private static string FormatTrafficValue(double bytes)
    {
        // Use fixed-width placeholder: "xxx.xx XX" (rate appends "/s"),
        // so traffic text width is stable across refreshes.
        var value = Math.Max(0, bytes) / 1024.0;
        string[] units = ["KB", "MB", "GB", "TB", "PB", "EB"];
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        if (rounded >= 1000 && unit < units.Length - 1)
        {
            value = rounded / 1024.0;
            unit++;
            rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        }

        if (rounded > 999.99)
        {
            rounded = 999.99;
        }

        var numericText = rounded.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(6, ' ');
        return $"{numericText} {units[unit]}";
    }

    private static async Task<AppConfig> LoadConfigAsync(string configPath)
    {
        var json = await File.ReadAllTextAsync(configPath);
        var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions)
            ?? throw new InvalidOperationException("Failed to parse configuration.");

        config.Validate();
        return config;
    }

    private void OnBrowseConfigClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = T("JSON files (*.json)|*.json|All files (*.*)|*.*", "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"),
            FileName = _configPathTextBox.Text,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _configPathTextBox.Text = dialog.FileName;
            TryLoadBypassCnSetting(dialog.FileName, logErrors: false);
        }
    }

    private void OnOpenConfigClicked(object? sender, EventArgs e)
    {
        var configPath = _configPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return;
        }

        configPath = Path.GetFullPath(configPath);
        if (!File.Exists(configPath))
        {
            MessageBox.Show(
                this,
                T($"Config file not found:\n{configPath}", $"未找到配置文件：\n{configPath}"),
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _ = Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = $"\"{configPath}\"",
            UseShellExecute = false
        });
    }

    private void OnEditConfigClicked(object? sender, EventArgs e)
    {
        var configPath = _configPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(configPath))
        {
            MessageBox.Show(
                this,
                T("Please select appsettings.json.", "请选择 appsettings.json。"),
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        configPath = Path.GetFullPath(configPath);
        _configPathTextBox.Text = configPath;
        if (!File.Exists(configPath))
        {
            MessageBox.Show(
                this,
                T($"Config file not found:\n{configPath}", $"未找到配置文件：\n{configPath}"),
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var editor = new ConfigEditorForm(configPath);
        _ = editor.ShowDialog(this);
        if (_isRunning)
        {
            _ = ReloadConfigAsync();
            return;
        }

        TryLoadBypassCnSetting(configPath, logErrors: true);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private async Task ExitApplicationAsync()
    {
        if (_isRunning)
        {
            StopVpn();
            if (_sessionTask is not null)
            {
                var finished = await Task.WhenAny(_sessionTask, Task.Delay(6000));
                if (finished != _sessionTask)
                {
                    var force = MessageBox.Show(
                        this,
                        T("VPN is still stopping. Exit anyway?", "VPN 仍在停止中，是否强制退出？"),
                        "EpTUN",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);
                    if (force != DialogResult.Yes)
                    {
                        return;
                    }
                }
            }
        }

        _exitRequested = true;
        _notifyIcon.Visible = false;
        Close();
    }

    private void HideToTray()
    {
        Hide();

        if (!_trayHintShown)
        {
            _notifyIcon.BalloonTipTitle = "EpTUN";
            _notifyIcon.BalloonTipText = T("Running in tray. Double-click tray icon to open.", "程序已最小化到托盘，双击托盘图标可打开。");
            _notifyIcon.ShowBalloonTip(1500);
            _trayHintShown = true;
        }
    }

    private void ShowWindow()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(ShowWindow));
            return;
        }

        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ApplyLogWrapSetting(bool enabled)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action<bool>(ApplyLogWrapSetting), enabled);
            return;
        }

        _logTextBox.WordWrap = enabled;
        _logTextBox.ScrollBars = enabled ? ScrollBars.Vertical : ScrollBars.Both;
    }

    private void ClearLogs()
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(ClearLogs));
            return;
        }

        _logTextBox.Clear();
    }

    private void AppendLog(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || IsDisposed)
        {
            return;
        }

        var normalized = message.Replace("\r", string.Empty).TrimEnd('\n');
        if (normalized.Length == 0)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(level, normalized)));
            return;
        }

        var effectiveLevel = LoggingConfig.ParseLevelOrDefault(level);
        var content = normalized;
        if (TryExtractEmbeddedLevel(normalized, out var embeddedLevel, out var stripped))
        {
            effectiveLevel = embeddedLevel;
            if (!string.IsNullOrWhiteSpace(stripped))
            {
                content = stripped;
            }
        }

        var writeWindow = ShouldWriteLevel(effectiveLevel, _windowLogLevel);
        var writeFile = ShouldWriteLevel(effectiveLevel, _fileLogLevel) && _fileLogSink is not null;
        if (!writeWindow && !writeFile)
        {
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss}] [{LoggingConfig.ToText(effectiveLevel)}] {content}";
        if (writeWindow)
        {
            _logTextBox.AppendText(line + Environment.NewLine);

            if (_logTextBox.TextLength > 60000)
            {
                _logTextBox.Text = _logTextBox.Text[^45000..];
            }

            _logTextBox.SelectionStart = _logTextBox.TextLength;
            _logTextBox.ScrollToCaret();
        }

        if (writeFile)
        {
            _fileLogSink!.WriteLine(line);
        }
    }

    private (LogLevelSetting WindowLevel, LogLevelSetting FileLevel, string[] Warnings) ResolveLogLevelSettings(string configPath)
    {
        var warnings = new List<string>();
        var windowLevel = LogLevelSetting.Info;
        var fileLevel = LogLevelSetting.Info;

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(configPath.Trim());
        }
        catch
        {
            return (windowLevel, fileLevel, warnings.ToArray());
        }

        if (!File.Exists(resolvedPath))
        {
            return (windowLevel, fileLevel, warnings.ToArray());
        }

        try
        {
            var json = File.ReadAllText(resolvedPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions);
            if (config is null)
            {
                warnings.Add(T("Failed to parse config for logging levels. Falling back to INFO.", "解析日志级别配置失败，已回退到 INFO。"));
                return (windowLevel, fileLevel, warnings.ToArray());
            }

            if (!LoggingConfig.TryParseLevel(config.Logging.WindowLevel, out windowLevel))
            {
                warnings.Add(T("Invalid logging.windowLevel. Use INFO/WARN/ERROR/OFF (or NONE). Falling back to INFO.", "logging.windowLevel 无效。请使用 INFO/WARN/ERROR/OFF（或 NONE），已回退到 INFO。"));
                windowLevel = LogLevelSetting.Info;
            }

            if (!LoggingConfig.TryParseLevel(config.Logging.FileLevel, out fileLevel))
            {
                warnings.Add(T("Invalid logging.fileLevel. Use INFO/WARN/ERROR/OFF (or NONE). Falling back to INFO.", "logging.fileLevel 无效。请使用 INFO/WARN/ERROR/OFF（或 NONE），已回退到 INFO。"));
                fileLevel = LogLevelSetting.Info;
            }
        }
        catch (Exception ex)
        {
            warnings.Add(T($"Failed to read logging levels. Falling back to INFO. {ex.Message}", $"读取日志级别失败，已回退到 INFO。{ex.Message}"));
        }

        return (windowLevel, fileLevel, warnings.ToArray());
    }

    private static bool ShouldWriteLevel(LogLevelSetting actualLevel, LogLevelSetting minLevel)
    {
        if (minLevel == LogLevelSetting.Off || actualLevel == LogLevelSetting.Off)
        {
            return false;
        }

        return actualLevel >= minLevel;
    }

    private static bool TryExtractEmbeddedLevel(string message, out LogLevelSetting level, out string content)
    {
        level = LogLevelSetting.Info;
        content = string.Empty;
        if (!message.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        var endBracket = message.IndexOf(']');
        if (endBracket <= 1)
        {
            return false;
        }

        var token = message[1..endBracket];
        if (!LoggingConfig.TryParseLevel(token, out level))
        {
            return false;
        }

        content = message[(endBracket + 1)..].TrimStart();
        return true;
    }

    private void TryLoadBypassCnSetting(string configPath, bool logErrors)
    {
        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(configPath.Trim());
        }
        catch
        {
            return;
        }

        if (!File.Exists(resolvedPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(resolvedPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppConfig.SerializerOptions);
            if (config is null)
            {
                return;
            }

            _bypassCnCheckBox.Checked = config.Vpn.BypassCn;
            if (logErrors)
            {
                AppendLog(
                    "INFO",
                    T(
                        $"Bypass CN default: {(config.Vpn.BypassCn ? "enabled" : "disabled")}",
                        $"Bypass CN 默认值：{(config.Vpn.BypassCn ? "启用" : "禁用")}"));
            }
        }
        catch (Exception ex)
        {
            if (logErrors)
            {
                AppendLog("ERROR", T($"Failed to read bypass CN setting: {ex.Message}", $"读取 Bypass CN 配置失败：{ex.Message}"));
            }
        }
    }

    private string T(string english, string chineseSimplified)
    {
        return _i18n.Text(english, chineseSimplified);
    }

    private sealed record TrafficSnapshot(DateTimeOffset Timestamp, ulong BytesReceived, ulong BytesSent);

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private Font CreateLogTextFont()
    {
        const float size = 9.0f;
        const FontStyle style = FontStyle.Regular;
        var monoFamily = FindInstalledFontFamilyName("Cascadia Mono", "Cascadia Code", "Consolas");

        if (_i18n.IsChineseSimplified)
        {
            var preferredChineseFamily = FindInstalledFontFamilyName(
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "微软雅黑",
                "Noto Sans SC",
                "Noto Sans CJK SC",
                "DengXian",
                "等线",
                "SimSun",
                "宋体",
                "SimHei",
                "黑体");
            if (!string.IsNullOrWhiteSpace(preferredChineseFamily))
            {
                return new Font(preferredChineseFamily, size, style, GraphicsUnit.Point);
            }
        }

        return !string.IsNullOrWhiteSpace(monoFamily)
            ? new Font(monoFamily, size, style, GraphicsUnit.Point)
            : new Font(FontFamily.GenericMonospace, size, style, GraphicsUnit.Point);
    }

    private static string? FindInstalledFontFamilyName(params string[] candidateNames)
    {
        foreach (var candidate in candidateNames)
        {
            var found = FontFamily.Families.FirstOrDefault(family =>
                string.Equals(family.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return found.Name;
            }
        }

        return null;
    }
}

