namespace SurfArchive;

internal static class PackageDialog
{
    public static string? AskFolderName(Form owner)
    {
        using var dialog = CreateDialog("Name the SURF folder", new Size(510, 181));
        var layout = Layout([31, 29, 30, 34]);
        dialog.Controls.Add(layout);
        layout.Controls.Add(DarkTheme.Label("Name the folder for these files on SURF", DarkTheme.Heading), 0, 0);
        var input = DarkTheme.Input("SURF folder name");
        input.PlaceholderText = "Folder name";
        input.Dock = DockStyle.Top;
        layout.Controls.Add(input, 0, 1);
        var feedback = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Error);
        feedback.AutoSize = false;
        feedback.Dock = DockStyle.Fill;
        layout.Controls.Add(feedback, 0, 2);
        var next = DarkTheme.Button("Continue", primary: true);
        var cancel = Cancel();
        layout.Controls.Add(Actions(next, cancel), 0, 3);
        dialog.AcceptButton = next;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => input.Focus();
        string? name = null;
        next.Click += (_, _) =>
        {
            try
            {
                string candidate = input.Text.Trim();
                ArchivePackage.ValidateFolderName(candidate);
                name = candidate;
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception error) when (error is IOException or ArgumentException)
            {
                feedback.Text = error.Message;
                input.Focus();
            }
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? name : null;
    }

    public static bool? Confirm(Form owner, IReadOnlyList<ArchiveRequest> requests)
    {
        using var dialog = CreateDialog("Archive to SURF Data Archive", new Size(764, 543));
        var layout = Layout([31, 80, 29, 55, 32, -1, 43]);
        dialog.Controls.Add(layout);
        layout.Controls.Add(DarkTheme.Label("Archive selected data to SURF?", DarkTheme.Heading), 0, 0);
        var explanation = DarkTheme.Label(
            "Compress with fast gzip and split into numbered parts of up to 200 MB. Finished parts upload while " +
            "packing continues. A text index and a browsable HTML folder tree are included. Local archive parts " +
            "are removed after every SURF SHA-256 matches. TAR does not preserve Windows permissions.");
        explanation.AutoSize = false;
        explanation.Dock = DockStyle.Fill;
        layout.Controls.Add(explanation, 0, 1);
        var remove = new CheckBox
        {
            Text = "Remove originals after the local archive is verified and all uploads finish",
            Checked = true, AutoSize = true, ForeColor = DarkTheme.Ink
        };
        layout.Controls.Add(remove, 0, 2);
        var warning = DarkTheme.Label("", DarkTheme.Small);
        warning.AutoSize = false;
        warning.Dock = DockStyle.Fill;
        layout.Controls.Add(warning, 0, 3);

        int itemsCount = requests.Sum(request => request.Plan.Entries.Count);
        long bytes = requests.Sum(request => request.Plan.Bytes);
        layout.Controls.Add(DarkTheme.Label(
            $"{requests.Count:N0} package(s) · {itemsCount:N0} items · {TransferJob.FormatBytes(bytes)} source data" +
            (requests.Any(request => request.Plan.UsesFallback) ? " · Some packages need staging on L:" : ""),
            DarkTheme.Small, DarkTheme.Muted), 0, 4);
        var items = DarkTheme.Input("Selected items and package destinations");
        items.Multiline = true;
        items.ReadOnly = true;
        items.WordWrap = false;
        items.Dock = DockStyle.Fill;
        items.ScrollBars = ScrollBars.Both;
        items.Lines = requests.SelectMany(request => new[] {
            $"SURF    {request.RemotePath}", $"Local   {request.LocalPath}",
            $"Items   {string.Join("  ·  ", request.Items.Select(item => item.Name + (item.IsDirectory ? " [folder]" : "")))}", ""
        }).ToArray();
        items.Select(0, 0);
        DarkTheme.Explorer(items);
        layout.Controls.Add(items, 0, 5);

        var confirm = DarkTheme.Button("Archive and remove originals");
        confirm.DialogResult = DialogResult.OK;
        var cancel = Cancel();
        var actions = Actions(confirm, cancel);
        actions.Margin = new Padding(0, 7, 0, 0);
        layout.Controls.Add(actions, 0, 6);
        dialog.AcceptButton = dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => cancel.Focus();
        remove.CheckedChanged += (_, _) => UpdateWarning();
        UpdateWarning();
        return dialog.ShowDialog(owner) == DialogResult.OK ? remove.Checked : null;

        void UpdateWarning()
        {
            warning.Text = remove.Checked
                ? "The originals will be permanently removed. This is the recommended setting for moving data to the archive."
                : "Warning: the originals stay on Windows and no source space is freed. Leave removal on for a normal archive move.";
            warning.ForeColor = remove.Checked ? DarkTheme.Error : DarkTheme.Amber;
            confirm.Text = remove.Checked ? "Archive and remove originals" : "Archive and keep originals";
            confirm.ForeColor = remove.Checked ? DarkTheme.Error : DarkTheme.Ink;
        }
    }

    private static Form CreateDialog(string title, Size size)
    {
        var dialog = new Form
        {
            Text = title, ClientSize = size, FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        DarkTheme.Window(dialog);
        return dialog;
    }

    private static TableLayoutPanel Layout(int[] heights)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20, 18, 20, 12), ColumnCount = 1, RowCount = heights.Length
        };
        foreach (int height in heights)
            layout.RowStyles.Add(new(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
        return layout;
    }

    private static Button Cancel()
    {
        var cancel = DarkTheme.Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        return cancel;
    }

    private static FlowLayoutPanel Actions(Button confirm, Button cancel)
    {
        var buttons = DarkTheme.Row(confirm, cancel);
        buttons.FlowDirection = FlowDirection.RightToLeft;
        return buttons;
    }
}
