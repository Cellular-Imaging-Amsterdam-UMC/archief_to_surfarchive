namespace SurfArchive;

internal sealed record FolderListing(string Path, List<ArchiveEntry> Entries);

internal sealed class BrowserPane : UserControl
{
    private readonly bool local;
    private readonly FixedTreeView folders = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, HideSelection = false,
        ShowLines = false, FullRowSelect = true, DrawMode = TreeViewDrawMode.OwnerDrawAll,
        Font = DarkTheme.Small, ItemHeight = 23, Indent = 16, PathSeparator = "/", ShowNodeToolTips = true
    };
    private readonly ListView files = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
        BorderStyle = BorderStyle.None, MultiSelect = true, OwnerDraw = true, ShowItemToolTips = true
    };
    private readonly TextBox address = DarkTheme.Input("Current folder");
    private readonly TextBox filter = DarkTheme.Input("Filter folder contents");
    private readonly Button up = DarkTheme.Button("Up", ActionIcon.Up);
    private readonly Button reload = DarkTheme.Button("Refresh", ActionIcon.Refresh);
    private readonly Button recent = DarkTheme.Button("Recent", ActionIcon.History);
    private readonly ContextMenuStrip recentMenu = new()
    {
        BackColor = DarkTheme.Panel, ForeColor = DarkTheme.Ink, Font = DarkTheme.Small,
        ShowImageMargin = false, ShowItemToolTips = true,
        Renderer = new ToolStripProfessionalRenderer(new RecentMenuColors())
    };
    private readonly Label count = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Muted);
    private readonly SplitContainer split = new() { Dock = DockStyle.Fill, SplitterWidth = 6 };
    private readonly ComboBox locations = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 151, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly Label storage;
    private readonly ImageList icons = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private List<ArchiveEntry> entries = [];
    private int sortColumn;
    private bool descending;
    private bool updating;
    private bool canNavigate;
    private bool firstLayout = true;
    private RecentFolders? history;

    public BrowserPane(string heading, bool local)
    {
        this.local = local;
        storage = DarkTheme.Label("Personal archive", DarkTheme.Small, DarkTheme.Muted);
        Dock = DockStyle.Fill;
        BackColor = DarkTheme.Panel;
        Padding = new Padding(12);
        Name = heading;
        AccessibleName = heading;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        foreach (int height in new[] { 27, 32, 28, -1, 22 })
            panel.RowStyles.Add(new(height < 0 ? SizeType.Percent : SizeType.Absolute, height < 0 ? 100 : height));
        Controls.Add(panel);
        panel.Controls.Add(DarkTheme.Label(heading, DarkTheme.Heading), 0, 0);

        locations.BackColor = DarkTheme.Raised;
        locations.ForeColor = DarkTheme.Ink;
        locations.Margin = new Padding(0, 3, 9, 0);
        locations.AccessibleName = "Local / NAS location";
        DarkTheme.Explorer(locations, "DarkMode_CFD");
        locations.DrawItem += (_, e) =>
        {
            using var fill = new SolidBrush(e.State.HasFlag(DrawItemState.Selected) ? DarkTheme.Selected : DarkTheme.Raised);
            e.Graphics.FillRectangle(fill, e.Bounds);
            string label = e.Index < 0 ? "" : locations.GetItemText(locations.Items[e.Index]) ?? "";
            CellText(e.Graphics, label, e.Bounds, DarkTheme.Ink, 0);
        };
        storage.Margin = new Padding(0, 6, 13, 0);
        panel.Controls.Add(local ? DarkTheme.Row(locations, up, reload, recent)
            : DarkTheme.Row(up, reload, recent, storage), 0, 1);

        address.Dock = DockStyle.Top;
        address.Margin = Padding.Empty;
        address.ReadOnly = !local;
        panel.Controls.Add(address, 0, 2);
        panel.Controls.Add(split, 0, 3);
        count.Margin = new Padding(0, 5, 0, 0);
        panel.Controls.Add(count, 0, 4);

        split.BackColor = DarkTheme.Border;
        split.AccessibleName = heading + " folder and file splitter";
        split.Panel1.BackColor = split.Panel2.BackColor = DarkTheme.Page;
        split.Panel1.Controls.Add(TreeLayout());
        split.Panel2.Controls.Add(FileLayout());
        split.Layout += (_, _) =>
        {
            if (!firstLayout || split.ClientSize.Width < 330)
                return;
            firstLayout = false;
            split.SplitterDistance = Math.Clamp(split.ClientSize.Width / 3, 102, split.ClientSize.Width - 206);
            split.Panel1MinSize = 102;
            split.Panel2MinSize = 200;
        };

        locations.SelectionChangeCommitted += (_, _) =>
        {
            if (locations.SelectedItem is StorageLocation location)
                Navigate?.Invoke(location.Path, location.Label);
        };
        up.Click += (_, _) => OpenNode(Current?.Parent);
        reload.Click += (_, _) => OpenNode(Current);
        recent.Click += (_, _) => ShowRecent();
        folders.AfterSelect += (_, _) => OpenNode(folders.SelectedNode);
        folders.BeforeSelect += (_, e) => e.Cancel = !updating && !canNavigate;
        folders.BeforeExpand += (_, e) =>
        {
            if (!updating && !canNavigate)
            {
                e.Cancel = true;
                return;
            }
            if (!updating && e.Node?.FirstNode?.Tag is null)
            {
                e.Cancel = true;
                OpenNode(e.Node);
            }
        };
        files.ItemActivate += (_, _) =>
        {
            if (!canNavigate || files.SelectedItems.Count != 1 || files.SelectedItems[0].Tag is not ArchiveEntry entry)
                return;
            if (entry.IsDirectory)
                OpenNode(Current?.Nodes.Cast<TreeNode>().FirstOrDefault(child => child.Name == entry.Name));
            else if (FilePreview.IsSupported(entry.Name) || (!local && RetentionMetadata.IsFileName(entry.Name)))
                OpenFile?.Invoke(entry);
        };
        files.ItemSelectionChanged += (_, _) => SelectionChanged?.Invoke();
        files.ColumnClick += (_, e) =>
        {
            descending = sortColumn == e.Column && !descending;
            sortColumn = e.Column;
            RenderFiles();
        };
        filter.TextChanged += (_, _) => RenderFiles();
        address.KeyDown += (_, e) =>
        {
            if (!local || e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            string path = address.Text.Trim();
            if (path.Length > 0 && !path.Equals(PathShown, StringComparison.OrdinalIgnoreCase))
                Navigate?.Invoke(path, path);
        };
        folders.AccessibleName = heading + " folders";
        files.AccessibleName = heading + " files";
    }

    public event Action<string, string?>? Navigate;
    public event Action<RecentFolder>? NavigateRecent;
    public event Action<ArchiveEntry>? OpenFile;
    public event Action? SelectionChanged;
    public string PathShown { get; private set; } = "";
    public bool HasFolder => Current is not null;
    public IReadOnlyList<ArchiveEntry> SelectedItems => files.SelectedItems.Cast<ListViewItem>()
        .Select(item => (ArchiveEntry)item.Tag!).ToArray();
    public IReadOnlyList<ArchiveEntry> SelectedFiles => SelectedItems.Where(entry => !entry.IsDirectory).ToArray();
    public ArchiveEntry[] SnapshotEntries() => entries.ToArray();
    public bool HasKnownFolder(string path) => Find(folders.Nodes, path) is not null;

    private TreeNode? Current { get; set; }

    public void AddLocation(string label, string path)
    {
        locations.Items.Add(new StorageLocation(label, path));
    }

    public void SetHistory(RecentFolders? value) => history = value;

    private void ShowRecent()
    {
        if (history is not { } currentHistory)
            return;
        foreach (var item in recentMenu.Items.Cast<ToolStripItem>().ToArray())
            item.Dispose();
        recentMenu.Items.Clear();
        if (currentHistory.Items.Count == 0)
            recentMenu.Items.Add(new ToolStripMenuItem("No recent folders") { Enabled = false });
        foreach (var folder in currentHistory.Items)
        {
            string path = folder.Path.Length <= 75 ? folder.Path : "…" + folder.Path[^74..];
            var item = new ToolStripMenuItem(path.Replace("&", "&&")) { ToolTipText = folder.Path };
            item.Click += (_, _) => NavigateRecent?.Invoke(folder);
            recentMenu.Items.Add(item);
        }
        if (currentHistory.Items.Count > 0)
        {
            recentMenu.Items.Add(new ToolStripSeparator());
            recentMenu.Items.Add("Clear recent folders", null, (_, _) => currentHistory.Clear());
        }
        recentMenu.Show(recent, new Point(0, recent.Height));
    }

    private Control TreeLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(5) };
        layout.RowStyles.Add(new(SizeType.Absolute, 22));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(DarkTheme.Label("FOLDERS", DarkTheme.Small, DarkTheme.Muted), 0, 0);
        layout.Controls.Add(folders, 0, 1);
        folders.BackColor = DarkTheme.Page;
        folders.ForeColor = DarkTheme.Ink;
        DarkTheme.Explorer(folders);

        using (var icon = new Bitmap(16, 16))
        {
            using var g = Graphics.FromImage(icon);
            DarkTheme.DrawIcon(g, new Rectangle(0, 0, 16, 16), folder: true);
            icons.Images.Add((Bitmap)icon.Clone());
        }
        folders.ImageList = icons;
        folders.DrawNode += (_, e) =>
        {
            if (e.Node is not { } node)
                return;
            Color background = node == Current ? DarkTheme.Selected : DarkTheme.Page;
            var area = new Rectangle(0, e.Bounds.Y, folders.ClientSize.Width, e.Bounds.Height);
            using var fill = new SolidBrush(background);
            e.Graphics.FillRectangle(fill, area);
            int textLeft = Math.Max(34, node.Bounds.Left);
            if (textLeft >= area.Right)
                return;
            int middle = area.Top + area.Height / 2;
            if (node.Nodes.Count > 0)
            {
                int x = textLeft - 30;
                using var pen = new Pen(DarkTheme.Muted, 1.4f);
                Point[] points = node.IsExpanded
                    ? [new(x - 3, middle - 1), new(x, middle + 2), new(x + 3, middle - 1)]
                    : [new(x - 1, middle - 3), new(x + 2, middle), new(x - 1, middle + 3)];
                e.Graphics.DrawLines(pen, points);
            }
            DarkTheme.DrawIcon(e.Graphics, new Rectangle(textLeft - 20, middle - 8, 16, 16), folder: true);
            CellText(e.Graphics, node.Text, new Rectangle(textLeft, area.Y, area.Right - textLeft, area.Height),
                DarkTheme.Ink, 0);
        };
        return layout;
    }

    private Control FileLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(5) };
        layout.RowStyles.Add(new(SizeType.Absolute, 30));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        filter.Dock = DockStyle.Top;
        filter.PlaceholderText = "Filter by name";
        filter.Margin = Padding.Empty;
        layout.Controls.Add(filter, 0, 0);
        layout.Controls.Add(files, 0, 1);

        files.BackColor = DarkTheme.Page;
        files.ForeColor = DarkTheme.Ink;
        DarkTheme.Explorer(files);
        files.Columns.Add("Name", 185);
        files.Columns.Add("Size", 68, HorizontalAlignment.Right);
        files.Columns.Add("Modified", 118);
        if (!local)
            files.Columns.Add("Archive status", 112);
        files.Resize += (_, _) => FitFileColumns();
        files.DrawColumnHeader += (_, e) =>
        {
            using var fill = new SolidBrush(DarkTheme.Raised);
            e.Graphics.FillRectangle(fill, e.Bounds);
            string name = e.Header?.Text ?? "";
            if (sortColumn == e.ColumnIndex)
                name += descending ? "  ↓" : "  ↑";
            CellText(e.Graphics, name, e.Bounds, DarkTheme.Muted, e.ColumnIndex);
        };
        files.DrawSubItem += (_, e) =>
        {
            if (e.Item?.Tag is not ArchiveEntry entry)
                return;
            using var fill = new SolidBrush(e.Item.Selected ? DarkTheme.Selected : DarkTheme.Page);
            e.Graphics.FillRectangle(fill, e.Bounds);
            Rectangle text = e.Bounds;
            if (e.ColumnIndex == 0)
            {
                DarkTheme.DrawIcon(e.Graphics, new Rectangle(text.X + 5, text.Y + (text.Height - 16) / 2, 16, 16), entry.IsDirectory);
                text.X += 20;
                text.Width -= 20;
            }
            Color color = e.ColumnIndex switch
            {
                0 => DarkTheme.Ink,
                3 when entry.Status is ArchiveState.REG or ArchiveState.DUL => DarkTheme.Mint,
                3 when entry.Status is ArchiveState.OFL or ArchiveState.STG or ArchiveState.QUE => DarkTheme.Amber,
                _ => DarkTheme.Muted
            };
            CellText(e.Graphics, e.SubItem?.Text ?? "", text, color, e.ColumnIndex);
        };
        return layout;
    }

    private static void CellText(Graphics graphics, string text, Rectangle bounds, Color color, int column)
    {
        bounds.Inflate(-5, 0);
        TextRenderer.DrawText(graphics, text, DarkTheme.Small, bounds, color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
            | (column == 1 ? TextFormatFlags.Right : TextFormatFlags.Left));
    }

    private void OpenNode(TreeNode? node)
    {
        if (!updating && canNavigate && node?.Tag is string path)
            Navigate?.Invoke(path, null);
    }

    public void ShowFolder(string path, string? rootLabel, List<ArchiveEntry> content,
        IReadOnlyList<FolderListing>? ancestors = null)
    {
        var comparison = local ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool refreshing = rootLabel is null && path.Equals(PathShown, comparison);
        var selected = refreshing ? SelectedItems.Select(item => item.Name).ToHashSet(StringComparer.Ordinal) : null;
        updating = true;
        folders.BeginUpdate();
        try
        {
            if (rootLabel is not null)
            {
                folders.Nodes.Clear();
                Current = folders.Nodes.Add(rootLabel);
                string rootPath = ancestors is { Count: > 0 } ? ancestors[0].Path : path;
                Current.Tag = rootPath;
                SelectLocation(rootPath);
                if (ancestors is not null)
                {
                    for (int i = 0; i < ancestors.Count; i++)
                    {
                        RefreshChildren(Current, ancestors[i].Entries);
                        Current.Expand();
                        string next = i + 1 < ancestors.Count ? ancestors[i + 1].Path : path;
                        Current = Current.Nodes.Cast<TreeNode>().First(node => Equals(node.Tag, next));
                    }
                }
            }
            else
            {
                Current = Find(folders.Nodes, path) ?? throw new InvalidOperationException("The folder is no longer in the tree.");
            }

            RefreshChildren(Current, content);
            folders.SelectedNode = Current;
            Current.Expand();
            Current.EnsureVisible();
            PathShown = path;
            address.Text = path;
            entries = content;
            if (!refreshing)
                filter.Clear();
            RenderFiles();
            if (selected is { Count: > 0 })
            {
                foreach (ListViewItem row in files.Items)
                    row.Selected = row.Tag is ArchiveEntry entry && selected.Contains(entry.Name);
            }
            if (folders.Nodes[0].Tag is string root)
                history?.Use(new(path, root, folders.Nodes[0].Text));
        }
        finally
        {
            folders.EndUpdate();
            updating = false;
        }
    }

    private static TreeNode? Find(TreeNodeCollection nodes, string path)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is not string candidate)
                continue;
            if (string.Equals(candidate, path, StringComparison.Ordinal))
                return node;
            if (path.StartsWith(candidate.EndsWith('\\') ? candidate : candidate + "\\", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(candidate + "/", StringComparison.Ordinal))
                return Find(node.Nodes, path);
        }
        return null;
    }

    private void RefreshChildren(TreeNode node, List<ArchiveEntry> content)
    {
        var directories = content.Where(item => item.IsDirectory)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var names = directories.Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        foreach (TreeNode stale in node.Nodes.Cast<TreeNode>().Where(child => child.Tag is not string || !names.Contains(child.Name)).ToArray())
            node.Nodes.Remove(stale);
        var existing = node.Nodes.Cast<TreeNode>().Select(child => child.Name).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < directories.Length; i++)
        {
            var entry = directories[i];
            if (existing.Contains(entry.Name))
                continue;
            string parent = (string)node.Tag!;
            string childPath = local ? Path.Combine(parent, entry.Name) : ArchiveSession.Child(parent, entry.Name);
            var child = new TreeNode(entry.Name) { Name = entry.Name, Tag = childPath, ToolTipText = entry.Name };
            child.Nodes.Add("Loading…");
            node.Nodes.Insert(i, child);
        }
    }

    public void RestoreLocation()
    {
        updating = true;
        try
        {
            folders.SelectedNode = Current;
            address.Text = PathShown;
            SelectLocation(folders.Nodes.Count == 0 ? null : folders.Nodes[0].Tag as string);
        }
        finally { updating = false; }
    }

    private void SelectLocation(string? path)
    {
        locations.SelectedItem = locations.Items.Cast<StorageLocation>().FirstOrDefault(
            location => location.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    public void ClearFolder()
    {
        updating = true;
        try
        {
            Current = null;
            PathShown = "";
            folders.Nodes.Clear();
            address.Clear();
            entries.Clear();
            RenderFiles();
        }
        finally { updating = false; }
    }

    public void SetAvailable(bool idle, bool available)
    {
        canNavigate = idle && available;
        locations.Enabled = idle;
        filter.Enabled = canNavigate;
        address.Enabled = idle && (local || available);
        up.Enabled = idle && Current?.Parent is not null;
        reload.Enabled = idle && Current is not null;
        recent.Enabled = idle && history is not null;
    }

    private void RenderFiles()
    {
        IEnumerable<ArchiveEntry> visible = entries.Where(item => item.Name.Contains(filter.Text, StringComparison.CurrentCultureIgnoreCase));
        var ordered = visible.OrderByDescending(item => item.IsDirectory);
        ordered = sortColumn switch
        {
            1 => descending ? ordered.ThenByDescending(item => item.Size) : ordered.ThenBy(item => item.Size),
            2 => descending ? ordered.ThenByDescending(item => item.Modified) : ordered.ThenBy(item => item.Modified),
            3 => descending ? ordered.ThenByDescending(item => item.Status) : ordered.ThenBy(item => item.Status),
            _ => descending ? ordered.ThenByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                : ordered.ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        var rows = ordered.Select(item =>
        {
            var row = new ListViewItem(new[] { item.Name, item.IsDirectory ? "Folder" : TransferJob.FormatBytes(item.Size),
                item.Modified.ToString("dd MMM yy HH:mm") }) { Tag = item, ToolTipText = item.Name };
            if (!local)
            {
                row.SubItems.Add(StatusText(item));
                row.ToolTipText += Environment.NewLine + StatusDetails(item);
                if (item.HasSubfolders == true)
                    row.ToolTipText += Environment.NewLine + "Has subfolders";
            }
            return row;
        }).ToArray();

        files.BeginUpdate();
        try
        {
            files.Items.Clear();
            files.Items.AddRange(rows);
            FitFileColumns();
            count.Text = HasFolder ? (rows.Length == entries.Count ? $"{rows.Length:N0} items" : $"{rows.Length:N0} of {entries.Count:N0} items") : "";
        }
        finally { files.EndUpdate(); }
        SelectionChanged?.Invoke();
    }

    private void FitFileColumns()
    {
        int width = files.ClientSize.Width;
        if (width < 240)
            return;
        if (!local)
        {
            files.Columns[1].Width = Math.Clamp(width * 15 / 100, 49, 70);
            files.Columns[2].Width = Math.Clamp(width * 24 / 100, 75, 120);
            files.Columns[3].Width = Math.Clamp(width * 26 / 100, 76, 125);
        }
        files.Columns[0].Width = Math.Max(65, width - files.Columns.Cast<ColumnHeader>().Skip(1).Sum(column => column.Width));
    }

    public void ApplyStatuses(IReadOnlyDictionary<string, ArchiveState> states, bool finish)
    {
        UpdateStatuses(entry =>
        {
            if (entry.IsDirectory)
                return entry;
            if (states.TryGetValue(entry.Name, out var state))
                return entry with { Status = state };
            return finish && entry.Status is null ? entry with { Status = ArchiveState.Unknown } : entry;
        });
    }

    public void ApplyFolderStatus(FolderArchiveStatus status) =>
        UpdateStatuses(entry => entry.IsDirectory && entry.Name == status.Name
            ? entry with { Status = status.State, HasSubfolders = status.HasSubfolders }
            : entry);

    private void UpdateStatuses(Func<ArchiveEntry, ArchiveEntry> apply)
    {
        if (local)
            return;
        for (int i = 0; i < entries.Count; i++)
            entries[i] = apply(entries[i]);
        foreach (ListViewItem row in files.Items)
        {
            var original = (ArchiveEntry)row.Tag!;
            var entry = apply(original);
            if (entry == original)
                continue;
            row.Tag = entry;
            row.SubItems[3].Text = StatusText(entry);
            row.ToolTipText = entry.Name + Environment.NewLine + StatusDetails(entry);
            if (entry.HasSubfolders == true)
                row.ToolTipText += Environment.NewLine + "Has subfolders";
        }
        files.Invalidate();
    }

    private static string StatusText(ArchiveEntry entry) => (entry.HasSubfolders, entry.IsDirectory, entry.Status) switch
    {
        (true, _, ArchiveState.OFL) => "Tape · Subfolders",
        (true, _, _) => "Has subfolders",
        (_, true, ArchiveState.OFL) => "Tape files",
        _ => StatusDetails(entry)
    };

    private static string StatusDetails(ArchiveEntry entry) => entry.IsDirectory ? entry.Status switch
    {
        ArchiveState.OFL => "Contains tape files",
        ArchiveState.QUE => "Waiting for disk",
        ArchiveState.STG => "Restoring files",
        ArchiveState.REG or ArchiveState.DUL => "Files on disk",
        ArchiveState.Unknown => "Unknown",
        _ => "Checking…"
    } : entry.Status is { } state ? ArchiveStatus.Label(state) : "Checking…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            icons.Dispose();
            recentMenu.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record StorageLocation(string Label, string Path)
    {
        public override string ToString() => Label;
    }

    private sealed class FixedTreeView : TreeView
    {
        protected override CreateParams CreateParams
        {
            get
            {
                const int TvsNoHorizontalScroll = 0x8000;
                var parameters = base.CreateParams;
                parameters.Style |= TvsNoHorizontalScroll;
                return parameters;
            }
        }
    }

    private sealed class RecentMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => DarkTheme.Panel;
        public override Color MenuBorder => DarkTheme.Border;
        public override Color MenuItemBorder => DarkTheme.Selected;
        public override Color MenuItemSelected => DarkTheme.Selected;
        public override Color SeparatorLight => DarkTheme.Border;
        public override Color SeparatorDark => DarkTheme.Border;
    }
}
