namespace SurfArchive;

internal static class PackageDialog
{
    public static bool Confirm(Form owner, PackagePlan plan, IReadOnlyList<ArchiveEntry> selection, string local, string remote)
    {
        using var dialog = new Form
        {
            Text = "Archive to SURF Data Archive", ClientSize = new Size(735, 555),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false
        };
        DarkTheme.Window(dialog);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20, 18, 20, 12), ColumnCount = 1, RowCount = 9
        };
        foreach (int height in new[] { 30, 128, 28, 21, 33, 21, 33, -1, 43 })
            layout.RowStyles.Add(new(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
        dialog.Controls.Add(layout);
        layout.Controls.Add(DarkTheme.Label("Archive selected data and remove the originals?", DarkTheme.Heading, DarkTheme.Error), 0, 0);
        var explanation = DarkTheme.Label(
            "Pack the selection into an uncompressed TAR split into parts of up to 200 MB. " +
            "Each finished part starts uploading while packing continues.\n\n" +
            "The originals are permanently removed after the full local TAR is checked and all parts have uploaded. " +
            "The local parts are removed only after every SHA-256 on SURF matches. TAR does not preserve Windows permissions.");
        explanation.AutoSize = false;
        explanation.Dock = DockStyle.Fill;
        layout.Controls.Add(explanation, 0, 1);
        layout.Controls.Add(DarkTheme.Label(
            $"{selection.Count:N0} selected · {plan.Entries.Count:N0} total items · {TransferJob.FormatBytes(plan.Bytes)} source data" +
            (plan.UsesFallback ? " · Using L: due to insufficient space" : ""), DarkTheme.Small,
            plan.UsesFallback ? DarkTheme.Amber : DarkTheme.Muted), 0, 2);

        AddPath("Local recovery folder", local, 3);
        AddPath("SURF destination", remote, 5);
        var items = DarkTheme.Input("Items selected for archiving");
        items.Multiline = true;
        items.ReadOnly = true;
        items.WordWrap = false;
        items.Dock = DockStyle.Fill;
        items.ScrollBars = ScrollBars.Both;
        items.Lines = selection.Select(item => item.Name + (item.IsDirectory ? "    [folder]" : "")).ToArray();
        DarkTheme.Explorer(items);
        layout.Controls.Add(items, 0, 7);

        var confirm = DarkTheme.Button("Archive and remove originals");
        confirm.ForeColor = DarkTheme.Error;
        confirm.DialogResult = DialogResult.OK;
        var cancel = DarkTheme.Button("Cancel", primary: true);
        cancel.DialogResult = DialogResult.Cancel;
        var actions = DarkTheme.Row(confirm, cancel);
        actions.FlowDirection = FlowDirection.RightToLeft;
        actions.Margin = new Padding(0, 7, 0, 0);
        layout.Controls.Add(actions, 0, 8);
        dialog.AcceptButton = dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => cancel.Focus();
        return dialog.ShowDialog(owner) == DialogResult.OK;

        void AddPath(string title, string value, int row)
        {
            layout.Controls.Add(DarkTheme.Label(title, DarkTheme.Small, DarkTheme.Muted), 0, row);
            var control = DarkTheme.Input(title);
            control.ReadOnly = true;
            control.Text = value;
            control.Select(0, 0);
            control.Dock = DockStyle.Top;
            layout.Controls.Add(control, 0, row + 1);
        }
    }
}
