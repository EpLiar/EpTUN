using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Diagnostics;

namespace EpTUN;

internal sealed class ConfigEditorForm : Form
{
    private static Font LabelMonoFont = CreateDefaultLabelFont();
    private static readonly JsonSerializerOptions IndentedJsonOptions = new(AppConfig.SerializerOptions)
    {
        WriteIndented = true
    };

    private UiLanguage _uiLanguage;
    private Localizer _i18n;
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
    private static readonly (string English, string Chinese)[] LocalizedTextPairs =
    [
        ("Reload", "重新加载"),
        ("Save", "保存"),
        ("Close", "关闭"),
        ("language", "语言"),
        ("autoStart", "开机自启动"),
        ("scheme", "协议"),
        ("host", "主机"),
        ("port", "端口"),
        ("executablePath", "可执行文件路径"),
        ("wintunDllPath", "Wintun DLL 路径"),
        ("argumentsTemplate", "启动参数模板"),
        ("Browse...", "浏览..."),
        ("Download", "下载"),
        ("Test", "测试"),
        ("Testing...", "测试中..."),
        ("interfaceName", "接口名"),
        ("tunAddress", "TUN 地址"),
        ("tunGateway", "TUN 网关"),
        ("tunMask", "TUN 子网掩码"),
        ("dnsServers (one per line)", "DNS 服务器（每行一条）"),
        ("includeCidrs (one per line)", "包含 CIDR（每行一条）"),
        ("excludeCidrs (one per line)", "排除 CIDR（每行一条）"),
        ("cnDatPath", "cn.dat 路径"),
        ("bypassCn", "绕过 CN"),
        ("routeMetric", "路由跃点"),
        ("startupDelayMs", "启动延迟（毫秒）"),
        ("defaultGatewayOverride", "默认网关覆盖"),
        ("addBypassRouteForProxyHost", "为代理主机添加绕过路由"),
        ("import v2ray config", "导入 v2ray 配置"),
        ("enabled", "启用"),
        ("baseUrl", "基础地址"),
        ("authorization", "授权令牌"),
        ("username", "用户名"),
        ("password", "密码"),
        ("requestId", "请求 ID"),
        ("timeoutMs", "超时（毫秒）"),
        ("resolveHostnames", "解析主机名"),
        ("autoDetectProxyPort", "自动检测代理端口"),
        ("preferPacPort", "优先 PAC 端口"),
        ("proxyHostOverride", "代理主机覆盖"),
        ("connectionTest", "连接测试"),
        ("windowLevel", "窗口日志级别"),
        ("fileLevel", "文件日志级别"),
        ("trafficSampleMilliseconds", "流量采样间隔（毫秒）")
    ];

    public ConfigEditorForm(string configPath)
    {
        _configPath = configPath;
        _uiLanguage = UiLanguageResolver.ResolveFromConfigPath(configPath);
        _i18n = new Localizer(_uiLanguage);
        LabelMonoFont = CreateLabelFont(_i18n);
        InitializeLayout();
        LoadConfigToEditors();
    }

    private void InitializeLayout()
    {
        Text = T("EpTUN Config Editor", "EpTUN 配置编辑器");
        StartPosition = FormStartPosition.CenterParent;
        Width = 980;
        Height = 700;
        MinimumSize = new Size(760, 520);
        Font = CreateEditorUiFont(_i18n);

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
        _pathLabel.Text = T($"Config: {_configPath}", $"配置：{_configPath}");
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

        _reloadButton.Text = T("Reload", "重新加载");
        _reloadButton.AutoSize = true;

        _saveButton.Text = T("Save", "保存");
        _saveButton.AutoSize = true;
        _saveButton.Enabled = false;

        _cancelButton.Text = T("Close", "关闭");
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
        var selectedTabKey = _tabControl.SelectedTab?.Tag as string;
        var selectedTabIndex = _tabControl.SelectedIndex;

        try
        {
            if (!File.Exists(_configPath))
            {
                throw new FileNotFoundException(T($"Config file not found: {_configPath}", $"未找到配置文件：{_configPath}"));
            }

            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var node = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidOperationException(T("Top-level JSON must be an object.", "顶层 JSON 必须是对象。"));

            EnsureDefaultSections(node);
            _ = ApplyUiLanguage(ResolveUiLanguageFromRoot(node));
            _rootObject = node;
            ReloadTabs(node, selectedTabKey, selectedTabIndex);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void EnsureDefaultSections(JsonObject root)
    {
        if (!root.ContainsKey("general"))
        {
            root["general"] = new JsonObject();
        }
    }

    private void ReloadTabs(JsonObject root, string? selectedTabKey, int selectedTabIndex)
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

            RestoreSelectedTab(selectedTabKey, selectedTabIndex);
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

        var page = new TabPage(GetSectionDisplayName(sectionName))
        {
            Padding = new Padding(8),
            Tag = sectionName
        };
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

                var page = _tabControl.TabPages
                    .Cast<TabPage>()
                    .FirstOrDefault(tab =>
                        string.Equals(tab.Tag as string, sectionName, StringComparison.Ordinal));
                if (page is not null)
                {
                    page.Text = GetSectionDisplayName(sectionName);
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
                .FirstOrDefault(tab =>
                    string.Equals(tab.Tag as string, sectionName, StringComparison.Ordinal));
            if (page is not null)
            {
                _tabControl.TabPages.Remove(page);
                page.Dispose();
            }
        }
    }

    private void RestoreSelectedTab(string? selectedTabKey, int selectedTabIndex)
    {
        if (!string.IsNullOrWhiteSpace(selectedTabKey))
        {
            var selectedTab = _tabControl.TabPages
                .Cast<TabPage>()
                .FirstOrDefault(tab =>
                    string.Equals(tab.Tag as string, selectedTabKey, StringComparison.Ordinal));
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
            "general" => new GeneralSectionEditor(MarkDirty, _i18n),
            "proxy" => new ProxySectionEditor(MarkDirty, _i18n),
            "tun2Socks" => new Tun2SocksSectionEditor(this, _configPath, MarkDirty, _i18n),
            "vpn" => new VpnSectionEditor(this, _configPath, MarkDirty, _i18n),
            "logging" => new LoggingSectionEditor(MarkDirty, _i18n),
            "v2rayA" => new V2RayASectionEditor(this, MarkDirty, _i18n, TestV2RayAConnectionAsync),
            _ => new RawJsonSectionEditor(MarkDirty)
        };
    }

    private string GetSectionDisplayName(string sectionName)
    {
        if (!_i18n.IsChineseSimplified)
        {
            return sectionName;
        }

        return sectionName switch
        {
            "general" => "通用",
            "logging" => "日志",
            _ => sectionName
        };
    }

    private async Task<(Uri ProxyUri, int ConnectedServerCount)> TestV2RayAConnectionAsync(V2RayAConfig v2rayAConfig)
    {
        if (!_sectionEditors.TryGetValue("proxy", out var proxyEditor))
        {
            throw new InvalidOperationException(T("proxy section is required for v2rayA test.", "v2rayA 测试依赖 proxy 分区。"));
        }

        JsonNode? proxyNode;
        try
        {
            proxyNode = proxyEditor.BuildNode();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(T($"Invalid value in tab 'proxy': {ex.Message}", $"Tab 'proxy' 中的值无效：{ex.Message}"));
        }

        var proxyConfig = JsonSerializer.Deserialize<ProxyConfig>(
                proxyNode?.ToJsonString() ?? "{}",
                AppConfig.SerializerOptions)
            ?? throw new InvalidOperationException(T("Failed to parse proxy configuration.", "解析 proxy 配置失败。"));

        proxyConfig.Validate();
        v2rayAConfig.Validate();

        var timeoutMs = Math.Clamp(v2rayAConfig.TimeoutMs * 4, 3000, 45000);
        using var cts = new CancellationTokenSource(timeoutMs);
        using var logWriter = new StringWriter();

        try
        {
            return await V2RayATouchClient.TestConnectionAsync(v2rayAConfig, proxyConfig, logWriter, cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(T($"v2rayA test timed out after {timeoutMs} ms.", $"v2rayA 测试超时（{timeoutMs} ms）。"));
        }
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
                        T($"Invalid value in tab '{sectionName}': {ex.Message}", $"Tab '{sectionName}' 中的值无效：{ex.Message}"));
                }

                _rootObject[sectionName] = sectionNode;
            }

            var serialized = _rootObject.ToJsonString(IndentedJsonOptions);

            var config = JsonSerializer.Deserialize<AppConfig>(serialized, AppConfig.SerializerOptions)
                ?? throw new InvalidOperationException(T("Failed to parse configuration.", "解析配置失败。"));
            config.Validate();
            var selectedTabKey = _tabControl.SelectedTab?.Tag as string;
            var selectedTabIndex = _tabControl.SelectedIndex;
            var nextLanguage = UiLanguageResolver.Resolve(config);
            var previousAutoStartEnabled = WindowsAutoStartManager.IsEnabled();
            var desiredAutoStartEnabled = config.General.AutoStart;
            var autoStartUpdated = false;

            try
            {
                if (desiredAutoStartEnabled)
                {
                    WindowsAutoStartManager.SetEnabled(_configPath, enabled: true);
                    autoStartUpdated = true;
                }
                else if (previousAutoStartEnabled)
                {
                    WindowsAutoStartManager.SetEnabled(_configPath, enabled: false);
                    autoStartUpdated = true;
                }

                File.WriteAllText(
                    _configPath,
                    serialized + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch
            {
                if (autoStartUpdated)
                {
                    try
                    {
                        WindowsAutoStartManager.SetEnabled(_configPath, previousAutoStartEnabled);
                    }
                    catch
                    {
                        // Preserve the original exception.
                    }
                }

                throw;
            }

            var normalizedRoot = JsonNode.Parse(serialized) as JsonObject;
            if (normalizedRoot is not null)
            {
                _rootObject = normalizedRoot;
            }

            _ = ApplyUiLanguage(nextLanguage);
            RestoreSelectedTab(selectedTabKey, selectedTabIndex);
            SetDirty(false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                T("EpTUN Config Editor", "EpTUN 配置编辑器"),
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

    private string T(string english, string chineseSimplified)
    {
        return _i18n.Text(english, chineseSimplified);
    }

    private bool ApplyUiLanguage(UiLanguage language)
    {
        if (language == _uiLanguage)
        {
            return false;
        }

        _uiLanguage = language;
        _i18n = new Localizer(language);
        LabelMonoFont = CreateLabelFont(_i18n);
        Font = CreateEditorUiFont(_i18n);
        foreach (var editor in _sectionEditors.Values)
        {
            editor.ApplyLocalization(_i18n);
        }

        ApplyLocalizedShellTexts();
        ApplyLocalizedTextsInPlace();
        return true;
    }

    private void ApplyLocalizedShellTexts()
    {
        Text = T("EpTUN Config Editor", "EpTUN 配置编辑器");
        _pathLabel.Text = T($"Config: {_configPath}", $"配置：{_configPath}");
        _reloadButton.Text = T("Reload", "重新加载");
        _saveButton.Text = T("Save", "保存");
        _cancelButton.Text = T("Close", "关闭");
    }

    private void ApplyLocalizedTextsInPlace()
    {
        foreach (TabPage tabPage in _tabControl.TabPages)
        {
            if (tabPage.Tag is string sectionName)
            {
                tabPage.Text = GetSectionDisplayName(sectionName);
            }

            ApplyLocalizedControlTextsRecursive(tabPage);
        }

        RecalculateSectionGridLabelWidths();
    }

    private void ApplyLocalizedControlTextsRecursive(Control root)
    {
        if (root is Label label && !ReferenceEquals(label, _pathLabel))
        {
            label.Font = LabelMonoFont;
            label.Text = TranslateKnownText(label.Text);
        }
        else if (root is Button or CheckBox)
        {
            root.Text = TranslateKnownText(root.Text);
        }

        foreach (Control child in root.Controls)
        {
            ApplyLocalizedControlTextsRecursive(child);
        }
    }

    private string TranslateKnownText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var (english, chinese) in LocalizedTextPairs)
        {
            if (string.Equals(text, english, StringComparison.Ordinal) ||
                string.Equals(text, chinese, StringComparison.Ordinal))
            {
                return _i18n.IsChineseSimplified ? chinese : english;
            }
        }

        return text;
    }

    private void RecalculateSectionGridLabelWidths()
    {
        foreach (var editor in _sectionEditors.Values)
        {
            foreach (var table in EnumerateTableLayouts(editor.RootControl))
            {
                if (table.ColumnStyles.Count == 0 || table.ColumnStyles[0].SizeType != SizeType.Absolute)
                {
                    continue;
                }

                var firstColumnControls = table.Controls
                    .Cast<Control>()
                    .Where(control => table.GetCellPosition(control).Column == 0)
                    .ToArray();
                if (firstColumnControls.Length == 0)
                {
                    continue;
                }

                var maxWidth = firstColumnControls
                    .Select(control => control.GetPreferredSize(Size.Empty).Width)
                    .DefaultIfEmpty(1)
                    .Max();
                var padding = TextRenderer.MeasureText("W", LabelMonoFont, Size.Empty, TextFormatFlags.NoPadding).Width * 2;
                table.ColumnStyles[0].Width = Math.Max(maxWidth + padding, 1);
            }
        }
    }

    private static IEnumerable<TableLayoutPanel> EnumerateTableLayouts(Control root)
    {
        var stack = new Stack<Control>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var control = stack.Pop();
            if (control is TableLayoutPanel tableLayout)
            {
                yield return tableLayout;
            }

            foreach (Control child in control.Controls)
            {
                stack.Push(child);
            }
        }
    }

    private static UiLanguage ResolveUiLanguageFromRoot(JsonObject root)
    {
        try
        {
            if (root["general"] is JsonObject general)
            {
                var normalized = GeneralConfig.NormalizeLanguage(general["language"]?.GetValue<string>());
                if (normalized == GeneralConfig.ChineseSimplified)
                {
                    return UiLanguage.ChineseSimplified;
                }
            }
        }
        catch
        {
            // Ignore parse errors and fallback to English.
        }

        return UiLanguage.English;
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

    private static string ReadOptionalString(JsonObject source, string key, string? fallback = null)
    {
        try
        {
            return source[key]?.GetValue<string>() ?? fallback ?? string.Empty;
        }
        catch
        {
            return fallback ?? string.Empty;
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

    private static V2RayOutboundImportResult ParseV2RayOutboundsToExcludeCidrs(string configPath)
    {
        var json = File.ReadAllText(configPath, Encoding.UTF8);
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("v2ray-core config top-level JSON must be an object.");
        if (root["outbounds"] is not JsonArray outbounds)
        {
            throw new InvalidOperationException("v2ray-core config does not contain an 'outbounds' array.");
        }

        var addressCandidates = new List<string>();
        foreach (var outbound in outbounds)
        {
            CollectAddressCandidates(outbound, addressCandidates);
        }

        var routes = new HashSet<CidrRoute>();
        var domainCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var address in addressCandidates)
        {
            if (TryParseAddressOrCidr(address, out var route))
            {
                routes.Add(route);
                continue;
            }

            if (TryExtractHost(address, out var host))
            {
                if (IPAddress.TryParse(host, out var ip))
                {
                    routes.Add(CidrRoute.Parse(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        ? $"{ip}/32"
                        : $"{ip}/128"));
                }
                else
                {
                    domainCandidates.Add(host);
                }
            }
            else
            {
                skipped++;
            }
        }

        var resolvedDomains = 0;
        var failedDomains = 0;
        foreach (var domain in domainCandidates)
        {
            if (TryResolveDomainToRoutes(domain, out var resolvedRoutes))
            {
                foreach (var route in resolvedRoutes)
                {
                    routes.Add(route);
                }

                resolvedDomains++;
            }
            else
            {
                failedDomains++;
            }
        }

        var ordered = routes
            .OrderBy(static x => x.IsIPv6)
            .ThenBy(static x => x.Network, StringComparer.Ordinal)
            .ThenBy(static x => x.PrefixLength)
            .ToArray();

        return new V2RayOutboundImportResult(
            ordered,
            addressCandidates.Count,
            skipped,
            domainCandidates.Count,
            resolvedDomains,
            failedDomains);
    }

    private static void CollectAddressCandidates(JsonNode? node, List<string> output)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (property.Key.Equals("address", StringComparison.OrdinalIgnoreCase))
                    {
                        CollectAddressValue(property.Value, output);
                        continue;
                    }

                    CollectAddressCandidates(property.Value, output);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    CollectAddressCandidates(item, output);
                }
                break;
        }
    }

    private static void CollectAddressValue(JsonNode? valueNode, List<string> output)
    {
        switch (valueNode)
        {
            case JsonValue scalar when scalar.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text):
                output.Add(text.Trim());
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonValue itemValue &&
                        itemValue.TryGetValue<string>(out var itemText) &&
                        !string.IsNullOrWhiteSpace(itemText))
                    {
                        output.Add(itemText.Trim());
                    }
                }
                break;
        }
    }

    private static bool TryParseAddressOrCidr(string value, out CidrRoute route)
    {
        route = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.Contains('/', StringComparison.Ordinal))
        {
            try
            {
                route = CidrRoute.Parse(text);
                return true;
            }
            catch
            {
                // Not a CIDR, continue with direct IP parsing.
            }
        }

        var normalized = text;
        if (normalized.StartsWith("[", StringComparison.Ordinal) &&
            normalized.EndsWith("]", StringComparison.Ordinal) &&
            normalized.Length > 2)
        {
            normalized = normalized[1..^1];
        }

        if (IPAddress.TryParse(normalized, out var ip))
        {
            route = CidrRoute.Parse(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? $"{ip}/32"
                : $"{ip}/128");
            return true;
        }

        if (normalized.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            IPAddress.TryParse(uri.Host, out ip))
        {
            route = CidrRoute.Parse(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? $"{ip}/32"
                : $"{ip}/128");
            return true;
        }

        if (Uri.TryCreate($"tcp://{normalized}", UriKind.Absolute, out var guessedUri) &&
            IPAddress.TryParse(guessedUri.Host, out ip))
        {
            route = CidrRoute.Parse(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? $"{ip}/32"
                : $"{ip}/128");
            return true;
        }

        return false;
    }

    private static bool TryExtractHost(string value, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal) &&
            normalized.EndsWith("]", StringComparison.Ordinal) &&
            normalized.Length > 2)
        {
            normalized = normalized[1..^1];
        }

        if (normalized.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
            return true;
        }

        if (Uri.TryCreate($"tcp://{normalized}", UriKind.Absolute, out var guessedUri) &&
            !string.IsNullOrWhiteSpace(guessedUri.Host))
        {
            host = guessedUri.Host;
            return true;
        }

        if (normalized.Contains(':', StringComparison.Ordinal) &&
            Uri.TryCreate($"tcp://{normalized}", UriKind.Absolute, out var withPort) &&
            !string.IsNullOrWhiteSpace(withPort.Host))
        {
            host = withPort.Host;
            return true;
        }

        if (!normalized.Contains('/', StringComparison.Ordinal))
        {
            host = normalized;
            return true;
        }

        return false;
    }

    private static bool TryResolveDomainToRoutes(string domain, out IReadOnlyCollection<CidrRoute> routes)
    {
        routes = [];
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(domain.Trim());
            var resolved = new HashSet<CidrRoute>();
            foreach (var address in addresses)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    resolved.Add(CidrRoute.Parse($"{address}/32"));
                }
                else if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    resolved.Add(CidrRoute.Parse($"{address}/128"));
                }
            }

            routes = resolved.ToArray();
            return routes.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed record V2RayOutboundImportResult(
        IReadOnlyCollection<CidrRoute> Routes,
        int AddressCandidateCount,
        int SkippedAddressCount,
        int DomainCandidateCount,
        int ResolvedDomainCount,
        int FailedDomainCount);

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

    private static Font CreateDefaultLabelFont()
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        var familyName = FindInstalledFontFamilyName("Cascadia Mono", "Cascadia Code", "Consolas");
        return !string.IsNullOrWhiteSpace(familyName)
            ? new Font(familyName, baseFont.Size, baseFont.Style, GraphicsUnit.Point)
            : baseFont;
    }

    private static int CalculateLabelColumnWidth(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
        {
            return 1;
        }

        var font = LabelMonoFont;
        var maxMeasured = labels
            .Select(label => TextRenderer.MeasureText(label, font, Size.Empty, TextFormatFlags.NoPadding).Width)
            .DefaultIfEmpty(1)
            .Max();
        var padding = TextRenderer.MeasureText("W", font, Size.Empty, TextFormatFlags.NoPadding).Width * 2;
        return Math.Max(maxMeasured + padding, 1);
    }

    private static Font CreateLabelFont(Localizer i18n)
    {
        var baseFont = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        if (i18n.IsChineseSimplified)
        {
            var chineseFamilyName = FindInstalledFontFamilyName(
                "Microsoft YaHei UI",
                "Microsoft YaHei",
                "微软雅黑",
                "Noto Sans SC",
                "Noto Sans CJK SC",
                "DengXian",
                "等线",
                "SimSun",
                "宋体");

            if (!string.IsNullOrWhiteSpace(chineseFamilyName))
            {
                return new Font(chineseFamilyName, baseFont.Size, baseFont.Style, GraphicsUnit.Point);
            }
        }

        var monoFamilyName = FindInstalledFontFamilyName("Cascadia Mono", "Cascadia Code", "Consolas");
        return !string.IsNullOrWhiteSpace(monoFamilyName)
            ? new Font(monoFamilyName, baseFont.Size, baseFont.Style, GraphicsUnit.Point)
            : baseFont;
    }

    private static Font CreateEditorUiFont(Localizer i18n)
    {
        return CreateLabelFont(i18n);
    }

    private static string? FindInstalledFontFamilyName(params string[] candidateNames)
    {
        foreach (var candidate in candidateNames)
        {
            var family = FontFamily.Families.FirstOrDefault(installed =>
                string.Equals(installed.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (family is not null)
            {
                return family.Name;
            }
        }

        return null;
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
        void ApplyLocalization(Localizer i18n);
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
            _editor.Text = sectionNode?.ToJsonString(IndentedJsonOptions) ?? "null";
        }

        public JsonNode? BuildNode()
        {
            var text = _editor.Text.Trim();
            return JsonNode.Parse(text);
        }

        public void ApplyLocalization(Localizer i18n)
        {
            // No static UI text to localize in raw editor.
        }
    }

    private sealed class GeneralSectionEditor : ISectionEditor
    {
        private static readonly string[] FieldLabels = ["language", "autoStart"];
        private static readonly string[] SupportedLanguages = [GeneralConfig.English, GeneralConfig.ChineseSimplified];

        private Localizer _i18n;
        private readonly Action _markDirty;
        private readonly Panel _panel = new() { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TableLayoutPanel _grid = CreateGrid(FieldLabels);
        private readonly ComboBox _language = CreateCombo(SupportedLanguages);
        private readonly CheckBox _autoStart = new() { AutoSize = true };

        public GeneralSectionEditor(Action markDirty, Localizer i18n)
        {
            _i18n = i18n;
            _markDirty = markDirty;
            _panel.Controls.Add(_grid);

            AddRow(_grid, 0, T("language", "语言"), _language);
            AddRow(_grid, 1, T("autoStart", "开机自启动"), _autoStart);
            _language.SelectedIndexChanged += (_, _) => _markDirty();
            _autoStart.CheckedChanged += (_, _) => _markDirty();
        }

        public Control RootControl => _panel;

        public void Load(JsonNode? sectionNode)
        {
            var defaults = new GeneralConfig();
            var obj = sectionNode as JsonObject;
            var value = obj is null
                ? defaults.Language
                : ReadString(obj, "language", defaults.Language);
            var configuredAutoStart = obj is null
                ? defaults.AutoStart
                : ReadBool(obj, "autoStart", defaults.AutoStart);

            var normalized = GeneralConfig.NormalizeLanguage(value) ?? defaults.Language;
            SetComboValue(_language, normalized);

            try
            {
                _autoStart.Checked = WindowsAutoStartManager.IsEnabled();
            }
            catch
            {
                _autoStart.Checked = configuredAutoStart;
            }
        }

        public JsonNode BuildNode()
        {
            var selected = _language.SelectedItem?.ToString() ?? GeneralConfig.English;
            var normalized = GeneralConfig.NormalizeLanguage(selected) ?? GeneralConfig.English;
            return new JsonObject
            {
                ["language"] = normalized,
                ["autoStart"] = _autoStart.Checked
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

        public void ApplyLocalization(Localizer i18n)
        {
            _i18n = i18n;
        }

        private string T(string english, string chineseSimplified)
        {
            return _i18n.Text(english, chineseSimplified);
        }
    }

    private sealed class ProxySectionEditor : ISectionEditor
    {
        private static readonly string[] FieldLabels = ["scheme", "host", "port"];

        private Localizer _i18n;
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

        public ProxySectionEditor(Action markDirty, Localizer i18n)
        {
            _i18n = i18n;
            _markDirty = markDirty;
            _panel.Controls.Add(_grid);

            AddRow(_grid, 0, T("scheme", "协议"), _scheme);
            AddRow(_grid, 1, T("host", "主机"), _host);
            AddRow(_grid, 2, T("port", "端口"), _port);

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

        public void ApplyLocalization(Localizer i18n)
        {
            _i18n = i18n;
        }

        private string T(string english, string chineseSimplified)
        {
            return _i18n.Text(english, chineseSimplified);
        }
    }

    private sealed class Tun2SocksSectionEditor : ISectionEditor
    {
        private static readonly string[] FieldLabels = ["executablePath", "wintunDllPath", "argumentsTemplate"];

        private Localizer _i18n;
        private readonly IWin32Window _owner;
        private readonly string _appConfigDirectory;
        private readonly Action _markDirty;
        private readonly Panel _panel = new() { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TableLayoutPanel _grid = CreateGrid(FieldLabels);
        private readonly TextBox _executablePath = new();
        private readonly TextBox _wintunDllPath = new();
        private readonly TextBox _argumentsTemplate = new()
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Height = 100,
            Font = new Font("Consolas", 9.0f, FontStyle.Regular, GraphicsUnit.Point)
        };
        private readonly Button _testButton = new() { AutoSize = true };
        private bool _isTesting;

        public Tun2SocksSectionEditor(IWin32Window owner, string appConfigPath, Action markDirty, Localizer i18n)
        {
            _i18n = i18n;
            _owner = owner;
            _appConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(appConfigPath))
                ?? Environment.CurrentDirectory;
            _markDirty = markDirty;
            _panel.Controls.Add(_grid);
            _testButton.Text = T("Test", "测试");

            var browseButton = new Button
            {
                Text = T("Browse...", "浏览..."),
                AutoSize = true
            };
            browseButton.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog
                {
                    Filter = T("Executable files (*.exe)|*.exe|All files (*.*)|*.*", "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*"),
                    CheckFileExists = true,
                    FileName = _executablePath.Text
                };
                if (dialog.ShowDialog(owner) == DialogResult.OK)
                {
                    _executablePath.Text = dialog.FileName;
                }
            };

            var downloadButton = new Button
            {
                Text = T("Download", "下载"),
                AutoSize = true
            };
            downloadButton.Click += (_, _) => OpenTun2SocksDownloadPage();

            var wintunBrowseButton = new Button
            {
                Text = T("Browse...", "浏览..."),
                AutoSize = true
            };
            wintunBrowseButton.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog
                {
                    Filter = T("DLL files (*.dll)|*.dll|All files (*.*)|*.*", "DLL 文件 (*.dll)|*.dll|所有文件 (*.*)|*.*"),
                    CheckFileExists = true,
                    FileName = _wintunDllPath.Text
                };
                if (dialog.ShowDialog(owner) == DialogResult.OK)
                {
                    _wintunDllPath.Text = dialog.FileName;
                }
            };

            var wintunDownloadButton = new Button
            {
                Text = T("Download", "下载"),
                AutoSize = true
            };
            wintunDownloadButton.Click += (_, _) => OpenWintunDownloadPage();

            _testButton.Click += async (_, _) => await TestExecutableAsync();
            ApplyUniformActionButtonWidth(
                browseButton,
                _testButton,
                downloadButton,
                wintunBrowseButton,
                wintunDownloadButton);
            AddExecutablePathRow(0, browseButton, downloadButton);
            AddWintunDllPathRow(1, wintunBrowseButton, wintunDownloadButton);
            AddRow(_grid, 2, T("argumentsTemplate", "启动参数模板"), _argumentsTemplate);

            _executablePath.TextChanged += (_, _) => _markDirty();
            _wintunDllPath.TextChanged += (_, _) => _markDirty();
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
            _wintunDllPath.Text = obj is null
                ? defaults.WintunDllPath
                : ReadString(obj, "wintunDllPath", defaults.WintunDllPath);
            _argumentsTemplate.Text = obj is null
                ? defaults.ArgumentsTemplate
                : ReadString(obj, "argumentsTemplate", defaults.ArgumentsTemplate);
        }

        public JsonNode BuildNode()
        {
            return new JsonObject
            {
                ["executablePath"] = _executablePath.Text.Trim(),
                ["wintunDllPath"] = _wintunDllPath.Text.Trim(),
                ["argumentsTemplate"] = _argumentsTemplate.Text
            };
        }

        private void AddExecutablePathRow(int row, Button browseButton, Button downloadButton)
        {
            var label = new Label
            {
                Text = T("executablePath", "可执行文件路径"),
                Font = LabelMonoFont,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 4, 4, 0)
            };

            var rowHost = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 4,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 0)
            };
            rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _executablePath.Dock = DockStyle.None;
            _executablePath.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _executablePath.Margin = new Padding(0);
            _executablePath.MinimumSize = new Size(120, 26);
            _executablePath.Font = _panel.Font;

            browseButton.Margin = new Padding(8, 0, 0, 0);
            browseButton.Anchor = AnchorStyles.Left;
            _testButton.Margin = new Padding(8, 0, 0, 0);
            _testButton.Anchor = AnchorStyles.Left;
            downloadButton.Margin = new Padding(8, 0, 0, 0);
            downloadButton.Anchor = AnchorStyles.Left;

            rowHost.Controls.Add(_executablePath, 0, 0);
            rowHost.Controls.Add(browseButton, 1, 0);
            rowHost.Controls.Add(_testButton, 2, 0);
            rowHost.Controls.Add(downloadButton, 3, 0);

            _grid.Controls.Add(label, 0, row);
            _grid.Controls.Add(rowHost, 1, row);
            _grid.SetColumnSpan(rowHost, 2);
        }

        private void ApplyUniformActionButtonWidth(params Button[] buttons)
        {
            if (buttons.Length == 0)
            {
                return;
            }

            var testingWidth = TextRenderer.MeasureText(T("Testing...", "测试中..."), _testButton.Font).Width + 12;
            var width = buttons
                .Select(button => Math.Max(button.PreferredSize.Width, testingWidth))
                .Max();

            foreach (var button in buttons)
            {
                button.MinimumSize = new Size(width, 0);
            }
        }

        private void AddWintunDllPathRow(int row, Button browseButton, Button downloadButton)
        {
            var label = new Label
            {
                Text = T("wintunDllPath", "Wintun DLL 路径"),
                Font = LabelMonoFont,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 4, 4, 0)
            };

            var rowHost = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 3,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 0)
            };
            rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _wintunDllPath.Dock = DockStyle.None;
            _wintunDllPath.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _wintunDllPath.Margin = new Padding(0);
            _wintunDllPath.MinimumSize = new Size(120, 26);
            _wintunDllPath.Font = _panel.Font;

            browseButton.Margin = new Padding(8, 0, 0, 0);
            browseButton.Anchor = AnchorStyles.Left;
            downloadButton.Margin = new Padding(8, 0, 0, 0);
            downloadButton.Anchor = AnchorStyles.Left;

            rowHost.Controls.Add(_wintunDllPath, 0, 0);
            rowHost.Controls.Add(browseButton, 1, 0);
            rowHost.Controls.Add(downloadButton, 2, 0);

            _grid.Controls.Add(label, 0, row);
            _grid.Controls.Add(rowHost, 1, row);
            _grid.SetColumnSpan(rowHost, 2);
        }

        private void OpenTun2SocksDownloadPage()
        {
            try
            {
                _ = Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/xjasonlyu/tun2socks/releases",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _owner,
                    T($"Failed to open download page: {ex.Message}", $"打开下载页面失败：{ex.Message}"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OpenWintunDownloadPage()
        {
            try
            {
                _ = Process.Start(new ProcessStartInfo
                {
                    FileName = "https://www.wintun.net/",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _owner,
                    T($"Failed to open download page: {ex.Message}", $"打开下载页面失败：{ex.Message}"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private async Task TestExecutableAsync()
        {
            if (_isTesting)
            {
                return;
            }

            var exePath = ResolveExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                MessageBox.Show(
                    _owner,
                    T("Please set tun2socks.executablePath first.", "请先设置 tun2socks.executablePath。"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(exePath))
            {
                MessageBox.Show(
                    _owner,
                    T(
                        $"Executable not found:{Environment.NewLine}{exePath}{Environment.NewLine}(configured value: {_executablePath.Text.Trim()})",
                        $"未找到可执行文件：{Environment.NewLine}{exePath}{Environment.NewLine}(配置值：{_executablePath.Text.Trim()})"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            _isTesting = true;
            _testButton.Enabled = false;
            var originalText = _testButton.Text;
            _testButton.Text = T("Testing...", "测试中...");

            try
            {
                var probes = new[] { "--version", "-version", "version" };
                ProcessProbeResult? successful = null;

                foreach (var probe in probes)
                {
                    var result = await RunProbeAsync(exePath, probe, timeoutMs: 5000);
                    if (!string.IsNullOrWhiteSpace(result.Output))
                    {
                        successful = result with { ProbeArguments = probe };
                        break;
                    }
                }

                if (successful is null)
                {
                    MessageBox.Show(
                        _owner,
                        T("Executable started but did not return any version/help output.", "可执行文件已启动，但未返回版本/帮助输出。"),
                        T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                var preview = successful.Output.Length > 500
                    ? successful.Output[..500] + "..."
                    : successful.Output;
                MessageBox.Show(
                    _owner,
                    T(
                        $"Test succeeded with `{successful.ProbeArguments}`.{Environment.NewLine}{Environment.NewLine}{preview}",
                        $"测试成功（参数 `{successful.ProbeArguments}`）。{Environment.NewLine}{Environment.NewLine}{preview}"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _owner,
                    T($"Executable test failed: {ex.Message}", $"可执行文件测试失败：{ex.Message}"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                _testButton.Text = originalText;
                _testButton.Enabled = true;
                _isTesting = false;
            }
        }

        private async Task<ProcessProbeResult> RunProbeAsync(string executablePath, string arguments, int timeoutMs)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException(T("Failed to start executable.", "启动可执行文件失败。"));
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore kill failures on timeout cleanup.
                }

                throw new TimeoutException(T($"Probe `{arguments}` timed out after {timeoutMs}ms.", $"探测 `{arguments}` 超时（{timeoutMs}ms）。"));
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var output = (stdout + Environment.NewLine + stderr).Trim();

            return new ProcessProbeResult(arguments, process.ExitCode, output);
        }

        private string ResolveExecutablePath()
        {
            var configured = _executablePath.Text.Trim();
            if (string.IsNullOrWhiteSpace(configured))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(configured))
            {
                return Path.GetFullPath(configured);
            }

            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(_appConfigDirectory, configured)),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)),
                Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, configured)),
                Path.GetFullPath(Path.Combine(_appConfigDirectory, Path.GetFileName(configured))),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Path.GetFileName(configured)))
            };

            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }

        private sealed record ProcessProbeResult(string ProbeArguments, int ExitCode, string Output);

        private string T(string english, string chineseSimplified)
        {
            return _i18n.Text(english, chineseSimplified);
        }

        public void ApplyLocalization(Localizer i18n)
        {
            _i18n = i18n;
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

        private Localizer _i18n;
        private readonly IWin32Window _owner;
        private readonly string _appConfigDirectory;
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

        public VpnSectionEditor(IWin32Window owner, string appConfigPath, Action markDirty, Localizer i18n)
        {
            _i18n = i18n;
            _owner = owner;
            _appConfigDirectory = Path.GetDirectoryName(Path.GetFullPath(appConfigPath))
                ?? Environment.CurrentDirectory;
            _markDirty = markDirty;

            _grid.Controls.Clear();
            _panel.Controls.Add(_grid);

            AddRow(_grid, 0, T("interfaceName", "接口名"), _interfaceName);
            AddRow(_grid, 1, T("tunAddress", "TUN 地址"), _tunAddress);
            AddRow(_grid, 2, T("tunGateway", "TUN 网关"), _tunGateway);
            AddRow(_grid, 3, T("tunMask", "TUN 子网掩码"), _tunMask);
            AddRow(_grid, 4, T("dnsServers (one per line)", "DNS 服务器（每行一条）"), _dnsServers);
            AddRow(_grid, 5, T("includeCidrs (one per line)", "包含 CIDR（每行一条）"), _includeCidrs);
            var importV2RayButton = new Button
            {
                Text = T("import v2ray config", "导入 v2ray 配置"),
                AutoSize = true
            };
            importV2RayButton.Click += (_, _) => ImportV2RayOutbounds();
            AddExcludeCidrsImportRow(6, importV2RayButton);

            var browseButton = new Button
            {
                Text = T("Browse...", "浏览..."),
                AutoSize = true
            };
            browseButton.Click += (_, _) =>
            {
                using var dialog = new OpenFileDialog
                {
                    Filter = T("DAT files (*.dat)|*.dat|All files (*.*)|*.*", "DAT 文件 (*.dat)|*.dat|所有文件 (*.*)|*.*"),
                    CheckFileExists = true,
                    FileName = _cnDatPath.Text
                };
                if (dialog.ShowDialog(owner) == DialogResult.OK)
                {
                    _cnDatPath.Text = dialog.FileName;
                }
            };

            var downloadButton = new Button
            {
                Text = T("Download", "下载"),
                AutoSize = true
            };
            downloadButton.Click += (_, _) => OpenCnDatDownloadPage();

            AddCnDatPathRow(7, browseButton, downloadButton);
            AddRow(_grid, 8, T("bypassCn", "绕过 CN"), _bypassCn);
            AddRow(_grid, 9, T("routeMetric", "路由跃点"), _routeMetric);
            AddRow(_grid, 10, T("startupDelayMs", "启动延迟（毫秒）"), _startupDelayMs);
            AddRow(_grid, 11, T("defaultGatewayOverride", "默认网关覆盖"), _defaultGatewayOverride);
            AddRow(_grid, 12, T("addBypassRouteForProxyHost", "为代理主机添加绕过路由"), _addBypassRouteForProxyHost);

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

        private void AddExcludeCidrsImportRow(int row, Button importButton)
        {
            var label = new Label
            {
                Text = T("excludeCidrs (one per line)", "排除 CIDR（每行一条）"),
                Font = LabelMonoFont,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 0)
            };

            importButton.Margin = new Padding(0, 4, 0, 0);
            importButton.Anchor = AnchorStyles.Left;

            var labelHost = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 8, 4, 0)
            };
            labelHost.Controls.Add(label);
            labelHost.Controls.Add(importButton);

            ConfigureInputControlLayout(_excludeCidrs);

            _grid.Controls.Add(labelHost, 0, row);
            _grid.Controls.Add(_excludeCidrs, 1, row);
            _grid.SetColumnSpan(_excludeCidrs, 2);
        }

        private void AddCnDatPathRow(int row, Button browseButton, Button downloadButton)
        {
            var label = new Label
            {
                Text = T("cnDatPath", "cn.dat 路径"),
                Font = LabelMonoFont,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 8, 4, 0)
            };

            var rowHost = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 3,
                RowCount = 1,
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 0)
            };
            rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            rowHost.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _cnDatPath.Dock = DockStyle.None;
            _cnDatPath.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            _cnDatPath.Margin = new Padding(0);
            _cnDatPath.MinimumSize = new Size(120, 26);
            _cnDatPath.Font = _panel.Font;

            browseButton.Margin = new Padding(8, 0, 0, 0);
            browseButton.Anchor = AnchorStyles.Left;
            downloadButton.Margin = new Padding(8, 0, 0, 0);
            downloadButton.Anchor = AnchorStyles.Left;

            rowHost.Controls.Add(_cnDatPath, 0, 0);
            rowHost.Controls.Add(browseButton, 1, 0);
            rowHost.Controls.Add(downloadButton, 2, 0);

            _grid.Controls.Add(label, 0, row);
            _grid.Controls.Add(rowHost, 1, row);
            _grid.SetColumnSpan(rowHost, 2);
        }

        private void ImportV2RayOutbounds()
        {
            var defaultPath = Path.Combine(_appConfigDirectory, "config.json");
            if (!File.Exists(defaultPath))
            {
                defaultPath = Path.Combine(Environment.CurrentDirectory, "config.json");
            }

            using var dialog = new OpenFileDialog
            {
                Filter = T("JSON files (*.json)|*.json|All files (*.*)|*.*", "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*"),
                CheckFileExists = true,
                FileName = defaultPath
            };
            if (dialog.ShowDialog(_owner) != DialogResult.OK)
            {
                return;
            }

            try
            {
                var parsed = ParseV2RayOutboundsToExcludeCidrs(dialog.FileName);
                var existingLines = SplitLines(_excludeCidrs.Text).ToList();
                var knownRoutes = new HashSet<CidrRoute>();
                foreach (var line in existingLines)
                {
                    if (TryParseAddressOrCidr(line, out var route))
                    {
                        knownRoutes.Add(route);
                    }
                }

                var added = 0;
                foreach (var route in parsed.Routes)
                {
                    if (!knownRoutes.Add(route))
                    {
                        continue;
                    }

                    existingLines.Add($"{route.Network}/{route.PrefixLength}");
                    added++;
                }

                if (added > 0)
                {
                    _excludeCidrs.Text = JoinLines(existingLines);
                }

                MessageBox.Show(
                    _owner,
                    T(
                        $"Imported {added} new CIDR routes from outbounds.{Environment.NewLine}" +
                        $"Address candidates: {parsed.AddressCandidateCount}, non-IP skipped: {parsed.SkippedAddressCount}.{Environment.NewLine}" +
                        $"Domain candidates: {parsed.DomainCandidateCount}, resolved: {parsed.ResolvedDomainCount}, failed: {parsed.FailedDomainCount}.",
                        $"已从 outbounds 导入 {added} 条 CIDR 路由。{Environment.NewLine}" +
                        $"地址候选：{parsed.AddressCandidateCount}，跳过的非 IP：{parsed.SkippedAddressCount}。{Environment.NewLine}" +
                        $"域名候选：{parsed.DomainCandidateCount}，解析成功：{parsed.ResolvedDomainCount}，失败：{parsed.FailedDomainCount}。"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _owner,
                    T($"Failed to import v2ray-core outbounds: {ex.Message}", $"导入 v2ray-core outbounds 失败：{ex.Message}"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void OpenCnDatDownloadPage()
        {
            try
            {
                _ = Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/v2fly/geoip/releases",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _owner,
                    T($"Failed to open download page: {ex.Message}", $"打开下载页面失败：{ex.Message}"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private string T(string english, string chineseSimplified)
        {
            return _i18n.Text(english, chineseSimplified);
        }

        public void ApplyLocalization(Localizer i18n)
        {
            _i18n = i18n;
        }
    }

    private sealed class V2RayASectionEditor : ISectionEditor
    {
        private static readonly string[] FieldLabels =
        [
            "enabled",
            "baseUrl",
            "authorization",
            "username",
            "password",
            "requestId",
            "timeoutMs",
            "resolveHostnames",
            "autoDetectProxyPort",
            "preferPacPort",
            "proxyHostOverride",
            "connectionTest"
        ];

        private Localizer _i18n;
        private readonly IWin32Window _owner;
        private readonly Action _markDirty;
        private readonly Func<V2RayAConfig, Task<(Uri ProxyUri, int ConnectedServerCount)>> _testConnectionAsync;
        private readonly Panel _panel = new() { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TableLayoutPanel _grid = CreateGrid(FieldLabels);

        private readonly CheckBox _enabled = new() { AutoSize = true };
        private readonly TextBox _baseUrl = new();
        private readonly TextBox _authorization = new();
        private readonly TextBox _username = new();
        private readonly TextBox _password = new() { UseSystemPasswordChar = true };
        private readonly TextBox _requestId = new();
        private readonly NumericUpDown _timeoutMs = new()
        {
            Minimum = 100,
            Maximum = 120000,
            DecimalPlaces = 0,
            Increment = 100
        };
        private readonly CheckBox _resolveHostnames = new() { AutoSize = true };
        private readonly CheckBox _autoDetectProxyPort = new() { AutoSize = true };
        private readonly CheckBox _preferPacPort = new() { AutoSize = true };
        private readonly TextBox _proxyHostOverride = new();
        private readonly Button _testButton = new() { AutoSize = true };
        private readonly Control[] _dependentControls;
        private bool _isTesting;

        public V2RayASectionEditor(
            IWin32Window owner,
            Action markDirty,
            Localizer i18n,
            Func<V2RayAConfig, Task<(Uri ProxyUri, int ConnectedServerCount)>> testConnectionAsync)
        {
            _i18n = i18n;
            _owner = owner;
            _markDirty = markDirty;
            _testConnectionAsync = testConnectionAsync;
            _testButton.Text = T("Test", "测试");
            _dependentControls =
            [
                _baseUrl,
                _authorization,
                _username,
                _password,
                _requestId,
                _timeoutMs,
                _resolveHostnames,
                _autoDetectProxyPort,
                _preferPacPort,
                _proxyHostOverride,
                _testButton
            ];

            _panel.Controls.Add(_grid);

            AddRow(_grid, 0, T("enabled", "启用"), _enabled);
            AddRow(_grid, 1, T("baseUrl", "基础地址"), _baseUrl);
            AddRow(_grid, 2, T("authorization", "授权令牌"), _authorization);
            AddRow(_grid, 3, T("username", "用户名"), _username);
            AddRow(_grid, 4, T("password", "密码"), _password);
            AddRow(_grid, 5, T("requestId", "请求 ID"), _requestId);
            AddRow(_grid, 6, T("timeoutMs", "超时（毫秒）"), _timeoutMs);
            AddRow(_grid, 7, T("resolveHostnames", "解析主机名"), _resolveHostnames);
            AddRow(_grid, 8, T("autoDetectProxyPort", "自动检测代理端口"), _autoDetectProxyPort);
            AddRow(_grid, 9, T("preferPacPort", "优先 PAC 端口"), _preferPacPort);
            AddRow(_grid, 10, T("proxyHostOverride", "代理主机覆盖"), _proxyHostOverride);

            var testHost = new FlowLayoutPanel
            {
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0)
            };
            testHost.Controls.Add(_testButton);
            AddRow(_grid, 11, T("connectionTest", "连接测试"), testHost);

            _enabled.CheckedChanged += (_, _) =>
            {
                UpdateEnabledState();
                _markDirty();
            };

            _testButton.Click += async (_, _) => await RunConnectionTestAsync();

            WireDirty(_baseUrl);
            WireDirty(_authorization);
            WireDirty(_username);
            WireDirty(_password);
            WireDirty(_requestId);
            _timeoutMs.ValueChanged += (_, _) => _markDirty();
            _resolveHostnames.CheckedChanged += (_, _) => _markDirty();
            _autoDetectProxyPort.CheckedChanged += (_, _) => _markDirty();
            _preferPacPort.CheckedChanged += (_, _) => _markDirty();
            WireDirty(_proxyHostOverride);
        }

        public Control RootControl => _panel;

        public void Load(JsonNode? sectionNode)
        {
            var defaults = new V2RayAConfig();
            var obj = sectionNode as JsonObject;

            _enabled.Checked = obj is null ? defaults.Enabled : ReadBool(obj, "enabled", defaults.Enabled);
            _baseUrl.Text = obj is null ? defaults.BaseUrl : ReadString(obj, "baseUrl", defaults.BaseUrl);
            _authorization.Text = obj is null
                ? defaults.Authorization ?? string.Empty
                : ReadOptionalString(obj, "authorization", defaults.Authorization);
            _username.Text = obj is null
                ? defaults.Username ?? string.Empty
                : ReadOptionalString(obj, "username", defaults.Username);
            _password.Text = obj is null
                ? defaults.Password ?? string.Empty
                : ReadOptionalString(obj, "password", defaults.Password);
            _requestId.Text = obj is null
                ? defaults.RequestId ?? string.Empty
                : ReadOptionalString(obj, "requestId", defaults.RequestId);
            _timeoutMs.Value = Math.Clamp(
                obj is null ? defaults.TimeoutMs : ReadInt(obj, "timeoutMs", defaults.TimeoutMs),
                (int)_timeoutMs.Minimum,
                (int)_timeoutMs.Maximum);
            _resolveHostnames.Checked = obj is null
                ? defaults.ResolveHostnames
                : ReadBool(obj, "resolveHostnames", defaults.ResolveHostnames);
            _autoDetectProxyPort.Checked = obj is null
                ? defaults.AutoDetectProxyPort
                : ReadBool(obj, "autoDetectProxyPort", defaults.AutoDetectProxyPort);
            _preferPacPort.Checked = obj is null
                ? defaults.PreferPacPort
                : ReadBool(obj, "preferPacPort", defaults.PreferPacPort);
            _proxyHostOverride.Text = obj is null
                ? defaults.ProxyHostOverride ?? string.Empty
                : ReadOptionalString(obj, "proxyHostOverride", defaults.ProxyHostOverride);

            UpdateEnabledState();
        }

        public JsonNode BuildNode()
        {
            return new JsonObject
            {
                ["enabled"] = _enabled.Checked,
                ["baseUrl"] = _baseUrl.Text.Trim(),
                ["authorization"] = _authorization.Text.Trim(),
                ["username"] = _username.Text.Trim(),
                ["password"] = _password.Text.Trim(),
                ["requestId"] = _requestId.Text.Trim(),
                ["timeoutMs"] = (int)_timeoutMs.Value,
                ["resolveHostnames"] = _resolveHostnames.Checked,
                ["autoDetectProxyPort"] = _autoDetectProxyPort.Checked,
                ["preferPacPort"] = _preferPacPort.Checked,
                ["proxyHostOverride"] = _proxyHostOverride.Text.Trim()
            };
        }

        private async Task RunConnectionTestAsync()
        {
            if (_isTesting)
            {
                return;
            }

            V2RayAConfig config;
            try
            {
                config = JsonSerializer.Deserialize<V2RayAConfig>(
                        BuildNode().ToJsonString(),
                        AppConfig.SerializerOptions)
                    ?? throw new InvalidOperationException(T("Failed to parse v2rayA configuration.", "解析 v2rayA 配置失败。"));
                config.Validate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _owner,
                    T($"v2rayA test failed: {ex.Message}", $"v2rayA 测试失败：{ex.Message}"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            _isTesting = true;
            UpdateEnabledState();
            var cursor = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;

            try
            {
                var result = await _testConnectionAsync(config);
                MessageBox.Show(
                    _owner,
                    T(
                        $"v2rayA test succeeded.{Environment.NewLine}" +
                        $"Proxy endpoint: {result.ProxyUri}{Environment.NewLine}" +
                        $"Connected servers: {result.ConnectedServerCount}",
                        $"v2rayA 测试成功。{Environment.NewLine}" +
                        $"代理端点：{result.ProxyUri}{Environment.NewLine}" +
                        $"已连接服务器数：{result.ConnectedServerCount}"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    _owner,
                    T($"v2rayA test failed: {ex.Message}", $"v2rayA 测试失败：{ex.Message}"),
                    T("EpTUN Config Editor", "EpTUN 配置编辑器"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                Cursor.Current = cursor;
                _isTesting = false;
                UpdateEnabledState();
            }
        }

        private void UpdateEnabledState()
        {
            _enabled.Enabled = !_isTesting;
            var enabled = _enabled.Checked && !_isTesting;
            foreach (var control in _dependentControls)
            {
                control.Enabled = enabled;
            }
        }

        private void WireDirty(TextBox textBox)
        {
            textBox.TextChanged += (_, _) => _markDirty();
        }

        private string T(string english, string chineseSimplified)
        {
            return _i18n.Text(english, chineseSimplified);
        }

        public void ApplyLocalization(Localizer i18n)
        {
            _i18n = i18n;
        }
    }

    private sealed class LoggingSectionEditor : ISectionEditor
    {
        private static readonly string[] FieldLabels = ["windowLevel", "fileLevel", "trafficSampleMilliseconds"];

        private Localizer _i18n;
        private readonly Action _markDirty;
        private readonly Panel _panel = new() { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TableLayoutPanel _grid = CreateGrid(FieldLabels);
        private readonly ComboBox _windowLevel = CreateCombo(["INFO", "WARN", "ERROR", "OFF"]);
        private readonly ComboBox _fileLevel = CreateCombo(["INFO", "WARN", "ERROR", "OFF"]);
        private readonly NumericUpDown _trafficSampleMilliseconds = new()
        {
            Minimum = 100,
            Maximum = 3600000,
            DecimalPlaces = 0
        };

        public LoggingSectionEditor(Action markDirty, Localizer i18n)
        {
            _i18n = i18n;
            _markDirty = markDirty;
            _panel.Controls.Add(_grid);

            AddRow(_grid, 0, T("windowLevel", "窗口日志级别"), _windowLevel);
            AddRow(_grid, 1, T("fileLevel", "文件日志级别"), _fileLevel);
            AddRow(_grid, 2, T("trafficSampleMilliseconds", "流量采样间隔（毫秒）"), _trafficSampleMilliseconds);

            _windowLevel.SelectedIndexChanged += (_, _) => _markDirty();
            _fileLevel.SelectedIndexChanged += (_, _) => _markDirty();
            _trafficSampleMilliseconds.ValueChanged += (_, _) => _markDirty();
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
            var sampleMilliseconds = defaults.TrafficSampleMilliseconds;
            if (obj is not null)
            {
                sampleMilliseconds = ReadInt(obj, "trafficSampleMilliseconds", defaults.TrafficSampleMilliseconds);
                if (!obj.ContainsKey("trafficSampleMilliseconds") && obj.ContainsKey("trafficSampleSeconds"))
                {
                    sampleMilliseconds = ReadInt(obj, "trafficSampleSeconds", 1) * 1000;
                }
            }

            SetComboValue(_windowLevel, window);
            SetComboValue(_fileLevel, file);
            _trafficSampleMilliseconds.Value = Math.Clamp(
                sampleMilliseconds,
                (int)_trafficSampleMilliseconds.Minimum,
                (int)_trafficSampleMilliseconds.Maximum);
        }

        public JsonNode BuildNode()
        {
            return new JsonObject
            {
                ["windowLevel"] = _windowLevel.SelectedItem?.ToString() ?? "INFO",
                ["fileLevel"] = _fileLevel.SelectedItem?.ToString() ?? "INFO",
                ["trafficSampleMilliseconds"] = (int)_trafficSampleMilliseconds.Value
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

        private string T(string english, string chineseSimplified)
        {
            return _i18n.Text(english, chineseSimplified);
        }

        public void ApplyLocalization(Localizer i18n)
        {
            _i18n = i18n;
        }
    }
}
