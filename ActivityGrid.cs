namespace SurfArchive;

internal sealed class ActivityGrid : DataGridView
{
    public const int ItemHeight = 26;
    public const int HeaderHeight = 27;

    public ActivityGrid()
    {
        Dock = DockStyle.Fill;
        ReadOnly = true;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        AllowUserToResizeRows = false;
        RowHeadersVisible = false;
        MultiSelect = false;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        Font = DarkTheme.Small;
        BorderStyle = BorderStyle.None;
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        GridColor = DarkTheme.Border;
        BackgroundColor = DarkTheme.Panel;
        EnableHeadersVisualStyles = false;
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        ColumnHeadersHeight = HeaderHeight;
        RowTemplate.Height = ItemHeight;
        DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = DarkTheme.Panel, ForeColor = DarkTheme.Ink,
            SelectionBackColor = DarkTheme.Selected, SelectionForeColor = DarkTheme.Ink,
            Padding = new Padding(5, 0, 5, 0)
        };
        AlternatingRowsDefaultCellStyle.BackColor = DarkTheme.Page;
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = DarkTheme.Raised, ForeColor = DarkTheme.Muted,
            SelectionBackColor = DarkTheme.Raised, SelectionForeColor = DarkTheme.Muted,
            Padding = new Padding(5, 0, 5, 0)
        };
        DoubleBuffered = true;
        DarkTheme.Explorer(VerticalScrollBar);
        DarkTheme.Explorer(HorizontalScrollBar);
    }

    public void AddTextColumn(string title, int width, bool fill = false) =>
        Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = title, Width = width, MinimumWidth = fill ? 130 : 45,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
}
