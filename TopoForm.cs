// SCO LIDEX - WinForms interface, workflow orchestration, and operator logging.
// Copyright (C) Scott Brunner, Beast of Burden
// Part of the SCO LIDEX Terrain Builder application.
// Licensed under GNU GPL v3 or later. See LICENSE.txt.

using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ORterr;

internal sealed partial class TopoForm : Form
{
    private static readonly Color AppBackColor = Color.FromArgb(45, 45, 45);
    private static readonly Color HeaderBackColor = Color.FromArgb(38, 38, 38);
    private static readonly Color PanelBackColor = Color.FromArgb(34, 34, 34);
    private static readonly Color InputBackColor = Color.FromArgb(27, 27, 27);
    private static readonly Color TextColor = Color.FromArgb(240, 240, 240);
    private static readonly Color HelpTextColor = Color.FromArgb(205, 205, 205);
    private static readonly Color MutedTextColor = Color.FromArgb(184, 184, 184);
    private static readonly Color AccentColor = Color.FromArgb(226, 178, 126);
    private static readonly Color AccentGreen = Color.FromArgb(205, 132, 52);
    private static readonly Color WarningColor = Color.FromArgb(235, 170, 81);
    private static readonly Color DangerColor = Color.FromArgb(214, 92, 75);
    private static readonly Color ButtonBackColor = Color.FromArgb(52, 52, 52);
    private static readonly Color PrimaryButtonBackColor = Color.FromArgb(58, 58, 58);
    private static readonly Color LogBackColor = Color.FromArgb(24, 24, 24);
    private static readonly Color LogTextColor = Color.FromArgb(202, 202, 202);

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SCOLIDEX");
    private static readonly string LastRoutePathFile = Path.Combine(SettingsDirectory, "last-route.txt");
    private static readonly string RouteHistoryFile = Path.Combine(SettingsDirectory, "route-history.json");
    private const int MaximumRouteHistoryEntries = 5;
    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr windowHandle, string? subAppName, string? subIdList);

    private readonly TextBox routePathText = new();
    private readonly Button routeHistoryButton = new();
    private readonly Button browseRouteButton = new();
    private readonly ContextMenuStrip routeHistoryMenu = new();
    private readonly RadioButton appendMode = new DarkRadioButton();
    private readonly RadioButton overwriteMode = new DarkRadioButton();
    private readonly CheckBox createRouteTiles = new DarkCheckBox();
    private readonly CheckBox distantMountains = new DarkCheckBox();
    private readonly CheckBox createMapTiles = new DarkCheckBox();
    private readonly CheckBox cleanTileTemplate = new DarkCheckBox();
    private readonly CheckBox scanOverride = new DarkCheckBox();
    private readonly CheckBox enableHd4mTiles = new DarkCheckBox();
    private readonly CheckBox enableHdMapTiles = new DarkCheckBox();
    private readonly ToolTip optionToolTip = new();
    private readonly RadioButton normalOutput = new DarkRadioButton();
    private readonly RadioButton experimentalOutput = new DarkRadioButton();
    private readonly RadioButton existingTilesCoverage = new DarkRadioButton();
    private readonly RadioButton markerCoverage = new DarkRadioButton();
    private readonly RadioButton kmlCoverage = new DarkRadioButton();
    private readonly RadioButton trackDatabaseCoverage = new DarkRadioButton();
    private readonly RadioButton textFileCoverage = new DarkRadioButton();
    private readonly DarkNumericInput terrainRadius = new();
    private readonly DarkNumericInput loTileRadius = new();
    private readonly DarkTrackBar postEastWestShiftSlider = new();
    private readonly DarkTrackBar postNorthSouthShiftSlider = new();
    private readonly DarkNumericInput postEastWestShiftValue = new();
    private readonly DarkNumericInput postNorthSouthShiftValue = new();
    private readonly Button commitPostProcessButton = new();
    private readonly Button scanButton = new();
    private readonly Button runButton = new();
    private readonly Button abortButton = new();
    private readonly Button exitButton = new();
    private readonly Button contactButton = new NoFocusEmphasisButton();
    private readonly Button helpButton = new NoFocusEmphasisButton();
    private readonly Label tileTotalValue = new();
    private readonly Label tileProcessedValue = new();
    private readonly Label tileSkippedValue = new();
    private readonly Label tileOneMeterValue = new();
    private readonly Label tileOprValue = new();
    private readonly Label tileTenMeterValue = new();
    private readonly Label tileGlobalValue = new();
    private readonly Label tileFailuresValue = new();
    private readonly Label dmTotalValue = new();
    private readonly Label dmProcessedValue = new();
    private readonly Label dmSkippedValue = new();
    private readonly Label dmOneMeterValue = new();
    private readonly Label dmOprValue = new();
    private readonly Label dmTenMeterValue = new();
    private readonly Label dmGlobalValue = new();
    private readonly Label dmFailuresValue = new();
    private readonly Label globalModeIndicator = new();
    private readonly System.Windows.Forms.Timer statusActivityTimer = new() { Interval = 1000 };
    private readonly TextBox logText = new();
    private readonly StringBuilder statusLineBuffer = new();
    private readonly StatusCounters routeStatus = new();
    private readonly StatusCounters dmStatus = new();
    private readonly Label versionLabel = new();
    private TableLayoutPanel? appShell;
    private bool readingDistantMountainOutput;
    private int activeDmIndex;
    private CancellationTokenSource? runCancellation;
    private CancellationTokenSource? scanCancellation;
    private bool scanPassed;
    private bool scanLocked;
    private bool terrainResolutionForceApproved;
    private Program.ScanSummary? lastScanSummary;
    private TextWriter? previousOut;
    private TextWriter? previousError;
    private StreamWriter? logFileWriter;
    private bool cacheExitDecisionMade;
    private bool operationFailed;
    private bool uiSoundsEnabled;
    private string operationMessage = "";
    private bool operationMessageAnimated;
    private int activityBulletCount;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int enabled = 1;
            _ = DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
    public TopoForm()
    {
        Text = "SCO LIDEX";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 700);
        Size = new Size(1180, 900);
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = AppBackColor;
        ForeColor = TextColor;

        Panel frame = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(4),
            BackColor = AppBackColor,
        };
        frame.Paint += PaintApplicationFrame;
        Controls.Add(frame);

        appShell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = AppBackColor,
        };
        appShell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLayoutPixels(320)));
        appShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        appShell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.Controls.Add(appShell);
        appShell.Controls.Add(BuildBrandRail(), 0, 0);

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(10),
            BackColor = AppBackColor,
            AutoScroll = false,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        appShell.Controls.Add(root, 1, 0);

        root.Controls.Add(BuildRoutePanel());
        root.Controls.Add(BuildOperationalPanel());
        logText.Dock = DockStyle.Fill;
        logText.Multiline = true;
        logText.ReadOnly = true;
        logText.ScrollBars = ScrollBars.Vertical;
        logText.Font = new Font("Consolas", 9f);
        logText.BackColor = LogBackColor;
        logText.ForeColor = LogTextColor;
        logText.BorderStyle = BorderStyle.FixedSingle;
        logText.MinimumSize = new Size(0, ScaleLayoutPixels(180));
        GroupBox logBox = new DarkGroupBox() { Text = "Running Log", Dock = DockStyle.Fill, Margin = new Padding(0) };
        StyleGroupBox(logBox);
        logBox.Controls.Add(logText);
        root.Controls.Add(logBox);
        root.Controls.Add(BuildButtonPanel());

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
            Font = new Font("Segoe UI", 10f, FontStyle.Regular),
            ForeColor = AccentColor,
            Text = "License: GNU GPL v3.0 or later",
            Margin = new Padding(0),
        };
        Label dataSourceTag = new()
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Font = new Font("Segoe UI", 10f, FontStyle.Regular),
            ForeColor = AccentColor,
            Text = "USGS + Copernicus Public Elevation Data",
            Margin = new Padding(0),
        };
        footer.Controls.Add(licenseTag, 0, 0);
        footer.Controls.Add(dataSourceTag, 1, 0);
        root.Controls.Add(footer);

        markerCoverage.CheckedChanged += (_, _) => UpdateRadiusState();
        kmlCoverage.CheckedChanged += (_, _) => UpdateRadiusState();
        trackDatabaseCoverage.CheckedChanged += (_, _) => UpdateRadiusState();
        textFileCoverage.CheckedChanged += (_, _) => UpdateRadiusState();
        distantMountains.CheckedChanged += (_, _) => UpdateRadiusState();
        enableHd4mTiles.CheckedChanged += (_, _) =>
        {
            if (!enableHd4mTiles.Checked)
            {
                normalOutput.Checked = true;
            }
            SetRunning(runCancellation is not null);
        };
        experimentalOutput.CheckedChanged += (_, _) =>
        {
            SetRunning(runCancellation is not null);
        };
        WireScanInvalidation();
        statusActivityTimer.Tick += StatusActivityTimer_Tick;
        FormClosing += TopoForm_FormClosing;
        FormClosed += (_, _) =>
        {
            statusActivityTimer.Dispose();
            optionToolTip.Dispose();
        };
        routePathText.Text = LoadLastRoutePath();
        ResetStatus();
        SetRunning(false);
        WireToggleSounds(this);
        Load += (_, _) => FitWindowToWorkingArea();
        Shown += (_, _) =>
        {
            ApplyDarkNativeTheme(this);
            uiSoundsEnabled = true;
        };
    }

    private int ScalePixels(int pixels)
    {
        return Math.Max(1, pixels);
    }

    private int ScaleLayoutPixels(int pixels)
    {
        return Math.Max(1, (int)Math.Round(pixels * DeviceDpi / 96f));
    }

    private static void ConfigureDarkDialog(Form dialog)
    {
        dialog.HandleCreated += (_, _) => ApplyDarkTitleBar(dialog);
        dialog.Shown += (_, _) => ApplyDarkNativeTheme(dialog);
    }

    private static void ApplyDarkTitleBar(Form form)
    {
        try
        {
            int enabled = 1;
            _ = DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
    private static void ApplyDarkNativeTheme(Control root)
    {
        try
        {
            ApplyDarkNativeThemeCore(root);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static void ApplyDarkNativeThemeCore(Control control)
    {
        control.CreateControl();
        if (control is TextBoxBase or NumericUpDown or ListView)
        {
            _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }

        foreach (Control child in control.Controls)
        {
            ApplyDarkNativeThemeCore(child);
        }
    }
    private void FitWindowToWorkingArea()
    {
        Rectangle workArea = Screen.FromControl(this).WorkingArea;
        MinimumSize = new Size(Math.Min(960, workArea.Width), Math.Min(700, workArea.Height));
        Size = new Size(Math.Min(1180, workArea.Width), Math.Min(900, workArea.Height));
        Location = new Point(
            workArea.Left + ((workArea.Width - Width) / 2),
            workArea.Top + ((workArea.Height - Height) / 2));
    }

    // Build the top half of the form from small panels instead of the designer.
    // That keeps the layout reproducible in source and avoids hidden .resx or
    // designer state when sharing the project publicly.
    private Control BuildBrandRail()
    {
        Panel rail = new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(12),
            BackColor = HeaderBackColor,
        };
        rail.Paint += PaintBrandRail;

        TableLayoutPanel shell = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLayoutPixels(80)));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        TableLayoutPanel identity = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(16, 6, 10, 6),
            BackColor = PanelBackColor,
        };
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        identity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ScaleLayoutPixels(58)));
        identity.RowStyles.Add(new RowStyle(SizeType.Absolute, ScaleLayoutPixels(43)));
        identity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        identity.Paint += PaintBrandIdentity;

        Label brandName = new()
        {
            Dock = DockStyle.Fill,
            Text = "LIDEX",
            Font = new Font("Segoe UI Semibold", 21f, FontStyle.Bold),
            ForeColor = AccentColor,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };
        Label brandSubtitle = new()
        {
            Dock = DockStyle.Fill,
            Text = "OPEN RAILS TERRAIN BUILDER",
            Font = new Font("Segoe UI Semibold", 8f, FontStyle.Regular),
            ForeColor = MutedTextColor,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(1, 1, 0, 0),
        };
        MeshGlobeControl brandGlobe = new()
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(5, 2, 1, 2),
        };
        identity.Controls.Add(brandName, 0, 0);
        identity.Controls.Add(brandSubtitle, 0, 1);
        identity.Controls.Add(brandGlobe, 1, 0);
        identity.SetRowSpan(brandGlobe, 2);
        Control configuration = BuildOptionsPanel();

        TableLayoutPanel utility = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(0, 9, 0, 0),
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };
        utility.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        utility.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        utility.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        utility.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        utility.Paint += PaintUtilityDivider;
        versionLabel.AutoSize = true;
        versionLabel.Anchor = AnchorStyles.None;
        versionLabel.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        versionLabel.ForeColor = MutedTextColor;
        versionLabel.Margin = new Padding(0, 5, 0, 7);
        versionLabel.Text = LoadVersionText();

        contactButton.Text = "Contact";
        contactButton.TabStop = false;
        StyleButton(contactButton, accent: true);
        contactButton.Dock = DockStyle.Fill;
        contactButton.Margin = new Padding(0, 0, 4, 0);
        contactButton.Click += Contact_Click;
        helpButton.Text = "Help";
        helpButton.TabStop = false;
        StyleButton(helpButton, accent: true);
        helpButton.Dock = DockStyle.Fill;
        helpButton.Margin = new Padding(4, 0, 0, 0);
        helpButton.Click += Help_Click;

        utility.Controls.Add(versionLabel, 0, 0);
        utility.SetColumnSpan(versionLabel, 2);
        utility.Controls.Add(contactButton, 0, 1);
        utility.Controls.Add(helpButton, 1, 1);
        shell.Controls.Add(identity, 0, 0);
        shell.Controls.Add(configuration, 0, 1);
        shell.Controls.Add(utility, 0, 2);
        rail.Controls.Add(shell);
        return rail;
    }

    private static void PaintBrandIdentity(object? sender, PaintEventArgs e)
    {
        if (sender is not Control identity || identity.ClientSize.Width < 2)
        {
            return;
        }

        using Pen accent = new(AccentGreen, 5);
        using Pen divider = new(Color.FromArgb(82, 82, 82));
        e.Graphics.DrawLine(accent, 2, 5, 2, identity.ClientSize.Height - 6);
        e.Graphics.DrawLine(divider, 0, identity.ClientSize.Height - 1, identity.ClientSize.Width, identity.ClientSize.Height - 1);
    }

    private static void PaintUtilityDivider(object? sender, PaintEventArgs e)
    {
        if (sender is not Control utility || utility.ClientSize.Width < 2)
        {
            return;
        }

        using Pen divider = new(Color.FromArgb(82, 82, 82));
        e.Graphics.DrawLine(divider, 0, 0, utility.ClientSize.Width, 0);
    }
    private static void PaintApplicationFrame(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel frame || frame.ClientSize.Width < 6 || frame.ClientSize.Height < 6)
        {
            return;
        }

        using Pen copper = new(AccentGreen);
        using Pen graphite = new(Color.FromArgb(82, 82, 82));
        e.Graphics.DrawRectangle(copper, 0, 0, frame.ClientSize.Width - 1, frame.ClientSize.Height - 1);
        e.Graphics.DrawRectangle(graphite, 2, 2, frame.ClientSize.Width - 5, frame.ClientSize.Height - 5);
    }

    private static void PaintBrandRail(object? sender, PaintEventArgs e)
    {
        if (sender is not Panel rail)
        {
            return;
        }

        Rectangle surface = rail.ClientRectangle;
        using SolidBrush plate = new(HeaderBackColor);
        e.Graphics.FillRectangle(plate, surface);
        using Pen edge = new(AccentGreen);
        e.Graphics.DrawLine(edge, surface.Width - 1, 0, surface.Width - 1, surface.Height);
    }
    private Control BuildRoutePanel()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            AutoSize = true,
            Padding = new Padding(0, 2, 0, 6),
            BackColor = AppBackColor,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label { AutoSize = true, Text = "Route Path:", Anchor = AnchorStyles.Left, ForeColor = TextColor }, 0, 0);
        routePathText.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        routePathText.BackColor = InputBackColor;
        routePathText.ForeColor = TextColor;
        routePathText.BorderStyle = BorderStyle.FixedSingle;
        routeHistoryMenu.BackColor = PanelBackColor;
        routeHistoryMenu.ForeColor = TextColor;
        panel.Controls.Add(routePathText, 1, 0);

        routeHistoryButton.Text = "Recent ▼";
        routeHistoryButton.Anchor = AnchorStyles.Left;
        routeHistoryButton.Click += ShowRouteHistory_Click;
        StyleButton(routeHistoryButton, accent: true);
        panel.Controls.Add(routeHistoryButton, 2, 0);

        browseRouteButton.Text = "Browse...";
        browseRouteButton.Anchor = AnchorStyles.Left;
        browseRouteButton.Click += BrowseRoute_Click;
        StyleButton(browseRouteButton, accent: true);
        panel.Controls.Add(browseRouteButton, 3, 0);
        return panel;
    }

    private static void StyleGroupBox(GroupBox box)
    {
        box.BackColor = PanelBackColor;
        box.ForeColor = AccentColor;
        box.Padding = new Padding(8, 6, 8, 8);
    }

    private static void StyleNumericInput(DarkNumericInput input)
    {
        input.BackColor = InputBackColor;
        input.ForeColor = TextColor;
        input.Height = 23;
    }
    private void StyleButton(Button button, bool primary = false, bool accent = false)
    {
        button.AutoSize = false;
        button.MinimumSize = new Size(ScalePixels(78), ScalePixels(28));
        button.Size = button.MinimumSize;
        button.Margin = new Padding(4, 3, 4, 3);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.UseVisualStyleBackColor = false;
        button.Tag = primary ? ButtonEmphasis.Primary : accent ? ButtonEmphasis.Accent : ButtonEmphasis.Neutral;
        button.MouseDown += (_, _) => UiSounds.PlayPress();
        button.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                UiSounds.PlayPress();
            }
        };
        button.EnabledChanged += (_, _) => UpdateButtonVisual(button);
        UpdateButtonVisual(button);
    }

    private static void SetButtonPrimary(Button button, bool primary)
    {
        button.Tag = primary ? ButtonEmphasis.Primary : ButtonEmphasis.Accent;
        UpdateButtonVisual(button);
    }

    private static void SetButtonAccent(Button button)
    {
        button.Tag = ButtonEmphasis.Accent;
        UpdateButtonVisual(button);
    }

    private static void WireToggleSounds(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is CheckBox or RadioButton)
            {
                control.Click += (_, _) => UiSounds.PlayTic();
            }

            if (control.HasChildren)
            {
                WireToggleSounds(control);
            }
        }
    }

    private static void UpdateButtonVisual(Button button)
    {
        ButtonEmphasis emphasis = button.Tag is ButtonEmphasis value ? value : ButtonEmphasis.Neutral;
        if (button.Enabled)
        {
            button.BackColor = emphasis == ButtonEmphasis.Primary ? PrimaryButtonBackColor : ButtonBackColor;
            button.ForeColor = TextColor;
            button.FlatAppearance.BorderSize = emphasis == ButtonEmphasis.Primary ? 2 : 1;
            button.FlatAppearance.BorderColor = emphasis switch
            {
                ButtonEmphasis.Primary => AccentGreen,
                ButtonEmphasis.Accent => Color.FromArgb(173, 111, 45),
                _ => Color.FromArgb(92, 92, 92),
            };
            return;
        }

        button.BackColor = Color.FromArgb(43, 43, 43);
        button.ForeColor = Color.FromArgb(112, 112, 112);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(72, 72, 72);
    }

    private Control BuildOptionsPanel()
    {
        Panel panel = new()
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            Padding = new Padding(8, 4, 8, 4),
            BackColor = Color.Transparent,
        };
        GroupBox modeBox = new DarkGroupBox() { Text = "Mode + Terrain Output" };
        StyleGroupBox(modeBox);
        TableLayoutPanel modeAndOutput = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(6, 7, 0, 0),
            BackColor = PanelBackColor,
            ForeColor = TextColor,
        };
        modeAndOutput.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        modeAndOutput.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        FlowLayoutPanel modeFlow = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = PanelBackColor, ForeColor = TextColor };
        FlowLayoutPanel outputFlow = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = PanelBackColor, ForeColor = TextColor };
        appendMode.Text = "Append";
        appendMode.AutoSize = true;
        appendMode.Checked = true;
        overwriteMode.Text = "Overwrite";
        overwriteMode.AutoSize = true;
        normalOutput.Text = "Normal - 8m Tiles";
        normalOutput.AutoSize = true;
        normalOutput.Checked = true;
        normalOutput.Enabled = true;
        experimentalOutput.Text = "HD Test - 4m Tiles";
        experimentalOutput.AutoSize = true;
        experimentalOutput.Enabled = true;
        modeFlow.Controls.Add(appendMode);
        modeFlow.Controls.Add(overwriteMode);
        outputFlow.Controls.Add(normalOutput);
        outputFlow.Controls.Add(experimentalOutput);
        modeAndOutput.Controls.Add(modeFlow, 0, 0);
        modeAndOutput.Controls.Add(outputFlow, 1, 0);
        modeBox.Controls.Add(modeAndOutput);

        GroupBox optionBox = new DarkGroupBox() { Text = "Build Options" };
        StyleGroupBox(optionBox);
        TableLayoutPanel optionPanel = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Padding = new Padding(6, 7, 2, 5),
            BackColor = PanelBackColor,
            ForeColor = TextColor,
        };
        optionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int row = 0; row < optionPanel.RowCount; row++)
        {
            optionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        }
        createRouteTiles.Text = "Route Tiles";
        createRouteTiles.AutoSize = true;
        createRouteTiles.Checked = true;
        distantMountains.Text = "DM Tiles";
        distantMountains.Checked = true;
        distantMountains.AutoSize = true;
        createMapTiles.Text = "OSM / Map Tiles";
        createMapTiles.Checked = true;
        createMapTiles.AutoSize = true;
        createMapTiles.Enabled = true;
        cleanTileTemplate.Text = "Clean Tile Wipe (Destructive)";
        cleanTileTemplate.AutoSize = true;
        scanOverride.Text = "Scan Override";
        scanOverride.AutoSize = true;
        enableHd4mTiles.Text = "HD Mesh Tiles";
        enableHd4mTiles.AutoSize = true;
        enableHd4mTiles.Checked = false;
        enableHdMapTiles.Text = "HD Map Tiles";
        enableHdMapTiles.AutoSize = true;
        enableHdMapTiles.Checked = false;
        optionToolTip.SetToolTip(createRouteTiles,
            "Create or update selected route terrain tiles.");
        optionToolTip.SetToolTip(distantMountains,
            "Create or update selected Distant Mountain tiles.");
        optionToolTip.SetToolTip(createMapTiles,
            "Create 2048 x 2048 OSM map tiles.");
        optionToolTip.SetToolTip(cleanTileTemplate,
            "Destructive: rebuild selected terrain from clean templates.");
        optionToolTip.SetToolTip(scanOverride,
            "Run without a successful Scan.");
        optionToolTip.SetToolTip(enableHd4mTiles,
            "Unlock 4m mesh output; otherwise terrain stays at 8m.");
        optionToolTip.SetToolTip(enableHdMapTiles,
            "Create 4096 x 4096 map tiles instead of 2048 x 2048.");
        CheckBox[] buildOptionControls =
        [
            createRouteTiles,
            distantMountains,
            createMapTiles,
            enableHdMapTiles,
            enableHd4mTiles,
            scanOverride,
            cleanTileTemplate,
        ];
        foreach (CheckBox option in buildOptionControls)
        {
            option.AutoSize = false;
            option.Dock = DockStyle.Fill;
            option.TextAlign = ContentAlignment.MiddleLeft;
            option.Margin = new Padding(3);
        }

        optionPanel.Controls.Add(createRouteTiles, 0, 0);
        optionPanel.Controls.Add(enableHd4mTiles, 1, 0);
        optionPanel.Controls.Add(distantMountains, 0, 1);
        optionPanel.Controls.Add(createMapTiles, 0, 2);
        optionPanel.Controls.Add(enableHdMapTiles, 1, 2);
        optionPanel.Controls.Add(scanOverride, 0, 3);
        optionPanel.SetColumnSpan(scanOverride, 2);
        optionPanel.Controls.Add(cleanTileTemplate, 0, 4);
        optionPanel.SetColumnSpan(cleanTileTemplate, 2);
        optionBox.Controls.Add(optionPanel);

        GroupBox coverageBox = new DarkGroupBox() { Text = "Selection" };
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
        trackDatabaseCoverage.Margin = new Padding(3, 3, 3, 6);
        textFileCoverage.Text = "Use Text File";
        textFileCoverage.AutoSize = true;
        terrainRadius.Minimum = 0;
        terrainRadius.Maximum = 100;
        terrainRadius.Value = 2;
        terrainRadius.Enabled = false;
        loTileRadius.Minimum = 1;
        loTileRadius.Maximum = 100;
        loTileRadius.Value = 1;
        loTileRadius.Enabled = false;
        StyleNumericInput(terrainRadius);
        StyleNumericInput(loTileRadius);

        coverage.Controls.Add(existingTilesCoverage, 0, 0);
        coverage.SetColumnSpan(existingTilesCoverage, 2);
        coverage.Controls.Add(textFileCoverage, 0, 1);
        coverage.SetColumnSpan(textFileCoverage, 2);
        Label separator = new()
        {
            AutoSize = false,
            BorderStyle = BorderStyle.Fixed3D,
            Height = 2,
            Margin = new Padding(0, 4, 0, 4),
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(70, 70, 70),
        };
        coverage.Controls.Add(separator, 0, 2);
        coverage.SetColumnSpan(separator, 2);
        coverage.Controls.Add(markerCoverage, 0, 3);
        coverage.SetColumnSpan(markerCoverage, 2);
        coverage.Controls.Add(kmlCoverage, 0, 4);
        coverage.SetColumnSpan(kmlCoverage, 2);
        coverage.Controls.Add(trackDatabaseCoverage, 0, 5);
        coverage.SetColumnSpan(trackDatabaseCoverage, 2);
        coverage.Controls.Add(new Label { AutoSize = true, Text = "Tile Radius:", Margin = new Padding(3, 4, 3, 3) }, 0, 6);
        terrainRadius.Margin = new Padding(3, 4, 3, 3);
        coverage.Controls.Add(terrainRadius, 1, 6);
        coverage.Controls.Add(new Label { AutoSize = true, Text = "DM Radius:" }, 0, 7);
        coverage.Controls.Add(loTileRadius, 1, 7);
        optionToolTip.SetToolTip(terrainRadius,
            "Expands selected coverage by this many tiles in every direction. Suggested: 2.");
        optionToolTip.SetToolTip(loTileRadius,
            "Expands Distant Mountain coverage by this many DM tiles in every direction. Suggested: 1.");

        coverageBox.Controls.Add(coverage);

        modeBox.Margin = new Padding(0, 0, 0, 8);
        coverageBox.Margin = new Padding(0, 0, 0, 8);
        optionBox.Margin = new Padding(0, 0, 0, 4);
        panel.Controls.Add(modeBox);
        panel.Controls.Add(coverageBox);
        panel.Controls.Add(optionBox);

        void SizeConfigurationCards()
        {
            int gap = ScaleLayoutPixels(6);
            int modeHeight = ScaleLayoutPixels(88);
            int coverageHeight = ScaleLayoutPixels(230);
            int optionHeight = ScaleLayoutPixels(153);
            int cardWidth = Math.Max(
                ScaleLayoutPixels(230),
                panel.ClientSize.Width - panel.Padding.Horizontal);
            int x = panel.Padding.Left;
            int y = panel.Padding.Top;
            modeBox.SetBounds(x, y, cardWidth, modeHeight);
            y += modeHeight + gap;
            coverageBox.SetBounds(x, y, cardWidth, coverageHeight);
            y += coverageHeight + gap;
            optionBox.SetBounds(x, y, cardWidth, optionHeight);
        }
        panel.Resize += (_, _) => SizeConfigurationCards();
        panel.HandleCreated += (_, _) => SizeConfigurationCards();
        return panel;
    }

    private Control BuildOperationalPanel()
    {
        Panel panel = new()
        {
            Dock = DockStyle.Top,
            Height = ScaleLayoutPixels(216),
            MinimumSize = new Size(0, ScaleLayoutPixels(216)),
            Margin = new Padding(0, 0, 0, 7),
            BackColor = AppBackColor,
        };
        Control statusPanel = BuildStatusPanel();
        Control postProcessPanel = BuildPostProcessPanel();
        panel.Controls.Add(statusPanel);
        panel.Controls.Add(postProcessPanel);

        void LayoutOperationalCards()
        {
            int gap = ScaleLayoutPixels(8);
            int statusHeight = ScaleLayoutPixels(216);
            int biasHeight = ScaleLayoutPixels(152);
            if (panel.ClientSize.Width >= ScaleLayoutPixels(620))
            {
                int statusWidth = (int)Math.Round((panel.ClientSize.Width - gap) * 0.52);
                panel.Height = statusHeight;
                panel.MinimumSize = new Size(0, statusHeight);
                statusPanel.SetBounds(0, 0, statusWidth, statusHeight);
                postProcessPanel.SetBounds(statusWidth + gap, 0, panel.ClientSize.Width - statusWidth - gap, statusHeight);
                return;
            }

            int stackedHeight = statusHeight + gap + biasHeight;
            panel.Height = stackedHeight;
            panel.MinimumSize = new Size(0, stackedHeight);
            statusPanel.SetBounds(0, 0, panel.ClientSize.Width, statusHeight);
            postProcessPanel.SetBounds(0, statusHeight + gap, panel.ClientSize.Width, biasHeight);
        }

        panel.Resize += (_, _) => LayoutOperationalCards();
        panel.HandleCreated += (_, _) => LayoutOperationalCards();
        return panel;
    }
    // Bias controls serve two purposes: during Run they shift where DEM samples
    // are taken from; during Commit/Post Processing they resample existing
    // terrain only, which is faster but slightly less faithful than rerunning DEM.
    private Control BuildPostProcessPanel()
    {
        GroupBox postBox = new DarkGroupBox() { Text = "Advanced Geo Bias" };
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
        shift.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shift.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        ConfigureBiasControls(postEastWestShiftSlider, postEastWestShiftValue);
        ConfigureBiasControls(postNorthSouthShiftSlider, postNorthSouthShiftValue);
        AddBiasRow(shift, 0, "N", "S", postNorthSouthShiftSlider, postNorthSouthShiftValue);
        AddBiasRow(shift, 1, "E", "W", postEastWestShiftSlider, postEastWestShiftValue);

        commitPostProcessButton.Text = "Commit";
        StyleButton(commitPostProcessButton, accent: true);
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
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
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

    private void ConfigureBiasControls(DarkTrackBar slider, DarkNumericInput value)
    {
        slider.Minimum = -100;
        slider.Maximum = 100;
        slider.TickFrequency = 25;
        slider.SmallChange = 1;
        slider.LargeChange = 5;
        slider.Value = 0;
        slider.AutoSize = false;
        slider.Height = ScalePixels(28);
        slider.Width = ScalePixels(220);
        slider.Anchor = AnchorStyles.Left | AnchorStyles.Right;

        value.Minimum = -100;
        value.Maximum = 100;
        value.Increment = 1;
        value.Value = 0;
        value.Width = ScalePixels(58);
        value.TextAlign = HorizontalAlignment.Right;
        StyleNumericInput(value);
        slider.BackColor = PanelBackColor;

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

    private static void AddBiasRow(TableLayoutPanel bias, int row, string positiveDirection, string negativeDirection, DarkTrackBar slider, DarkNumericInput value)
    {
        bias.Controls.Add(new Label { AutoSize = true, Text = positiveDirection, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) }, 0, row);
        bias.Controls.Add(slider, 1, row);
        bias.Controls.Add(new Label { AutoSize = true, Text = negativeDirection, Anchor = AnchorStyles.Left, Margin = new Padding(6, 6, 12, 3) }, 2, row);
        bias.Controls.Add(value, 3, row);
        bias.Controls.Add(new Label { AutoSize = true, Text = "m", Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 4, row);
    }

    private Control BuildStatusPanel()
    {
        GroupBox statusBox = new DarkGroupBox() { Text = "Status" };
        StyleGroupBox(statusBox);
        TableLayoutPanel status = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 9,
            AutoSize = false,
            Padding = new Padding(6, 4, 6, 4),
            BackColor = PanelBackColor,
            ForeColor = TextColor,
        };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
        for (int row = 0; row < 9; row++)
        {
            status.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 9f));
        }

        AddStatusHeader(status, "Tiles", 1);
        AddStatusHeader(status, "DM Tiles", 2);
        AddStatusRow(status, 1, "Total", tileTotalValue, dmTotalValue);
        AddStatusRow(status, 2, "Proc", tileProcessedValue, dmProcessedValue);
        AddStatusRow(status, 3, "• 1m", tileOneMeterValue, dmOneMeterValue, indentTitle: true);
        AddStatusRow(status, 4, "• 5m~", tileOprValue, dmOprValue, indentTitle: true);
        AddStatusRow(status, 5, "• 10m", tileTenMeterValue, dmTenMeterValue, indentTitle: true);
        dmTenMeterValue.Visible = false;
        AddStatusRow(status, 6, "• 30m (global)", tileGlobalValue, dmGlobalValue, indentTitle: true);
        AddStatusRow(status, 7, "Skip", tileSkippedValue, dmSkippedValue);
        AddStatusRow(status, 8, "Fail", tileFailuresValue, dmFailuresValue);
        statusBox.Controls.Add(status);
        return statusBox;
    }

    private void AddStatusHeader(TableLayoutPanel status, string text, int column)
    {
        Label label = new()
        {
            AutoSize = true,
            Text = text,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
            ForeColor = AccentColor,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 0, 3, 0),
        };
        status.Controls.Add(label, column, 0);
    }

    private void AddStatusRow(TableLayoutPanel status, int row, string title, Label tileValue, Label dmValue, bool indentTitle = false)
    {
        status.Controls.Add(new Label { AutoSize = true, Text = title, Anchor = AnchorStyles.Left, ForeColor = TextColor, Margin = indentTitle ? new Padding(16, 0, 3, 0) : new Padding(3, 0, 3, 0) }, 0, row);
        ConfigureStatusValue(tileValue);
        ConfigureStatusValue(dmValue);
        status.Controls.Add(tileValue, 1, row);
        status.Controls.Add(dmValue, 2, row);
    }

    private void ConfigureStatusValue(Label label)
    {
        label.AutoSize = true;
        label.Text = "0";
        label.Font = new Font("Consolas", 9f);
        label.ForeColor = MutedTextColor;
        label.Anchor = AnchorStyles.Left;
        label.Margin = new Padding(3, 0, 3, 0);
    }

    private Control BuildButtonPanel()
    {
        TableLayoutPanel panel = new()
        {
            Dock = DockStyle.Top,
            Padding = new Padding(0, 5, 0, 5),
            AutoSize = true,
            ColumnCount = 2,
            BackColor = AppBackColor,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        FlowLayoutPanel leftButtons = new()
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            WrapContents = false,
            BackColor = AppBackColor,
        };
        scanButton.Text = "Scan";
        StyleButton(scanButton, accent: true);
        scanButton.Click += Scan_Click;
        runButton.Text = "Run";
        StyleButton(runButton, accent: true);
        runButton.Click += Run_Click;
        abortButton.Text = "Abort";
        StyleButton(abortButton, accent: true);
        abortButton.Click += Abort_Click;
        exitButton.Text = "Exit";
        StyleButton(exitButton, accent: true);
        exitButton.Click += (_, _) => Close();
        leftButtons.Controls.Add(scanButton);
        leftButtons.Controls.Add(runButton);
        leftButtons.Controls.Add(abortButton);
        leftButtons.Controls.Add(exitButton);
        globalModeIndicator.AutoSize = false;
        globalModeIndicator.Size = new Size(ScalePixels(230), ScalePixels(28));
        globalModeIndicator.MinimumSize = globalModeIndicator.Size;
        globalModeIndicator.Text = "READY";
        globalModeIndicator.TextAlign = ContentAlignment.MiddleCenter;
        globalModeIndicator.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
        globalModeIndicator.ForeColor = WarningColor;
        globalModeIndicator.BackColor = HeaderBackColor;
        globalModeIndicator.BorderStyle = BorderStyle.FixedSingle;
        globalModeIndicator.Margin = new Padding(4, 3, 4, 3);
        globalModeIndicator.Anchor = AnchorStyles.None;
        globalModeIndicator.Visible = true;

        panel.Controls.Add(leftButtons, 0, 0);
        panel.Controls.Add(globalModeIndicator, 1, 0);
        return panel;
    }

    private void Contact_Click(object? sender, EventArgs e)
    {
        using Image? contactImage = LoadContentImage("Contact.png");
        if (contactImage is null)
        {
            StyledMessageDialog.Show(this, "Could not find Contact.png.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        ConfigureDarkDialog(dialog);
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
            Path.Combine(AppContext.BaseDirectory, "docsMaster", "INSTRUCTIONS.txt"),
            Path.Combine(AppContext.BaseDirectory, "..", "INSTRUCTIONS.txt"),
            Path.Combine(AppContext.BaseDirectory, "..", "docsMaster", "INSTRUCTIONS.txt"),
            Path.Combine(Environment.CurrentDirectory, "INSTRUCTIONS.txt"),
            Path.Combine(Environment.CurrentDirectory, "docsMaster", "INSTRUCTIONS.txt"),
        ];

        string? helpPath = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (helpPath is null)
        {
            StyledMessageDialog.Show(this, "Could not find INSTRUCTIONS.txt.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            ShowReadOnlyTextDocument("SCO LIDEX Instructions", helpPath);
        }
        catch (Exception ex)
        {
            StyledMessageDialog.Show(this, $"Could not open INSTRUCTIONS.txt:{Environment.NewLine}{ex.Message}", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        ConfigureDarkDialog(dialog);
        RichTextBox documentText = new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            WordWrap = true,
            DetectUrls = false,
            Font = new Font("Segoe UI", 11.5f),
            BackColor = LogBackColor,
            ForeColor = HelpTextColor,
            BorderStyle = BorderStyle.FixedSingle,
            ShortcutsEnabled = true,
            Margin = new Padding(12),
        };
        FormatHelpDocument(documentText, text);

        Panel documentFrame = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            BackColor = AppBackColor,
        };
        documentFrame.Controls.Add(documentText);

        Button closeButton = new()
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Right,
            Width = 86,
            Height = 28,
        };
        StyleButton(closeButton, accent: true);

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
        dialog.Controls.Add(documentFrame);
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
        box.SelectionFont = new Font("Segoe UI", 11.5f);

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
                AppendHelpIndentedText(box, trimmed + Environment.NewLine, CreateHelpBoldFont(11.5f), HelpTextColor);
                continue;
            }

            if (IsExampleLine(line))
            {
                AppendHelpExample(box, trimmed);
                continue;
            }

            StringBuilder paragraph = new(trimmed);
            while (i + 1 < lines.Length)
            {
                string candidateLine = lines[i + 1];
                string candidateNext = i + 2 < lines.Length ? lines[i + 2].Trim() : "";
                if (IsHelpStructuralLine(candidateLine, candidateNext))
                {
                    break;
                }

                paragraph.Append(' ').Append(candidateLine.Trim());
                i++;
            }

            AppendHelpParagraph(box, paragraph.ToString());
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

    private static bool IsHelpStructuralLine(string line, string nextLine)
    {
        string trimmed = line.Trim();
        return trimmed.Length == 0 ||
            IsRuleLine(trimmed) ||
            IsRuleLine(nextLine) ||
            trimmed.StartsWith("- ", StringComparison.Ordinal) ||
            (Regex.IsMatch(trimmed, @"^\d+\.\s+") && line.StartsWith("  ", StringComparison.Ordinal)) ||
            (trimmed.EndsWith(':') && trimmed.Length <= 48 && !trimmed.Contains('\\')) ||
            IsExampleLine(line);
    }

    private void AppendHelpHeading(RichTextBox box, string text, bool title)
    {
        if (box.TextLength > 0)
        {
            AppendHelpText(box, Environment.NewLine, new Font("Segoe UI", 4f), HelpTextColor);
        }

        Font font = title
            ? CreateHelpBoldFont(20f)
            : CreateHelpBoldFont(14f);
        AppendHelpIndentedText(box, text + Environment.NewLine, font, AccentColor);
        if (!title)
        {
            AppendHelpIndentedText(box, new string('-', 48) + Environment.NewLine, new Font("Segoe UI", 7f), Color.FromArgb(210, 204, 194));
        }
    }

    private void AppendHelpParagraph(RichTextBox box, string text)
    {
        AppendHelpIndentedText(box, text + Environment.NewLine, new Font("Segoe UI", 11.5f), HelpTextColor);
    }

    private void AppendHelpBullet(RichTextBox box, string text)
    {
        box.SelectionBullet = true;
        box.SelectionIndent = 38;
        box.SelectionRightIndent = 10;
        box.SelectionHangingIndent = 8;
        AppendHelpText(box, text + Environment.NewLine, new Font("Segoe UI", 11.5f), HelpTextColor);
        box.SelectionBullet = false;
        box.SelectionIndent = 0;
        box.SelectionRightIndent = 0;
        box.SelectionHangingIndent = 0;
    }

    private void AppendHelpExample(RichTextBox box, string text)
    {
        box.SelectionIndent = 32;
        box.SelectionRightIndent = 10;
        AppendHelpText(box, text + Environment.NewLine, new Font("Consolas", 10.5f), HelpTextColor);
        box.SelectionIndent = 0;
        box.SelectionRightIndent = 0;
    }

    private static void AppendHelpIndentedText(RichTextBox box, string text, Font font, Color color)
    {
        box.SelectionIndent = 14;
        box.SelectionRightIndent = 10;
        AppendHelpText(box, text, font, color);
        box.SelectionIndent = 0;
        box.SelectionRightIndent = 0;
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

    private bool EnsureTerrainResolutionCompatibility(
        string routePath,
        out string logEntry)
    {
        logEntry = "";
        if (!createRouteTiles.Checked)
        {
            terrainResolutionForceApproved = false;
            return true;
        }

        Program.TerrainOutputResolution selectedResolution =
            experimentalOutput.Checked
                ? Program.TerrainOutputResolution.HdTest4m
                : Program.TerrainOutputResolution.Normal8m;
        Program.TerrainResolutionInspection inspection;
        try
        {
            inspection = Program.InspectTerrainResolutions(
                routePath, selectedResolution);
        }
        catch (Exception ex)
        {
            StyledMessageDialog.Show(
                this,
                $"Terrain resolution could not be inspected:\n\n{ex.Message}",
                "SCO LIDEX - Terrain Resolution",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        if (inspection.UnrecognizedTiles.Count > 0)
        {
            string details = string.Join(
                Environment.NewLine,
                inspection.UnrecognizedTiles.Take(12)
                    .Select(tile => $"  • {tile.TileName}: {tile.Detail}"));
            string remainder = inspection.UnrecognizedTiles.Count > 12
                ? $"{Environment.NewLine}  • ...and {inspection.UnrecognizedTiles.Count - 12:N0} more"
                : "";
            StyledMessageDialog.Show(
                this,
                $"LIDEX cannot safely determine the resolution of " +
                $"{inspection.UnrecognizedTiles.Count:N0} terrain tile(s):\n\n" +
                details + remainder + "\n\nRepair or replace these tiles before Scan. " +
                "No files were changed.",
                "SCO LIDEX - Terrain Resolution Stopped",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return false;
        }

        string selectedLabel = Program.TerrainOutputLabel(selectedResolution);
        if (inspection.MismatchedTiles.Count == 0)
        {
            terrainResolutionForceApproved = false;
            logEntry =
                $"Terrain resolution preflight: {inspection.MatchingTiles:N0} route tile(s) match {selectedLabel}." +
                Environment.NewLine + Environment.NewLine;
            return true;
        }

        string mismatchDetails = string.Join(
            Environment.NewLine,
            inspection.MismatchedTiles.Take(12)
                .Select(tile => $"  • {tile.TileName}: {tile.DetectedLabel}"));
        string mismatchRemainder = inspection.MismatchedTiles.Count > 12
            ? $"{Environment.NewLine}  • ...and {inspection.MismatchedTiles.Count - 12:N0} more"
            : "";
        DialogResult confirm = StyledMessageDialog.Show(
            this,
            $"The route contains {inspection.MismatchedTiles.Count:N0} terrain tile(s) " +
            $"that do not match the selected output:\n\n" +
            $"Selected: {selectedLabel}\n\n" + mismatchDetails + mismatchRemainder +
            "\n\nMixed 8m and 4m terrain is not permitted. If you continue, Run will " +
            $"force every mismatched tile to {selectedLabel}. Its incompatible " +
            "height grid will be rebuilt in place; terrain coordinates, textures, " +
            "world files, and route coverage will be preserved. Scan itself will " +
            "not change the route.\n\n" +
            "This route-wide correction applies regardless of the current mode or " +
            "coverage selection. Create Route Tiles and Use Route Tiles will be selected.\n\n" +
            "Proceed?",
            "SCO LIDEX - Terrain Resolution Mismatch",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return false;
        }

        createRouteTiles.Checked = true;
        existingTilesCoverage.Checked = true;
        terrainResolutionForceApproved = true;
        logEntry =
            $"Terrain resolution preflight: approved route-wide forcing of " +
            $"{inspection.MismatchedTiles.Count:N0} mismatched tile(s) to {selectedLabel}. " +
            "Scan remains read-only; Run will rebuild the mismatches." +
            Environment.NewLine + Environment.NewLine;
        return true;
    }

    // Scan is read-only after the explicit route-wide resolution preflight.
    // A passing scan locks the settings so Run uses the validated selection.
    private async void Scan_Click(object? sender, EventArgs e)
    {
        string routePath = NormalizeRoutePath(routePathText.Text);
        if (string.IsNullOrWhiteSpace(routePath) || !Directory.Exists(routePath))
        {
            StyledMessageDialog.Show(this, "Select a valid route folder first.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        routePathText.Text = routePath;
        if (!EnsureTerrainResolutionCompatibility(routePath, out string resolutionLog))
        {
            return;
        }

        operationFailed = false;
        SaveLastRoutePath(routePath);
        logText.Clear();
        ResetStatus();
        SetOperationMessage("SCANNING");
        scanPassed = false;
        lastScanSummary = null;
        scanLocked = true;
        SetScanning(true);
        scanCancellation = new CancellationTokenSource();
        previousOut = Console.Out;
        previousError = Console.Error;
        logFileWriter = OpenLogFile();
        WriteRunSettingsHeader(routePath, "Scan");
        AppendLog(resolutionLog);
        using TextWriter writer = new UiTextWriter(AppendLog);
        Console.SetOut(writer);
        Console.SetError(writer);

        try
        {
            Program.ScanOptions options = new(
                CreateRouteTiles: createRouteTiles.Checked,
                CreateDistantMountains: distantMountains.Checked,
                CreateMapTiles: createMapTiles.Checked,
                HdMapTiles: enableHdMapTiles.Checked,
                MarkerCoverage: markerCoverage.Checked,
                TrackDatabaseCoverage: trackDatabaseCoverage.Checked,
                KmlCoverage: kmlCoverage.Checked,
                TextFileCoverage: textFileCoverage.Checked,
                CleanTileWipe: cleanTileTemplate.Checked,
                TerrainRadius: (int)terrainRadius.Value,
                LoTileRadius: (int)loTileRadius.Value,
                Hd4mOutput: experimentalOutput.Checked,
                ForceResolutionMismatches: terrainResolutionForceApproved);

            Program.ScanSummary summary = await Task.Run(() => Program.ScanRouteAsync(routePath, options, scanCancellation.Token));
            routeStatus.Total = summary.RouteTileTotal;
            routeStatus.Failures = summary.UnreadableRouteTiles;
            dmStatus.Total = summary.DistantMountainTotal;
            dmStatus.Failures = summary.UnreadableDistantMountainTiles;
            UpdateStatusDisplay();
            lastScanSummary = summary;
            scanPassed = summary.CanRun;
            SetOperationMessage(scanPassed
                ? summary.HasWarnings ? "SCAN COMPLETE - WARNINGS" : "SCAN COMPLETE"
                : "SCAN FAILED");
            AppendLog(scanPassed
                ? summary.HasWarnings
                    ? $"{Environment.NewLine}Scan passed with source warnings. Run is enabled for the viable stages shown in the Run plan; failed sources will not be polled.{Environment.NewLine}"
                    : $"{Environment.NewLine}Scan passed. Run is enabled. Use Abort to unlock and change settings.{Environment.NewLine}"
                : $"{Environment.NewLine}Scan failed. Fix blocking issues, then scan again.{Environment.NewLine}");
        }
        catch (OperationCanceledException)
        {
            SetOperationMessage("SCAN ABORTED");
            AppendLog($"{Environment.NewLine}Scan aborted. Settings unlocked.{Environment.NewLine}");
            ResetScanState();
        }
        catch (Exception ex)
        {
            SetOperationMessage("SCAN FAILED");
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
            if (runButton.Enabled)
            {
                runButton.Select();
            }
            else if (scanButton.Enabled)
            {
                scanButton.Select();
            }
            else if (abortButton.Enabled)
            {
                abortButton.Select();
            }
        }
    }

    // Run redirects the engine's Console output into both the GUI log window and
    // the desktop log file. The engine remains usable from the CLI for testing.
    private async void Run_Click(object? sender, EventArgs e)
    {
        if (!scanPassed && !scanOverride.Checked)
        {
            StyledMessageDialog.Show(this, "Run requires a passing Scan first.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string routePath = NormalizeRoutePath(routePathText.Text);
        string runResolutionLog = "";
        if (string.IsNullOrWhiteSpace(routePath) || !Directory.Exists(routePath))
        {
            StyledMessageDialog.Show(this, "Select a valid route folder first.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        routePathText.Text = routePath;
        if (scanOverride.Checked &&
            !EnsureTerrainResolutionCompatibility(routePath, out runResolutionLog))
        {
            return;
        }

        if (experimentalOutput.Checked)
        {
            DialogResult experimentalConfirm = StyledMessageDialog.Show(
                this,
                "Create HD Test - 4m Tiles?" + Environment.NewLine + Environment.NewLine +
                "Every selected normal terrain tile requiring generation will be built as a 512x512, 4m grid. " +
                "Distant Mountains and maps will run when their Create options are checked. " +
                "The 4m terrain format requires the matching Open Rails development build." +
                Environment.NewLine + Environment.NewLine + "Use only on a backed-up test route.",
                "SCO LIDEX - HD Test Terrain",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (experimentalConfirm != DialogResult.OK)
            {
                return;
            }
        }

        operationFailed = false;
        SetOperationMessage("STARTING OPERATION");
        SaveLastRoutePath(routePath);
        SetRunning(true);
        logText.Clear();
        runCancellation = new CancellationTokenSource();
        previousOut = Console.Out;
        previousError = Console.Error;
        logFileWriter = OpenLogFile();
        WriteRunSettingsHeader(routePath, "Run");
        AppendLog(runResolutionLog);
        using TextWriter writer = new UiTextWriter(AppendLog);
        Console.SetOut(writer);
        Console.SetError(writer);
        Program.ResetUsgsDataCounter();
        Program.ResetCopernicusDataCounter();
        Stopwatch runTimer = Stopwatch.StartNew();

        try
        {
            string[] args = BuildRunArguments(routePath);
            await Task.Run(() => Program.RunConsoleAsync(args, runCancellation.Token));
        }
        catch (OperationCanceledException)
        {
            SetOperationMessage("OPERATION ABORTED");
            AppendLog(
                $"{Environment.NewLine}OPERATION ABORTED{Environment.NewLine}" +
                $"-----------------{Environment.NewLine}" +
                $"  RESULT: STOPPED SAFELY at the next available operation boundary.{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            SetOperationMessage("OPERATION FAILED");
            AppendLog($"Error: {ex.Message}{Environment.NewLine}");
        }
        finally
        {
            runTimer.Stop();
            AppendLog(
                $"{Environment.NewLine}RUN TOTALS{Environment.NewLine}" +
                $"----------{Environment.NewLine}" +
                $"  ELAPSED: {FormatElapsed(runTimer.Elapsed)}{Environment.NewLine}" +
                $"  USGS DATA READ: {Program.FormatUsgsDataBytesRead()}{Environment.NewLine}" +
                $"  COPERNICUS DATA READ: {Program.FormatCopernicusDataBytesRead()}{Environment.NewLine}");
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            logFileWriter?.Dispose();
            logFileWriter = null;
            runCancellation.Dispose();
            runCancellation = null;
            ResetScanState();
            SetRunning(false);
            if (!operationFailed)
            {
                SetOperationMessage("OPERATION COMPLETE");
            }
        }
    }

    private async void CommitPostProcess_Click(object? sender, EventArgs e)
    {
        int eastWestShift = (int)postEastWestShiftValue.Value;
        int northSouthShift = (int)postNorthSouthShiftValue.Value;
        if (eastWestShift == 0 && northSouthShift == 0)
        {
            StyledMessageDialog.Show(this, "Set an Advanced Geo Bias offset before committing Post Processing.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string routePath = NormalizeRoutePath(routePathText.Text);
        if (string.IsNullOrWhiteSpace(routePath) || !Directory.Exists(routePath))
        {
            StyledMessageDialog.Show(this, "Select a valid route folder first.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            StyledMessageDialog.Show(this, "Select Create Route Tiles and/or Create DM Tiles before committing.", "SCO LIDEX", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        DialogResult confirm = StyledMessageDialog.Show(
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
                LoTileRadius: (int)loTileRadius.Value,
                Hd4mOutput: experimentalOutput.Checked);

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

    private void BrowseRoute_Click(object? sender, EventArgs e)
    {
        string currentPath = NormalizeRoutePath(routePathText.Text);
        using FolderBrowserDialog dialog = new()
        {
            Description = "Select an Open Rails route folder",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(currentPath) ? currentPath : "",
            ShowNewFolderButton = false,
            AutoUpgradeEnabled = true,
        };

        try
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            string selectedPath = NormalizeRoutePath(dialog.SelectedPath);
            if (!IsValidRouteFolder(selectedPath))
            {
                StyledMessageDialog.Show(
                    this,
                    "The selected folder is not an Open Rails route folder. Select a folder containing a .trk file.",
                    "SCO LIDEX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            routePathText.Text = selectedPath;
            routePathText.SelectionStart = routePathText.Text.Length;
            SaveLastRoutePath(selectedPath);
        }
        catch (Exception ex)
        {
            StyledMessageDialog.Show(
                this,
                $"Windows could not open the route folder browser.{Environment.NewLine}{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}You can still type or paste the route path.",
                "SCO LIDEX",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
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
            SaveRouteHistoryEntry(routePath);
        }
        catch
        {
            // Remembering the route is convenience-only; never block a terrain run for it.
        }
    }

    private void ShowRouteHistory_Click(object? sender, EventArgs e)
    {
        List<string> routes = LoadRouteHistory();
        routeHistoryMenu.Items.Clear();
        routeHistoryMenu.ShowImageMargin = false;
        routeHistoryMenu.Font = Font;

        if (routes.Count == 0)
        {
            routeHistoryMenu.Items.Add(new ToolStripMenuItem("No recent valid routes") { Enabled = false });
        }
        else
        {
            foreach (string route in routes)
            {
                ToolStripMenuItem item = new(route) { ToolTipText = route };
                item.Click += (_, _) =>
                {
                    routePathText.Text = route;
                    routePathText.SelectionStart = routePathText.Text.Length;
                    routePathText.Focus();
                    SaveLastRoutePath(route);
                };
                routeHistoryMenu.Items.Add(item);
            }
        }

        routeHistoryMenu.Show(routeHistoryButton, new Point(0, routeHistoryButton.Height));
    }

    private static List<string> LoadRouteHistory()
    {
        try
        {
            List<string> saved = File.Exists(RouteHistoryFile)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(RouteHistoryFile)) ?? []
                : [];
            string lastRoute = LoadLastRoutePath();
            IEnumerable<string> candidates = IsValidRouteFolder(lastRoute)
                ? saved.Prepend(lastRoute)
                : saved;
            List<string> valid = candidates
                .Select(NormalizeRoutePath)
                .Where(IsValidRouteFolder)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaximumRouteHistoryEntries)
                .ToList();

            if (!saved.SequenceEqual(valid, StringComparer.OrdinalIgnoreCase))
            {
                WriteRouteHistory(valid);
            }

            return valid;
        }
        catch
        {
            return [];
        }
    }

    private static void SaveRouteHistoryEntry(string routePath)
    {
        string normalized = NormalizeRoutePath(routePath);
        if (!IsValidRouteFolder(normalized))
        {
            return;
        }

        List<string> routes = LoadRouteHistory();
        routes.RemoveAll(path => string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase));
        routes.Insert(0, normalized);
        WriteRouteHistory(routes.Take(MaximumRouteHistoryEntries).ToList());
    }

    private static void WriteRouteHistory(List<string> routes)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(
            RouteHistoryFile,
            JsonSerializer.Serialize(routes, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsValidRouteFolder(string routePath)
    {
        try
        {
            return Directory.Exists(routePath) &&
                   Directory.EnumerateFiles(routePath, "*.trk", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
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

        Program.ScanSummary? sourcePlan = scanOverride.Checked ? null : lastScanSummary;
        if (!createRouteTiles.Checked || sourcePlan is { RouteCanRun: false })
        {
            args.Add("--no-route-tiles");
            if (createRouteTiles.Checked && sourcePlan is { RouteCanRun: false })
            {
                args.Add("--scan-skipped-route");
            }
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

        if (distantMountains.Checked && sourcePlan is not { DistantMountainCanRun: false })
        {
            args.Add("--distant-mountains");
            args.Add("--lo-radius");
            args.Add(((int)loTileRadius.Value).ToString());
        }
        else if (distantMountains.Checked && sourcePlan is { DistantMountainCanRun: false })
        {
            args.Add("--scan-skipped-dm");
        }

        if (createMapTiles.Checked && sourcePlan is not { MapCanRun: false })
        {
            args.Add("--map-tiles");
            if (sourcePlan is { MapCacheOnly: true })
            {
                args.Add("--map-cache-only");
            }
        }
        else if (createMapTiles.Checked && sourcePlan is { MapCanRun: false })
        {
            args.Add("--scan-skipped-maps");
        }

        if (sourcePlan is not null)
        {
            AddDisabledDemSourceArguments(args, sourcePlan.DemSources);
            AddUsgsServiceStatusArguments(args, sourcePlan);
        }

        if (experimentalOutput.Checked)
        {
            args.Add("--hd-4m");
        }

        if (enableHdMapTiles.Checked)
        {
            args.Add("--hd-map-tiles");
        }

        if (terrainResolutionForceApproved)
        {
            args.Add("--force-terrain-resolution");
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

    private static void AddDisabledDemSourceArguments(List<string> args, Program.DemSourcePolicy sources)
    {
        if (!sources.UsePrimary) args.Add("--skip-usgs-1m");
        if (!sources.UseIntermediate) args.Add("--skip-usgs-5m");
        if (!sources.UseFallback) args.Add("--skip-usgs-10m");
        if (!sources.UseGlobal) args.Add("--skip-copernicus");
    }

    private static void AddUsgsServiceStatusArguments(List<string> args, Program.ScanSummary summary)
    {
        if (summary.PrimaryServiceAvailable) args.Add("--usgs-1m-service-online");
        if (summary.IntermediateServiceAvailable) args.Add("--usgs-5m-service-online");
        if (summary.FallbackServiceAvailable) args.Add("--usgs-10m-service-online");
    }

    private void WriteRunSettingsHeader(string routePath, string operation)
    {
        StringBuilder settings = new();
        string title = $"{operation.ToUpperInvariant()} SETTINGS";
        settings.AppendLine(title);
        settings.AppendLine(new string('-', title.Length));
        settings.AppendLine($"  OPERATION: {operation}");
        settings.AppendLine($"  ROUTE: {routePath}");
        settings.AppendLine($"  VERSION: {versionLabel.Text}");
        settings.AppendLine($"  MODE: {(overwriteMode.Checked ? "Overwrite" : "Append")}");
        settings.AppendLine($"  TERRAIN OUTPUT: {(experimentalOutput.Checked ? "HD Test - 4m Tiles" : "Normal - 8m Tiles")}");
        settings.AppendLine($"  SELECTION: {GetSelectionText()}");
        settings.AppendLine();
        settings.AppendLine("  OUTPUTS");
        settings.AppendLine("  -------");
        settings.AppendLine($"    • Route Tiles: {YesNo(createRouteTiles.Checked)}");
        settings.AppendLine($"    • Distant Mountains: {YesNo(distantMountains.Checked)}");
        settings.AppendLine($"    • OSM / Map Tiles: {YesNo(createMapTiles.Checked)}");
        settings.AppendLine($"    • HD Map Tiles: {YesNo(enableHdMapTiles.Checked)}");
        settings.AppendLine($"    • HD Mesh Tiles: {YesNo(enableHd4mTiles.Checked)}");
        settings.AppendLine();
        settings.AppendLine("  OPTIONS");
        settings.AppendLine("  -------");
        settings.AppendLine($"    • Tile Radius: {(int)terrainRadius.Value}");
        settings.AppendLine($"    • DM Radius: {(int)loTileRadius.Value}");
        settings.AppendLine($"    • Clean Tile Wipe: {YesNo(cleanTileTemplate.Checked)}");
        settings.AppendLine($"    • Scan Override: {YesNo(scanOverride.Checked)}");
        settings.AppendLine($"    • Geo Bias N/S: {(int)postNorthSouthShiftValue.Value} m");
        settings.AppendLine($"    • Geo Bias E/W: {(int)postEastWestShiftValue.Value} m");
        settings.AppendLine();
        settings.AppendLine("  DATA SOURCES");
        settings.AppendLine("  ------------");
        settings.AppendLine("    • USGS: 1m, 5m~, and 10m DEM tiers as enabled by Scan");
        settings.AppendLine("    • Global fallback: 30m Copernicus DEM GLO-30 Public | anonymous AWS Open Data | low-resolution DSM");
        settings.AppendLine();
        settings.AppendLine($"  LOG: {Path.Combine(GetUserFacingLogDirectory(), "SCOLIDEX.log")}");
        settings.AppendLine();
        AppendLog(settings.ToString());
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
            SetOperationMessage("ABORT REQUESTED");
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
        routeHistoryButton.Enabled = !locked;
        browseRouteButton.Enabled = !locked;
        appendMode.Enabled = !locked;
        overwriteMode.Enabled = !locked;
        createRouteTiles.Enabled = !locked;
        createMapTiles.Enabled = !locked;
        enableHd4mTiles.Enabled = !locked;
        enableHdMapTiles.Enabled = !locked;
        cleanTileTemplate.Enabled = !locked;
        scanOverride.Enabled = !busy && !scanLocked;
        existingTilesCoverage.Enabled = !locked;
        markerCoverage.Enabled = !locked;
        kmlCoverage.Enabled = !locked;
        trackDatabaseCoverage.Enabled = !locked;
        textFileCoverage.Enabled = !locked;
        terrainRadius.Enabled = !locked && UsesTileRadius();
        distantMountains.Enabled = !locked;
        loTileRadius.Enabled = !locked && distantMountains.Checked && UsesTileRadius();
        normalOutput.Enabled = !locked && enableHd4mTiles.Checked;
        experimentalOutput.Enabled = !locked && enableHd4mTiles.Checked;
        postEastWestShiftSlider.Enabled = !locked;
        postEastWestShiftValue.Enabled = !locked;
        postNorthSouthShiftSlider.Enabled = !locked;
        postNorthSouthShiftValue.Enabled = !locked;
        commitPostProcessButton.Enabled = !busy && !scanLocked;
        exitButton.Enabled = !busy;
        contactButton.Enabled = true;
        helpButton.Enabled = true;

        SetButtonPrimary(scanButton, scanButton.Enabled);
        SetButtonPrimary(runButton, runButton.Enabled && !busy);
        SetButtonAccent(abortButton);
        SetButtonAccent(exitButton);
        SetButtonAccent(commitPostProcessButton);
        SetButtonAccent(contactButton);
        SetButtonAccent(helpButton);
        if (busy)
        {
            logText.Select();
        }
    }

    private void ResetScanState()
    {
        scanPassed = false;
        lastScanSummary = null;
        terrainResolutionForceApproved = false;
        scanLocked = false;
        SetRunning(runCancellation is not null);
    }

    private void TopoForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.UserClosing || cacheExitDecisionMade)
        {
            return;
        }

        if (runCancellation is not null || scanCancellation is not null)
        {
            e.Cancel = true;
            StyledMessageDialog.Show(
                this,
                "Abort the active operation before exiting SCO LIDEX.",
                "SCO LIDEX",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        IReadOnlyList<Program.MapCacheEntry> caches;
        try
        {
            string currentRoute = NormalizeRoutePath(routePathText.Text);
            caches = Program.GetKnownMapCaches(Directory.Exists(currentRoute) ? currentRoute : null);
        }
        catch (Exception ex)
        {
            e.Cancel = true;
            StyledMessageDialog.Show(
                this,
                $"SCO LIDEX could not inspect registered cache data:\n\n{ex.Message}",
                "SCO LIDEX - Cache Data",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (caches.Count == 0)
        {
            return;
        }

        using MapCacheExitDialog dialog = new(caches);
        DialogResult result = dialog.ShowDialog(this);
        if (result == DialogResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == DialogResult.No)
        {
            try
            {
                Program.PurgeMapCaches(dialog.SelectedEntries);
            }
            catch (Exception ex)
            {
                e.Cancel = true;
                StyledMessageDialog.Show(
                    this,
                    $"The selected cache data could not be purged:\n\n{ex.Message}",
                    "SCO LIDEX - Cache Data",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
        }

        cacheExitDecisionMade = true;
    }

    private bool UsesTileRadius()
    {
        return markerCoverage.Checked || kmlCoverage.Checked || trackDatabaseCoverage.Checked;
    }

    private void UpdateRadiusState()
    {
        bool interactive = runCancellation is null && scanCancellation is null && !scanLocked;
        bool usesRadius = UsesTileRadius();
        terrainRadius.Enabled = interactive && usesRadius;
        loTileRadius.Enabled = interactive && distantMountains.Checked && usesRadius;
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
        createMapTiles.CheckedChanged += invalidate;
        distantMountains.CheckedChanged += invalidate;
        enableHd4mTiles.CheckedChanged += invalidate;
        enableHdMapTiles.CheckedChanged += invalidate;
        normalOutput.CheckedChanged += invalidate;
        experimentalOutput.CheckedChanged += invalidate;
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

        if (IsInternalStatusControlText(text))
        {
            TrackStatusText(text);
            return;
        }

        logText.AppendText(text);
        logFileWriter?.Write(text);
        logFileWriter?.Flush();
        TrackStatusText(text);
    }

    private static bool IsInternalStatusControlText(string text) =>
        text.Trim().StartsWith("STATUS:", StringComparison.Ordinal);

    internal static bool HidesInternalStatusControlTextForProbe(string text) =>
        IsInternalStatusControlText(text);

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
        SetOperationMessage("READY");
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

        const string statusPrefix = "STATUS: ";
        if (line.StartsWith(statusPrefix, StringComparison.Ordinal))
        {
            SetOperationMessage(line[statusPrefix.Length..].Trim());
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

            if (ParseNumber(dmPrepared.Groups["global"].Value) > 0)
            {
                dmStatus.Global++;
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

            if (ParseNumber(routeSources.Groups["global"].Value) > 0)
            {
                routeStatus.Global++;
                SetOperationMessage("TILES - GLOBAL - LOW RES");
            }

            UpdateStatusDisplay();
            return;
        }

        Match routeProgress = RouteProgressRegex().Match(line);
        if (routeProgress.Success && createRouteTiles.Checked)
        {
            routeStatus.Total = ParseNumber(routeProgress.Groups["total"].Value);
            routeStatus.Processed = ParseNumber(routeProgress.Groups["generated"].Value);
            routeStatus.Skipped = ParseNumber(routeProgress.Groups["skipped"].Value);
            routeStatus.Failures = ParseNumber(routeProgress.Groups["failed"].Value);
            UpdateStatusDisplay();
            return;
        }

        Match done = RouteDoneRegex().Match(line);
        if (done.Success && createRouteTiles.Checked)
        {
            routeStatus.Total = ParseNumber(done.Groups["total"].Value);
            routeStatus.Processed = ParseNumber(done.Groups["generated"].Value);
            routeStatus.Skipped = ParseNumber(done.Groups["skipped"].Value);
            routeStatus.Failures = ParseNumber(done.Groups["failed"].Value);
            UpdateStatusDisplay();
            return;
        }

        Match sourceUseSummary = SourceUseSummaryRegex().Match(line);
        if (sourceUseSummary.Success && createRouteTiles.Checked)
        {
            routeStatus.OneMeter = ParseNumber(sourceUseSummary.Groups["primary"].Value);
            routeStatus.Opr = ParseNumber(sourceUseSummary.Groups["opr"].Value);
            routeStatus.TenMeter = ParseNumber(sourceUseSummary.Groups["ten"].Value);
            routeStatus.Global = ParseNumber(sourceUseSummary.Groups["global"].Value);
            UpdateStatusDisplay();
        }
    }

    private void SetOperationMessage(string message)
    {
        message = message.Trim();
        if (string.Equals(operationMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        operationMessage = message;
        operationMessageAnimated = !IsFinalOperationMessage(message);
        activityBulletCount = 0;
        statusActivityTimer.Stop();

        if (message.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ABORT", StringComparison.OrdinalIgnoreCase))
        {
            operationFailed = true;
        }
        globalModeIndicator.Text = message;
        globalModeIndicator.Visible = true;
        globalModeIndicator.ForeColor = WarningColor;
        globalModeIndicator.BackColor = HeaderBackColor;

        if (operationMessageAnimated)
        {
            statusActivityTimer.Start();
        }

        if (!uiSoundsEnabled)
        {
            return;
        }

        if (message.StartsWith("SCAN COMPLETE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message, "OPERATION COMPLETE", StringComparison.OrdinalIgnoreCase))
        {
            UiSounds.PlaySuccess();
        }
        else if (message.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("ABORT", StringComparison.OrdinalIgnoreCase))
        {
            UiSounds.PlayBuzz();
        }
        else
        {
            UiSounds.PlayProgress();
        }
    }

    private void StatusActivityTimer_Tick(object? sender, EventArgs e)
    {
        if (!operationMessageAnimated || string.IsNullOrWhiteSpace(operationMessage))
        {
            statusActivityTimer.Stop();
            return;
        }

        activityBulletCount = (activityBulletCount + 1) % 4;
        globalModeIndicator.Text = activityBulletCount switch
        {
            1 => $"• {operationMessage} •",
            2 => $"• • {operationMessage} • •",
            3 => $"• • • {operationMessage} • • •",
            _ => operationMessage,
        };
    }

    private static bool IsFinalOperationMessage(string message)
    {
        return message.StartsWith("SCAN COMPLETE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message, "OPERATION COMPLETE", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ABORT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message, "READY", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateStatusDisplay()
    {
        tileTotalValue.Text = routeStatus.Total.ToString("N0");
        tileProcessedValue.Text = routeStatus.Processed.ToString("N0");
        tileSkippedValue.Text = routeStatus.Skipped.ToString("N0");
        tileOneMeterValue.Text = routeStatus.OneMeter.ToString("N0");
        tileOprValue.Text = routeStatus.Opr.ToString("N0");
        tileTenMeterValue.Text = routeStatus.TenMeter.ToString("N0");
        tileGlobalValue.Text = routeStatus.Global.ToString("N0");
        tileFailuresValue.Text = routeStatus.Failures.ToString("N0");
        bool showDm = distantMountains.Checked || dmStatus.Total > 0 || dmStatus.Processed > 0 || dmStatus.Skipped > 0 || dmStatus.Failures > 0;
        dmTotalValue.Text = showDm ? dmStatus.Total.ToString("N0") : "";
        dmProcessedValue.Text = showDm ? dmStatus.Processed.ToString("N0") : "";
        dmSkippedValue.Text = showDm ? dmStatus.Skipped.ToString("N0") : "";
        dmOneMeterValue.Text = "";
        dmOprValue.Text = "";
        dmTenMeterValue.Text = showDm ? dmStatus.TenMeter.ToString("N0") : "";
        dmGlobalValue.Text = showDm ? dmStatus.Global.ToString("N0") : "";
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
        ColorizeValue(tileGlobalValue, routeStatus.Global, MutedTextColor);
        ColorizeValue(tileSkippedValue, routeStatus.Skipped, MutedTextColor);
        ColorizeValue(tileFailuresValue, routeStatus.Failures, DangerColor);

        ColorizeValue(dmTotalValue, showDm ? dmStatus.Total : 0, MutedTextColor);
        ColorizeValue(dmProcessedValue, showDm ? dmStatus.Processed : 0, MutedTextColor);
        ColorizeValue(dmSkippedValue, showDm ? dmStatus.Skipped : 0, MutedTextColor);
        ColorizeValue(dmTenMeterValue, showDm ? dmStatus.TenMeter : 0, MutedTextColor);
        ColorizeValue(dmGlobalValue, showDm ? dmStatus.Global : 0, MutedTextColor);
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

    private sealed class NoFocusEmphasisButton : Button
    {
        protected override bool ShowFocusCues => false;

        public override void NotifyDefault(bool value)
        {
            base.NotifyDefault(false);
        }
    }

    private sealed class MeshGlobeControl : Control
    {
        public MeshGlobeControl()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width < 12 || ClientSize.Height < 12)
            {
                return;
            }

            float scale = DeviceDpi / 96f;
            float diameter = Math.Min(ClientSize.Width, ClientSize.Height) - (8f * scale);
            RectangleF globe = new(
                (ClientSize.Width - diameter) / 2f,
                (ClientSize.Height - diameter) / 2f,
                diameter,
                diameter);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using Pen mesh = new(AccentColor, Math.Max(1f, 1.15f * scale));
            using Pen outline = new(AccentColor, Math.Max(1.5f, 2f * scale));
            using GraphicsPath clipPath = new();
            clipPath.AddEllipse(globe);
            GraphicsState state = e.Graphics.Save();
            e.Graphics.SetClip(clipPath);

            e.Graphics.DrawLine(
                mesh,
                globe.Left,
                globe.Top + (globe.Height / 2f),
                globe.Right,
                globe.Top + (globe.Height / 2f));
            e.Graphics.DrawLine(
                mesh,
                globe.Left + (globe.Width / 2f),
                globe.Top,
                globe.Left + (globe.Width / 2f),
                globe.Bottom);
            e.Graphics.DrawEllipse(
                mesh,
                globe.Left + (globe.Width * 0.27f),
                globe.Top,
                globe.Width * 0.46f,
                globe.Height);
            e.Graphics.DrawEllipse(
                mesh,
                globe.Left,
                globe.Top + (globe.Height * 0.28f),
                globe.Width,
                globe.Height * 0.44f);

            e.Graphics.Restore(state);
            e.Graphics.DrawEllipse(outline, globe);
        }
    }

    private enum ButtonEmphasis
    {
        Neutral,
        Accent,
        Primary,
    }

    private sealed class DarkRadioButton : RadioButton
    {
        public DarkRadioButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int glyphSize = ScaleLogical(13);
            int glyphY = Math.Max(0, (ClientSize.Height - glyphSize) / 2);
            Rectangle glyph = new(1, glyphY, glyphSize, glyphSize);
            Color borderColor = Enabled ? Color.FromArgb(185, 185, 185) : Color.FromArgb(102, 102, 102);
            using Pen border = new(borderColor, Math.Max(1, ScaleLogical(1)));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawEllipse(border, glyph);
            if (Checked)
            {
                int inset = Math.Max(3, ScaleLogical(4));
                Rectangle dot = Rectangle.Inflate(glyph, -inset, -inset);
                using SolidBrush fill = new(Enabled ? AccentColor : Color.FromArgb(118, 118, 118));
                e.Graphics.FillEllipse(fill, dot);
            }

            DrawToggleText(e.Graphics, glyph.Right + ScaleLogical(6));
        }

        public override Size GetPreferredSize(Size proposedSize) => PreferredToggleSize(this);

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        private void DrawToggleText(Graphics graphics, int left)
        {
            Color color = Enabled ? ForeColor : Color.FromArgb(132, 132, 132);
            TextRenderer.DrawText(graphics, Text, Font,
                new Rectangle(left, 0, Math.Max(0, ClientSize.Width - left), ClientSize.Height), color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }

        private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
    }

    private sealed class DarkCheckBox : CheckBox
    {
        public DarkCheckBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int glyphSize = ScaleLogical(13);
            int glyphY = Math.Max(0, (ClientSize.Height - glyphSize) / 2);
            Rectangle glyph = new(1, glyphY, glyphSize, glyphSize);
            using SolidBrush fill = new(Color.FromArgb(28, 28, 28));
            using Pen border = new(Enabled ? Color.FromArgb(185, 185, 185) : Color.FromArgb(102, 102, 102));
            e.Graphics.FillRectangle(fill, glyph);
            e.Graphics.DrawRectangle(border, glyph);
            if (Checked)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using Pen check = new(Enabled ? AccentColor : Color.FromArgb(118, 118, 118), ScaleLogical(2));
                Point a = new(glyph.Left + ScaleLogical(3), glyph.Top + ScaleLogical(7));
                Point b = new(glyph.Left + ScaleLogical(6), glyph.Bottom - ScaleLogical(3));
                Point c = new(glyph.Right - ScaleLogical(2), glyph.Top + ScaleLogical(3));
                e.Graphics.DrawLines(check, [a, b, c]);
            }

            int textLeft = glyph.Right + ScaleLogical(6);
            Color color = Enabled ? ForeColor : Color.FromArgb(132, 132, 132);
            TextRenderer.DrawText(e.Graphics, Text, Font,
                new Rectangle(textLeft, 0, Math.Max(0, ClientSize.Width - textLeft), ClientSize.Height), color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix);
        }

        public override Size GetPreferredSize(Size proposedSize) => PreferredToggleSize(this);

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
    }

    private static Size PreferredToggleSize(Control control)
    {
        Size textSize = TextRenderer.MeasureText(control.Text, control.Font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        int glyphAndGap = Math.Max(28, (int)Math.Round(28 * control.DeviceDpi / 96f));
        return new Size(textSize.Width + glyphAndGap + control.Padding.Horizontal,
            Math.Max(textSize.Height, Math.Max(17, (int)Math.Round(17 * control.DeviceDpi / 96f))) +
            control.Padding.Vertical);
    }

    private sealed class DarkNumericInput : Control
    {
        private readonly TextBox editor;
        private decimal minimum;
        private decimal maximum = 100;
        private decimal increment = 1;
        private decimal currentValue;
        private bool updatingText;

        public DarkNumericInput()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = false;
            editor = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = InputBackColor,
                ForeColor = TextColor,
                TextAlign = HorizontalAlignment.Right,
                TabStop = true,
            };
            editor.TextChanged += (_, _) => ReadEditorValue();
            editor.LostFocus += (_, _) => UpdateEditorText();
            editor.KeyDown += Editor_KeyDown;
            Controls.Add(editor);
            Size = new Size(120, 23);
            UpdateEditorText();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public decimal Minimum
        {
            get => minimum;
            set
            {
                minimum = value;
                if (maximum < minimum) maximum = minimum;
                Value = currentValue;
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public decimal Maximum
        {
            get => maximum;
            set
            {
                maximum = Math.Max(value, minimum);
                Value = currentValue;
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public decimal Increment
        {
            get => increment;
            set => increment = Math.Max(0.0001m, value);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public decimal Value
        {
            get => currentValue;
            set
            {
                decimal adjusted = Math.Clamp(value, minimum, maximum);
                if (adjusted == currentValue)
                {
                    UpdateEditorText();
                    return;
                }
                currentValue = adjusted;
                UpdateEditorText();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public HorizontalAlignment TextAlign
        {
            get => editor.TextAlign;
            set => editor.TextAlign = value;
        }

        public event EventHandler? ValueChanged;

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Rectangle borderBounds = new(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            int buttonWidth = ScaleLogical(18);
            int buttonLeft = Math.Max(0, Width - buttonWidth);
            Color borderColor = Enabled ? Color.FromArgb(104, 104, 104) : Color.FromArgb(68, 68, 68);
            using Pen border = new(borderColor);
            using SolidBrush buttons = new(Enabled ? Color.FromArgb(45, 45, 45) : Color.FromArgb(36, 36, 36));
            e.Graphics.FillRectangle(buttons, buttonLeft, 1, buttonWidth - 1, Math.Max(0, Height - 2));
            e.Graphics.DrawRectangle(border, borderBounds);
            e.Graphics.DrawLine(border, buttonLeft, 1, buttonLeft, Height - 2);
            e.Graphics.DrawLine(border, buttonLeft, Height / 2, Width - 2, Height / 2);

            Color arrowColor = Enabled ? AccentColor : Color.FromArgb(100, 100, 100);
            using Pen arrow = new(arrowColor, ScaleLogical(1));
            int centerX = buttonLeft + (buttonWidth / 2);
            int topY = Height / 4;
            int bottomY = (Height * 3) / 4;
            int arm = ScaleLogical(3);
            e.Graphics.DrawLines(arrow,
                [new Point(centerX - arm, topY + 1), new Point(centerX, topY - 2), new Point(centerX + arm, topY + 1)]);
            e.Graphics.DrawLines(arrow,
                [new Point(centerX - arm, bottomY - 1), new Point(centerX, bottomY + 2), new Point(centerX + arm, bottomY - 1)]);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            int buttonWidth = ScaleLogical(18);
            int editorHeight = Math.Min(editor.PreferredHeight, Math.Max(1, Height - 4));
            editor.SetBounds(4, Math.Max(2, (Height - editorHeight) / 2),
                Math.Max(1, Width - buttonWidth - 7), editorHeight);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled) return;
            Focus();
            if (e.X >= Width - ScaleLogical(18))
            {
                Step(e.Y < Height / 2 ? increment : -increment);
            }
            else
            {
                editor.Focus();
                editor.SelectAll();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Enabled) Step(e.Delta > 0 ? increment : -increment);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            editor.Enabled = Enabled;
            editor.BackColor = Enabled ? InputBackColor : Color.FromArgb(34, 34, 34);
            editor.ForeColor = Enabled ? TextColor : Color.FromArgb(112, 112, 112);
            Invalidate();
        }

        private void Editor_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode is not (Keys.Up or Keys.Down)) return;
            Step(e.KeyCode == Keys.Up ? increment : -increment);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        private void Step(decimal amount) => Value = currentValue + amount;

        private void ReadEditorValue()
        {
            if (updatingText || !decimal.TryParse(editor.Text, NumberStyles.Number,
                    CultureInfo.CurrentCulture, out decimal parsed)) return;
            decimal adjusted = Math.Clamp(decimal.Truncate(parsed), minimum, maximum);
            if (adjusted == currentValue) return;
            currentValue = adjusted;
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateEditorText()
        {
            string valueText = currentValue.ToString(CultureInfo.CurrentCulture);
            if (editor.Text == valueText) return;
            updatingText = true;
            editor.Text = valueText;
            editor.SelectionStart = editor.TextLength;
            updatingText = false;
        }

        private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
    }
    private sealed class DarkTrackBar : Control
    {
        private int minimum;
        private int maximum = 10;
        private int currentValue;
        private bool dragging;

        public DarkTrackBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Minimum
        {
            get => minimum;
            set
            {
                minimum = value;
                if (maximum < minimum) maximum = minimum;
                Value = currentValue;
                Invalidate();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Maximum
        {
            get => maximum;
            set
            {
                maximum = Math.Max(value, minimum);
                Value = currentValue;
                Invalidate();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => currentValue;
            set
            {
                int adjusted = Math.Clamp(value, minimum, maximum);
                if (adjusted == currentValue) return;
                currentValue = adjusted;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int TickFrequency { get; set; } = 1;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SmallChange { get; set; } = 1;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int LargeChange { get; set; } = 5;
        public event EventHandler? ValueChanged;

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int left = ScaleLogical(8);
            int right = Math.Max(left + 1, ClientSize.Width - ScaleLogical(9));
            int centerY = ClientSize.Height / 2;
            using Pen rail = new(Enabled ? Color.FromArgb(94, 94, 94) : Color.FromArgb(65, 65, 65), ScaleLogical(3));
            e.Graphics.DrawLine(rail, left, centerY, right, centerY);

            if (TickFrequency > 0 && maximum > minimum)
            {
                using Pen ticks = new(Color.FromArgb(82, 82, 82));
                for (int tick = minimum; tick <= maximum; tick += TickFrequency)
                {
                    int x = ValueToX(tick, left, right);
                    e.Graphics.DrawLine(ticks, x, centerY + ScaleLogical(6), x, centerY + ScaleLogical(8));
                }
            }

            int thumbX = ValueToX(currentValue, left, right);
            int radius = ScaleLogical(6);
            using SolidBrush thumb = new(Enabled ? AccentGreen : Color.FromArgb(92, 92, 92));
            using Pen thumbBorder = new(Enabled ? AccentColor : Color.FromArgb(120, 120, 120));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(thumb, thumbX - radius, centerY - radius, radius * 2, radius * 2);
            e.Graphics.DrawEllipse(thumbBorder, thumbX - radius, centerY - radius, radius * 2, radius * 2);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Focus();
            dragging = true;
            Capture = true;
            SetValueFromMouse(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetValueFromMouse(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int change = e.KeyCode switch
            {
                Keys.Left or Keys.Down => -SmallChange,
                Keys.Right or Keys.Up => SmallChange,
                Keys.PageDown => -LargeChange,
                Keys.PageUp => LargeChange,
                Keys.Home => minimum - currentValue,
                Keys.End => maximum - currentValue,
                _ => 0,
            };
            if (change == 0) return;
            Value += change;
            e.Handled = true;
        }

        private void SetValueFromMouse(int x)
        {
            int left = ScaleLogical(8);
            int right = Math.Max(left + 1, ClientSize.Width - ScaleLogical(9));
            double ratio = Math.Clamp((x - left) / (double)(right - left), 0d, 1d);
            Value = minimum + (int)Math.Round(ratio * (maximum - minimum));
        }

        private int ValueToX(int value, int left, int right)
        {
            if (maximum == minimum) return left;
            double ratio = (value - minimum) / (double)(maximum - minimum);
            return left + (int)Math.Round(ratio * (right - left));
        }

        private int ScaleLogical(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
    }
    private sealed class DarkGroupBox : GroupBox
    {
        public DarkGroupBox()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            Rectangle border = new(0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1));
            using SolidBrush header = new(Color.FromArgb(39, 39, 39));
            using Pen borderPen = new(Color.FromArgb(78, 78, 78));
            e.Graphics.FillRectangle(header, 0, 0, ClientSize.Width, ScaleLogical(23));
            e.Graphics.DrawRectangle(borderPen, border);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                new Rectangle(9, 2, Math.Max(0, ClientSize.Width - 18), ScaleLogical(19)),
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private int ScaleLogical(int value)
        {
            return Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
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
        public int Global { get; set; }
        public int Failures { get; set; }

        public void Reset()
        {
            Total = 0;
            Processed = 0;
            Skipped = 0;
            OneMeter = 0;
            Opr = 0;
            TenMeter = 0;
            Global = 0;
            Failures = 0;
        }
    }

    [GeneratedRegex(@"^\[(?:4m\s+)?(?<index>[\d,]+)/(?<total>[\d,]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex RouteTileRegex();

    [GeneratedRegex(@"Progress:\s+(?<processed>[\d,]+)/(?<total>[\d,]+)\s+processed.*?(?<generated>[\d,]+)\s+generated.*?(?<skipped>[\d,]+)\s+skipped.*?(?<failed>[\d,]+)\s+failed", RegexOptions.IgnoreCase)]
    private static partial Regex RouteProgressRegex();

    [GeneratedRegex(@"Done\.\s+Generated=(?<generated>[\d,]+),\s+skipped=(?<skipped>[\d,]+),\s+failed=(?<failed>[\d,]+),\s+total=(?<total>[\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RouteDoneRegex();

    [GeneratedRegex(@"Distant Mountains:\s+.*?,\s+radius\s+[\d,]+,\s+(?<total>[\d,]+)\s+lo_tiles", RegexOptions.IgnoreCase)]
    private static partial Regex DistantMountainStartRegex();

    [GeneratedRegex(@"\[DM\s+(?<index>[\d,]+)/(?<total>[\d,]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex DistantMountainTileRegex();

    [GeneratedRegex(@"Prepared TSRE-style lo_tile with (?:10m=(?<ten>[\d,]+),\s+)?30m \(global\)=(?<global>[\d,]+),", RegexOptions.IgnoreCase)]
    private static partial Regex DistantMountainPreparedRegex();

    [GeneratedRegex(@"Distant Mountains done\.\s+Generated=(?<generated>[\d,]+),\s+skipped=(?<skipped>[\d,]+),\s+failed=(?<failed>[\d,]+),\s+total=(?<total>[\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DistantMountainDoneRegex();

    [GeneratedRegex(@"Source samples used:\s+1m=(?<primary>[\d,]+),\s+5m~=(?<opr>[\d,]+),\s+10m=(?<ten>[\d,]+),\s+30m \(global\)=(?<global>[\d,]+),", RegexOptions.IgnoreCase)]
    private static partial Regex RouteSourceSamplesRegex();

    [GeneratedRegex(@"Source use summary:\s+tiles using 1m=(?<primary>[\d,]+),\s+5m~=(?<opr>[\d,]+),\s+10m=(?<ten>[\d,]+),\s+30m \(global\)=(?<global>[\d,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SourceUseSummaryRegex();

    internal static bool RecognizesRouteTileProgressForProbe(string line)
    {
        return RouteTileRegex().IsMatch(line);
    }

    internal static bool RecognizesRouteSourceProgressForProbe(string line)
    {
        return RouteSourceSamplesRegex().IsMatch(line);
    }

}
