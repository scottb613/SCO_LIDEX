// SCO LIDEX - application-styled replacement for native Windows message boxes.
// Copyright (C) Scott Brunner, Beast of Burden
// SCO LIDEX is distributed under GNU GPL v3 or later. See LICENSE.txt.

using System.Drawing;
using System.Windows.Forms;

namespace ORterr;

internal sealed class StyledMessageDialog : Form
{
    private static readonly Color AppBackColor = Color.FromArgb(242, 241, 238);
    private static readonly Color PanelBackColor = Color.FromArgb(248, 247, 244);
    private static readonly Color TextColor = Color.FromArgb(28, 29, 30);
    private static readonly Color AccentColor = Color.FromArgb(126, 77, 48);
    private static readonly Color AccentGreen = Color.FromArgb(69, 118, 73);
    private static readonly Color ButtonBackColor = Color.FromArgb(232, 229, 224);
    private static readonly Color PrimaryButtonBackColor = Color.FromArgb(216, 232, 216);

    private StyledMessageDialog(
        IWin32Window? owner,
        string message,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon)
    {
        Text = string.IsNullOrWhiteSpace(caption) ? "SCO LIDEX" : caption;
        StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(440, 0);
        MaximumSize = new Size(680, 900);
        BackColor = AppBackColor;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular);
        if (owner is Form ownerForm)
        {
            Icon = ownerForm.Icon;
        }

        (string glyph, Color color, Color badgeColor, string accessibleName) = IconStyle(icon);
        Panel accent = new()
        {
            Dock = DockStyle.Fill,
            Height = 5,
            Margin = new Padding(0),
            BackColor = color,
        };

        TableLayoutPanel shell = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(0),
            BackColor = AppBackColor,
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 5));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(shell);
        shell.Controls.Add(accent, 0, 0);

        TableLayoutPanel root = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(16, 15, 16, 12),
            Margin = new Padding(0),
            BackColor = AppBackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.Controls.Add(root, 0, 1);

        TableLayoutPanel content = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(12),
            Margin = new Padding(0, 0, 0, 12),
            BackColor = PanelBackColor,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label badge = new()
        {
            Text = glyph,
            AccessibleName = accessibleName,
            AutoSize = false,
            Size = new Size(44, 44),
            Margin = new Padding(0, 2, 12, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 18F, FontStyle.Bold),
            ForeColor = color,
            BackColor = badgeColor,
            BorderStyle = BorderStyle.FixedSingle,
        };
        content.Controls.Add(badge, 0, 0);
        content.SetRowSpan(badge, 2);

        Label heading = new()
        {
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            Text = Text,
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold),
            ForeColor = AccentColor,
            Margin = new Padding(0, 0, 0, 7),
        };
        content.Controls.Add(heading, 1, 0);

        Label body = new()
        {
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            Text = message,
            ForeColor = TextColor,
            Margin = new Padding(0),
            UseMnemonic = false,
        };
        content.Controls.Add(body, 1, 1);
        root.Controls.Add(content, 0, 0);

        FlowLayoutPanel buttonPanel = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = new Padding(0),
            BackColor = AppBackColor,
        };
        foreach ((string text, DialogResult result, bool primary, bool cancel) in ButtonDefinitions(buttons))
        {
            Button button = CreateButton(text, result, primary);
            buttonPanel.Controls.Add(button);
            if (primary && AcceptButton is null)
            {
                AcceptButton = button;
            }
            if (cancel)
            {
                CancelButton = button;
            }
        }
        root.Controls.Add(buttonPanel, 0, 1);
    }

    internal static DialogResult Show(
        IWin32Window? owner,
        string message,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon)
    {
        using StyledMessageDialog dialog = new(owner, message, caption, buttons, icon);
        return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    internal static DialogResult Show(
        string message,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon) =>
        Show(null, message, caption, buttons, icon);

    internal static void RunConstructionProbe()
    {
        ApplicationConfiguration.Initialize();
        using StyledMessageDialog information = new(
            null, "Information message", "SCO LIDEX", MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        using StyledMessageDialog confirmation = new(
            null, "Confirmation message", "SCO LIDEX", MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);
        Button[] informationButtons = Descendants(information).OfType<Button>().ToArray();
        Button[] confirmationButtons = Descendants(confirmation).OfType<Button>().ToArray();
        if (informationButtons.Length != 1 ||
            informationButtons[0].DialogResult != DialogResult.OK ||
            information.AcceptButton != informationButtons[0] ||
            information.CancelButton != informationButtons[0] ||
            confirmationButtons.Length != 2 ||
            !confirmationButtons.Any(button => button.DialogResult == DialogResult.OK) ||
            !confirmationButtons.Any(button => button.DialogResult == DialogResult.Cancel) ||
            confirmation.AcceptButton is null || confirmation.CancelButton is null)
        {
            throw new InvalidOperationException("styled message dialog construction failed");
        }

        Console.WriteLine("Styled message dialog probe: PASSED");
        Console.WriteLine("  information and confirmation layouts constructed");
        Console.WriteLine("  accept/cancel results and application styling are wired");
    }

    private static Button CreateButton(string text, DialogResult result, bool primary)
    {
        Button button = new()
        {
            Text = text,
            DialogResult = result,
            AutoSize = true,
            MinimumSize = new Size(90, 30),
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? PrimaryButtonBackColor : ButtonBackColor,
            ForeColor = TextColor,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary
            ? AccentGreen
            : Color.FromArgb(150, 145, 137);
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

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static IReadOnlyList<(string Text, DialogResult Result, bool Primary, bool Cancel)>
        ButtonDefinitions(MessageBoxButtons buttons) => buttons switch
        {
            MessageBoxButtons.OKCancel =>
            [
                ("Cancel", DialogResult.Cancel, false, true),
                ("OK", DialogResult.OK, true, false),
            ],
            MessageBoxButtons.YesNo =>
            [
                ("No", DialogResult.No, false, true),
                ("Yes", DialogResult.Yes, true, false),
            ],
            MessageBoxButtons.YesNoCancel =>
            [
                ("Cancel", DialogResult.Cancel, false, true),
                ("No", DialogResult.No, false, false),
                ("Yes", DialogResult.Yes, true, false),
            ],
            MessageBoxButtons.RetryCancel =>
            [
                ("Cancel", DialogResult.Cancel, false, true),
                ("Retry", DialogResult.Retry, true, false),
            ],
            MessageBoxButtons.AbortRetryIgnore =>
            [
                ("Ignore", DialogResult.Ignore, false, true),
                ("Retry", DialogResult.Retry, true, false),
                ("Abort", DialogResult.Abort, false, false),
            ],
            MessageBoxButtons.CancelTryContinue =>
            [
                ("Cancel", DialogResult.Cancel, false, true),
                ("Try Again", DialogResult.TryAgain, true, false),
                ("Continue", DialogResult.Continue, false, false),
            ],
            _ =>
            [
                ("OK", DialogResult.OK, true, true),
            ],
        };

    private static (string Glyph, Color Color, Color BadgeColor, string AccessibleName)
        IconStyle(MessageBoxIcon icon)
    {
        if (icon == MessageBoxIcon.Error)
        {
            return ("×", Color.FromArgb(166, 58, 44), Color.FromArgb(244, 219, 214), "Error");
        }
        if (icon == MessageBoxIcon.Warning)
        {
            return ("!", Color.FromArgb(154, 104, 35), Color.FromArgb(247, 231, 196), "Warning");
        }
        if (icon == MessageBoxIcon.Question)
        {
            return ("?", AccentColor, Color.FromArgb(239, 227, 217), "Question");
        }
        return ("i", AccentGreen, PrimaryButtonBackColor, "Information");
    }
}
