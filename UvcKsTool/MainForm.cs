using System.Drawing;

namespace UvcKsTool;

internal sealed class MainForm : Form
{
    private const int ThermalPreviewWidth = 256;
    private const int ThermalPreviewHeight = 192;
    private const int BottomInfoRowHeight = 210;
    private const int LiveMeasurementsTargetWidth = 650;
    private const int MinimumLogWidth = 260;
    private const int CommandSidebarWidth = 400;
    private const int CommonCommandButtonRowHeight = 74;

    private readonly KsCommandSender _sender = new();
    private readonly ComboBox _deviceComboBox = new();
    private readonly Button _refreshButton = new();
    private readonly Button _startMeasurementButton = new();
    private readonly Button _stopMeasurementButton = new();
    private readonly PictureBox _previewBox = new();
    private readonly TableLayoutPanel _bottomInfoLayout = new();
    private readonly Panel _previewViewportPanel = new();
    private readonly Panel _previewHostPanel = new();
    private readonly TextBox _logTextBox = new();
    private readonly ComboBox _paletteComboBox = new();
    private readonly TextBox _rawCommandTextBox = new();
    private readonly Label _previewLabel = new();
    private readonly Label _centerLabel = new();
    private readonly Label _maxLabel = new();
    private readonly Label _minLabel = new();
    private readonly Label _hoverLabel = new();
    private readonly Label _probeLabel = new();
    private readonly Label _frameInfoLabel = new();
    private readonly Label _versionLabel = new();
    private readonly NumericUpDown _fixEditor = CreateDecimalEditor(-50, 50, 0.1m, 2, 0);
    private readonly NumericUpDown _reflectedEditor = CreateDecimalEditor(-50, 500, 0.5m, 1, 20);
    private readonly NumericUpDown _ambientEditor = CreateDecimalEditor(-50, 500, 0.5m, 1, 20);
    private readonly NumericUpDown _humidityEditor = CreateDecimalEditor(0, 100, 0.5m, 1, 50);
    private readonly NumericUpDown _emissivityEditor = CreateDecimalEditor(0.10m, 1.00m, 0.01m, 2, 0.95m);
    private readonly NumericUpDown _distanceEditor = CreateDecimalEditor(0, 5000, 1m, 0, 1);
    private readonly NumericUpDown _shutterFixEditor = CreateDecimalEditor(-50, 50, 0.1m, 2, 0);
    private readonly ComboBox _rangeModeComboBox = new();
    private readonly ComboBox _cameraLensComboBox = new();

    private CameraPreviewController? _previewController;
    private RadiometricFrame? _latestFrame;
    private Point? _hoverPoint;
    private Point? _lockedProbePoint;
    private bool _measurementEditorsInitializedFromFrame;
    private bool _measurementSettingsTouched;
    private bool _suppressMeasurementSettingEvents;
    private bool _paletteEventsWired;
    private bool _suppressPaletteSelectionEvents;
    private int _selectedPaletteIndex;

    public MainForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        Text = "UVC KS Tool";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1220, 820);
        Size = new Size(1460, 940);

        InitializeLayout();

        Load += (_, _) => RefreshDevices(autoStartMeasurement: true);
        Shown += (_, _) =>
        {
            UpdatePreviewLayout();
            UpdateBottomInfoLayout();
        };
        FormClosed += (_, _) => _previewController?.Dispose();
    }

    private void InitializeLayout()
    {
        _deviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceComboBox.Width = 360;
        _deviceComboBox.DisplayMember = nameof(VideoDevice.DisplayText);

        _refreshButton.Text = "Refresh";
        _refreshButton.AutoSize = true;
        _refreshButton.Click += (_, _) => RefreshDevices(autoStartMeasurement: false);

        _startMeasurementButton.Text = "Start Measurement";
        _startMeasurementButton.AutoSize = true;
        _startMeasurementButton.Click += (_, _) => StartMeasurement();

        _stopMeasurementButton.Text = "Stop Measurement";
        _stopMeasurementButton.AutoSize = true;
        _stopMeasurementButton.Click += (_, _) => StopMeasurement();

        _previewLabel.Text = "Measurement stopped.";
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
        topBar.Controls.Add(_startMeasurementButton);
        topBar.Controls.Add(_stopMeasurementButton);
        topBar.Controls.Add(_previewLabel);

        _previewViewportPanel.Dock = DockStyle.Fill;
        _previewViewportPanel.BackColor = SystemColors.Control;
        _previewViewportPanel.Resize += (_, _) => UpdatePreviewLayout();

        _previewHostPanel.BackColor = SystemColors.Control;

        _previewBox.Dock = DockStyle.Fill;
        _previewBox.Size = new Size(ThermalPreviewWidth, ThermalPreviewHeight);
        _previewBox.BackColor = Color.FromArgb(20, 20, 20);
        _previewBox.BorderStyle = BorderStyle.FixedSingle;
        _previewBox.SizeMode = PictureBoxSizeMode.Zoom;
        _previewBox.MouseMove += PreviewBoxOnMouseMove;
        _previewBox.MouseLeave += (_, _) =>
        {
            _hoverPoint = null;
            UpdateReadouts();
        };
        _previewBox.MouseClick += PreviewBoxOnMouseClick;
        _previewHostPanel.Controls.Add(_previewBox);
        _previewViewportPanel.Controls.Add(_previewHostPanel);

        var previewGroup = new GroupBox
        {
            Text = "Measurement Preview",
            Dock = DockStyle.Fill,
            Padding = new Padding(12)
        };
        previewGroup.Controls.Add(_previewViewportPanel);

        var measurementSummaryGroup = new GroupBox
        {
            Text = "Live Measurements",
            Dock = DockStyle.Fill,
            Padding = new Padding(10)
        };
        measurementSummaryGroup.Controls.Add(BuildMeasurementSummaryContent());

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

        _bottomInfoLayout.Dock = DockStyle.Fill;
        _bottomInfoLayout.ColumnCount = 2;
        _bottomInfoLayout.RowCount = 1;
        _bottomInfoLayout.Margin = new Padding(0);
        _bottomInfoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LiveMeasurementsTargetWidth));
        _bottomInfoLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _bottomInfoLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _bottomInfoLayout.Resize += (_, _) => UpdateBottomInfoLayout();
        _bottomInfoLayout.Controls.Add(measurementSummaryGroup, 0, 0);
        _bottomInfoLayout.Controls.Add(logGroup, 1, 0);

        var leftPane = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        leftPane.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        leftPane.RowStyles.Add(new RowStyle(SizeType.Absolute, BottomInfoRowHeight));
        leftPane.Controls.Add(previewGroup, 0, 0);
        leftPane.Controls.Add(_bottomInfoLayout, 0, 1);

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
            commandStack.Width = Math.Max(360, commandScrollPanel.ClientSize.Width - 20);
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(12, 0, 12, 8)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, CommandSidebarWidth));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        content.Controls.Add(leftPane, 0, 0);
        content.Controls.Add(commandScrollPanel, 1, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.Controls.Add(topBar, 0, 0);
        root.Controls.Add(content, 0, 1);

        Controls.Add(root);
        UpdateReadouts();
        UpdateMeasurementButtons();
    }

    private void UpdatePreviewLayout()
    {
        if (_previewViewportPanel.IsDisposed || _previewHostPanel.IsDisposed || _previewBox.IsDisposed)
        {
            return;
        }

        var availableWidth = _previewViewportPanel.ClientSize.Width;
        var availableHeight = _previewViewportPanel.ClientSize.Height;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        var nativeSize = GetPreviewNativeSize();
        var scale = Math.Min(
            availableWidth / (float)nativeSize.Width,
            availableHeight / (float)nativeSize.Height);
        var targetWidth = Math.Max(1, (int)Math.Round(nativeSize.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(nativeSize.Height * scale));

        _previewHostPanel.Size = new Size(targetWidth, targetHeight);
        _previewHostPanel.Location = new Point(
            Math.Max(0, (availableWidth - targetWidth) / 2),
            Math.Max(0, (availableHeight - targetHeight) / 2));
    }

    private void UpdateBottomInfoLayout()
    {
        if (_bottomInfoLayout.IsDisposed || _bottomInfoLayout.ColumnStyles.Count < 2)
        {
            return;
        }

        var availableWidth = _bottomInfoLayout.ClientSize.Width;
        if (availableWidth <= 0)
        {
            return;
        }

        var liveMeasurementsWidth = Math.Clamp(
            availableWidth - MinimumLogWidth,
            0,
            LiveMeasurementsTargetWidth);

        _bottomInfoLayout.ColumnStyles[0].SizeType = SizeType.Absolute;
        _bottomInfoLayout.ColumnStyles[0].Width = liveMeasurementsWidth;
        _bottomInfoLayout.ColumnStyles[1].SizeType = SizeType.Percent;
        _bottomInfoLayout.ColumnStyles[1].Width = 100F;
    }

    private Size GetPreviewNativeSize()
    {
        if (_previewBox.Image is { Width: > 0, Height: > 0 } image)
        {
            return image.Size;
        }

        return new Size(ThermalPreviewWidth, ThermalPreviewHeight);
    }

    private Control BuildMeasurementSummaryContent()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        AddSummaryRow(table, 0, "Center", _centerLabel);
        AddSummaryRow(table, 1, "Max", _maxLabel);
        AddSummaryRow(table, 2, "Min", _minLabel);
        AddSummaryRow(table, 3, "Hover", _hoverLabel);
        AddSummaryRow(table, 4, "Probe", _probeLabel);
        AddSummaryRow(table, 5, "Frame", _frameInfoLabel);
        AddSummaryRow(table, 6, "Version", _versionLabel);
        return table;
    }

    private static void AddSummaryRow(TableLayoutPanel table, int rowIndex, string label, Label valueLabel)
    {
        valueLabel.AutoSize = true;
        valueLabel.Margin = new Padding(0, 6, 0, 0);
        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 6, 10, 0) }, 0, rowIndex);
        table.Controls.Add(valueLabel, 1, rowIndex);
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
        stack.Controls.Add(CreateSectionGroup("Palette", BuildPaletteContent()));
        stack.Controls.Add(CreateSectionGroup("Measurement", BuildMeasurementControlsContent()));
        stack.Controls.Add(CreateSectionGroup("Common Commands", BuildCommonCommandsContent()));
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

    private Control BuildPaletteContent()
    {
        if (_paletteComboBox.Items.Count == 0)
        {
            foreach (var option in PaletteOption.All)
            {
                _paletteComboBox.Items.Add(option);
            }
        }

        if (!_paletteEventsWired)
        {
            _paletteEventsWired = true;
            _paletteComboBox.SelectedIndexChanged += (_, _) =>
            {
                if (_suppressPaletteSelectionEvents)
                {
                    return;
                }

                if (_paletteComboBox.SelectedItem is PaletteOption option)
                {
                    ApplyPalette(option);
                }
            };
        }

        _paletteComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _paletteComboBox.Width = 300;

        if (_paletteComboBox.Items.Count > 0)
        {
            _suppressPaletteSelectionEvents = true;
            _paletteComboBox.SelectedIndex = Math.Clamp(_selectedPaletteIndex, 0, _paletteComboBox.Items.Count - 1);
            _suppressPaletteSelectionEvents = false;
        }

        var layout = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true
        };
        layout.Controls.Add(_paletteComboBox);
        return layout;
    }

    private Control BuildMeasurementControlsContent()
    {
        InitializeMeasurementSelectors();
        WireMeasurementSettingEvents();

        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        AddMeasurementEditor(grid, 0, "Fix", _fixEditor);
        AddMeasurementEditor(grid, 1, "Reflected", _reflectedEditor);
        AddMeasurementEditor(grid, 2, "Ambient", _ambientEditor);
        AddMeasurementEditor(grid, 3, "Humidity", _humidityEditor);
        AddMeasurementEditor(grid, 4, "Emissivity", _emissivityEditor);
        AddMeasurementEditor(grid, 5, "Distance", _distanceEditor);
        AddMeasurementEditor(grid, 6, "Shutter Fix", _shutterFixEditor);
        AddMeasurementEditor(grid, 7, "Range Mode", _rangeModeComboBox);
        AddMeasurementEditor(grid, 8, "Camera Lens", _cameraLensComboBox);

        var applyButton = new Button { Text = "Apply Settings", AutoSize = true };
        applyButton.Click += (_, _) => ApplyMeasurementSettings(forceLog: true);

        var refreshButton = new Button { Text = "Shutter / NUC Refresh", AutoSize = true };
        refreshButton.Click += (_, _) => RequestMeasurementRefresh();

        var exportButton = new Button { Text = "Export Debug", AutoSize = true };
        exportButton.Click += (_, _) => ExportDebugCapture();

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Margin = new Padding(0, 10, 0, 0)
        };
        actions.Controls.Add(applyButton);
        actions.Controls.Add(refreshButton);
        actions.Controls.Add(exportButton);

        var note = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 8, 0, 0),
            Text = "This mode consumes raw 256x196 transport frames, parses the 128-byte tail block, computes a managed temperature field, and renders preview from the decoded temperatures."
        };

        var outer = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        outer.Controls.Add(grid, 0, 0);
        outer.Controls.Add(actions, 0, 1);
        outer.Controls.Add(note, 0, 2);
        return outer;
    }

    private static void AddMeasurementEditor(TableLayoutPanel layout, int rowIndex, string label, Control editor)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 8, 10, 0) }, 0, rowIndex);
        editor.Margin = new Padding(0, 4, 0, 0);
        layout.Controls.Add(editor, 1, rowIndex);
    }

    private Control BuildCommonCommandsContent()
    {
        return BuildButtonGrid(
            ("Source Raw", "source-raw"),
            ("Source YUV", "source-yuv"),
            ("Wide Dyn On", "wide-dynamic-on"),
            ("Wide Dyn Off", "wide-dynamic-off"));
    }

    private Control BuildRawCommandContent()
    {
        _rawCommandTextBox.Width = 180;
        _rawCommandTextBox.Text = "0x8000";

        var sendButton = new Button { Text = "Send Raw", AutoSize = true };
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
            Text = "Measurement preview uses the raw 16-bit transport stream and a managed thermometry/search pipeline. CLI and raw command access are unchanged."
        };
    }

    private Control BuildButtonGrid(params (string Text, string CommandName)[] buttons)
    {
        var rowCount = Math.Max(1, (int)Math.Ceiling(buttons.Length / 2d));
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = rowCount,
            GrowStyle = TableLayoutPanelGrowStyle.FixedSize
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, CommonCommandButtonRowHeight));
        }

        for (var i = 0; i < buttons.Length; i++)
        {
            var button = new Button
            {
                Text = buttons[i].Text,
                AutoSize = false,
                Dock = DockStyle.Fill,
                Margin = new Padding(4)
            };

            var commandName = buttons[i].CommandName;
            button.Click += (_, _) => SendNamedCommand(commandName);
            layout.Controls.Add(button, i % 2, i / 2);
        }

        return layout;
    }

    private static NumericUpDown CreateDecimalEditor(decimal minimum, decimal maximum, decimal increment, int decimalPlaces, decimal value)
    {
        return new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            Increment = increment,
            DecimalPlaces = decimalPlaces,
            Value = value,
            Width = 120
        };
    }

    private void InitializeMeasurementSelectors()
    {
        if (_rangeModeComboBox.Items.Count == 0)
        {
            _rangeModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _rangeModeComboBox.Width = 220;
            _rangeModeComboBox.Items.Add(new RangeModeOption(CameraThermometryState.NormalRangeMode, "0x78 Normal (-20C to 120C)"));
            _rangeModeComboBox.Items.Add(new RangeModeOption(CameraThermometryState.WideRangeMode, "0x190 Wide (120C to 450C)"));
            _rangeModeComboBox.SelectedIndex = 0;
        }

        if (_cameraLensComboBox.Items.Count == 0)
        {
            _cameraLensComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _cameraLensComboBox.Width = 220;
            _cameraLensComboBox.Items.Add(new CameraLensOption(CameraThermometryState.DefaultCameraLens, "0x44 Default lens path"));
            _cameraLensComboBox.Items.Add(new CameraLensOption(CameraThermometryState.AlternateCameraLens, "0x82 Alternate lens path"));
            _cameraLensComboBox.SelectedIndex = 0;
        }
    }

    private void WireMeasurementSettingEvents()
    {
        _fixEditor.ValueChanged -= OnMeasurementSettingEdited;
        _reflectedEditor.ValueChanged -= OnMeasurementSettingEdited;
        _ambientEditor.ValueChanged -= OnMeasurementSettingEdited;
        _humidityEditor.ValueChanged -= OnMeasurementSettingEdited;
        _emissivityEditor.ValueChanged -= OnMeasurementSettingEdited;
        _distanceEditor.ValueChanged -= OnMeasurementSettingEdited;
        _shutterFixEditor.ValueChanged -= OnMeasurementSettingEdited;
        _rangeModeComboBox.SelectedIndexChanged -= OnMeasurementSettingEdited;
        _cameraLensComboBox.SelectedIndexChanged -= OnMeasurementSettingEdited;

        _fixEditor.ValueChanged += OnMeasurementSettingEdited;
        _reflectedEditor.ValueChanged += OnMeasurementSettingEdited;
        _ambientEditor.ValueChanged += OnMeasurementSettingEdited;
        _humidityEditor.ValueChanged += OnMeasurementSettingEdited;
        _emissivityEditor.ValueChanged += OnMeasurementSettingEdited;
        _distanceEditor.ValueChanged += OnMeasurementSettingEdited;
        _shutterFixEditor.ValueChanged += OnMeasurementSettingEdited;
        _rangeModeComboBox.SelectedIndexChanged += OnMeasurementSettingEdited;
        _cameraLensComboBox.SelectedIndexChanged += OnMeasurementSettingEdited;
    }

    private void OnMeasurementSettingEdited(object? sender, EventArgs e)
    {
        if (_suppressMeasurementSettingEvents)
        {
            return;
        }

        _measurementSettingsTouched = true;
    }

    private void RefreshDevices(bool autoStartMeasurement)
    {
        try
        {
            var previousSelection = (_deviceComboBox.SelectedItem as VideoDevice)?.FriendlyName;
            var devices = VideoDeviceFinder.ListVideoInputDevices().ToList();

            _deviceComboBox.DataSource = null;
            _deviceComboBox.DataSource = devices;
            _deviceComboBox.DisplayMember = nameof(VideoDevice.DisplayText);

            if (devices.Count == 0)
            {
                Log("No DirectShow video input devices found.");
                UpdateMeasurementButtons();
                return;
            }

            var preferredIndex = FindPreferredMeasurementDeviceIndex(devices);
            var selectedIndex = ResolveSelectionIndex(devices, previousSelection, preferredIndex, autoStartMeasurement);
            _deviceComboBox.SelectedIndex = selectedIndex;
            Log($"Found {devices.Count} video device(s).");

            if (autoStartMeasurement && preferredIndex >= 0 && !(_previewController?.IsRunning ?? false))
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (!(_previewController?.IsRunning ?? false))
                    {
                        StartMeasurement();
                    }
                }));
            }

            UpdateMeasurementButtons();
        }
        catch (Exception ex)
        {
            Log($"Refresh failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static int ResolveSelectionIndex(IReadOnlyList<VideoDevice> devices, string? previousSelection, int preferredIndex, bool autoStartMeasurement)
    {
        if (autoStartMeasurement && preferredIndex >= 0)
        {
            return preferredIndex;
        }

        if (previousSelection is not null)
        {
            for (var i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i].FriendlyName, previousSelection, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        return 0;
    }

    private static int FindPreferredMeasurementDeviceIndex(IReadOnlyList<VideoDevice> devices)
    {
        for (var i = 0; i < devices.Count; i++)
        {
            if (IsT2SDevice(devices[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsT2SDevice(VideoDevice device)
    {
        return device.FriendlyName.Contains("T2S+", StringComparison.OrdinalIgnoreCase)
            || device.FriendlyName.Contains("T2S Plus", StringComparison.OrdinalIgnoreCase)
            || device.FriendlyName.Contains("Xinfrared T2S", StringComparison.OrdinalIgnoreCase);
    }

    private void StartMeasurement()
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
            _previewController.FrameDecoded -= OnMeasurementFrameDecoded;
            _previewController.StatusChanged += OnPreviewStatusChanged;
            _previewController.FrameDecoded += OnMeasurementFrameDecoded;

            ApplyMeasurementSettings(forceLog: false);
            _previewController.UpdatePalette(_selectedPaletteIndex);
            SendNamedCommand(device, "source-raw", null);

            _previewController.Start(device.Index);
            _previewLabel.Text = $"Measurement starting: {device.DisplayText}";
            UpdateMeasurementButtons();
        }
        catch (Exception ex)
        {
            Log($"Measurement start failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Measurement failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopMeasurement()
    {
        _previewController?.Stop();
        _previewLabel.Text = "Measurement stopped.";
        UpdateMeasurementButtons();
    }

    private void ApplyMeasurementSettings(bool forceLog)
    {
        if (_previewController is not null)
        {
            if (_measurementSettingsTouched || _measurementEditorsInitializedFromFrame)
            {
                _previewController.UpdateThermometryParams(ReadMeasurementParams());
            }

            if (_rangeModeComboBox.SelectedItem is RangeModeOption rangeMode)
            {
                _previewController.UpdateRangeMode(rangeMode.Value);
            }

            if (_cameraLensComboBox.SelectedItem is CameraLensOption lensOption)
            {
                _previewController.UpdateCameraLens(lensOption.Value);
            }

            _previewController.UpdateShutterFix((float)_shutterFixEditor.Value);
        }

        if (forceLog)
        {
            var parameters = ReadMeasurementParams();
            var rangeValue = (_rangeModeComboBox.SelectedItem as RangeModeOption)?.Value ?? CameraThermometryState.NormalRangeMode;
            var lensValue = (_cameraLensComboBox.SelectedItem as CameraLensOption)?.Value ?? CameraThermometryState.DefaultCameraLens;
            Log(
                $"Measurement settings staged: fix={parameters.Fix:F2}, refl={parameters.ReflectedTemp:F1}, air={parameters.AmbientTemp:F1}, humi={parameters.Humidity:F1}, emiss={parameters.Emissivity:F2}, dist={parameters.Distance}, shutterFix={(float)_shutterFixEditor.Value:F2}, range=0x{rangeValue:X}, lens=0x{lensValue:X}");
        }
    }

    private void RequestMeasurementRefresh()
    {
        var device = GetSelectedDevice();
        if (device is null)
        {
            return;
        }

        SendNamedCommand(device, "nuc", null);
        _previewController?.RequestShutterRefresh();
        Log("Measurement refresh requested.");
    }

    private void ExportDebugCapture()
    {
        try
        {
            if (_previewController is null)
            {
                throw new InvalidOperationException("Start measurement first so there is a decoded frame to export.");
            }

            var exportRoot = Path.Combine(AppContext.BaseDirectory, "exports");
            var outputDirectory = _previewController.ExportLatestCapture(exportRoot);
            Log($"Exported debug capture to {outputDirectory}");
        }
        catch (Exception ex)
        {
            Log($"Export failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnPreviewStatusChanged(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke((MethodInvoker)(() => OnPreviewStatusChanged(message)));
            return;
        }

        Log(message);
        if (message.StartsWith("Measurement started on camera index", StringComparison.OrdinalIgnoreCase))
        {
            if (_deviceComboBox.SelectedItem is VideoDevice device)
            {
                _previewLabel.Text = $"Measurement running: {device.DisplayText}";
            }
            else
            {
                _previewLabel.Text = "Measurement running.";
            }
        }

        if (!(_previewController?.IsRunning ?? false))
        {
            _previewLabel.Text = "Measurement stopped.";
        }

        UpdateMeasurementButtons();
    }

    private void OnMeasurementFrameDecoded(RadiometricFrame frame)
    {
        if (InvokeRequired)
        {
            BeginInvoke((MethodInvoker)(() => OnMeasurementFrameDecoded(frame)));
            return;
        }

        _latestFrame = frame;
        if (!_measurementEditorsInitializedFromFrame)
        {
            SynchronizeMeasurementEditors(frame);
        }

        UpdatePreviewLayout();
        UpdateReadouts();
    }

    private void SynchronizeMeasurementEditors(RadiometricFrame frame)
    {
        _suppressMeasurementSettingEvents = true;
        try
        {
            _fixEditor.Value = ClampEditorValue(_fixEditor, (decimal)frame.ActiveParameters.Fix);
            _reflectedEditor.Value = ClampEditorValue(_reflectedEditor, (decimal)frame.ActiveParameters.ReflectedTemp);
            _ambientEditor.Value = ClampEditorValue(_ambientEditor, (decimal)frame.ActiveParameters.AmbientTemp);
            _humidityEditor.Value = ClampEditorValue(_humidityEditor, (decimal)frame.ActiveParameters.Humidity);
            _emissivityEditor.Value = ClampEditorValue(_emissivityEditor, (decimal)frame.ActiveParameters.Emissivity);
            _distanceEditor.Value = ClampEditorValue(_distanceEditor, frame.ActiveParameters.Distance);
            _shutterFixEditor.Value = ClampEditorValue(_shutterFixEditor, (decimal)frame.State.ShutterFix);
            SelectRangeMode(frame.State.RangeMode);
            SelectCameraLens(frame.State.CameraLens);
        }
        finally
        {
            _suppressMeasurementSettingEvents = false;
        }

        _measurementEditorsInitializedFromFrame = true;
        _measurementSettingsTouched = false;
        Log("Measurement editors initialized from embedded frame parameters.");
    }

    private void SelectRangeMode(int value)
    {
        for (var i = 0; i < _rangeModeComboBox.Items.Count; i++)
        {
            if (_rangeModeComboBox.Items[i] is RangeModeOption option && option.Value == value)
            {
                _rangeModeComboBox.SelectedIndex = i;
                return;
            }
        }
    }

    private void SelectCameraLens(int value)
    {
        for (var i = 0; i < _cameraLensComboBox.Items.Count; i++)
        {
            if (_cameraLensComboBox.Items[i] is CameraLensOption option && option.Value == value)
            {
                _cameraLensComboBox.SelectedIndex = i;
                return;
            }
        }
    }

    private static decimal ClampEditorValue(NumericUpDown editor, decimal value)
    {
        return Math.Min(editor.Maximum, Math.Max(editor.Minimum, value));
    }

    private void UpdateMeasurementButtons()
    {
        var running = _previewController?.IsRunning ?? false;
        _startMeasurementButton.Enabled = !running && _deviceComboBox.Items.Count > 0;
        _stopMeasurementButton.Enabled = running;
    }

    private void UpdateReadouts()
    {
        if (_latestFrame is null)
        {
            _centerLabel.Text = "-";
            _maxLabel.Text = "-";
            _minLabel.Text = "-";
            _hoverLabel.Text = "-";
            _probeLabel.Text = "-";
            _frameInfoLabel.Text = "Waiting for a raw frame.";
            _versionLabel.Text = "-";
            return;
        }

        _centerLabel.Text = $"{_latestFrame.CenterTemp:F1} C";
        _maxLabel.Text = $"{_latestFrame.MaxTemp:F1} C @ ({_latestFrame.MaxPoint.X}, {_latestFrame.MaxPoint.Y})";
        _minLabel.Text = $"{_latestFrame.MinTemp:F1} C @ ({_latestFrame.MinPoint.X}, {_latestFrame.MinPoint.Y})";
        _hoverLabel.Text = FormatPointTemperature(_hoverPoint, "Move over the image");
        _probeLabel.Text = FormatPointTemperature(_lockedProbePoint, "Click the image to lock a probe");
        _frameInfoLabel.Text =
            $"{_latestFrame.ThermalWidth}x{_latestFrame.ThermalHeight}, header[7/8]={_latestFrame.Header[7]:F0}/{_latestFrame.Header[8]:F0}, range=0x{_latestFrame.State.RangeMode:X}, lens=0x{_latestFrame.State.CameraLens:X}, dirty={_latestFrame.State.Dirty}";
        _versionLabel.Text = string.IsNullOrWhiteSpace(_latestFrame.TailMetadata.ProductVersion)
            ? "<unavailable>"
            : _latestFrame.TailMetadata.ProductVersion;
    }

    private string FormatPointTemperature(Point? point, string fallback)
    {
        if (_latestFrame is null || point is null)
        {
            return fallback;
        }

        if (!TryGetTemperature(point.Value, out var temperature))
        {
            return fallback;
        }

        return $"{temperature:F1} C @ ({point.Value.X}, {point.Value.Y})";
    }

    private bool TryGetTemperature(Point point, out float temperature)
    {
        temperature = 0f;
        if (_latestFrame is null)
        {
            return false;
        }

        if (point.X < 0 || point.X >= _latestFrame.ThermalWidth || point.Y < 0 || point.Y >= _latestFrame.ThermalHeight)
        {
            return false;
        }

        var index = (point.Y * _latestFrame.ThermalWidth) + point.X;
        temperature = _latestFrame.Temperatures[index];
        return true;
    }

    private void PreviewBoxOnMouseMove(object? sender, MouseEventArgs e)
    {
        _hoverPoint = TryMapPreviewPoint(e.Location);
        UpdateReadouts();
    }

    private void PreviewBoxOnMouseClick(object? sender, MouseEventArgs e)
    {
        var mappedPoint = TryMapPreviewPoint(e.Location);
        if (mappedPoint is null)
        {
            return;
        }

        _lockedProbePoint = mappedPoint;
        UpdateReadouts();
    }

    private Point? TryMapPreviewPoint(Point clientPoint)
    {
        if (_latestFrame is null || _previewBox.Image is null)
        {
            return null;
        }

        var imageRect = GetImageDisplayRectangle(_previewBox);
        if (imageRect.Width <= 0 || imageRect.Height <= 0 || !imageRect.Contains(clientPoint))
        {
            return null;
        }

        var normalizedX = (clientPoint.X - imageRect.X) / (float)imageRect.Width;
        var normalizedY = (clientPoint.Y - imageRect.Y) / (float)imageRect.Height;
        var x = Math.Clamp((int)(normalizedX * _latestFrame.ThermalWidth), 0, _latestFrame.ThermalWidth - 1);
        var y = Math.Clamp((int)(normalizedY * _latestFrame.ThermalHeight), 0, _latestFrame.ThermalHeight - 1);
        return new Point(x, y);
    }

    private static Rectangle GetImageDisplayRectangle(PictureBox pictureBox)
    {
        if (pictureBox.Image is null || pictureBox.ClientSize.Width <= 0 || pictureBox.ClientSize.Height <= 0)
        {
            return Rectangle.Empty;
        }

        var image = pictureBox.Image;
        var scale = Math.Min(
            pictureBox.ClientSize.Width / (float)image.Width,
            pictureBox.ClientSize.Height / (float)image.Height);
        var drawWidth = (int)Math.Round(image.Width * scale);
        var drawHeight = (int)Math.Round(image.Height * scale);
        var x = (pictureBox.ClientSize.Width - drawWidth) / 2;
        var y = (pictureBox.ClientSize.Height - drawHeight) / 2;
        return new Rectangle(x, y, drawWidth, drawHeight);
    }

    private ThermometryParams ReadMeasurementParams()
    {
        return new ThermometryParams(
            (float)_fixEditor.Value,
            (float)_reflectedEditor.Value,
            (float)_ambientEditor.Value,
            (float)_humidityEditor.Value,
            (float)_emissivityEditor.Value,
            (int)_distanceEditor.Value);
    }

    private void ApplyPalette(PaletteOption option)
    {
        _selectedPaletteIndex = option.Index;
        _previewController?.UpdatePalette(option.Index);

        var device = _deviceComboBox.SelectedItem as VideoDevice;
        var deviceApplied = device is not null && SendNamedCommand(device, "palette", option.Index);
        if (device is null)
        {
            Log($"Preview palette set to {option.Name} for local measurement rendering.");
            return;
        }

        if (deviceApplied)
        {
            Log($"Applied palette {option.Name} to local preview and device.");
            return;
        }

        Log($"Applied palette {option.Name} to local preview, but device command failed.");
    }

    private bool SendNamedCommand(string commandName, int? value = null)
    {
        var device = GetSelectedDevice();
        return device is not null && SendNamedCommand(device, commandName, value);
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
            Log($"[{device.DisplayText}] {label} -> 0x{value:X4} ({pattern}) => 0x{unchecked((uint)hr):X8} {HResultFormatter.Format(hr)}{bytesSuffix}");
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
            _logTextBox.BeginInvoke((MethodInvoker)(() => Log(message)));
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

    private sealed record RangeModeOption(int Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CameraLensOption(int Value, string Label)
    {
        public override string ToString() => Label;
    }
}
