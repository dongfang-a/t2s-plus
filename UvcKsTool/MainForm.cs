using System.Drawing;

namespace UvcKsTool;

internal sealed class MainForm : Form
{
    private static readonly string[] PointXCommands =
    [
        "temp-point1-x",
        "temp-point2-x",
        "temp-point3-x"
    ];

    private static readonly string[] PointYCommands =
    [
        "temp-point1-y",
        "temp-point2-y",
        "temp-point3-y"
    ];

    private readonly KsCommandSender _sender = new();
    private readonly ComboBox _deviceComboBox = new();
    private readonly Button _refreshButton = new();
    private readonly Button _startPreviewButton = new();
    private readonly Button _stopPreviewButton = new();
    private readonly PictureBox _previewBox = new();
    private readonly TextBox _logTextBox = new();
    private readonly ComboBox _paletteComboBox = new();
    private readonly TextBox _rawCommandTextBox = new();
    private readonly Label _previewLabel = new();
    private readonly NumericUpDown[] _pointXEditors = new NumericUpDown[3];
    private readonly NumericUpDown[] _pointYEditors = new NumericUpDown[3];

    private CameraPreviewController? _previewController;

    public MainForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        Text = "UVC KS Tool";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1260, 760);
        Size = new Size(1440, 900);

        InitializeLayout();

        Load += (_, _) => RefreshDevices();
        FormClosed += (_, _) => _previewController?.Dispose();
    }

    private void InitializeLayout()
    {
        _deviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceComboBox.Width = 360;
        _deviceComboBox.DisplayMember = nameof(VideoDevice.DisplayText);

        _refreshButton.Text = "Refresh";
        _refreshButton.AutoSize = true;
        _refreshButton.Click += (_, _) => RefreshDevices();

        _startPreviewButton.Text = "Start Preview";
        _startPreviewButton.AutoSize = true;
        _startPreviewButton.Click += (_, _) => StartPreview();

        _stopPreviewButton.Text = "Stop Preview";
        _stopPreviewButton.AutoSize = true;
        _stopPreviewButton.Click += (_, _) => StopPreview();

        _previewLabel.Text = "Preview stopped.";
        _previewLabel.AutoSize = true;
        _previewLabel.Margin = new Padding(8, 8, 3, 0);

        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(12, 10, 12, 6)
        };
        topBar.Controls.Add(new Label { Text = "Device", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        topBar.Controls.Add(_deviceComboBox);
        topBar.Controls.Add(_refreshButton);
        topBar.Controls.Add(_startPreviewButton);
        topBar.Controls.Add(_stopPreviewButton);
        topBar.Controls.Add(_previewLabel);

        _previewBox.Dock = DockStyle.Fill;
        _previewBox.BackColor = Color.FromArgb(20, 20, 20);
        _previewBox.BorderStyle = BorderStyle.FixedSingle;
        _previewBox.SizeMode = PictureBoxSizeMode.Zoom;

        var previewGroup = new GroupBox
        {
            Text = "Preview",
            Dock = DockStyle.Fill,
            Padding = new Padding(12)
        };
        previewGroup.Controls.Add(_previewBox);

        var commandScrollPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0, 0, 12, 0)
        };

        var commandStack = BuildCommandStack();
        commandScrollPanel.Controls.Add(commandStack);
        commandScrollPanel.Resize += (_, _) =>
        {
            commandStack.Width = Math.Max(380, commandScrollPanel.ClientSize.Width - 20);
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12, 0, 12, 8)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430F));
        content.Controls.Add(previewGroup, 0, 0);
        content.Controls.Add(commandScrollPanel, 1, 0);

        _logTextBox.Dock = DockStyle.Fill;
        _logTextBox.Multiline = true;
        _logTextBox.ReadOnly = true;
        _logTextBox.ScrollBars = ScrollBars.Vertical;
        _logTextBox.BackColor = Color.FromArgb(248, 246, 239);
        _logTextBox.Font = new Font("Consolas", 10F, FontStyle.Regular, GraphicsUnit.Point);

        var logGroup = new GroupBox
        {
            Text = "Log",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };
        logGroup.Controls.Add(_logTextBox);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190F));
        root.Controls.Add(topBar, 0, 0);
        root.Controls.Add(content, 0, 1);
        root.Controls.Add(logGroup, 0, 2);

        Controls.Add(root);
    }

    private TableLayoutPanel BuildCommandStack()
    {
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        stack.Controls.Add(CreateSectionGroup("Common Commands", BuildCommonCommandsContent()));
        stack.Controls.Add(CreateSectionGroup("Legacy Follow-up", BuildLegacyFollowUpContent()));
        stack.Controls.Add(CreateSectionGroup("Palette", BuildPaletteContent()));
        stack.Controls.Add(CreateSectionGroup("Temperature Points", BuildTemperaturePointsContent()));
        stack.Controls.Add(CreateSectionGroup("Raw Command", BuildRawCommandContent()));
        stack.Controls.Add(CreateSectionGroup("Notes", BuildNotesContent()));

        return stack;
    }

    private static GroupBox CreateSectionGroup(string title, Control content)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 10)
        };
        content.Dock = DockStyle.Top;
        group.Controls.Add(content);
        return group;
    }

    private Control BuildCommonCommandsContent()
    {
        return BuildButtonGrid(
            ("Startup (0xA120)", "startup-newdemo"),
            ("NUC / Shutter", "nuc"),
            ("Source Raw", "source-raw"),
            ("Source YUV", "source-yuv"),
            ("K-Table", "k-table"),
            ("Wide Dyn On", "wide-dynamic-on"),
            ("Wide Dyn Off", "wide-dynamic-off"));
    }

    private Control BuildLegacyFollowUpContent()
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var commitButton = new Button
        {
            Text = "Commit Temp Points",
            AutoSize = true
        };
        commitButton.Click += (_, _) => CommitTemperaturePoints();
        layout.Controls.Add(commitButton, 0, 0);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 8, 3, 0),
            Text = "Runs the observed legacy follow-up sequence 0x8027 -> 0x80FE. Standalone 0x8027 is hidden because some devices reject it as an incomplete request."
        }, 0, 1);

        return layout;
    }

    private Control BuildPaletteContent()
    {
        foreach (var option in PaletteOption.All)
        {
            _paletteComboBox.Items.Add(option);
        }

        if (_paletteComboBox.Items.Count > 0)
        {
            _paletteComboBox.SelectedIndex = 0;
        }

        _paletteComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _paletteComboBox.Width = 220;

        var applyButton = new Button
        {
            Text = "Apply Palette",
            AutoSize = true
        };
        applyButton.Click += (_, _) =>
        {
            if (_paletteComboBox.SelectedItem is PaletteOption option)
            {
                SendNamedCommand("palette", option.Index);
            }
        };

        var layout = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true
        };
        layout.Controls.Add(_paletteComboBox);
        layout.Controls.Add(applyButton);
        return layout;
    }

    private Control BuildTemperaturePointsContent()
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "Point", AutoSize = true, Margin = new Padding(3, 6, 10, 0) }, 0, 0);
        layout.Controls.Add(new Label { Text = "X", AutoSize = true, Margin = new Padding(3, 6, 10, 0) }, 1, 0);
        layout.Controls.Add(new Label { Text = "Y", AutoSize = true, Margin = new Padding(3, 6, 10, 0) }, 2, 0);
        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 3, 0);

        for (var i = 0; i < 3; i++)
        {
            _pointXEditors[i] = CreateCoordinateEditor();
            _pointYEditors[i] = CreateCoordinateEditor();

            var pointIndex = i;
            var sendPointButton = new Button
            {
                Text = $"Send P{i + 1}",
                AutoSize = true
            };
            sendPointButton.Click += (_, _) => SendTemperaturePoint(pointIndex);

            layout.Controls.Add(new Label { Text = $"P{i + 1}", AutoSize = true, Margin = new Padding(3, 8, 10, 0) }, 0, i + 1);
            layout.Controls.Add(_pointXEditors[i], 1, i + 1);
            layout.Controls.Add(_pointYEditors[i], 2, i + 1);
            layout.Controls.Add(sendPointButton, 3, i + 1);
        }

        var sendAllButton = new Button
        {
            Text = "Send All Points",
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0)
        };
        sendAllButton.Click += (_, _) => SendAllTemperaturePoints();

        var outer = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        outer.Controls.Add(layout, 0, 0);
        outer.Controls.Add(sendAllButton, 0, 1);

        return outer;
    }

    private Control BuildRawCommandContent()
    {
        _rawCommandTextBox.Width = 180;
        _rawCommandTextBox.Text = "0x8000";

        var sendButton = new Button
        {
            Text = "Send Raw",
            AutoSize = true
        };
        sendButton.Click += (_, _) => SendRawCommand();

        var hintLabel = new Label
        {
            Text = "Hex or decimal, for example 0x8081.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(3, 8, 3, 0)
        };

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true
        };
        row.Controls.Add(_rawCommandTextBox);
        row.Controls.Add(sendButton);

        layout.Controls.Add(row, 0, 0);
        layout.Controls.Add(hintLabel, 0, 1);
        return layout;
    }

    private static Control BuildNotesContent()
    {
        return new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "Preview uses OpenCV via DirectShow. All command buttons send IKsControl requests through PROPSETID_VIDCAP_CAMERACONTROL / ZOOM / SET."
        };
    }

    private Control BuildButtonGrid(params (string Text, string CommandName)[] buttons)
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

        for (var i = 0; i < buttons.Length; i++)
        {
            var button = new Button
            {
                Text = buttons[i].Text,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(4)
            };

            var commandName = buttons[i].CommandName;
            button.Click += (_, _) => SendNamedCommand(commandName);

            layout.Controls.Add(button, i % 2, i / 2);
        }

        return layout;
    }

    private static NumericUpDown CreateCoordinateEditor()
    {
        return new NumericUpDown
        {
            Minimum = 0,
            Maximum = 255,
            Width = 70
        };
    }

    private void RefreshDevices()
    {
        try
        {
            var previousSelection = (_deviceComboBox.SelectedItem as VideoDevice)?.FriendlyName;
            var devices = VideoDeviceFinder.ListVideoInputDevices().ToList();

            _deviceComboBox.DataSource = null;
            _deviceComboBox.DataSource = devices;
            _deviceComboBox.DisplayMember = nameof(VideoDevice.DisplayText);

            if (devices.Count > 0)
            {
                var selectedIndex = previousSelection is null
                    ? 0
                    : Math.Max(0, devices.FindIndex(device =>
                        string.Equals(device.FriendlyName, previousSelection, StringComparison.OrdinalIgnoreCase)));

                _deviceComboBox.SelectedIndex = selectedIndex;
                Log($"Found {devices.Count} video device(s).");
            }
            else
            {
                Log("No DirectShow video input devices found.");
            }

            UpdatePreviewButtons();
        }
        catch (Exception ex)
        {
            Log($"Refresh failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartPreview()
    {
        var device = GetSelectedDevice();
        if (device is null)
        {
            return;
        }

        try
        {
            _previewController ??= new CameraPreviewController(_previewBox);
            _previewController.StatusChanged -= OnPreviewStatusChanged;
            _previewController.StatusChanged += OnPreviewStatusChanged;
            _previewController.Start(device.Index);
            _previewLabel.Text = $"Preview running: {device.DisplayText}";
            UpdatePreviewButtons();
        }
        catch (Exception ex)
        {
            Log($"Preview start failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Preview failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopPreview()
    {
        _previewController?.Stop();
        _previewLabel.Text = "Preview stopped.";
        UpdatePreviewButtons();
    }

    private void OnPreviewStatusChanged(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnPreviewStatusChanged(message));
            return;
        }

        Log(message);

        if (!(_previewController?.IsRunning ?? false))
        {
            _previewLabel.Text = "Preview stopped.";
        }
    }

    private void UpdatePreviewButtons()
    {
        var running = _previewController?.IsRunning ?? false;
        _startPreviewButton.Enabled = !running && _deviceComboBox.Items.Count > 0;
        _stopPreviewButton.Enabled = running;
    }

    private void SendTemperaturePoint(int pointIndex)
    {
        if (pointIndex < 0 || pointIndex >= 3)
        {
            return;
        }

        var device = GetSelectedDevice();
        if (device is null)
        {
            return;
        }

        var x = (int)_pointXEditors[pointIndex].Value;
        var y = (int)_pointYEditors[pointIndex].Value;

        SendTemperaturePoint(device, pointIndex, x, y, includeLegacyCommit: true);
    }

    private void SendAllTemperaturePoints()
    {
        var device = GetSelectedDevice();
        if (device is null)
        {
            return;
        }

        for (var i = 0; i < 3; i++)
        {
            var x = (int)_pointXEditors[i].Value;
            var y = (int)_pointYEditors[i].Value;
            if (!SendTemperaturePoint(device, i, x, y, includeLegacyCommit: false))
            {
                return;
            }
        }

        SendTemperatureCommitSequence(device);
    }

    private bool SendTemperaturePoint(VideoDevice device, int pointIndex, int x, int y, bool includeLegacyCommit)
    {
        if (!SendNamedCommand(device, PointXCommands[pointIndex], x))
        {
            return false;
        }

        if (!SendNamedCommand(device, PointYCommands[pointIndex], y))
        {
            return false;
        }

        return !includeLegacyCommit || SendTemperatureCommitSequence(device);
    }

    private void CommitTemperaturePoints()
    {
        var device = GetSelectedDevice();
        if (device is null)
        {
            return;
        }

        SendTemperatureCommitSequence(device);
    }

    private bool SendTemperatureCommitSequence(VideoDevice device)
    {
        Log($"[{device.DisplayText}] commit-temp-points => 0x8027 then 0x80FE");
        return SendNamedCommand(device, "legacy-39", null)
            && SendNamedCommand(device, "legacy-apply", null);
    }

    private bool SendNamedCommand(string commandName, int? value = null)
    {
        var device = GetSelectedDevice();
        if (device is null)
        {
            return false;
        }

        return SendNamedCommand(device, commandName, value);
    }

    private bool SendNamedCommand(VideoDevice device, string commandName, int? value)
    {
        var command = LegacyCommandCatalog.TryGetNamedCommand(commandName);
        if (command is null)
        {
            MessageBox.Show(this, $"Unknown command: {commandName}", "Command error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        ushort resolvedValue;
        try
        {
            resolvedValue = command.Resolve(value);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Command error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        return SendCommand(device, command.Name, resolvedValue, command.Pattern);
    }

    private void SendRawCommand()
    {
        var device = GetSelectedDevice();
        if (device is null)
        {
            return;
        }

        try
        {
            var value = ParseRawValue(_rawCommandTextBox.Text);
            SendCommand(device, "raw", value, $"0x{value:X4}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Raw command error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool SendCommand(VideoDevice device, string label, ushort value, string pattern)
    {
        try
        {
            var result = _sender.SendAbsoluteZoomCommand(device.FriendlyName, value);
            var hr = result.HResult;
            var bytesSuffix = result.BytesReturned > 0 ? $", bytes={result.BytesReturned}" : string.Empty;
            Log(
                $"[{device.DisplayText}] {label} -> 0x{value:X4} ({pattern}) => 0x{unchecked((uint)hr):X8} {HResultFormatter.Format(hr)}{bytesSuffix}");
            return hr == 0;
        }
        catch (Exception ex)
        {
            Log($"Command send failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Command send failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private VideoDevice? GetSelectedDevice()
    {
        if (_deviceComboBox.SelectedItem is VideoDevice device)
        {
            return device;
        }

        MessageBox.Show(this, "Select a device first.", "No device selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return null;
    }

    private static ushort ParseRawValue(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Raw command cannot be empty.");
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToUInt16(trimmed[2..], 16);
        }

        return Convert.ToUInt16(trimmed, 10);
    }

    private void Log(string message)
    {
        if (_logTextBox.IsDisposed)
        {
            return;
        }

        if (_logTextBox.InvokeRequired)
        {
            _logTextBox.BeginInvoke(() => Log(message));
            return;
        }

        _logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private sealed record PaletteOption(int Index, string Name)
    {
        public static readonly IReadOnlyList<PaletteOption> All =
        [
            new(0, "0 White Hot"),
            new(1, "1 Black Hot"),
            new(2, "2 Blue / Red / Yellow"),
            new(3, "3 Purple / Red / Yellow"),
            new(4, "4 Blue / Green / Red"),
            new(5, "5 Rainbow 1"),
            new(6, "6 Rainbow 2"),
            new(7, "7 Black / Red"),
            new(8, "8 Dark Green / Red"),
            new(9, "9 Blue / Green / Red / Pink"),
            new(10, "10 Mixed"),
            new(11, "11 Red Head")
        ];

        public override string ToString() => Name;
    }
}
