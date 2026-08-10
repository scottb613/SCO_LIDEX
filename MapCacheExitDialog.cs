// SCO LIDEX - selectable cache choices shown whenever the GUI exits.
// Copyright (C) Scott Brunner, Beast of Burden
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ORterr;

internal sealed class MapCacheExitDialog : Form
{
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
        BackColor = Color.FromArgb(242, 241, 238);
        ForeColor = Color.FromArgb(28, 29, 30);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular);

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(16),
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
            Margin = new Padding(0, 0, 0, 5),
        };
        root.Controls.Add(heading);

        Label explanation = new()
        {
            AutoSize = true,
            Text = "Cache data is kept by default. Check only the individual caches you want to purge.",
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
            BackColor = Color.White,
            Margin = new Padding(0, 0, 0, 12),
        };
        cacheList.Columns.Add("Cache", 230);
        cacheList.Columns.Add("Route / owner", 140);
        cacheList.Columns.Add("Files", 60, HorizontalAlignment.Right);
        cacheList.Columns.Add("Size", 95, HorizontalAlignment.Right);
        cacheList.Columns.Add("Location", 410);
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
        };
        Button cancel = CreateButton("Cancel Exit", DialogResult.Cancel);
        purgeButton = CreateButton("Purge Selected", DialogResult.None, playPress: false);
        purgeButton.Click += (_, _) => PurgeSelected();
        Button keep = CreateButton("Keep All", DialogResult.Yes);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(purgeButton);
        buttons.Controls.Add(keep);
        root.Controls.Add(buttons);
        AcceptButton = keep;
        CancelButton = cancel;
    }

    public IReadOnlyList<Program.MapCacheEntry> SelectedEntries =>
        cacheList.CheckedItems
            .Cast<ListViewItem>()
            .Select(item => (Program.MapCacheEntry)item.Tag!)
            .ToArray();

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

    private static Button CreateButton(string text, DialogResult result, bool playPress = true)
    {
        Button button = new()
        {
            Text = text,
            DialogResult = result,
            AutoSize = true,
            MinimumSize = new Size(115, 30),
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = result == DialogResult.Yes
                ? Color.FromArgb(216, 232, 216)
                : result == DialogResult.No
                    ? Color.FromArgb(244, 219, 214)
                    : Color.FromArgb(232, 229, 224),
        };
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
