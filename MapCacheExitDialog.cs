// SCO LIDEX - map-cache choice shown whenever the GUI exits.
// Copyright (C) Scott Brunner, Beast of Burden
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ORterr;

internal sealed class MapCacheExitDialog : Form
{
    public MapCacheExitDialog(string cachePath, IReadOnlyList<FileInfo> files)
    {
        long totalBytes = files.Sum(file => file.Length);
        Text = "SCO LIDEX - Map Cache";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(590, 310);
        BackColor = Color.FromArgb(242, 241, 238);
        ForeColor = Color.FromArgb(28, 29, 30);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular);

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        Label heading = new()
        {
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
            Text = $"OpenStreetMap cache: {FormatSize(totalBytes)}",
            Margin = new Padding(0, 0, 0, 5),
        };
        root.Controls.Add(heading);

        Label explanation = new()
        {
            AutoSize = true,
            Text = "Keep the cache for faster future map runs, or purge it to recover disk space.",
            Margin = new Padding(0, 0, 0, 10),
        };
        root.Controls.Add(explanation);

        ListView fileList = new()
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 8),
        };
        fileList.Columns.Add("Cached file", 410);
        fileList.Columns.Add("Size", 120, HorizontalAlignment.Right);
        foreach (FileInfo file in files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
        {
            ListViewItem item = new(file.Name);
            item.SubItems.Add(FormatSize(file.Length));
            fileList.Items.Add(item);
        }
        root.Controls.Add(fileList);

        TextBox pathText = new()
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Text = cachePath,
            BackColor = Color.FromArgb(248, 247, 244),
            Margin = new Padding(0, 0, 0, 12),
        };
        root.Controls.Add(pathText);

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
        };
        Button cancel = CreateButton("Cancel Exit", DialogResult.Cancel);
        Button purge = CreateButton("Purge Cache", DialogResult.No);
        Button keep = CreateButton("Keep Cache", DialogResult.Yes);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(purge);
        buttons.Controls.Add(keep);
        root.Controls.Add(buttons);
        AcceptButton = keep;
        CancelButton = cancel;
    }

    private static Button CreateButton(string text, DialogResult result)
    {
        Button button = new()
        {
            Text = text,
            DialogResult = result,
            AutoSize = true,
            MinimumSize = new Size(105, 30),
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = result == DialogResult.Yes
                ? Color.FromArgb(216, 232, 216)
                : Color.FromArgb(232, 229, 224),
        };
        button.MouseDown += (_, _) => UiSounds.PlayPress();
        button.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                UiSounds.PlayPress();
            }
        };
        return button;
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
