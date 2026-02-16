using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace EpTUN;

internal sealed class MainForm : Form
{
    private readonly TextBox _configPathTextBox = new();
    private readonly Button _browseConfigButton = new();
    private readonly Button _openConfigButton = new();
    private readonly CheckBox _bypassCnCheckBox = new();
    private readonly Button _editConfigButton = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _restartButton = new();
    private readonly Button _hideButton = new();
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
    private readonly LogLevelSetting _windowLogLevel;
    private readonly LogLevelSetting _fileLogLevel;
    private readonly string[] _logLevelLoadWarnings;
    private readonly FileLogSink? _fileLogSink;
    private readonly UiLogWriter _logWriter;
    private readonly UiLogWriter _errorWriter;

    private CancellationTokenSource? _sessionCts;
    private Task? _sessionTask;
    private bool _isRunning;
    private bool _exitRequested;
    private bool _trayHintShown;

    public MainForm(string configPath)
    {
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

        _trayShowWindowItem = new ToolStripMenuItem("Show Window");
        _trayStartItem = new ToolStripMenuItem("Start VPN");
        _trayRestartItem = new ToolStripMenuItem("Restart VPN");
        _trayStopItem = new ToolStripMenuItem("Stop VPN");
        _trayExitItem = new ToolStripMenuItem("Exit");

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

        AppendLog("INFO", "UI ready.");
        AppendLog("INFO", $"Log levels: window>={LoggingConfig.ToText(_windowLogLevel)}, file>={LoggingConfig.ToText(_fileLogLevel)}");
        if (_fileLogSink is not null)
        {
            AppendLog("INFO", $"Local log file: {_fileLogSink.FilePath}");
        }
        else if (_fileLogLevel == LogLevelSetting.Off)
        {
            AppendLog("INFO", "Local file logging is disabled by logging.fileLevel.");
        }
        else if (!string.IsNullOrWhiteSpace(fileLogInitError))
        {
            AppendLog("WARN", $"Local file logging disabled: {fileLogInitError}");
        }

        foreach (var warning in _logLevelLoadWarnings)
        {
            AppendLog("WARN", warning);
        }

        if (!IsAdministrator())
        {
            AppendLog("ERROR", "Administrator privileges are required. Restart app and approve UAC.");
            _statusLabel.Text = "Status: not elevated";
        }

        UpdateUiState();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _appIcon.Dispose();
            _logWriter.Dispose();
            _errorWriter.Dispose();
            _fileLogSink?.Dispose();
            _sessionCts?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void InitializeLayout(string configPath)
    {
        Text = "EpTUN";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1020;
        Height = 640;
        MinimumSize = new Size(860, 520);

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
            Text = "Config:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 0)
        };

        _configPathTextBox.Dock = DockStyle.Fill;
        _configPathTextBox.Text = configPath;

        _browseConfigButton.Text = "Browse...";
        _browseConfigButton.AutoSize = true;

        _openConfigButton.Text = "Open";
        _openConfigButton.AutoSize = true;

        _bypassCnCheckBox.Text = "Bypass CN";
        _bypassCnCheckBox.AutoSize = true;
        _bypassCnCheckBox.Anchor = AnchorStyles.Left;
        _bypassCnCheckBox.Margin = new Padding(12, 8, 0, 0);

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

        _editConfigButton.Text = "Config Editor";
        _editConfigButton.AutoSize = true;

        _startButton.Text = "Start VPN";
        _startButton.AutoSize = true;

        _stopButton.Text = "Stop VPN";
        _stopButton.AutoSize = true;

        _restartButton.Text = "Restart VPN";
        _restartButton.AutoSize = true;

        _hideButton.Text = "Minimize To Tray";
        _hideButton.AutoSize = true;

        _clearLogButton.Text = "Clear Logs";
        _clearLogButton.AutoSize = true;

        _wrapLogsCheckBox.Text = "Wrap Logs";
        _wrapLogsCheckBox.AutoSize = true;
        _wrapLogsCheckBox.Checked = true;
        _wrapLogsCheckBox.Margin = new Padding(8, 6, 0, 0);

        controlRow.Controls.Add(_editConfigButton);
        controlRow.Controls.Add(_startButton);
        controlRow.Controls.Add(_stopButton);
        controlRow.Controls.Add(_restartButton);
        controlRow.Controls.Add(_hideButton);
        controlRow.Controls.Add(_clearLogButton);
        controlRow.Controls.Add(_wrapLogsCheckBox);

        _statusLabel.AutoSize = true;
        _statusLabel.Text = "Status: idle";
        _statusLabel.Margin = new Padding(0, 8, 0, 8);

        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ReadOnly = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.WordWrap = true;
        _logTextBox.Font = new Font("Consolas", 9.0f, FontStyle.Regular, GraphicsUnit.Point);

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
        _startButton.Click += async (_, _) => await StartVpnAsync();
        _stopButton.Click += (_, _) => StopVpn();
        _restartButton.Click += async (_, _) => await RestartVpnAsync();
        _hideButton.Click += (_, _) => HideToTray();
        _clearLogButton.Click += (_, _) => ClearLogs();
        _wrapLogsCheckBox.CheckedChanged += (_, _) => ApplyLogWrapSetting(_wrapLogsCheckBox.Checked);

        _trayShowWindowItem.Click += (_, _) => ShowWindow();
        _trayStartItem.Click += async (_, _) => await StartVpnAsync();
        _trayRestartItem.Click += async (_, _) => await RestartVpnAsync();
        _trayStopItem.Click += (_, _) => StopVpn();
        _trayExitItem.Click += async (_, _) => await ExitApplicationAsync();
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        Resize += OnFormResize;
        FormClosing += OnFormClosing;
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
                "Administrator privileges are required.",
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
                "Please select appsettings.json.",
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
                $"Config file not found:\n{configPath}",
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
            AppendLog("INFO", "Bypass CN is enabled.");
        }

        _sessionCts = new CancellationTokenSource();
        var session = new VpnSession(config, configPath, _logWriter, _errorWriter, bypassCnEnabled);

        _isRunning = true;
        _statusLabel.Text = "Status: running";
        UpdateUiState();

        _sessionTask = Task.Run(async () => await session.RunAsync(_sessionCts.Token));
        _ = ObserveSessionAsync(_sessionTask);

        AppendLog("INFO", "VPN session started.");
    }

    private void StopVpn()
    {
        if (!_isRunning)
        {
            return;
        }

        _statusLabel.Text = "Status: stopping";
        _sessionCts?.Cancel();
        UpdateUiState();
        AppendLog("INFO", "Stop signal sent.");
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
            AppendLog("ERROR", "Restart timed out: previous VPN session is still stopping.");
            return;
        }

        await StartVpnAsync();
    }

    private async Task ObserveSessionAsync(Task task)
    {
        try
        {
            await task;
            AppendLog("INFO", "VPN session stopped.");
        }
        catch (OperationCanceledException)
        {
            AppendLog("INFO", "VPN session canceled.");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", $"VPN session failed: {ex.Message}");
        }
        finally
        {
            _sessionCts?.Dispose();
            _sessionCts = null;
            _sessionTask = null;
            _isRunning = false;

            if (!IsDisposed)
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _statusLabel.Text = "Status: stopped";
                        UpdateUiState();
                    }));
                }
                else
                {
                    _statusLabel.Text = "Status: stopped";
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
        _startButton.Enabled = !_isRunning && elevated;
        _editConfigButton.Enabled = !_isRunning;
        _stopButton.Enabled = _isRunning;
        _restartButton.Enabled = _isRunning && elevated;
        _trayStartItem.Enabled = _startButton.Enabled;
        _trayRestartItem.Enabled = _restartButton.Enabled;
        _trayStopItem.Enabled = _stopButton.Enabled;
        _bypassCnCheckBox.Enabled = !_isRunning;
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
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
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
                $"Config file not found:\n{configPath}",
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
                "Please select appsettings.json.",
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
                $"Config file not found:\n{configPath}",
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        using var editor = new ConfigEditorForm(configPath);
        _ = editor.ShowDialog(this);
        TryLoadBypassCnSetting(configPath, logErrors: true);
    }

    private void OnFormResize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            HideToTray();
        }
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
                        "VPN is still stopping. Exit anyway?",
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
            _notifyIcon.BalloonTipText = "Running in tray. Double-click tray icon to open.";
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

    private static (LogLevelSetting WindowLevel, LogLevelSetting FileLevel, string[] Warnings) ResolveLogLevelSettings(string configPath)
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
                warnings.Add("Failed to parse config for logging levels. Falling back to INFO.");
                return (windowLevel, fileLevel, warnings.ToArray());
            }

            if (!LoggingConfig.TryParseLevel(config.Logging.WindowLevel, out windowLevel))
            {
                warnings.Add("Invalid logging.windowLevel. Use INFO/WARN/ERROR/OFF (or NONE). Falling back to INFO.");
                windowLevel = LogLevelSetting.Info;
            }

            if (!LoggingConfig.TryParseLevel(config.Logging.FileLevel, out fileLevel))
            {
                warnings.Add("Invalid logging.fileLevel. Use INFO/WARN/ERROR/OFF (or NONE). Falling back to INFO.");
                fileLevel = LogLevelSetting.Info;
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to read logging levels. Falling back to INFO. {ex.Message}");
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
                AppendLog("INFO", $"Bypass CN default: {(config.Vpn.BypassCn ? "enabled" : "disabled")}");
            }
        }
        catch (Exception ex)
        {
            if (logErrors)
            {
                AppendLog("ERROR", $"Failed to read bypass CN setting: {ex.Message}");
            }
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

