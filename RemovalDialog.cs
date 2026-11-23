namespace SurfArchive;

internal static class RemovalDialog
{
    public static bool Confirm(Form owner, bool local, string directory, IReadOnlyList<ArchiveEntry> items)
    {
        using var dialog = new Form
        {
            Text = local ? "Remove from Local / NAS" : "Remove from SURF Data Archive",
            ClientSize = new Size(555, 324), FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
            ShowInTaskbar = false
        };
        dialog.SuspendLayout();
        DarkTheme.Window(dialog);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20, 18, 20, 12), ColumnCount = 1, RowCount = 5
        };
        layout.RowStyles.Add(new(SizeType.Absolute, 34));
        layout.RowStyles.Add(new(SizeType.Absolute, 45));
        layout.RowStyles.Add(new(SizeType.Absolute, 30));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 44));
        layout.Controls.Add(DarkTheme.Label($"Permanently remove {items.Count:N0} item{(items.Count == 1 ? "" : "s")}?",
            DarkTheme.Heading, DarkTheme.Error), 0, 0);
        var warning = DarkTheme.Label(items.Any(item => item.IsDirectory)
            ? "This cannot be undone. Selected folders and everything inside them will be removed."
            : "This cannot be undone.");
        warning.AutoSize = false;
        warning.Dock = DockStyle.Fill;
        layout.Controls.Add(warning, 0, 1);

        var location = DarkTheme.Input("Removal location");
        location.Dock = DockStyle.Top;
        location.ReadOnly = true;
        location.Text = directory;
        layout.Controls.Add(location, 0, 2);

        var names = DarkTheme.Input("Items selected for removal");
        names.Dock = DockStyle.Fill;
        names.ReadOnly = true;
        names.Multiline = true;
        names.WordWrap = false;
        names.ScrollBars = ScrollBars.Both;
        names.Lines = items.Select(item => item.Name + (item.IsDirectory ? "    [folder]" : "")).ToArray();
        DarkTheme.Explorer(names);
        layout.Controls.Add(names, 0, 3);

        var remove = DarkTheme.Button("Remove permanently", ActionIcon.Trash);
        remove.ForeColor = DarkTheme.Error;
        remove.DialogResult = DialogResult.OK;
        var cancel = DarkTheme.Button("Cancel", ActionIcon.Cancel, primary: true);
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = DarkTheme.Toolbar(remove, cancel);
        buttons.Margin = new Padding(0, 7, 0, 0);
        buttons.FlowDirection = FlowDirection.RightToLeft;
        layout.Controls.Add(buttons, 0, 4);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => cancel.Focus();
        return DarkTheme.ShowDialog(dialog, owner) == DialogResult.OK;
    }
}
