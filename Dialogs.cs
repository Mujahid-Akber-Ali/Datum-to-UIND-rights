using System.Drawing;
using System.Text;
using System.Windows.Forms;

/// <summary>
/// Every prompt the tool shows the operator. Each one is a modal popup, so the
/// flow works the same whether or not a console window is visible.
/// </summary>
internal static class Dialogs
{
    private const string AppTitle = "Portal Rights Sync";

    private static readonly Font BodyFont = new("Segoe UI", 9F);
    private static readonly Font BoldFont = new("Segoe UI", 9F, FontStyle.Bold);
    private static readonly Font MonoFont = new("Consolas", 9F);
    private static readonly Font HeadingFont = new("Segoe UI", 12F, FontStyle.Bold);

    /// <summary>
    /// Asks for both employee IDs in one popup, so the direction of the copy is
    /// visible in a single glance. Returns null if the operator cancels.
    /// </summary>
    public static EmployeeIds? AskEmployeeIds(string sourceUrl, string targetUrl)
    {
        using var form = NewForm($"{AppTitle} - Employee IDs", 560, 356);

        var heading = new Label
        {
            Text = "Employee IDs",
            Font = HeadingFont,
            AutoSize = true,
            Location = new Point(20, 16)
        };

        var help = new Label
        {
            Text = "The same person usually has a different ID on each portal.\r\n" +
                   "To open a position-rights page directly instead, type # and its ID - " +
                   "for example  #8579",
            Font = BodyFont,
            AutoSize = true,
            Location = new Point(22, 46)
        };

        var sourceInput = new TextBox
        {
            Font = new Font("Segoe UI", 12F),
            Location = new Point(16, 44),
            Size = new Size(480, 30)
        };

        var sourceBox = new GroupBox
        {
            Text = $"COPY FROM      {sourceUrl}",
            Font = BoldFont,
            ForeColor = Color.FromArgb(0, 90, 158),
            Location = new Point(20, 92),
            Size = new Size(512, 88)
        };

        sourceBox.Controls.Add(new Label
        {
            Text = "Employee ID (serial number) to copy rights from",
            Font = BodyFont,
            ForeColor = SystemColors.ControlText,
            AutoSize = true,
            Location = new Point(14, 22)
        });

        sourceBox.Controls.Add(sourceInput);

        var targetInput = new TextBox
        {
            Font = new Font("Segoe UI", 12F),
            Location = new Point(16, 44),
            Size = new Size(480, 30)
        };

        var targetBox = new GroupBox
        {
            Text = $"COPY TO      {targetUrl}      (this portal gets modified)",
            Font = BoldFont,
            ForeColor = Color.FromArgb(160, 40, 0),
            Location = new Point(20, 192),
            Size = new Size(512, 88)
        };

        targetBox.Controls.Add(new Label
        {
            Text = "Employee ID (serial number) whose rights will be overwritten",
            Font = BodyFont,
            ForeColor = SystemColors.ControlText,
            AutoSize = true,
            Location = new Point(14, 22)
        });

        targetBox.Controls.Add(targetInput);

        var ok = new Button
        {
            Text = "Continue",
            DialogResult = DialogResult.OK,
            Location = new Point(342, 296),
            Size = new Size(90, 32)
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(439, 296),
            Size = new Size(93, 32)
        };

        form.Controls.AddRange(new Control[] { heading, help, sourceBox, targetBox, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.ActiveControl = sourceInput;

        while (true)
        {
            if (form.ShowDialog() != DialogResult.OK)
            {
                return null;
            }

            var source = sourceInput.Text.Trim();
            var target = targetInput.Text.Trim();

            if (source.Length == 0 || target.Length == 0)
            {
                Warn("Please enter an employee ID for both portals.");
                continue;
            }

            return new EmployeeIds(source, target);
        }
    }

    /// <summary>
    /// Lets the operator pick when an employee search returns several people.
    /// </summary>
    public static int AskChoice(string title, string prompt, IReadOnlyList<string> options)
    {
        using var form = NewForm($"{AppTitle} - {title}", 620, 380);

        var label = new Label
        {
            Text = prompt,
            Font = BodyFont,
            AutoSize = true,
            Location = new Point(20, 18)
        };

        var list = new ListBox
        {
            Font = BodyFont,
            Location = new Point(20, 48),
            Size = new Size(562, 240),
            IntegralHeight = false
        };

        foreach (var option in options)
        {
            list.Items.Add(option);
        }

        list.SelectedIndex = 0;

        var ok = new Button
        {
            Text = "Select",
            DialogResult = DialogResult.OK,
            Location = new Point(402, 300),
            Size = new Size(90, 30)
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(499, 300),
            Size = new Size(83, 30)
        };

        form.Controls.AddRange(new Control[] { label, list, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? list.SelectedIndex : -1;
    }

    /// <summary>
    /// Confirms the two records before anything is compared.
    /// </summary>
    public static bool ConfirmRecords(
        string sourceHeading, string sourceUrl,
        string targetHeading, string targetUrl)
    {
        var message = new StringBuilder()
            .AppendLine("Rights will be copied between these two records.")
            .AppendLine()
            .AppendLine("COPY FROM")
            .AppendLine($"    {sourceHeading}")
            .AppendLine($"    {sourceUrl}")
            .AppendLine()
            .AppendLine("COPY TO")
            .AppendLine($"    {targetHeading}")
            .AppendLine($"    {targetUrl}")
            .AppendLine()
            .AppendLine("Only the second record will be modified.")
            .AppendLine()
            .AppendLine("Are these the correct two people?")
            .ToString();

        return MessageBox.Show(
            message,
            $"{AppTitle} - Confirm records",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    /// <summary>
    /// Shows the full comparison and asks whether to apply it. When the change
    /// set would revoke the target's own portal administration rights, offers a
    /// pre-ticked option to hold those particular fields back.
    /// </summary>
    public static ReviewResult ReviewChanges(
        string summaryLine,
        string details,
        IReadOnlyList<string> lockoutFields)
    {
        using var form = NewForm($"{AppTitle} - Review changes", 900, 660);

        var heading = new Label
        {
            Text = summaryLine,
            Font = HeadingFont,
            AutoSize = true,
            Location = new Point(20, 16)
        };

        var body = new TextBox
        {
            Text = details,
            Font = MonoFont,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            BackColor = Color.White,
            Location = new Point(20, 52),
            Size = new Size(844, 460)
        };

        form.Controls.AddRange(new Control[] { heading, body });

        var protectAdmin = new CheckBox
        {
            Text = "Keep the target's own portal-administration rights " +
                   "(recommended if this is your account)",
            Font = BoldFont,
            ForeColor = Color.FromArgb(160, 40, 0),
            Checked = true,
            AutoSize = true,
            Location = new Point(22, 524)
        };

        if (lockoutFields.Count > 0)
        {
            var warning = new Label
            {
                Text = "WARNING  This change set switches off " +
                       string.Join(" and ", lockoutFields) +
                       ", which is what grants access to the position-rights screen.",
                Font = BodyFont,
                ForeColor = Color.FromArgb(160, 40, 0),
                AutoSize = true,
                Location = new Point(22, 548)
            };

            form.Controls.Add(protectAdmin);
            form.Controls.Add(warning);
        }

        var apply = new Button
        {
            Text = "Apply changes",
            DialogResult = DialogResult.OK,
            Location = new Point(660, 578),
            Size = new Size(120, 32)
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(787, 578),
            Size = new Size(83, 32)
        };

        form.Controls.AddRange(new Control[] { apply, cancel });
        form.AcceptButton = apply;
        form.CancelButton = cancel;

        var approved = form.ShowDialog() == DialogResult.OK;

        return new ReviewResult(
            approved,
            lockoutFields.Count > 0 && protectAdmin.Checked);
    }

    /// <summary>
    /// Last gate. The changes are on screen but nothing has been submitted.
    /// </summary>
    public static bool ConfirmSave(int changeCount)
    {
        var message =
            $"{changeCount} field(s) are now set in the browser window, " +
            "but nothing has been submitted yet.\r\n\r\n" +
            "Review the target portal window, then choose Yes to save.\r\n\r\n" +
            "Save these rights now?";

        return MessageBox.Show(
            message,
            $"{AppTitle} - Confirm save",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    public static void Info(string message) =>
        MessageBox.Show(message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Warn(string message) =>
        MessageBox.Show(message, AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static void Error(string message) =>
        MessageBox.Show(message, $"{AppTitle} - Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>
    /// Used while waiting for a manual step in the browser (a login, an OTP).
    /// </summary>
    public static void WaitForBrowserStep(string message) =>
        MessageBox.Show(
            message,
            $"{AppTitle} - Action needed in the browser",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

    private static Form NewForm(string title, int width, int height) => new()
    {
        Text = title,
        Font = BodyFont,
        FormBorderStyle = FormBorderStyle.FixedDialog,
        StartPosition = FormStartPosition.CenterScreen,
        MaximizeBox = false,
        MinimizeBox = false,
        ClientSize = new Size(width, height),
        TopMost = true
    };

    internal readonly record struct ReviewResult(bool Approved, bool ProtectAdminRights);

    internal readonly record struct EmployeeIds(string Source, string Target);
}
