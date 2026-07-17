// SCO LIDEX - WinForms Front End
// Copyright (C) Scott Brunner, Beast of Burden
//
// This file contains the desktop interface, run/scan orchestration, live
// status counters, logging, help/contact viewers, and post-processing controls.
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.

using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ORterr;

internal sealed partial class TopoForm : Form
{
    private static readonly Color AppBackColor = Color.FromArgb(242, 241, 238);
    private static readonly Color HeaderBackColor = Color.FromArgb(232, 229, 224);
    private static readonly Color PanelBackColor = Color.FromArgb(248, 247, 244);
    private static readonly Color TextColor = Color.FromArgb(28, 29, 30);
    private static readonly Color HelpTextColor = Color.FromArgb(35, 35, 35);
    private static readonly Color MutedTextColor = Color.FromArgb(88, 88, 84);
    private static readonly Color AccentColor = Color.FromArgb(126, 77, 48);
    private static readonly Color AccentGreen = Color.FromArgb(69, 118, 73);
    private static readonly Color WarningColor = Color.FromArgb(154, 104, 35);
    private static readonly Color DangerColor = Color.FromArgb(166, 58, 44);
    private static readonly Color ButtonBackColor = Color.FromArgb(232, 229, 224);
    private static readonly Color PrimaryButtonBackColor = Color.FromArgb(216, 232, 216);
    private static readonly Color LogBackColor = Color.FromArgb(35, 35, 35);
    private static readonly Color LogTextColor = Color.FromArgb(226, 226, 218);

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SCOLIDEX");
    private static readonly string LastRoutePathFile = Path.Combine(SettingsDirectory, "last-route.txt");

    private readonly TextBox routePathText = new();
    private readonly RadioButton appendMode = new();
    private readonly RadioButton overwriteMode = new();
    private readonly CheckBox createRouteTiles = new();
    private readonly CheckBox distantMountains = new();
    private readonly CheckBox cleanTileTemplate = new();
    private readonly CheckBox scanOverride = new();
    private readonly RadioButton existingTilesCoverage = new();
    private readonly RadioButton markerCoverage = new();
    private readonly RadioButton kmlCoverage = new();
    private readonly RadioButton trackDatabaseCoverage = new();
    private readonly RadioButton textFileCoverage = new();
    private readonly NumericUpDown terrainRadius = new();
    private readonly NumericUpDown loTileRadius = new();
    private readonly TrackBar postEastWestShiftSlider = new();
    private readonly TrackBar postNorthSouthShiftSlider = new();
    private readonly NumericUpDown postEastWestShiftValue = new();
    private readonly NumericUpDown postNorthSouthShiftValue = new();
    private readonly Button commitPostProcessButton = new();
    private readonly Button scanButton = new();
    private readonly Button runButton = new();
    private readonly Button abortButton = new();
    private readonly Button exitButton = new();
    private readonly Button contactButton = new();
    private readonly Button helpButton = new();
    private readonly Label tileTotalValue = new();
    private readonly Label tileProcessedValue = new();
    private readonly Label tileSkippedValue = new();
    private readonly Label tileOneMeterValue = new();
    private readonly Label tileOprValue = new();
    private readonly Label tileTenMeterValue = new();
    private readonly Label tileFailuresValue = new();
    private readonly Label dmTotalValue = new();
    private readonly Label dmProcessedValue = new();
    private readonly Label dmSkippedValue = new();
    private readonly Label dmOneMeterValue = new();
    private readonly Label dmOprValue = new();
    private readonly Label dmTenMeterValue = new();
    private readonly Label dmFailuresValue = new();
    private readonly TextBox logText = new();
    private readonly StringBuilder statusLineBuffer = new();
    private readonly StatusCounters routeStatus = new();
    private readonly StatusCounters dmStatus = new();
    private readonly Label versionLabel = new();
    private bool readingDistantMountainOutput;
    private int activeDmIndex;
    private CancellationTokenSource? runCancellation;
    private CancellationTokenSource? scanCancellation;
    private bool scanPassed;
    private bool scanLocked;
    private TextWriter? previousOut;
    private TextWriter? previousError;
    private StreamWriter? logFileWriter;

    public TopoForm()
    {
        Text = "SCO LIDEX";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 960);
        Size = new Size(940, 1010);
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = AppBackColor;
        ForeColor = TextColor;

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(12),
            BackColor = AppBackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        TableLayoutPanel titlePanel = new()
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            BackColor = HeaderBackColor,
            Padding = new Padding(12, 9, 12, 0),
            Margin = new Padding(0, 0, 0, 8),
        };
        titlePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titlePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titlePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titlePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 2));

        TableLayoutPanel brandPanel = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = HeaderBackColor,
            Margin = new Padding(0),
        };
        brandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        brandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        PictureBox titleImage = new()
        {
            Size = new Size(162, 108),
            Margin = new Padding(0, 0, 20, 0),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = LoadTitleImage(),
        };
        titleImage.Visible = titleImage.Image is not null;

        TableLayoutPanel brandText = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = HeaderBackColor,
            Margin = new Padding(0, 24, 0, 0),
        };
        brandText.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        brandText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        brandText.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        TableLayoutPanel titleLine = new()
        {
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = HeaderBackColor,
            Margin = new Padding(0),
        };
        titleLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        titleLine.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label title = new()
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 22, FontStyle.Regular),
            ForeColor = HelpTextColor,
            Text = "SCO LIDEX",
            Margin = new Padding(0, 0, 0, 0),
        };
        Label openRailsTag = new()
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Regular),
            ForeColor = MutedTextColor,
            Anchor = AnchorStyles.Bottom,
            Text = "for Open Rails",
            Margin = new Padding(10, 0, 0, 5),
        };
        Label brandName = new()
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Regular),
            ForeColor = AccentColor,
            Text = "Beast of Burden",
            Margin = new Padding(2, 0, 0, 0),
        };
        titleLine.Controls.Add(title, 0, 0);
        titleLine.Controls.Add(openRailsTag, 1, 0);
        brandText.Controls.Add(titleLine, 0, 0);
        brandText.Controls.Add(brandName, 0, 1);
        brandPanel.Controls.Add(titleImage, 0, 0);
        brandPanel.Controls.Add(brandText, 1, 0);

        versionLabel.AutoSize = true;
        versionLabel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        versionLabel.BackColor = PanelBackColor;
        versionLabel.BorderStyle = BorderStyle.FixedSingle;
        versionLabel.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        versionLabel.ForeColor = MutedTextColor;
        versionLabel.Padding = new Padding(8, 3, 8, 3);
        versionLabel.Margin = new Padding(22, 9, 0, 0);
        versionLabel.Text = LoadVersionText();

        Panel divider = new()
        {
            Dock = DockStyle.Fill,
            Height = 1,
            BackColor = Color.FromArgb(188, 172, 158),
            Margin = new Padding(0, 8, 0, 0),
        };
        titlePanel.Controls.Add(brandPanel, 0, 0);
        titlePanel.Controls.Add(versionLabel, 1, 0);
        titlePanel.Controls.Add(divider, 0, 1);
        titlePanel.SetColumnSpan(divider, 2);
        root.Controls.Add(titlePanel);

        root.Controls.Add(BuildRoutePanel());
        root.Controls.Add(BuildOptionsPanel());
        root.Controls.Add(BuildButtonPanel());

        logText.Dock = DockStyle.Fill;
        logText.Multiline = true;
        logText.ReadOnly = true;
        logText.ScrollBars = ScrollBars.Vertical;
        logText.Font = new Font("Consolas", 9);
        logText.BackColor = LogBackColor;
        logText.ForeColor = LogTextColor;
        logText.BorderStyle = BorderStyle.FixedSingle;
        logText.MinimumSize = new Size(0, 150);
        root.Controls.Add(logText);

        TableLayoutPanel footer = new()
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            BackColor = AppBackColor,
            Margin = new Padding(0, 6, 4, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        Label licenseTag = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 10, FontStyle.Regular),
            ForeColor = AccentColor,
            Text = "License: GNU GPL v3.0 or later",
            Margin = new Padding(0),
        };
        Label dataSourceTag = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Font = new Font("Segoe UI", 10, FontStyle.Regular),
            ForeColor = AccentColor,
            Text = "USGS 1m LIDAR Cloud Service Data",
            Margin = new Padding(0),
        };
        footer.Controls.Add(licenseTag, 0, 0);
        footer.Controls.Add(dataSourceTag, 1, 0);
        root.Controls.Add(footer);

        markerCoverage.CheckedChanged += (_, _) => UpdateRadiusState();
        kmlCoverage.CheckedChanged += (_, _) => UpdateRadiusState();
        trackDatabaseCoverage.CheckedChanged += (_, _) => UpdateRadiusState();
        textFileCoverage.CheckedChanged += (_, _) => UpdateRadiusState();
        distantMountains.CheckedChanged += (_, _) => loTileRadius.Enabled = distantMountains.Checked;
        WireScanInvalidation();
        routePathText.Text = LoadLastRoutePath();
        ResetStatus();
        SetRunning(false);
    }

    // Build the top half of the form from small panels instead of the designer.
    // That keeps the layout reproducible in source and avoids hidden .resx or
    // designer state when sharing the project publicly.
    private Control BuildRoutePanel()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 6),
            BackColor = AppBackColor,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        panel.Controls.Add(new Label { AutoSize = true, Text = "Route Path:", Anchor = AnchorStyles.Left, ForeColor = TextColor }, 0, 0);
        routePathText.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        routePathText.BackColor = Color.White;
        routePathText.ForeColor = TextColor;
        routePathText.BorderStyle = BorderStyle.FixedSingle;
        panel.Controls.Add(routePathText, 1, 0);
        return panel;
    }

    private static void StyleGroupBox(GroupBox box)
    {
        box.BackColor = PanelBackColor;
        box.ForeColor = AccentColor;
        box.Padding = new Padding(8, 6, 8, 8);
    }

    private static void StyleButton(Button button, bool primary = false)
    {
        button.AutoSize = false;
        button.MinimumSize = new Size(78, 28);
        button.Size = new Size(78, 28);
        button.Margin = new Padding(4, 3, 4, 3);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.UseVisualStyleBackColor = false;
        button.Tag = primary;
        button.EnabledChanged += (_, _) => UpdateButtonVisual(button);
        UpdateButtonVisual(button);
    }

    private static void SetButtonPrimary(Button button, bool primary)
    {
        button.Tag = primary;
        UpdateButtonVisual(button);
    }

    private static void UpdateButtonVisual(Button button)
    {
        bool primary = button.Tag is bool value && value;
        if (button.Enabled)
        {
            button.BackColor = primary ? PrimaryButtonBackColor : ButtonBackColor;
            button.ForeColor = TextColor;
            button.FlatAppearance.BorderColor = primary ? AccentGreen : Color.FromArgb(150, 145, 137);
            return;
        }

        button.BackColor = Color.FromArgb(220, 218, 213);
        button.ForeColor = Color.FromArgb(155, 151, 143);
        button.FlatAppearance.BorderColor = Color.FromArgb(198, 194, 187);
    }

    private Control BuildOptionsPanel()
    {
        Panel panel = new()
        {
            Dock = DockStyle.Top,
            Height = 472,
            MinimumSize = new Size(0, 472),
            BackColor = AppBackColor,
        };

        GroupBox modeBox = new() { Text = "Mode" };
        StyleGroupBox(modeBox);
        FlowLayoutPanel modeFlow = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = false, BackColor = PanelBackColor, ForeColor = TextColor };
        appendMode.Text = "Append";
        appendMode.AutoSize = true;
        appendMode.Checked = true;
        overwriteMode.Text = "Overwrite";
        overwriteMode.AutoSize = true;
        modeFlow.Padding = new Padding(6, 8, 0, 0);
        modeFlow.Controls.Add(appendMode);
        modeFlow.Controls.Add(overwriteMode);
        modeBox.Controls.Add(modeFlow);

        GroupBox optionBox = new() { Text = "Options" };
        StyleGroupBox(optionBox);
        TableLayoutPanel optionPanel = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(6, 8, 0, 0),
            BackColor = PanelBackColor,
            ForeColor = TextColor,
        };
        optionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        optionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        optionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        optionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        createRouteTiles.Text = "Create Route Tiles";
        createRouteTiles.AutoSize = true;
        createRouteTiles.Checked = true;
        distantMountains.Text = "Create DM Tiles";
        distantMountains.AutoSize = true;
        cleanTileTemplate.Text = "Clean Tile Wipe (Destructive)";
        cleanTileTemplate.AutoSize = true;
        scanOverride.Text = "Scan Override";
        scanOverride.AutoSize = true;
        createRouteTiles.Margin = new Padding(3, 0, 3, 3);
        cleanTileTemplate.Margin = new Padding(3, 0, 3, 3);
        distantMountains.Margin = new Padding(3, 3, 3, 3);
        scanOverride.Margin = new Padding(3, 3, 3, 3);
        optionPanel.Controls.Add(createRouteTiles, 0, 0);
        optionPanel.Controls.Add(cleanTileTemplate, 1, 0);
        optionPanel.Controls.Add(distantMountains, 0, 1);
        optionPanel.Controls.Add(scanOverride, 1, 1);
        optionBox.Controls.Add(optionPanel);

        GroupBox coverageBox = new() { Text = "Selection" };
        StyleGroupBox(coverageBox);
        TableLayoutPanel coverage = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(6),
            BackColor = PanelBackColor,
            ForeColor = TextColor,
        };
        coverage.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        coverage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        existingTilesCoverage.Text = "Use Route Tiles";
        existingTilesCoverage.AutoSize = true;
        existingTilesCoverage.Checked = true;
        markerCoverage.Text = "Use Marker File";
        markerCoverage.AutoSize = true;
        kmlCoverage.Text = "Use KML File";
        kmlCoverage.AutoSize = true;
        trackDatabaseCoverage.Text = "Use Track Database";
        trackDatabaseCoverage.AutoSize = true;
        trackDatabaseCoverage.Margin = new Padding(3, 3, 3, 10);
        textFileCoverage.Text = "Use Text File";
        textFileCoverage.AutoSize = true;
        terrainRadius.Minimum = 0;
        terrainRadius.Maximum = 100;
        terrainRadius.Value = 1;
        terrainRadius.Enabled = false;
        loTileRadius.Minimum = 1;
        loTileRadius.Maximum = 100;
        loTileRadius.Value = 1;
        loTileRadius.Enabled = false;

        coverage.Controls.Add(existingTilesCoverage, 0, 0);
        coverage.SetColumnSpan(existingTilesCoverage, 2);
        coverage.Controls.Add(textFileCoverage, 0, 1);
        coverage.SetColumnSpan(textFileCoverage, 2);
        Label separator = new()
        {
            AutoSize = false,
            BorderStyle = BorderStyle.Fixed3D,
            Height = 2,
            Margin = new Padding(0, 8, 0, 8),
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(210, 205, 196),
        };
        coverage.Controls.Add(separator, 0, 2);
        coverage.SetColumnSpan(separator, 2);
        coverage.Controls.Add(markerCoverage, 0, 3);
        coverage.SetColumnSpan(markerCoverage, 2);
        coverage.Controls.Add(kmlCoverage, 0, 4);
        coverage.SetColumnSpan(kmlCoverage, 2);
        coverage.Controls.Add(trackDatabaseCoverage, 0, 5);
        coverage.SetColumnSpan(trackDatabaseCoverage, 2);
        coverage.Controls.Add(new Label { AutoSize = true, Text = "Tile Radius:", Margin = new Padding(3, 8, 3, 3) }, 0, 6);
        terrainRadius.Margin = new Padding(3, 8, 3, 3);
        coverage.Controls.Add(terrainRadius, 1, 6);
        coverage.Controls.Add(new Label { AutoSize = true, Text = "DM Radius:" }, 0, 7);
        coverage.Controls.Add(loTileRadius, 1, 7);

        coverageBox.Controls.Add(coverage);
        Control postProcessPanel = BuildPostProcessPanel();
        Control statusPanel = BuildStatusPanel();
        panel.Controls.Add(modeBox);
        panel.Controls.Add(optionBox);
        panel.Controls.Add(coverageBox);
        panel.Controls.Add(postProcessPanel);
        panel.Controls.Add(statusPanel);

        void LayoutOptionControls()
        {
            int gap = 8;
            int columnWidth = (panel.ClientSize.Width - gap) / 2;
            int rightX = columnWidth + gap;
            modeBox.SetBounds(0, 0, columnWidth, 90);
            optionBox.SetBounds(rightX, 0, panel.ClientSize.Width - rightX, 90);
            coverageBox.SetBounds(0, 100, columnWidth, 364);
            statusPanel.SetBounds(rightX, 100, panel.ClientSize.Width - rightX, 192);
            postProcessPanel.SetBounds(rightX, 300, panel.ClientSize.Width - rightX, 164);
        }

        panel.Resize += (_, _) => LayoutOptionControls();
        panel.HandleCreated += (_, _) => LayoutOptionControls();
        return panel;
    }

    // Bias controls serve two purposes: during Run they shift where DEM samples
    // are taken from; during Commit/Post Processing they resample existing
    // terrain only, which is faster but slightly less faithful than rerunning DEM.
    private Control BuildPostProcessPanel()
    {
        GroupBox postBox = new() { Text = "Advanced Geo Bias" };
        StyleGroupBox(postBox);
        TableLayoutPanel shell = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(6),
            BackColor = PanelBackColor,
            ForeColor = TextColor,
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        TableLayoutPanel shift = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 5,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 2),
            BackColor = PanelBackColor,
            ForeColor = TextColor,
        };
        shift.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        shift.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shift.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        shift.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        shift.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        shift.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        shift.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        ConfigureBiasControls(postEastWestShiftSlider, postEastWestShiftValue);
        ConfigureBiasControls(postNorthSouthShiftSlider, postNorthSouthShiftValue);
        AddBiasRow(shift, 0, "N", "S", postNorthSouthShiftSlider, postNorthSouthShiftValue);
        AddBiasRow(shift, 1, "E", "W", postEastWestShiftSlider, postEastWestShiftValue);

        commitPostProcessButton.Text = "Commit";
        StyleButton(commitPostProcessButton, primary: true);
        commitPostProcessButton.Click += CommitPostProcess_Click;
        FlowLayoutPanel postProcessAction = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.None,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 0),
            BackColor = PanelBackColor,
        };
        Label postProcessLabel = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            ForeColor = MutedTextColor,
            Text = "Post Processing:",
            Margin = new Padding(0, 7, 6, 0),
        };
        postProcessAction.Controls.Add(postProcessLabel);
        postProcessAction.Controls.Add(commitPostProcessButton);

        shell.Controls.Add(shift, 0, 0);
        shell.Controls.Add(postProcessAction, 0, 1);
        postBox.Controls.Add(shell);
        return postBox;
    }

    private static void ConfigureBiasControls(TrackBar slider, NumericUpDown value)
    {
        slider.Minimum = -100;
        slider.Maximum = 100;
        slider.TickFrequency = 25;
        slider.SmallChange = 1;
        slider.LargeChange = 5;
        slider.Value = 0;
        slider.AutoSize = false;
        slider.Height = 28;
        slider.Width = 220;
        slider.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        value.Minimum = -100;
        value.Maximum = 100;
        value.Increment = 1;
        value.Value = 0;
        value.Width = 58;
        value.TextAlign = HorizontalAlignment.Right;

        slider.ValueChanged += (_, _) =>
        {
            int biasValue = -slider.Value;
            if (value.Value != biasValue)
            {
                value.Value = biasValue;
            }
        };
        value.ValueChanged += (_, _) =>
        {
            int sliderValue = -(int)value.Value;
            if (slider.Value != sliderValue)
            {
                slider.Value = sliderValue;
            }
        };
    }

    private static void AddBiasRow(TableLayoutPanel bias, int row, string positiveDirection, string negativeDirection, TrackBar slider, NumericUpDown value)
    {
        bias.Controls.Add(new Label { AutoSize = true, Text = positiveDirection, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        bias.Controls.Add(slider, 1, row);
        bias.Controls.Add(new Label { AutoSize = true, Text = negativeDirection, Anchor = AnchorStyles.Left, Margin = new Padding(6, 6, 12, 3) }, 2, row);
        bias.Controls.Add(value, 3, row);
        bias.Controls.Add(new Label { AutoSize = true, Text = "m", Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 4, row);
    }

    private Control BuildStatusPanel()
    {
        GroupBox statusBox = new() { Text = "Status" };
        StyleGroupBox(statusBox);
        TableLayoutPanel status = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 8,
            AutoSize = false,
            Padding = new Padding(6, 4, 6, 4),
            BackColor = PanelBackColor,
            ForeColor = TextColor,
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        for (int row = 0; row < 8; row++)
        {
            status.RowStyles.Add(new RowStyle(SizeType.Percent, 12.5f));
        }

        AddStatusHeader(status, "Tiles", 1);
        AddStatusHeader(status, "DM Tiles", 2);
        AddStatusRow(status, 1, "Total", tileTotalValue, dmTotalValue);
        AddStatusRow(status, 2, "Proc", tileProcessedValue, dmProcessedValue);
        AddStatusRow(status, 3, "• 1m", tileOneMeterValue, dmOneMeterValue, indentTitle: true);
        AddStatusRow(status, 4, "• 5m~", tileOprValue, dmOprValue, indentTitle: true);
        AddStatusRow(status, 5, "• 10m", tileTenMeterValue, dmTenMeterValue, indentTitle: true);
        AddStatusRow(status, 6, "Skip", tileSkippedValue, dmSkippedValue);
        AddStatusRow(status, 7, "Fail", tileFailuresValue, dmFailuresValue);
        statusBox.Controls.Add(status);
        return statusBox;
    }

    private static void AddStatusHeader(TableLayoutPanel status, string text, int column)
    {
        Label label = new()
        {
            AutoSize = true,
            Text = text,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            ForeColor = AccentColor,
            Anchor = AnchorStyles.Left,
        };
        status.Controls.Add(label, column, 0);
    }

    private static void AddStatusRow(TableLayoutPanel status, int row, string title, Label tileValue, Label dmValue, bool indentTitle = false)
    {
        status.Controls.Add(new Label { AutoSize = true, Text = title, Anchor = AnchorStyles.Left, ForeColor = TextColor, Margin = indentTitle ? new Padding(16, 3, 3, 3) : new Padding(3) }, 0, row);
        ConfigureStatusValue(tileValue);
        ConfigureStatusValue(dmValue);
        status.Controls.Add(tileValue, 1, row);
        status.Controls.Add(dmValue, 2, row);
    }

    private static void ConfigureStatusValue(Label label)
    {
        label.AutoSize = true;
        label.Text = "0";
        label.Font = new Font("Consolas", 9);
        label.ForeColor = MutedTextColor;
        label.Anchor = AnchorStyles.Left;
    }

    private Control BuildButtonPanel()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Top,
            Padding = new Padding(0, 8, 0, 8),
            AutoSize = true,
            ColumnCount = 2,
            BackColor = AppBackColor,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        FlowLayoutPanel leftButtons = new()
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            BackColor = AppBackColor,
        };
        FlowLayoutPanel rightButtons = new()
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = AppBackColor,
        };
        scanButton.Text = "Scan";
        StyleButton(scanButton, primary: true);
        scanButton.Click += Scan_Click;
        runButton.Text = "Run";
        StyleButton(runButton, primary: true);
        runButton.Click += Run_Click;
        abortButton.Text = "Abort";
        StyleButton(abortButton);
        abortButton.Click += Abort_Click;
        exitButton.Text = "Exit";
        StyleButton(exitButton);
        exitButton.Click += (_, _) => Close();
        leftButtons.Controls.Add(scanButton);
        leftButtons.Controls.Add(runButton);
        leftButtons.Controls.Add(abortButton);
        leftButtons.Controls.Add(exitButton);
        contactButton.Text = "Contact";
        StyleButton(contactButton, primary: true);
        contactButton.Anchor = AnchorStyles.Right;
        contactButton.Click += Contact_Click;
        helpButton.Text = "Help";
        StyleButton(helpButton, primary: true);
        helpButton.Anchor = AnchorStyles.Right;
        helpButton.Click += Help_Click;
        rightButtons.Controls.Add(contactButton);
        rightButtons.Controls.Add(helpButton);
        panel.Controls.Add(leftButtons, 0, 0);
        panel.Controls.Add(rightButtons, 1, 0);
        return panel;
    }

    private void Contact_Click(object? sender, EventArgs e)
    {
        using Image? contactImage = LoadContentImage("Contact.png");
        if (contactImage is null)
        {
            MessageBox.Show(this, "Could not find Contact.png.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Rectangle workArea = Screen.FromControl(this).WorkingArea;
        int maxImageSize = Math.Min(760, Math.Min(workArea.Width - 160, workArea.Height - 180));
        maxImageSize = Math.Max(360, maxImageSize);
        double scale = Math.Min((double)maxImageSize / contactImage.Width, (double)maxImageSize / contactImage.Height);
        Size imageSize = new(
            Math.Max(1, (int)Math.Round(contactImage.Width * scale)),
            Math.Max(1, (int)Math.Round(contactImage.Height * scale)));

        using Form dialog = new()
        {
            Text = "SCO LIDEX Contact",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = AppBackColor,
            ClientSize = new Size(imageSize.Width + 24, imageSize.Height + 24),
        };
        PictureBox picture = new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(12),
            Padding = new Padding(12),
            BackColor = AppBackColor,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = new Bitmap(contactImage),
        };
        dialog.Controls.Add(picture);
        dialog.ShowDialog(this);
        picture.Image?.Dispose();
    }

    private void Help_Click(object? sender, EventArgs e)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "INSTRUCTIONS.txt"),
            Path.Combine(AppContext.BaseDirectory, "..", "INSTRUCTIONS.txt"),
            Path.Combine(Environment.CurrentDirectory, "INSTRUCTIONS.txt"),
        ];

        string? helpPath = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (helpPath is null)
        {
            MessageBox.Show(this, "Could not find INSTRUCTIONS.txt.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            ShowReadOnlyTextDocument("SCO LIDEX Instructions", helpPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open INSTRUCTIONS.txt:{Environment.NewLine}{ex.Message}", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // Help is rendered in-app so users can read instructions without editing the
    // text file by accident. INSTRUCTIONS.txt remains the single source content.
    private void ShowReadOnlyTextDocument(string title, string path)
    {
        string text = File.ReadAllText(path, Encoding.UTF8);
        Rectangle workArea = Screen.FromControl(this).WorkingArea;
        Size dialogSize = new(
            Math.Min(920, Math.Max(640, workArea.Width - 180)),
            Math.Min(760, Math.Max(520, workArea.Height - 180)));

        using Form dialog = new()
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(640, 480),
            Size = dialogSize,
            BackColor = AppBackColor,
            Icon = Icon,
        };

        RichTextBox documentText = new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = true,
            DetectUrls = false,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.White,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            ShortcutsEnabled = true,
            Margin = new Padding(12),
        };
        FormatHelpDocument(documentText, text);

        Button closeButton = new()
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Right,
            Width = 86,
            Height = 28,
        };
        StyleButton(closeButton, primary: true);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 8, 12, 10),
            BackColor = AppBackColor,
        };
        buttons.Controls.Add(closeButton);

        dialog.AcceptButton = closeButton;
        dialog.CancelButton = closeButton;
        dialog.Controls.Add(documentText);
        dialog.Controls.Add(buttons);
        dialog.ShowDialog(this);
    }

    // Tiny plain-text formatter: headings come from underline rules, indented
    // numbered lines become bullets, and path/example lines use monospace text.
    private void FormatHelpDocument(RichTextBox box, string text)
    {
        box.SuspendLayout();
        box.Clear();
        box.SelectionColor = HelpTextColor;
        box.SelectionFont = new Font("Segoe UI", 10f);

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            string next = i + 1 < lines.Length ? lines[i + 1].Trim() : "";

            if (trimmed.Length > 0 && IsRuleLine(next))
            {
                AppendHelpHeading(box, trimmed, i == 0);
                i++;
                continue;
            }

            if (trimmed.Length == 0)
            {
                AppendHelpText(box, Environment.NewLine, new Font("Segoe UI", 5f), HelpTextColor);
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                AppendHelpBullet(box, trimmed[2..]);
                continue;
            }

            if (Regex.IsMatch(trimmed, @"^\d+\.\s+") && line.StartsWith("  ", StringComparison.Ordinal))
            {
                AppendHelpBullet(box, trimmed);
                continue;
            }

            if (trimmed.EndsWith(':') && trimmed.Length <= 48 && !trimmed.Contains('\\'))
            {
                AppendHelpText(box, trimmed + Environment.NewLine, CreateHelpBoldFont(10f), HelpTextColor);
                continue;
            }

            if (IsExampleLine(line))
            {
                AppendHelpExample(box, trimmed);
                continue;
            }

            AppendHelpParagraph(box, trimmed);
        }

        box.SelectionStart = 0;
        box.SelectionLength = 0;
        box.ResumeLayout();
    }

    private static bool IsRuleLine(string text)
    {
        return text.Length >= 3 && text.All(c => c == '=' || c == '-');
    }

    private static bool IsExampleLine(string line)
    {
        string trimmed = line.Trim();
        return trimmed.StartsWith("-0", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains('\\') ||
            trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private void AppendHelpHeading(RichTextBox box, string text, bool title)
    {
        if (box.TextLength > 0)
        {
            AppendHelpText(box, Environment.NewLine, new Font("Segoe UI", 4f), HelpTextColor);
        }

        Font font = title
            ? CreateHelpBoldFont(18f)
            : CreateHelpBoldFont(12.5f);
        AppendHelpText(box, text + Environment.NewLine, font, HelpTextColor);
        if (!title)
        {
            AppendHelpText(box, new string('-', 48) + Environment.NewLine, new Font("Segoe UI", 7f), Color.FromArgb(210, 204, 194));
        }
    }

    private void AppendHelpParagraph(RichTextBox box, string text)
    {
        AppendHelpText(box, text + Environment.NewLine, new Font("Segoe UI", 10f), HelpTextColor);
    }

    private void AppendHelpBullet(RichTextBox box, string text)
    {
        box.SelectionBullet = true;
        box.SelectionIndent = 24;
        box.SelectionHangingIndent = 8;
        AppendHelpText(box, text + Environment.NewLine, new Font("Segoe UI", 10f), HelpTextColor);
        box.SelectionBullet = false;
        box.SelectionIndent = 0;
        box.SelectionHangingIndent = 0;
    }

    private void AppendHelpExample(RichTextBox box, string text)
    {
        box.SelectionIndent = 18;
        AppendHelpText(box, text + Environment.NewLine, new Font("Consolas", 9.25f), HelpTextColor);
        box.SelectionIndent = 0;
    }

    private static void AppendHelpText(RichTextBox box, string text, Font font, Color color)
    {
        box.SelectionFont = font;
        box.SelectionColor = color;
        box.AppendText(text);
    }

    private static Font CreateHelpBoldFont(float size)
    {
        return new Font("Segoe UI Semibold", size, FontStyle.Bold);
    }

    // Scan is intentionally read-only. A passing scan locks the current settings
    // so Run uses the same route/selection that was validated.
    private async void Scan_Click(object? sender, EventArgs e)
    {
        string routePath = NormalizeRoutePath(routePathText.Text);
        if (string.IsNullOrWhiteSpace(routePath) || !Directory.Exists(routePath))
        {
            MessageBox.Show(this, "Select a valid route folder first.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        routePathText.Text = routePath;
        SaveLastRoutePath(routePath);
        logText.Clear();
        ResetStatus();
        scanPassed = false;
        scanLocked = true;
        SetScanning(true);
        scanCancellation = new CancellationTokenSource();
        previousOut = Console.Out;
        previousError = Console.Error;
        logFileWriter = OpenLogFile();
        WriteRunSettingsHeader(routePath, "Scan");
        using TextWriter writer = new UiTextWriter(AppendLog);
        Console.SetOut(writer);
        Console.SetError(writer);

        try
        {
            Program.ScanOptions options = new(
                CreateRouteTiles: createRouteTiles.Checked,
                CreateDistantMountains: distantMountains.Checked,
                MarkerCoverage: markerCoverage.Checked,
                TrackDatabaseCoverage: trackDatabaseCoverage.Checked,
                KmlCoverage: kmlCoverage.Checked,
                TextFileCoverage: textFileCoverage.Checked,
                CleanTileWipe: cleanTileTemplate.Checked,
                TerrainRadius: (int)terrainRadius.Value,
                LoTileRadius: (int)loTileRadius.Value);

            Program.ScanSummary summary = await Task.Run(() => Program.ScanRouteAsync(routePath, options, scanCancellation.Token));
            routeStatus.Total = summary.RouteTileTotal;
            routeStatus.Failures = summary.UnreadableRouteTiles;
            dmStatus.Total = summary.DistantMountainTotal;
            dmStatus.Failures = summary.UnreadableDistantMountainTiles;
            UpdateStatusDisplay();
            scanPassed = summary.CanRun;
            AppendLog(scanPassed
                ? $"{Environment.NewLine}Scan passed. Run is enabled. Use Abort to unlock and change settings.{Environment.NewLine}"
                : $"{Environment.NewLine}Scan failed. Fix blocking issues, then scan again.{Environment.NewLine}");
        }
        catch (OperationCanceledException)
        {
            AppendLog($"{Environment.NewLine}Scan aborted. Settings unlocked.{Environment.NewLine}");
            ResetScanState();
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}{Environment.NewLine}");
            scanPassed = false;
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            logFileWriter?.Dispose();
            logFileWriter = null;
            scanCancellation?.Dispose();
            scanCancellation = null;
            SetScanning(false);
        }
    }

    // Run redirects the engine's Console output into both the GUI log window and
    // the desktop log file. The engine remains usable from the CLI for testing.
    private async void Run_Click(object? sender, EventArgs e)
    {
        if (!scanPassed && !scanOverride.Checked)
        {
            MessageBox.Show(this, "Run requires a passing Scan first.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string routePath = NormalizeRoutePath(routePathText.Text);
        if (string.IsNullOrWhiteSpace(routePath) || !Directory.Exists(routePath))
        {
            MessageBox.Show(this, "Select a valid route folder first.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        routePathText.Text = routePath;
        SaveLastRoutePath(routePath);
        SetRunning(true);
        logText.Clear();
        runCancellation = new CancellationTokenSource();
        previousOut = Console.Out;
        previousError = Console.Error;
        logFileWriter = OpenLogFile();
        WriteRunSettingsHeader(routePath, "Run");
        using TextWriter writer = new UiTextWriter(AppendLog);
        Console.SetOut(writer);
        Console.SetError(writer);
        Program.ResetUsgsDataCounter();
        Stopwatch runTimer = Stopwatch.StartNew();

        try
        {
            string[] args = BuildRunArguments(routePath);
            await Task.Run(() => Program.RunConsoleAsync(args, runCancellation.Token));
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}{Environment.NewLine}");
        }
        finally
        {
            runTimer.Stop();
            AppendLog(
                $"{Environment.NewLine}Elapsed time: {FormatElapsed(runTimer.Elapsed)}{Environment.NewLine}" +
                $"USGS data read: {Program.FormatUsgsDataBytesRead()}{Environment.NewLine}");
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            logFileWriter?.Dispose();
            logFileWriter = null;
            runCancellation.Dispose();
            runCancellation = null;
            ResetScanState();
            SetRunning(false);
        }
    }

    private async void CommitPostProcess_Click(object? sender, EventArgs e)
    {
        int eastWestShift = (int)postEastWestShiftValue.Value;
        int northSouthShift = (int)postNorthSouthShiftValue.Value;
        if (eastWestShift == 0 && northSouthShift == 0)
        {
            MessageBox.Show(this, "Set an Advanced Geo Bias offset before committing Post Processing.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string routePath = NormalizeRoutePath(routePathText.Text);
        if (string.IsNullOrWhiteSpace(routePath) || !Directory.Exists(routePath))
        {
            MessageBox.Show(this, "Select a valid route folder first.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string targetText = createRouteTiles.Checked && distantMountains.Checked
            ? "selected normal terrain and DM _y.raw grids"
            : createRouteTiles.Checked
                ? "selected normal terrain _y.raw grids"
                : distantMountains.Checked
                    ? "selected DM _y.raw grids"
                    : "no selected terrain grids";
        if (!createRouteTiles.Checked && !distantMountains.Checked)
        {
            MessageBox.Show(this, "Select Create Route Tiles and/or Create DM Tiles before committing.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        DialogResult confirm = MessageBox.Show(
            this,
            $"Commit Post Processing shift using existing terrain?{Environment.NewLine}{Environment.NewLine}East/West: {eastWestShift} m{Environment.NewLine}North/South: {northSouthShift} m{Environment.NewLine}{Environment.NewLine}This resamples and rewrites {targetText}. Back up the route first.",
            "SCO LIDEX",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        routePathText.Text = routePath;
        SaveLastRoutePath(routePath);
        SetRunning(true);
        logText.Clear();
        runCancellation = new CancellationTokenSource();
        previousOut = Console.Out;
        previousError = Console.Error;
        logFileWriter = OpenLogFile();
        WriteRunSettingsHeader(routePath, "Post Process");
        using TextWriter writer = new UiTextWriter(AppendLog);
        Console.SetOut(writer);
        Console.SetError(writer);
        Stopwatch runTimer = Stopwatch.StartNew();

        try
        {
            Program.PostProcessSelectionOptions options = new(
                ShiftRouteTiles: createRouteTiles.Checked,
                ShiftDistantMountains: distantMountains.Checked,
                MarkerCoverage: markerCoverage.Checked,
                TrackDatabaseCoverage: trackDatabaseCoverage.Checked,
                KmlCoverage: kmlCoverage.Checked,
                TextFileCoverage: textFileCoverage.Checked,
                TerrainRadius: (int)terrainRadius.Value,
                LoTileRadius: (int)loTileRadius.Value);

            await Task.Run(() => Program.PostProcessTerrainShiftAsync(routePath, options, eastWestShift, northSouthShift, runCancellation.Token));
        }
        catch (OperationCanceledException)
        {
            AppendLog($"{Environment.NewLine}Post Process aborted.{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}{Environment.NewLine}");
        }
        finally
        {
            runTimer.Stop();
            AppendLog($"{Environment.NewLine}Elapsed time: {FormatElapsed(runTimer.Elapsed)}{Environment.NewLine}");
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            logFileWriter?.Dispose();
            logFileWriter = null;
            runCancellation.Dispose();
            runCancellation = null;
            ResetScanState();
            SetRunning(false);
        }
    }

    private static string NormalizeRoutePath(string path)
    {
        string value = path.Trim('"', ' ');
        return value.EndsWith(".trk", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(value) ?? value
            : value;
    }

    private static string LoadLastRoutePath()
    {
        try
        {
            return File.Exists(LastRoutePathFile)
                ? File.ReadAllText(LastRoutePathFile).Trim()
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static void SaveLastRoutePath(string routePath)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(LastRoutePathFile, routePath);
        }
        catch
        {
            // Remembering the route is convenience-only; never block a terrain run for it.
        }
    }

    // Convert GUI state to the same argument model used by CLI runs. Keeping one
    // engine path prevents the GUI and command line from drifting apart.
    private string[] BuildRunArguments(string routePath)
    {
        List<string> args = [routePath, "--write"];
        if (overwriteMode.Checked)
        {
            args.Add("--overwrite");
        }

        if (!createRouteTiles.Checked)
        {
            args.Add("--no-route-tiles");
        }

        if (cleanTileTemplate.Checked)
        {
            args.Add("--clean-tile-template");
        }

        if (markerCoverage.Checked)
        {
            args.Add("--marker-coverage");
            args.Add("--terrain-radius");
            args.Add(((int)terrainRadius.Value).ToString());
        }
        else if (trackDatabaseCoverage.Checked)
        {
            args.Add("--track-database-coverage");
            args.Add("--terrain-radius");
            args.Add(((int)terrainRadius.Value).ToString());
        }
        else if (kmlCoverage.Checked)
        {
            args.Add("--kml-coverage");
            args.Add("--terrain-radius");
            args.Add(((int)terrainRadius.Value).ToString());
        }
        else if (textFileCoverage.Checked)
        {
            args.Add("--text-file-coverage");
        }

        if (distantMountains.Checked)
        {
            args.Add("--distant-mountains");
            args.Add("--lo-radius");
            args.Add(((int)loTileRadius.Value).ToString());
        }

        int eastWestBias = (int)postEastWestShiftValue.Value;
        int northSouthBias = (int)postNorthSouthShiftValue.Value;
        if (eastWestBias != 0)
        {
            args.Add("--source-bias-east");
            args.Add(eastWestBias.ToString(CultureInfo.InvariantCulture));
        }

        if (northSouthBias != 0)
        {
            args.Add("--source-bias-north");
            args.Add(northSouthBias.ToString(CultureInfo.InvariantCulture));
        }

        return args.ToArray();
    }

    private void WriteRunSettingsHeader(string routePath, string operation)
    {
        string settings =
            $"Version: {versionLabel.Text}{Environment.NewLine}" +
            $"Operation: {operation}{Environment.NewLine}" +
            $"Route: {routePath}{Environment.NewLine}" +
            $"Mode: {(overwriteMode.Checked ? "Overwrite" : "Append")}{Environment.NewLine}" +
            $"Create Route Tiles: {YesNo(createRouteTiles.Checked)}{Environment.NewLine}" +
            $"Create Distant Mountains: {YesNo(distantMountains.Checked)}{Environment.NewLine}" +
            $"Clean Tile Wipe: {YesNo(cleanTileTemplate.Checked)}{Environment.NewLine}" +
            $"Scan Override: {YesNo(scanOverride.Checked)}{Environment.NewLine}" +
            $"Selection: {GetSelectionText()}{Environment.NewLine}" +
            $"Tile Radius: {(int)terrainRadius.Value}{Environment.NewLine}" +
            $"DM Radius: {(int)loTileRadius.Value}{Environment.NewLine}" +
            $"Advanced Geo Bias N/S: {(int)postNorthSouthShiftValue.Value} m{Environment.NewLine}" +
            $"Advanced Geo Bias E/W: {(int)postEastWestShiftValue.Value} m{Environment.NewLine}" +
            $"Log Path: {Path.Combine(GetUserFacingLogDirectory(), "SCOLIDEX.log")}{Environment.NewLine}" +
            Environment.NewLine;
        AppendLog(settings);
    }

    private string GetSelectionText()
    {
        if (markerCoverage.Checked)
        {
            return "Use Marker File";
        }

        if (kmlCoverage.Checked)
        {
            return "Use KML File";
        }

        if (trackDatabaseCoverage.Checked)
        {
            return "Use Track Database";
        }

        if (textFileCoverage.Checked)
        {
            return "Use Text File";
        }

        return "Use Route Tiles";
    }

    private static string YesNo(bool value)
    {
        return value ? "Yes" : "No";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void Abort_Click(object? sender, EventArgs e)
    {
        if (scanCancellation is not null)
        {
            scanCancellation.Cancel();
            return;
        }

        if (runCancellation is not null)
        {
            runCancellation.Cancel();
            AppendLog($"{Environment.NewLine}Abort requested. Processing will stop before the next tile/write step.{Environment.NewLine}");
            return;
        }

        ResetScanState();
        AppendLog($"{Environment.NewLine}Scan reset. Settings unlocked; scan again before running.{Environment.NewLine}");
    }

    private void SetRunning(bool running)
    {
        ApplyInteractiveState(running, scanCancellation is not null);
    }

    private void SetScanning(bool scanning)
    {
        ApplyInteractiveState(runCancellation is not null, scanning);
    }

    // Centralized enable/disable logic keeps Scan, Run, Abort, and Help behavior
    // predictable during long terrain operations.
    private void ApplyInteractiveState(bool running, bool scanning)
    {
        bool busy = running || scanning;
        bool locked = busy || scanLocked;
        scanButton.Enabled = !busy && !scanLocked && !scanOverride.Checked;
        runButton.Enabled = !busy && (scanPassed || scanOverride.Checked);
        abortButton.Enabled = busy || scanLocked;
        routePathText.Enabled = !locked;
        appendMode.Enabled = !locked;
        overwriteMode.Enabled = !locked;
        createRouteTiles.Enabled = !locked;
        cleanTileTemplate.Enabled = !locked;
        scanOverride.Enabled = !busy && !scanLocked;
        existingTilesCoverage.Enabled = !locked;
        markerCoverage.Enabled = !locked;
        kmlCoverage.Enabled = !locked;
        trackDatabaseCoverage.Enabled = !locked;
        textFileCoverage.Enabled = !locked;
        terrainRadius.Enabled = !locked && UsesTileRadius();
        distantMountains.Enabled = !locked;
        loTileRadius.Enabled = !locked && distantMountains.Checked;
        postEastWestShiftSlider.Enabled = !locked;
        postEastWestShiftValue.Enabled = !locked;
        postNorthSouthShiftSlider.Enabled = !locked;
        postNorthSouthShiftValue.Enabled = !locked;
        commitPostProcessButton.Enabled = !busy && !scanLocked;
        exitButton.Enabled = !busy;
        contactButton.Enabled = true;
        helpButton.Enabled = true;

        SetButtonPrimary(scanButton, scanButton.Enabled);
        SetButtonPrimary(runButton, runButton.Enabled);
        SetButtonPrimary(abortButton, abortButton.Enabled);
        SetButtonPrimary(exitButton, exitButton.Enabled);
        SetButtonPrimary(commitPostProcessButton, commitPostProcessButton.Enabled);
        SetButtonPrimary(contactButton, true);
        SetButtonPrimary(helpButton, true);
    }

    private void ResetScanState()
    {
        scanPassed = false;
        scanLocked = false;
        SetRunning(runCancellation is not null);
    }

    private bool UsesTileRadius()
    {
        return markerCoverage.Checked || kmlCoverage.Checked || trackDatabaseCoverage.Checked;
    }

    private void UpdateRadiusState()
    {
        terrainRadius.Enabled = runCancellation is null && scanCancellation is null && !scanLocked && UsesTileRadius();
    }

    private void WireScanInvalidation()
    {
        EventHandler invalidate = (_, _) =>
        {
            if (!scanLocked && !scanPassed)
            {
                return;
            }

            ResetScanState();
        };

        routePathText.TextChanged += invalidate;
        appendMode.CheckedChanged += invalidate;
        overwriteMode.CheckedChanged += invalidate;
        createRouteTiles.CheckedChanged += invalidate;
        distantMountains.CheckedChanged += invalidate;
        cleanTileTemplate.CheckedChanged += invalidate;
        scanOverride.CheckedChanged += (_, _) =>
        {
            if (scanOverride.Checked)
            {
                scanPassed = false;
                scanLocked = false;
            }

            SetRunning(runCancellation is not null);
        };
        existingTilesCoverage.CheckedChanged += invalidate;
        markerCoverage.CheckedChanged += invalidate;
        kmlCoverage.CheckedChanged += invalidate;
        trackDatabaseCoverage.CheckedChanged += invalidate;
        textFileCoverage.CheckedChanged += invalidate;
        terrainRadius.ValueChanged += invalidate;
        loTileRadius.ValueChanged += invalidate;
        postEastWestShiftValue.ValueChanged += invalidate;
        postNorthSouthShiftValue.ValueChanged += invalidate;
    }

    private void AppendLog(string text)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(text));
            return;
        }

        logText.AppendText(text);
        logFileWriter?.Write(text);
        logFileWriter?.Flush();
        TrackStatusText(text);
    }

    private static StreamWriter? OpenLogFile()
    {
        try
        {
            string path = Path.Combine(GetUserFacingLogDirectory(), "SCOLIDEX.log");
            StreamWriter writer = new(path, append: false, Encoding.UTF8);
            writer.WriteLine($"SCO LIDEX log started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine();
            return writer;
        }
        catch
        {
            return null;
        }
    }

    private static string GetUserFacingLogDirectory()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return string.IsNullOrWhiteSpace(desktop) ? AppContext.BaseDirectory : desktop;
    }

    private static string LoadVersionText()
    {
        try
        {
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "VERSION"),
                Path.Combine(Environment.CurrentDirectory, "VERSION"),
            ];
            foreach (string path in candidates)
            {
                if (File.Exists(path))
                {
                    string version = File.ReadAllText(path).Trim();
                    return string.IsNullOrWhiteSpace(version) ? "" : version;
                }
            }
        }
        catch
        {
        }

        return "";
    }

    private static Image? LoadTitleImage()
    {
        return LoadContentImage("ScoBull.png");
    }

    private static Image? LoadContentImage(string fileName)
    {
        try
        {
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "content", fileName),
                Path.Combine(AppContext.BaseDirectory, fileName),
                Path.Combine(Environment.CurrentDirectory, "content", fileName),
                Path.Combine(Environment.CurrentDirectory, fileName),
            ];

            foreach (string path in candidates)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                using Image image = Image.FromFile(path);
                return new Bitmap(image);
            }
        }
        catch
        {
        }

        return null;
    }

    private void ResetStatus()
    {
        routeStatus.Reset();
        dmStatus.Reset();
        readingDistantMountainOutput = false;
        activeDmIndex = 0;
        statusLineBuffer.Clear();
        UpdateStatusDisplay();
    }

    private void TrackStatusText(string text)
    {
        foreach (char ch in text)
        {
            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                ProcessStatusLine(statusLineBuffer.ToString());
                statusLineBuffer.Clear();
                continue;
            }

            statusLineBuffer.Append(ch);
        }
    }

    // The engine logs plain text; the GUI listens for stable progress/summary
    // phrases and updates live counters without coupling to engine internals.
    private void ProcessStatusLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Match dmStart = DistantMountainStartRegex().Match(line);
        if (dmStart.Success)
        {
            readingDistantMountainOutput = true;
            dmStatus.Total = ParseNumber(dmStart.Groups["total"].Value);
            UpdateStatusDisplay();
            return;
        }

        Match dmTile = DistantMountainTileRegex().Match(line);
        if (dmTile.Success)
        {
            readingDistantMountainOutput = true;
            activeDmIndex = ParseNumber(dmTile.Groups["index"].Value);
            dmStatus.Total = Math.Max(dmStatus.Total, ParseNumber(dmTile.Groups["total"].Value));
            UpdateStatusDisplay();
            return;
        }

        Match dmPrepared = DistantMountainPreparedRegex().Match(line);
        if (dmPrepared.Success)
        {
            dmStatus.Processed++;
            if (ParseNumber(dmPrepared.Groups["ten"].Value) > 0)
            {
                dmStatus.TenMeter++;
            }

            UpdateStatusDisplay();
            return;
        }

        if (line.Contains("Skipped: lo_tile files already exist", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Skipped: lo_tile raw grid already has", StringComparison.OrdinalIgnoreCase))
        {
            dmStatus.Skipped++;
            UpdateStatusDisplay();
            return;
        }

        if (line.Contains("Distant Mountain generation failed", StringComparison.OrdinalIgnoreCase))
        {
            dmStatus.Failures++;
            UpdateStatusDisplay();
            return;
        }

        Match dmDone = DistantMountainDoneRegex().Match(line);
        if (dmDone.Success)
        {
            dmStatus.Total = ParseNumber(dmDone.Groups["total"].Value);
            dmStatus.Processed = ParseNumber(dmDone.Groups["generated"].Value);
            dmStatus.Skipped = ParseNumber(dmDone.Groups["skipped"].Value);
            dmStatus.Failures = ParseNumber(dmDone.Groups["failed"].Value);
            UpdateStatusDisplay();
            return;
        }

        Match routeTile = RouteTileRegex().Match(line);
        if (routeTile.Success && !readingDistantMountainOutput)
        {
            routeStatus.Total = ParseNumber(routeTile.Groups["total"].Value);
            UpdateStatusDisplay();
            return;
        }

        if (line.Contains("Skipped:", StringComparison.OrdinalIgnoreCase) && !readingDistantMountainOutput)
        {
            routeStatus.Skipped++;
            UpdateStatusDisplay();
            return;
        }

        Match routeSources = RouteSourceSamplesRegex().Match(line);
        if (routeSources.Success)
        {
            routeStatus.Processed++;
            if (ParseNumber(routeSources.Groups["primary"].Value) > 0)
            {
                routeStatus.OneMeter++;
            }

            if (ParseNumber(routeSources.Groups["opr"].Value) > 0)
            {
                routeStatus.Opr++;
            }

            if (ParseNumber(routeSources.Groups["ten"].Value) > 0)
            {
                routeStatus.TenMeter++;
            }

            UpdateStatusDisplay();
            return;
        }

        Match routeProgress = RouteProgressRegex().Match(line);
        if (routeProgress.Success)
        {
            routeStatus.Total = ParseNumber(routeProgress.Groups["total"].Value);
            routeStatus.Processed = ParseNumber(routeProgress.Groups["generated"].Value);
            routeStatus.Skipped = ParseNumber(routeProgress.Groups["skipped"].Value);
            routeStatus.Failures = ParseNumber(routeProgress.Groups["failed"].Value);
            UpdateStatusDisplay();
            return;
        }

        Match done = RouteDoneRegex().Match(line);
        if (done.Success)
        {
            routeStatus.Total = ParseNumber(done.Groups["total"].Value);
            routeStatus.Processed = ParseNumber(done.Groups["generated"].Value);
            routeStatus.Skipped = ParseNumber(done.Groups["skipped"].Value);
            routeStatus.Failures = ParseNumber(done.Groups["failed"].Value);
            UpdateStatusDisplay();
            return;
        }

        Match sourceUseSummary = SourceUseSummaryRegex().Match(line);
        if (sourceUseSummary.Success)
        {
            routeStatus.OneMeter = ParseNumber(sourceUseSummary.Groups["primary"].Value);
            routeStatus.Opr = ParseNumber(sourceUseSummary.Groups["opr"].Value);
            routeStatus.TenMeter = ParseNumber(sourceUseSummary.Groups["ten"].Value);
            UpdateStatusDisplay();
        }
    }

    private void UpdateStatusDisplay()
    {
        tileTotalValue.Text = routeStatus.Total.ToString("N0");
        tileProcessedValue.Text = routeStatus.Processed.ToString("N0");
        tileSkippedValue.Text = routeStatus.Skipped.ToString("N0");
        tileOneMeterValue.Text = routeStatus.OneMeter.ToString("N0");
        tileOprValue.Text = routeStatus.Opr.ToString("N0");
        tileTenMeterValue.Text = routeStatus.TenMeter.ToString("N0");
        tileFailuresValue.Text = routeStatus.Failures.ToString("N0");
        bool showDm = distantMountains.Checked || dmStatus.Total > 0 || dmStatus.Processed > 0 || dmStatus.Skipped > 0 || dmStatus.Failures > 0;
        dmTotalValue.Text = showDm ? dmStatus.Total.ToString("N0") : "";
        dmProcessedValue.Text = showDm ? dmStatus.Processed.ToString("N0") : "";
        dmSkippedValue.Text = showDm ? dmStatus.Skipped.ToString("N0") : "";
        dmOneMeterValue.Text = "";
        dmOprValue.Text = "";
        dmTenMeterValue.Text = showDm ? dmStatus.TenMeter.ToString("N0") : "";
        dmFailuresValue.Text = showDm ? dmStatus.Failures.ToString("N0") : "";
        UpdateStatusColors(showDm);
    }

    private void UpdateStatusColors(bool showDm)
    {
        ColorizeValue(tileTotalValue, routeStatus.Total, MutedTextColor);
        ColorizeValue(tileProcessedValue, routeStatus.Processed, MutedTextColor);
        ColorizeValue(tileOneMeterValue, routeStatus.OneMeter, MutedTextColor);
        ColorizeValue(tileOprValue, routeStatus.Opr, MutedTextColor);
        ColorizeValue(tileTenMeterValue, routeStatus.TenMeter, MutedTextColor);
        ColorizeValue(tileSkippedValue, routeStatus.Skipped, MutedTextColor);
        ColorizeValue(tileFailuresValue, routeStatus.Failures, DangerColor);

        ColorizeValue(dmTotalValue, showDm ? dmStatus.Total : 0, MutedTextColor);
        ColorizeValue(dmProcessedValue, showDm ? dmStatus.Processed : 0, MutedTextColor);
        ColorizeValue(dmSkippedValue, showDm ? dmStatus.Skipped : 0, MutedTextColor);
        ColorizeValue(dmTenMeterValue, showDm ? dmStatus.TenMeter : 0, MutedTextColor);
        ColorizeValue(dmFailuresValue, showDm ? dmStatus.Failures : 0, DangerColor);
    }

    private static void ColorizeValue(Label label, int value, Color positiveColor)
    {
        label.ForeColor = value > 0 ? positiveColor : MutedTextColor;
    }

    private static int ParseNumber(string text)
    {
        return int.TryParse(text.Replace(",", "", StringComparison.Ordinal), out int value) ? value : 0;
    }

    private sealed class UiTextWriter(Action<string> write) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            write(value.ToString());
        }

        public override void Write(string? value)
        {
            if (value is not null)
            {
                write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            write((value ?? "") + Environment.NewLine);
        }
    }

    private sealed class StatusCounters
    {
        public int Total { get; set; }
        public int Processed { get; set; }
        public int Skipped { get; set; }
        public int OneMeter { get; set; }
        public int Opr { get; set; }
        public int TenMeter { get; set; }
        public int Failures { get; set; }

        public void Reset()
        {
            Total = 0;
            Processed = 0;
            Skipped = 0;
            OneMeter = 0;
            Opr = 0;
            TenMeter = 0;
            Failures = 0;
        }
    }

    [GeneratedRegex(@"^\[(?<index>[\d,]+)/(?<total>[\d,]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex RouteTileRegex();

    [GeneratedRegex(@"Progress:\s+(?<processed>[\d,]+)/(?<total>[\d,]+)\s+processed.*?(?<generated>[\d,]+)\s+generated,\s+(?<skipped>[\d,]+)\s+skipped,\s+(?<failed>[\d,]+)\s+failed", RegexOptions.IgnoreCase)]
    private static partial Regex RouteProgressRegex();

    [GeneratedRegex(@"Done\.\s+Generated=(?<generated>[\d,]+),\s+skipped=(?<skipped>[\d,]+),\s+failed=(?<failed>[\d,]+),\s+total=(?<total>[\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RouteDoneRegex();

    [GeneratedRegex(@"Distant Mountains:\s+.*?,\s+radius\s+[\d,]+,\s+(?<total>[\d,]+)\s+lo_tiles", RegexOptions.IgnoreCase)]
    private static partial Regex DistantMountainStartRegex();

    [GeneratedRegex(@"\[DM\s+(?<index>[\d,]+)/(?<total>[\d,]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex DistantMountainTileRegex();

    [GeneratedRegex(@"Prepared TSRE-style lo_tile with 10m=(?<ten>[\d,]+),", RegexOptions.IgnoreCase)]
    private static partial Regex DistantMountainPreparedRegex();

    [GeneratedRegex(@"Distant Mountains done\.\s+Generated=(?<generated>[\d,]+),\s+skipped=(?<skipped>[\d,]+),\s+failed=(?<failed>[\d,]+),\s+total=(?<total>[\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DistantMountainDoneRegex();

    [GeneratedRegex(@"Source samples used:\s+1m=(?<primary>[\d,]+),\s+5m~=(?<opr>[\d,]+),\s+10m=(?<ten>[\d,]+),", RegexOptions.IgnoreCase)]
    private static partial Regex RouteSourceSamplesRegex();

    [GeneratedRegex(@"Source use summary:\s+tiles using 1m=(?<primary>[\d,]+),\s+5m~=(?<opr>[\d,]+),\s+10m=(?<ten>[\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceUseSummaryRegex();

}
