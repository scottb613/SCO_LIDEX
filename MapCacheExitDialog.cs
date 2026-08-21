// SCO LIDEX - selectable cache choices shown whenever the GUI exits.
// Copyright (C) Scott Brunner, Beast of Burden
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ORterr;

internal sealed class MapCacheExitDialog : Form
{
    private const int DwmUseImmersiveDarkMode = 20;
    private static readonly Color AppBackColor = Color.FromArgb(45, 45, 45);
    private static readonly Color InputBackColor = Color.FromArgb(27, 27, 27);
    private static readonly Color TextColor = Color.FromArgb(240, 240, 240);
    private static readonly Color MutedTextColor = Color.FromArgb(184, 184, 184);
    private static readonly Color AccentColor = Color.FromArgb(226, 178, 126);
    private static readonly Color AccentBorderColor = Color.FromArgb(205, 132, 52);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr windowHandle, string? subAppName, string? subIdList);
    private readonly ListView cacheList;
    private readonly Button purgeButton;

    public MapCacheExitDialog(IReadOnlyList<Program.MapCacheEntry> caches)
    {
        long totalBytes = caches.Sum(cache => cache.SizeBytes);
        Text = "SCO LIDEX - Cache Data";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(980, 410);
        BackColor = AppBackColor;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular);

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16),
            BackColor = AppBackColor,
            ForeColor = TextColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        Label heading = new()
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
            Text = $"SCO LIDEX cache data: {FormatSize(totalBytes)}",
            ForeColor = AccentColor,
            Margin = new Padding(0, 0, 0, 5),
        };
        root.Controls.Add(heading);

        Label explanation = new()
        {
            AutoSize = true,
            Text = "Cache data is kept by default. Check only the individual caches you want to purge.",
            ForeColor = MutedTextColor,
            Margin = new Padding(0, 0, 0, 10),
        };
        root.Controls.Add(explanation);

        cacheList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            OwnerDraw = true,
            BackColor = InputBackColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 12),
        };
        cacheList.Columns.Add("Cache", 230);
        cacheList.Columns.Add("Route / owner", 140);
        cacheList.Columns.Add("Files", 60, HorizontalAlignment.Right);
        cacheList.Columns.Add("Size", 95, HorizontalAlignment.Right);
        ColumnHeader locationColumn = cacheList.Columns.Add("Location", 410);
        bool layingOutCacheList = false;
        void LayoutCacheList()
        {
            if (layingOutCacheList || cacheList.ClientSize.Height <= 0)
            {
                return;
            }

            layingOutCacheList = true;
            try
            {
                int headerHeight = cacheList.Font.Height + 10;
                int rowHeight = cacheList.Font.Height + 6;
                int visibleRowHeight = Math.Max(0, cacheList.ClientSize.Height - headerHeight);
                bool needsScrolling = (cacheList.Items.Count * rowHeight) > visibleRowHeight;
                if (cacheList.Scrollable != needsScrolling)
                {
                    cacheList.Scrollable = needsScrolling;
                }

                int fixedColumnWidth = cacheList.Columns.Cast<ColumnHeader>()
                    .Where(column => column != locationColumn)
                    .Sum(column => column.Width);
                locationColumn.Width = Math.Max(180, cacheList.ClientSize.Width - fixedColumnWidth);
            }
            finally
            {
                layingOutCacheList = false;
            }
        }
        cacheList.Resize += (_, _) => LayoutCacheList();
        cacheList.HandleCreated += (_, _) => LayoutCacheList();
        cacheList.DrawColumnHeader += DrawCacheColumnHeader;
        cacheList.DrawItem += (_, e) => e.DrawDefault = false;
        cacheList.DrawSubItem += DrawCacheSubItem;
        foreach (Program.MapCacheEntry cache in caches)
        {
            ListViewItem item = new(cache.Name)
            {
                Tag = cache,
                Checked = false,
            };
            item.SubItems.Add(cache.Owner);
            item.SubItems.Add(cache.FileCount.ToString("N0"));
            item.SubItems.Add(FormatSize(cache.SizeBytes));
            item.SubItems.Add(cache.Location);
            cacheList.Items.Add(item);
        }
        root.Controls.Add(cacheList);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = AppBackColor,
        };
        Button cancel = CreateButton("Cancel Exit", DialogResult.Cancel, ButtonEmphasis.Accent);
        purgeButton = CreateButton("Purge Selected", DialogResult.None, ButtonEmphasis.Neutral, playPress: false);
        purgeButton.Click += (_, _) => PurgeSelected();
        cacheList.ItemChecked += (_, _) => BeginInvoke((MethodInvoker)UpdatePurgeButtonAccent);
        UpdatePurgeButtonAccent();
        Button keep = CreateButton("Keep All", DialogResult.Yes, ButtonEmphasis.Primary);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(purgeButton);
        buttons.Controls.Add(keep);
        root.Controls.Add(buttons);
        AcceptButton = keep;
        CancelButton = cancel;
        Shown += (_, _) =>
        {
            LayoutCacheList();
            ApplyDarkTheme();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkTitleBar();
    }

    public IReadOnlyList<Program.MapCacheEntry> SelectedEntries =>
        cacheList.CheckedItems
            .Cast<ListViewItem>()
            .Select(item => (Program.MapCacheEntry)item.Tag!)
            .ToArray();

    private static void DrawCacheColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using SolidBrush background = new(Color.FromArgb(38, 38, 38));
        using Pen divider = new(Color.FromArgb(78, 78, 78));
        e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.DrawLine(divider, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
        TextFormatFlags alignment = e.ColumnIndex is 2 or 3
            ? TextFormatFlags.Right
            : TextFormatFlags.Left;
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty,
            SystemFonts.MessageBoxFont, Rectangle.Inflate(e.Bounds, -6, 0), AccentColor,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
    private static void DrawCacheSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        ListViewItem? item = e.Item;
        ListViewItem.ListViewSubItem? subItem = e.SubItem;
        if (sender is not ListView list || item is null || subItem is null)
        {
            return;
        }

        bool selected = item.Selected;
        using SolidBrush background = new(selected ? Color.FromArgb(58, 58, 58) : InputBackColor);
        e.Graphics.FillRectangle(background, e.Bounds);

        Rectangle textBounds = Rectangle.Inflate(e.Bounds, -6, 0);
        if (e.ColumnIndex == 0)
        {
            int glyphSize = Math.Max(13, (int)Math.Round(13 * list.DeviceDpi / 96f));
            int glyphLeft = e.Bounds.Left + Math.Max(5, (int)Math.Round(5 * list.DeviceDpi / 96f));
            int glyphTop = e.Bounds.Top + Math.Max(0, (e.Bounds.Height - glyphSize) / 2);
            Rectangle glyph = new(glyphLeft, glyphTop, glyphSize, glyphSize);
            using SolidBrush glyphFill = new(Color.FromArgb(28, 28, 28));
            using Pen glyphBorder = new(Color.FromArgb(185, 185, 185));
            e.Graphics.FillRectangle(glyphFill, glyph);
            e.Graphics.DrawRectangle(glyphBorder, glyph);
            if (item.Checked)
            {
                float scale = list.DeviceDpi / 96f;
                int Scale(int value) => Math.Max(1, (int)Math.Round(value * scale));
                using Pen check = new(AccentColor, Scale(2));
                Point a = new(glyph.Left + Scale(3), glyph.Top + Scale(7));
                Point b = new(glyph.Left + Scale(6), glyph.Bottom - Scale(3));
                Point c = new(glyph.Right - Scale(2), glyph.Top + Scale(3));
                e.Graphics.DrawLines(check, [a, b, c]);
            }

            int gap = Math.Max(6, (int)Math.Round(6 * list.DeviceDpi / 96f));
            textBounds = new Rectangle(glyph.Right + gap, e.Bounds.Top,
                Math.Max(0, e.Bounds.Right - glyph.Right - (gap * 2)), e.Bounds.Height);
        }

        TextFormatFlags alignment = e.ColumnIndex is 2 or 3
            ? TextFormatFlags.Right
            : TextFormatFlags.Left;
        TextRenderer.DrawText(e.Graphics, subItem.Text, list.Font, textBounds, TextColor,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
    private void PurgeSelected()
    {
        if (cacheList.CheckedItems.Count == 0)
        {
            UiSounds.PlayBuzz();
            return;
        }

        UiSounds.PlayPress();
        DialogResult = DialogResult.No;
    }

    private static Button CreateButton(string text, DialogResult result, ButtonEmphasis emphasis, bool playPress = true)
    {
        Button button = new()
        {
            Text = text,
            DialogResult = result,
            AutoSize = true,
            MinimumSize = new Size(115, 30),
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 48, 48),
            ForeColor = TextColor,
            UseVisualStyleBackColor = false,
        };
        ApplyButtonEmphasis(button, emphasis);
        if (playPress)
        {
            button.MouseDown += (_, _) => UiSounds.PlayPress();
            button.KeyDown += (_, e) =>
            {
                if (e.KeyCode is Keys.Enter or Keys.Space)
                {
                    UiSounds.PlayPress();
                }
            };
        }
        return button;
    }

    private void UpdatePurgeButtonAccent()
    {
        bool hasSelection = cacheList.Items.Cast<ListViewItem>().Any(item => item.Checked);
        purgeButton.Enabled = hasSelection;
        ApplyButtonEmphasis(purgeButton, ButtonEmphasis.Accent);
    }

    private static void ApplyButtonEmphasis(Button button, ButtonEmphasis emphasis)
    {
        if (!button.Enabled)
        {
            button.BackColor = Color.FromArgb(43, 43, 43);
            button.ForeColor = Color.FromArgb(112, 112, 112);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(72, 72, 72);
            return;
        }

        button.ForeColor = TextColor;
        button.BackColor = emphasis == ButtonEmphasis.Primary
            ? Color.FromArgb(58, 58, 58)
            : Color.FromArgb(48, 48, 48);
        button.FlatAppearance.BorderSize = emphasis == ButtonEmphasis.Primary ? 2 : 1;
        button.FlatAppearance.BorderColor = emphasis switch
        {
            ButtonEmphasis.Primary => AccentBorderColor,
            ButtonEmphasis.Accent => Color.FromArgb(173, 111, 45),
            _ => Color.FromArgb(92, 92, 92),
        };
    }

    private void ApplyDarkTheme()
    {
        ApplyDarkTitleBar();
        try
        {
            cacheList.CreateControl();
            _ = SetWindowTheme(cacheList.Handle, "DarkMode_Explorer", null);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private void ApplyDarkTitleBar()
    {
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

    private enum ButtonEmphasis
    {
        Neutral,
        Accent,
        Primary,
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} {units[unit]}" : $"{value:N2} {units[unit]}";
    }
}
