using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EpTUN;

internal sealed class ConfigEditorForm : Form
{
    private static readonly Font LabelMonoFont = CreateLabelMonoFont();

    private readonly string _configPath;
    private readonly Label _pathLabel = new();
    private readonly TabControl _tabControl = new();
    private readonly Button _reloadButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _cancelButton = new();
    private readonly Dictionary<string, ISectionEditor> _sectionEditors = new(StringComparer.Ordinal);

    private JsonObject _rootObject = new();
    private bool _isDirty;
    private bool _isLoading;

    public ConfigEditorForm(string configPath)
    {
        _configPath = configPath;
        InitializeLayout();
        LoadConfigToEditors();
    }

    private void InitializeLayout()
    {
        Text = "EpTUN Config Editor";
        StartPosition = FormStartPosition.CenterParent;
        Width = 980;
        Height = 700;
        MinimumSize = new Size(760, 520);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _pathLabel.AutoSize = true;
        _pathLabel.Text = $"Config: {_configPath}";
        _pathLabel.Margin = new Padding(0, 0, 0, 8);

        _tabControl.Dock = DockStyle.Fill;

        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        };

        _reloadButton.Text = "Reload";
        _reloadButton.AutoSize = true;

        _saveButton.Text = "Save";
        _saveButton.AutoSize = true;
        _saveButton.Enabled = false;

        _cancelButton.Text = "Close";
        _cancelButton.AutoSize = true;

        actionRow.Controls.Add(_cancelButton);
        actionRow.Controls.Add(_saveButton);
        actionRow.Controls.Add(_reloadButton);

        root.Controls.Add(_pathLabel, 0, 0);
        root.Controls.Add(_tabControl, 0, 1);
        root.Controls.Add(actionRow, 0, 2);

        Controls.Add(root);

        _reloadButton.Click += (_, _) => LoadConfigToEditors();
        _saveButton.Click += (_, _) => SaveEditorsToConfig();
        _cancelButton.Click += (_, _) => Close();
    }

    private void LoadConfigToEditors()
    {
        var selectedTabName = _tabControl.SelectedTab?.Text;
        var selectedTabIndex = _tabControl.SelectedIndex;

        try
        {
            if (!File.Exists(_configPath))
            {
                throw new FileNotFoundException($"Config file not found: {_configPath}");
            }

            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var node = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidOperationException("Top-level JSON must be an object.");

            _rootObject = node;
            ReloadTabs(node, selectedTabName, selectedTabIndex);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "EpTUN Config Editor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ReloadTabs(JsonObject root, string? selectedTabName, int selectedTabIndex)
    {
        _isLoading = true;
        try
        {
            _tabControl.SuspendLayout();
            if (_sectionEditors.Count == 0 || _tabControl.TabPages.Count == 0)
            {
                foreach (var (sectionName, sectionValue) in root)
                {
                    AddTabPage(sectionName, sectionValue);
                }
            }
            else
            {
                UpdateTabsInPlace(root);
            }

            RestoreSelectedTab(selectedTabName, selectedTabIndex);
        }
        finally
        {
            _tabControl.ResumeLayout();
            _isLoading = false;
            SetDirty(false);
        }
    }

    private void AddTabPage(string sectionName, JsonNode? sectionValue)
    {
        var editor = CreateEditor(sectionName);
        editor.Load(sectionValue);

        var page = new TabPage(sectionName) { Padding = new Padding(8) };
        var rootControl = editor.RootControl;
        rootControl.Dock = DockStyle.Fill;
        page.Controls.Add(rootControl);

        _tabControl.TabPages.Add(page);
        _sectionEditors[sectionName] = editor;
    }

    private void UpdateTabsInPlace(JsonObject root)
    {
        var obsoleteSections = new HashSet<string>(_sectionEditors.Keys, StringComparer.Ordinal);

        foreach (var (sectionName, sectionValue) in root)
        {
            if (_sectionEditors.TryGetValue(sectionName, out var editor))
            {
                editor.RootControl.SuspendLayout();
                try
                {
                    editor.Load(sectionValue);
                }
                finally
                {
                    editor.RootControl.ResumeLayout();
                }

                obsoleteSections.Remove(sectionName);
            }
            else
            {
                AddTabPage(sectionName, sectionValue);
            }
        }

        foreach (var sectionName in obsoleteSections)
        {
            if (!_sectionEditors.Remove(sectionName))
            {
                continue;
            }

            var page = _tabControl.TabPages
                .Cast<TabPage>()
                .FirstOrDefault(tab => string.Equals(tab.Text, sectionName, StringComparison.Ordinal));
            if (page is not null)
            {
                _tabControl.TabPages.Remove(page);
                page.Dispose();
            }
        }
    }

    private void RestoreSelectedTab(string? selectedTabName, int selectedTabIndex)
    {
        if (!string.IsNullOrWhiteSpace(selectedTabName))
        {
            var selectedTab = _tabControl.TabPages
                .Cast<TabPage>()
                .FirstOrDefault(tab => string.Equals(tab.Text, selectedTabName, StringComparison.Ordinal));
            if (selectedTab is not null)
            {
                _tabControl.SelectedTab = selectedTab;
                return;
            }
        }

        if (selectedTabIndex >= 0 && selectedTabIndex < _tabControl.TabPages.Count)
        {
            _tabControl.SelectedIndex = selectedTabIndex;
        }
    }

    private ISectionEditor CreateEditor(string sectionName)
    {
        return sectionName switch
        {
            "proxy" => new ProxySectionEditor(MarkDirty),
            "tun2Socks" => new Tun2SocksSectionEditor(this, MarkDirty),
            "vpn" => new VpnSectionEditor(this, MarkDirty),
            "logging" => new LoggingSectionEditor(MarkDirty),
            "v2rayA" => new RawJsonSectionEditor(MarkDirty),
            _ => new RawJsonSectionEditor(MarkDirty)
        };
    }

    private void SaveEditorsToConfig()
    {
        if (!_isDirty)
        {
            return;
        }

        try
        {
            foreach (var (sectionName, editor) in _sectionEditors)
            {
                JsonNode? sectionNode;
                try
                {
                    sectionNode = editor.BuildNode();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Invalid value in tab '{sectionName}': {ex.Message}");
                }

                _rootObject[sectionName] = sectionNode;
            }

            var serialized = _rootObject.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var config = JsonSerializer.Deserialize<AppConfig>(serialized, AppConfig.SerializerOptions)
                ?? throw new InvalidOperationException("Failed to parse configuration.");
            config.Validate();

            File.WriteAllText(
                _configPath,
                serialized + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var normalizedRoot = JsonNode.Parse(serialized) as JsonObject;
            if (normalizedRoot is not null)
            {
                _rootObject = normalizedRoot;
            }

            SetDirty(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "EpTUN Config Editor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void MarkDirty()
    {
        if (_isLoading)
        {
            return;
        }

        SetDirty(true);
    }

    private void SetDirty(bool value)
    {
        _isDirty = value;
        _saveButton.Enabled = _isDirty;
    }

    private static string ReadString(JsonObject source, string key, string fallback)
    {
        try
        {
            var value = source[key]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static int ReadInt(JsonObject source, string key, int fallback)
    {
        try
        {
            return source[key]?.GetValue<int>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadBool(JsonObject source, string key, bool fallback)
    {
        try
        {
            return source[key]?.GetValue<bool>() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string[] ReadStringArray(JsonObject source, string key, IEnumerable<string> fallback)
    {
        if (source[key] is not JsonArray array)
        {
            return fallback.ToArray();
        }

        var values = new List<string>();
        foreach (var node in array)
        {
            var value = node?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        return values.Count > 0 ? values.ToArray() : fallback.ToArray();
    }

    private static JsonArray BuildArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Where(static x => !string.IsNullOrWhiteSpace(x)))
        {
            array.Add(value.Trim());
        }

        return array;
    }

    private static string JoinLines(IEnumerable<string> values)
    {
        return string.Join(Environment.NewLine, values);
    }

    private static string[] SplitLines(string text)
    {
        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static x => x.Trim())
            .Where(static x => x.Length > 0)
            .ToArray();
    }

    private static TableLayoutPanel CreateGrid(IReadOnlyList<string> labels)
    {
        var labelWidth = CalculateLabelColumnWidth(labels);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = labels.Count,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < labels.Count; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        return table;
    }

    private static Font CreateLabelMonoFont()
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        var hasConsolas = FontFamily.Families.Any(static family =>
            string.Equals(family.Name, "Consolas", StringComparison.OrdinalIgnoreCase));
        return hasConsolas
            ? new Font("Consolas", baseFont.Size, baseFont.Style)
            : new Font(FontFamily.GenericMonospace, baseFont.Size, baseFont.Style);
    }

    private static int CalculateLabelColumnWidth(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
        {
            return 1;
        }

        var font = LabelMonoFont;
        var longestLength = labels.Max(static x => x.Length);
        var extraChars = 2;
        var charWidth = TextRenderer.MeasureText("W", font, Size.Empty, TextFormatFlags.NoPadding).Width;
        return Math.Max(charWidth * (longestLength + extraChars), 1);
    }

    private static void AddRow(
        TableLayoutPanel table,
        int row,
        string labelText,
        Control inputControl,
        Control? actionControl = null)
    {
        var label = new Label
        {
            Text = labelText,
            Font = LabelMonoFont,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 4, 0)
        };

        ConfigureInputControlLayout(inputControl);

        table.Controls.Add(label, 0, row);
        table.Controls.Add(inputControl, 1, row);

        if (actionControl is null)
        {
            table.SetColumnSpan(inputControl, 2);
            return;
        }

        actionControl.Margin = new Padding(6, 4, 0, 0);
        actionControl.Anchor = AnchorStyles.Left;
        table.Controls.Add(actionControl, 2, row);
    }

    private static void ConfigureInputControlLayout(Control control)
    {
        control.Margin = new Padding(0, 4, 0, 0);

        switch (control)
        {
            case TextBox textBox when !textBox.Multiline:
                textBox.Dock = DockStyle.Top;
                break;
            case TextBox textBox when textBox.Multiline:
                textBox.Dock = DockStyle.Top;
                break;
            case ComboBox comboBox:
                comboBox.Dock = DockStyle.Top;
                break;
            case NumericUpDown numeric:
                numeric.Dock = DockStyle.Top;
                break;
            case CheckBox checkBox:
                checkBox.Anchor = AnchorStyles.Left;
                checkBox.AutoSize = true;
                break;
            default:
                control.Dock = DockStyle.Fill;
                break;
        }
    }

    private static ComboBox CreateCombo(IEnumerable<string> options)
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        combo.Items.AddRange(options.Cast<object>().ToArray());
        return combo;
    }

    private interface ISectionEditor
    {
        Control RootControl { get; }
        void Load(JsonNode? sectionNode);
        JsonNode? BuildNode();
    }

    private sealed class RawJsonSectionEditor : ISectionEditor
    {
        private readonly Action _markDirty;
        private readonly TextBox _editor = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            AcceptsTab = true,
            WordWrap = false,
            Font = new Font("Consolas", 9.0f, FontStyle.Regular, GraphicsUnit.Point)
        };

        public RawJsonSectionEditor(Action markDirty)
        {
            _markDirty = markDirty;
            _editor.TextChanged += (_, _) => _markDirty();
        }

        public Control RootControl => _editor;

        public void Load(JsonNode? sectionNode)
        {
            _editor.Text = sectionNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
        }

        public JsonNode? BuildNode()
        {
            var text = _editor.Text.Trim();
            return JsonNode.Parse(text);
        }
    }

    private sealed class ProxySectionEditor : ISectionEditor
    {
        private static readonly string[] FieldLabels = ["scheme", "host", "port"];

        private readonly Action _markDirty;
        private readonly Panel _panel = new() { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TableLayoutPanel _grid = CreateGrid(FieldLabels);
        private readonly ComboBox _scheme = CreateCombo(["socks5", "http"]);
        private readonly TextBox _host = new();
        private readonly NumericUpDown _port = new()
        {
            Minimum = 1,
            Maximum = 65535,
            DecimalPlaces = 0
        };

        public ProxySectionEditor(Action markDirty)
        {
            _markDirty = markDirty;
            _panel.Controls.Add(_grid);

            AddRow(_grid, 0, "scheme", _scheme);
            AddRow(_grid, 1, "host", _host);
            AddRow(_grid, 2, "port", _port);

            _scheme.SelectedIndexChanged += (_, _) => _markDirty();
            _host.TextChanged += (_, _) => _markDirty();
            _port.ValueChanged += (_, _) => _markDirty();
        }

        public Control RootControl => _panel;

        public void Load(JsonNode? sectionNode)
        {
            var defaults = new ProxyConfig();
            var obj = sectionNode as JsonObject;

            var scheme = obj is null ? defaults.Scheme : ReadString(obj, "scheme", defaults.Scheme);
            if (_scheme.Items.IndexOf(scheme) < 0)
            {
                _scheme.Items.Add(scheme);
            }

            _scheme.SelectedItem = scheme;
            _host.Text = obj is null ? defaults.Host : ReadString(obj, "host", defaults.Host);
            _port.Value = Math.Clamp(
                obj is null ? defaults.Port : ReadInt(obj, "port", defaults.Port),
                (int)_port.Minimum,
                (int)_port.Maximum);
        }

        public JsonNode BuildNode()
        {
            return new JsonObject
            {
                ["scheme"] = (_scheme.SelectedItem?.ToString() ?? "socks5").Trim(),
                ["host"] = _host.Text.Trim(),
                ["port"] = (int)_port.Value
            };
        }
    }

    private sealed class Tun2SocksSectionEditor : ISectionEditor
    {
        private static readonly string[] FieldLabels = ["executablePath", "argumentsTemplate"];

        private readonly Action _markDirty;
        private readonly Panel _panel = new() { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TableLayoutPanel _grid = CreateGrid(FieldLabels);
        private readonly TextBox _executablePath = new();
        private readonly TextBox _argumentsTemplate = new()
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Height = 100,
            Font = new Font("Consolas", 9.0f, FontStyle.Regular, GraphicsUnit.Point)
        };

        public Tun2SocksSectionEditor(IWin32Window owner, Action markDirty)
        {
            _markDirty = markDirty;
            _panel.Controls.Add(_grid);

            var browseButton = new Button
            {
                Text = "Browse...",
                AutoSize = true
            };
            browseButton.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog
                {
                    Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
                    CheckFileExists = true,
                    FileName = _executablePath.Text
                };
                if (dialog.ShowDialog(owner) == DialogResult.OK)
                {
                    _executablePath.Text = dialog.FileName;
                }
            };

            AddRow(_grid, 0, "executablePath", _executablePath, browseButton);
            AddRow(_grid, 1, "argumentsTemplate", _argumentsTemplate);

            _executablePath.TextChanged += (_, _) => _markDirty();
            _argumentsTemplate.TextChanged += (_, _) => _markDirty();
        }

        public Control RootControl => _panel;

        public void Load(JsonNode? sectionNode)
        {
            var defaults = new Tun2SocksConfig();
            var obj = sectionNode as JsonObject;

            _executablePath.Text = obj is null
                ? defaults.ExecutablePath
                : ReadString(obj, "executablePath", defaults.ExecutablePath);
            _argumentsTemplate.Text = obj is null
                ? defaults.ArgumentsTemplate
                : ReadString(obj, "argumentsTemplate", defaults.ArgumentsTemplate);
        }

        public JsonNode BuildNode()
        {
            return new JsonObject
            {
                ["executablePath"] = _executablePath.Text.Trim(),
                ["argumentsTemplate"] = _argumentsTemplate.Text
            };
        }
    }

    private sealed class VpnSectionEditor : ISectionEditor
    {
        private static readonly string[] FieldLabels =
        [
            "interfaceName",
            "tunAddress",
            "tunGateway",
            "tunMask",
            "dnsServers (one per line)",
            "includeCidrs (one per line)",
            "excludeCidrs (one per line)",
            "cnDatPath",
            "bypassCn",
            "routeMetric",
            "startupDelayMs",
            "defaultGatewayOverride",
            "addBypassRouteForProxyHost"
        ];

        private readonly Action _markDirty;
        private readonly Panel _panel = new() { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TableLayoutPanel _grid = CreateGrid(FieldLabels);

        private readonly TextBox _interfaceName = new();
        private readonly TextBox _tunAddress = new();
        private readonly TextBox _tunGateway = new();
        private readonly TextBox _tunMask = new();
        private readonly TextBox _dnsServers = CreateLargeTextBox();
        private readonly TextBox _includeCidrs = CreateLargeTextBox();
        private readonly TextBox _excludeCidrs = CreateLargeTextBox();
        private readonly TextBox _cnDatPath = new();
        private readonly CheckBox _bypassCn = new() { AutoSize = true };
        private readonly NumericUpDown _routeMetric = new() { Minimum = 1, Maximum = 1024, DecimalPlaces = 0 };
        private readonly NumericUpDown _startupDelayMs = new() { Minimum = 0, Maximum = 120000, DecimalPlaces = 0 };
        private readonly TextBox _defaultGatewayOverride = new();
        private readonly CheckBox _addBypassRouteForProxyHost = new() { AutoSize = true };

        public VpnSectionEditor(IWin32Window owner, Action markDirty)
        {
            _markDirty = markDirty;

            _grid.Controls.Clear();
            _panel.Controls.Add(_grid);

            AddRow(_grid, 0, "interfaceName", _interfaceName);
            AddRow(_grid, 1, "tunAddress", _tunAddress);
            AddRow(_grid, 2, "tunGateway", _tunGateway);
            AddRow(_grid, 3, "tunMask", _tunMask);
            AddRow(_grid, 4, "dnsServers (one per line)", _dnsServers);
            AddRow(_grid, 5, "includeCidrs (one per line)", _includeCidrs);
            AddRow(_grid, 6, "excludeCidrs (one per line)", _excludeCidrs);

            var browseButton = new Button
            {
                Text = "Browse...",
                AutoSize = true
            };
            browseButton.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog
                {
                    Filter = "DAT files (*.dat)|*.dat|All files (*.*)|*.*",
                    CheckFileExists = true,
                    FileName = _cnDatPath.Text
                };
                if (dialog.ShowDialog(owner) == DialogResult.OK)
                {
                    _cnDatPath.Text = dialog.FileName;
                }
            };

            AddRow(_grid, 7, "cnDatPath", _cnDatPath, browseButton);
            AddRow(_grid, 8, "bypassCn", _bypassCn);
            AddRow(_grid, 9, "routeMetric", _routeMetric);
            AddRow(_grid, 10, "startupDelayMs", _startupDelayMs);
            AddRow(_grid, 11, "defaultGatewayOverride", _defaultGatewayOverride);
            AddRow(_grid, 12, "addBypassRouteForProxyHost", _addBypassRouteForProxyHost);

            WireDirty(_interfaceName);
            WireDirty(_tunAddress);
            WireDirty(_tunGateway);
            WireDirty(_tunMask);
            WireDirty(_dnsServers);
            WireDirty(_includeCidrs);
            WireDirty(_excludeCidrs);
            WireDirty(_cnDatPath);
            _bypassCn.CheckedChanged += (_, _) => _markDirty();
            _routeMetric.ValueChanged += (_, _) => _markDirty();
            _startupDelayMs.ValueChanged += (_, _) => _markDirty();
            WireDirty(_defaultGatewayOverride);
            _addBypassRouteForProxyHost.CheckedChanged += (_, _) => _markDirty();
        }

        public Control RootControl => _panel;

        public void Load(JsonNode? sectionNode)
        {
            var defaults = new VpnConfig();
            var obj = sectionNode as JsonObject;

            _interfaceName.Text = obj is null ? defaults.InterfaceName : ReadString(obj, "interfaceName", defaults.InterfaceName);
            _tunAddress.Text = obj is null ? defaults.TunAddress : ReadString(obj, "tunAddress", defaults.TunAddress);
            _tunGateway.Text = obj is null ? defaults.TunGateway : ReadString(obj, "tunGateway", defaults.TunGateway);
            _tunMask.Text = obj is null ? defaults.TunMask : ReadString(obj, "tunMask", defaults.TunMask);
            _dnsServers.Text = JoinLines(obj is null
                ? defaults.DnsServers
                : ReadStringArray(obj, "dnsServers", defaults.DnsServers));
            _includeCidrs.Text = JoinLines(obj is null
                ? defaults.IncludeCidrs
                : ReadStringArray(obj, "includeCidrs", defaults.IncludeCidrs));
            _excludeCidrs.Text = JoinLines(obj is null
                ? defaults.ExcludeCidrs
                : ReadStringArray(obj, "excludeCidrs", defaults.ExcludeCidrs));
            _cnDatPath.Text = obj is null ? defaults.CnDatPath : ReadString(obj, "cnDatPath", defaults.CnDatPath);
            _bypassCn.Checked = obj is null ? defaults.BypassCn : ReadBool(obj, "bypassCn", defaults.BypassCn);
            _routeMetric.Value = Math.Clamp(
                obj is null ? defaults.RouteMetric : ReadInt(obj, "routeMetric", defaults.RouteMetric),
                (int)_routeMetric.Minimum,
                (int)_routeMetric.Maximum);
            _startupDelayMs.Value = Math.Clamp(
                obj is null ? defaults.StartupDelayMs : ReadInt(obj, "startupDelayMs", defaults.StartupDelayMs),
                (int)_startupDelayMs.Minimum,
                (int)_startupDelayMs.Maximum);
            _defaultGatewayOverride.Text = obj is null
                ? defaults.DefaultGatewayOverride ?? string.Empty
                : ReadString(obj, "defaultGatewayOverride", defaults.DefaultGatewayOverride ?? string.Empty);
            _addBypassRouteForProxyHost.Checked = obj is null
                ? defaults.AddBypassRouteForProxyHost
                : ReadBool(obj, "addBypassRouteForProxyHost", defaults.AddBypassRouteForProxyHost);
        }

        public JsonNode BuildNode()
        {
            return new JsonObject
            {
                ["interfaceName"] = _interfaceName.Text.Trim(),
                ["tunAddress"] = _tunAddress.Text.Trim(),
                ["tunGateway"] = _tunGateway.Text.Trim(),
                ["tunMask"] = _tunMask.Text.Trim(),
                ["dnsServers"] = BuildArray(SplitLines(_dnsServers.Text)),
                ["includeCidrs"] = BuildArray(SplitLines(_includeCidrs.Text)),
                ["excludeCidrs"] = BuildArray(SplitLines(_excludeCidrs.Text)),
                ["cnDatPath"] = _cnDatPath.Text.Trim(),
                ["bypassCn"] = _bypassCn.Checked,
                ["routeMetric"] = (int)_routeMetric.Value,
                ["startupDelayMs"] = (int)_startupDelayMs.Value,
                ["defaultGatewayOverride"] = string.IsNullOrWhiteSpace(_defaultGatewayOverride.Text)
                    ? null
                    : _defaultGatewayOverride.Text.Trim(),
                ["addBypassRouteForProxyHost"] = _addBypassRouteForProxyHost.Checked
            };
        }

        private static TextBox CreateLargeTextBox()
        {
            return new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = false,
                Height = 100
            };
        }

        private void WireDirty(TextBox textBox)
        {
            textBox.TextChanged += (_, _) => _markDirty();
        }
    }

    private sealed class LoggingSectionEditor : ISectionEditor
    {
        private static readonly string[] FieldLabels = ["windowLevel", "fileLevel"];

        private readonly Action _markDirty;
        private readonly Panel _panel = new() { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TableLayoutPanel _grid = CreateGrid(FieldLabels);
        private readonly ComboBox _windowLevel = CreateCombo(["INFO", "WARN", "ERROR", "OFF"]);
        private readonly ComboBox _fileLevel = CreateCombo(["INFO", "WARN", "ERROR", "OFF"]);

        public LoggingSectionEditor(Action markDirty)
        {
            _markDirty = markDirty;
            _panel.Controls.Add(_grid);

            AddRow(_grid, 0, "windowLevel", _windowLevel);
            AddRow(_grid, 1, "fileLevel", _fileLevel);

            _windowLevel.SelectedIndexChanged += (_, _) => _markDirty();
            _fileLevel.SelectedIndexChanged += (_, _) => _markDirty();
        }

        public Control RootControl => _panel;

        public void Load(JsonNode? sectionNode)
        {
            var defaults = new LoggingConfig();
            var obj = sectionNode as JsonObject;

            var window = obj is null
                ? defaults.WindowLevel ?? "INFO"
                : ReadString(obj, "windowLevel", defaults.WindowLevel ?? "INFO");
            var file = obj is null
                ? defaults.FileLevel ?? "INFO"
                : ReadString(obj, "fileLevel", defaults.FileLevel ?? "INFO");

            SetComboValue(_windowLevel, window);
            SetComboValue(_fileLevel, file);
        }

        public JsonNode BuildNode()
        {
            return new JsonObject
            {
                ["windowLevel"] = _windowLevel.SelectedItem?.ToString() ?? "INFO",
                ["fileLevel"] = _fileLevel.SelectedItem?.ToString() ?? "INFO"
            };
        }

        private static void SetComboValue(ComboBox combo, string value)
        {
            if (combo.Items.IndexOf(value) < 0)
            {
                combo.Items.Add(value);
            }

            combo.SelectedItem = value;
        }
    }
}
