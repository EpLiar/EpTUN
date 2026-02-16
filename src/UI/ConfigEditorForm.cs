using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EpTUN;

internal sealed class ConfigEditorForm : Form
{
    private readonly string _configPath;
    private readonly Label _pathLabel = new();
    private readonly TabControl _tabControl = new();
    private readonly Button _reloadButton = new();
    private readonly Button _saveButton = new();
    private readonly Button _cancelButton = new();
    private readonly Dictionary<string, TextBox> _editors = new(StringComparer.Ordinal);

    private JsonObject _rootObject = new();
    private bool _isDirty;

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
            RebuildTabs(node);
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

    private void RebuildTabs(JsonObject root)
    {
        _tabControl.SuspendLayout();
        _tabControl.TabPages.Clear();
        _editors.Clear();

        foreach (var (sectionName, sectionValue) in root)
        {
            var page = new TabPage(sectionName)
            {
                Padding = new Padding(8)
            };

            var editor = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                AcceptsTab = true,
                WordWrap = false,
                Font = new Font("Consolas", 9.0f, FontStyle.Regular, GraphicsUnit.Point)
            };

            editor.Text = ToSectionJsonText(sectionValue);
            editor.TextChanged += (_, _) => SetDirty(true);
            page.Controls.Add(editor);
            _tabControl.TabPages.Add(page);
            _editors[sectionName] = editor;
        }

        _tabControl.ResumeLayout();
    }

    private static string ToSectionJsonText(JsonNode? value)
    {
        return value?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";
    }

    private void SaveEditorsToConfig()
    {
        if (!_isDirty)
        {
            return;
        }

        try
        {
            foreach (var (sectionName, editor) in _editors)
            {
                var sectionText = editor.Text.Trim();
                JsonNode? parsedNode;
                try
                {
                    parsedNode = JsonNode.Parse(sectionText);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        $"Invalid JSON in tab '{sectionName}': {ex.Message}");
                }

                _rootObject[sectionName] = parsedNode;
            }

            var serialized = _rootObject.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var config = JsonSerializer.Deserialize<AppConfig>(serialized, AppConfig.SerializerOptions)
                ?? throw new InvalidOperationException("Failed to parse configuration.");
            config.Validate();

            File.WriteAllText(_configPath, serialized + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    private void SetDirty(bool value)
    {
        _isDirty = value;
        _saveButton.Enabled = _isDirty;
    }
}
