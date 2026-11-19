namespace SurfArchive;

internal sealed record PackageOptions(bool RemoveOriginals, RetentionMetadata Retention, string? LocalStagingFolder = null);

internal static class PackageDialog
{
    public static bool ExplainResume(Form owner)
    {
        using var dialog = CreateDialog("Resume archive upload", new Size(695, 476));
        var layout = Layout([32, 49, 25, 65, 25, 57, 25, 60, -1, 38]);
        dialog.Controls.Add(layout);
        layout.Controls.Add(DarkTheme.Label("Continue an interrupted archive upload", DarkTheme.Heading), 0, 0);
        Body(1, "Use this if an Archive to SURF job failed or was cancelled and the app kept a verified local recovery folder.");

        Section(2, "1. Find the local recovery folder",
            "Look for “Local package folder kept” in Activity. Check next to the source, on L:, or in the remembered local staging folder. Select the SURF-… folder containing package.json.");
        Section(4, "2. Finish the upload to the original destination",
            "The app uploads missing package files to the same SURF folder. It checks the SHA-256 of every file on SURF, including files already uploaded.");
        Section(6, "3. Clean up the recovery folder",
            "The app removes local package files only after all checks pass. It leaves the original source files alone. If a check fails, the recovery files remain available for another attempt.");
        Body(8, "For files already on tape, use Restore from tape. Double-click an HTML or TXT file to preview it, or retention.json to edit it.",
            DarkTheme.Muted);

        var next = DarkTheme.Button("Choose recovery folder…", ActionIcon.Browse, primary: true);
        next.DialogResult = DialogResult.OK;
        var cancel = Cancel();
        layout.Controls.Add(Actions(next, cancel), 0, 9);
        dialog.AcceptButton = next;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK;

        void Section(int row, string heading, string text)
        {
            layout.Controls.Add(DarkTheme.Label(heading, DarkTheme.Small, DarkTheme.Mint), 0, row);
            Body(row + 1, text);
        }

        void Body(int row, string text, Color? color = null)
        {
            var label = DarkTheme.Label(text, DarkTheme.Small, color);
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            layout.Controls.Add(label, 0, row);
        }
    }

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
        var next = DarkTheme.Button("Continue", ActionIcon.Check, primary: true);
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

    public static PackageOptions? Confirm(Form owner, IReadOnlyList<ArchiveRequest> requests)
    {
        using var dialog = CreateDialog("Archive to SURF Data Archive", new Size(764, 735));
        var layout = Layout([31, 54, 28, 38, 24, 29, 35, 46, -1, 29, 150, 42, 40]);
        dialog.Controls.Add(layout);
        layout.Controls.Add(DarkTheme.Label("Add selected archives to the queue?", DarkTheme.Heading), 0, 0);
        var explanation = DarkTheme.Label(
            "Create a ZIP in numbered parts of up to 200 MB. Finished parts upload while packing continues. " +
            "The archive includes contents files and retention.json. Open the .zip.001 file in 7-Zip to extract it. " +
            "Windows permissions are outside the ZIP archive.");
        explanation.AutoSize = false;
        explanation.Dock = DockStyle.Fill;
        layout.Controls.Add(explanation, 0, 1);
        var remove = new CheckBox
        {
            Text = "Remove originals after the local archive and all SURF SHA-256 checks pass",
            Checked = true, AutoSize = true, ForeColor = DarkTheme.Ink
        };
        layout.Controls.Add(remove, 0, 2);
        var warning = DarkTheme.Label("", DarkTheme.Small);
        warning.AutoSize = false;
        warning.Dock = DockStyle.Fill;
        layout.Controls.Add(warning, 0, 3);

        int itemsCount = requests.Sum(request => request.Plan.Entries.Count);
        long bytes = requests.Sum(request => request.Plan.Bytes);
        string archives = requests.Count == 1 ? "1 archive" : $"{requests.Count:N0} archives";
        string entries = itemsCount == 1 ? "1 item" : $"{itemsCount:N0} items";
        layout.Controls.Add(DarkTheme.Label($"{archives} · {entries} · {TransferJob.FormatBytes(bytes)} source data",
            DarkTheme.Small, DarkTheme.Muted), 0, 4);
        var preference = LocalStagingSettings.Read();
        var stageLocally = new CheckBox
        {
            Text = "Use a temporary staging folder on this PC",
            AutoSize = true, ForeColor = DarkTheme.Ink, Checked = preference.Enabled
        };
        layout.Controls.Add(stageLocally, 0, 5);
        var stagingPath = DarkTheme.Input("Local staging folder");
        stagingPath.Text = preference.Folder;
        stagingPath.ReadOnly = true;
        stagingPath.Dock = DockStyle.Top;
        var browse = DarkTheme.Button("Browse…", ActionIcon.Browse);
        var location = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        location.ColumnStyles.Add(new(SizeType.Percent, 100));
        location.ColumnStyles.Add(new(SizeType.AutoSize));
        location.Controls.Add(stagingPath, 0, 0);
        location.Controls.Add(browse, 1, 0);
        layout.Controls.Add(location, 0, 6);
        var stagingInfo = StatusLabel("");
        layout.Controls.Add(stagingInfo, 0, 7);
        var items = DarkTheme.Input("Selected items and package destinations");
        items.Multiline = true;
        items.ReadOnly = true;
        items.WordWrap = false;
        items.Dock = DockStyle.Fill;
        items.ScrollBars = ScrollBars.Both;
        DarkTheme.Explorer(items);
        layout.Controls.Add(items, 0, 8);

        var retention = new RetentionMetadata(DateOnly.FromDateTime(DateTime.Today), new RetentionFields());
        var fields = new RetentionInput(retention);
        layout.Controls.Add(DarkTheme.Label(requests.Count > 1 ? "Retention for all selected archives" : "Archive retention",
            DarkTheme.Heading), 0, 9);
        layout.Controls.Add(fields, 0, 10);
        var feedback = StatusLabel("The retention file records your intended minimum storage period. It does not change SURF's storage settings.");
        layout.Controls.Add(feedback, 0, 11);

        var confirm = DarkTheme.Button("Queue archive", ActionIcon.Archive);
        var cancel = Cancel();
        var actions = Actions(confirm, cancel);
        actions.Margin = new Padding(0, 7, 0, 0);
        layout.Controls.Add(actions, 0, 12);
        dialog.AcceptButton = dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => cancel.Focus();
        remove.CheckedChanged += (_, _) => UpdateWarning();
        stageLocally.CheckedChanged += (_, _) => RememberStaging();
        browse.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Choose a temporary staging folder on this PC",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(stagingPath.Text) ? stagingPath.Text : "",
                ShowNewFolderButton = true
            };
            if (picker.ShowDialog(dialog) != DialogResult.OK)
                return;
            stagingPath.Text = picker.SelectedPath;
            RememberStaging();
        };
        UpdateWarning();
        UpdateStaging();
        PackageOptions? options = null;
        confirm.Click += (_, _) =>
        {
            if (!RememberStaging())
                return;
            if (fields.Read(out string error) is not { } values)
            {
                feedback.ForeColor = DarkTheme.Error;
                feedback.Text = error;
                return;
            }
            options = new PackageOptions(remove.Checked, retention with { Fields = values },
                stageLocally.Checked ? stagingPath.Text : null);
            dialog.DialogResult = DialogResult.OK;
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? options : null;

        bool RememberStaging()
        {
            if (!UpdateStaging())
                return false;
            var selected = new LocalStagingSettings(stageLocally.Checked, stagingPath.Text);
            if (selected != preference)
            {
                if (selected.Save())
                    preference = selected;
                else
                    stagingInfo.Text += " The app could not save the staging preference.";
            }
            return true;
        }

        bool UpdateStaging()
        {
            IReadOnlyList<ArchiveRequest> shown = requests;
            stagingInfo.ForeColor = DarkTheme.Muted;
            browse.Enabled = stageLocally.Checked;
            bool ready = true;
            if (stageLocally.Checked)
            {
                try
                {
                    ulong free = PackageFiles.LocalStagingFreeBytes(stagingPath.Text);
                    shown = requests.Select(request => request with
                    {
                        Plan = request.Plan.WithPreferredStaging(stagingPath.Text, free)
                    }).ToArray();
                    long required = shown.Max(request => request.Plan.Required);
                    int fallback = shown.Count(request => request.Plan.UsesFallback);
                    stagingInfo.Text = $"{TransferJob.FormatBytes(free)} free locally. " +
                        $"The largest archive needs about {TransferJob.FormatBytes(required)}.";
                    if (fallback > 0)
                    {
                        stagingInfo.ForeColor = DarkTheme.Amber;
                        stagingInfo.Text += fallback == 1 ? " One archive will use the staging folder on L:."
                            : $" {fallback:N0} archives will use the staging folder on L:.";
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    ready = false;
                    stagingInfo.ForeColor = DarkTheme.Error;
                    stagingInfo.Text = error.Message;
                }
            }
            else
            {
                stagingInfo.Text = requests.Any(request => request.Plan.UsesFallback)
                    ? "Some archives will use the staging folder on L: because the source drive has less free space."
                    : "ZIP parts will be staged next to the source data.";
            }
            items.Lines = shown.SelectMany(request => new[] {
                $"SURF    {request.RemotePath}", $"Local   {request.LocalPath}",
                $"Items   {string.Join("  ·  ", request.Items.Select(item => item.Name + (item.IsDirectory ? " [folder]" : "")))}", ""
            }).ToArray();
            items.Select(0, 0);
            return ready;
        }

        void UpdateWarning()
        {
            warning.Text = remove.Checked
                ? "The originals will be permanently removed. This is the recommended setting for moving data to the archive."
                : "The originals stay in the source folder and continue to use space there.";
            warning.ForeColor = remove.Checked ? DarkTheme.Error : DarkTheme.Amber;
            confirm.Text = remove.Checked ? "Queue archive and remove originals" : "Queue archive and keep originals";
            confirm.ForeColor = remove.Checked ? DarkTheme.Error : DarkTheme.Ink;
        }
    }

    public static bool EditRetention(Form owner, string path, RetentionMetadata original,
        Func<byte[], Task> save, CancellationToken token)
    {
        using var dialog = CreateDialog("Edit archive retention", new Size(690, 394));
        var layout = Layout([31, 48, 26, -1, 52, 38]);
        dialog.Controls.Add(layout);
        layout.Controls.Add(DarkTheme.Label("Edit archive retention", DarkTheme.Heading), 0, 0);
        layout.Controls.Add(StatusLabel(path), 0, 1);
        layout.Controls.Add(DarkTheme.Label(original.ArchivedOn is { } date ? $"Archived on {date:yyyy-MM-dd}" : "Archive date unknown",
            DarkTheme.Small, DarkTheme.Muted), 0, 2);
        var fields = new RetentionInput(original);
        layout.Controls.Add(fields, 0, 3);
        var feedback = StatusLabel("Save updates retention.json on SURF.");
        layout.Controls.Add(feedback, 0, 4);
        var confirm = DarkTheme.Button("Save to SURF", ActionIcon.Save, primary: true);
        var cancel = Cancel();
        layout.Controls.Add(Actions(confirm, cancel), 0, 5);
        dialog.CancelButton = cancel;
        bool saving = false;
        dialog.FormClosing += (_, e) => e.Cancel = saving && !token.IsCancellationRequested;
        using var registration = token.Register(() =>
        {
            if (dialog.IsHandleCreated)
                dialog.BeginInvoke(() => dialog.Close());
        });
        confirm.Click += async (_, _) =>
        {
            if (fields.Read(out string validationError) is not { } values)
            {
                feedback.ForeColor = DarkTheme.Error;
                feedback.Text = validationError;
                return;
            }
            saving = true;
            confirm.Enabled = cancel.Enabled = fields.Enabled = false;
            feedback.ForeColor = DarkTheme.Muted;
            feedback.Text = "Saving and checking the updated retention file on SURF…";
            try
            {
                token.ThrowIfCancellationRequested();
                await save((original with { Fields = values }).ToJson());
                saving = false;
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception error)
            {
                saving = false;
                if (!dialog.IsDisposed)
                {
                    confirm.Enabled = cancel.Enabled = fields.Enabled = true;
                    feedback.ForeColor = DarkTheme.Error;
                    feedback.Text = error.Message;
                }
            }
        };
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    private static Label StatusLabel(string text)
    {
        var label = DarkTheme.Label(text, DarkTheme.Small, DarkTheme.Muted);
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        return label;
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
        var cancel = DarkTheme.Button("Cancel", ActionIcon.Cancel);
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
