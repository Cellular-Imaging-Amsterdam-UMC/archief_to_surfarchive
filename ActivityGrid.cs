namespace SurfArchive;

internal sealed class ActivityGrid : DataGridView
{
    public const int ItemHeight = 26;
    public const int HeaderHeight = 27;
    private readonly Dictionary<DataGridViewColumn, (int Width, int Minimum)> columnsAt96Dpi = [];
    public int HorizontalScrollbarHeight => HorizontalScrollBar.Visible ? HorizontalScrollBar.Height : 0;
    public event EventHandler? HorizontalScrollbarChanged
    {
        add => HorizontalScrollBar.VisibleChanged += value;
        remove => HorizontalScrollBar.VisibleChanged -= value;
    }

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
        FontChanged += (_, _) => FitDpi();
        DarkTheme.Explorer(VerticalScrollBar);
        DarkTheme.Explorer(HorizontalScrollBar);
    }

    public int HeightFor(int rows) => ColumnHeadersHeight + rows * RowTemplate.Height + HorizontalScrollbarHeight;

    public void AddTextColumn(string title, int width, bool fill = false) =>
        AddScaledColumn(new DataGridViewTextBoxColumn
        {
            HeaderText = title,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable
        }, width, fill ? 130 : 45);

    public void AddActionColumn() => AddScaledColumn(new DataGridViewImageColumn
    {
        Resizable = DataGridViewTriState.False,
        DefaultCellStyle = new DataGridViewCellStyle { NullValue = null, Padding = Padding.Empty }
    }, 34, 34);

    private void AddScaledColumn(DataGridViewColumn column, int width, int minimum)
    {
        columnsAt96Dpi.Add(column, (width, minimum));
        column.MinimumWidth = Pixels(minimum);
        column.Width = Pixels(width);
        Columns.Add(column);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FitDpi();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        FitDpi();
    }

    private void FitDpi()
    {
        int textHeight = TextRenderer.MeasureText("Ag", Font).Height;
        ColumnHeadersHeight = Math.Max(Pixels(HeaderHeight), textHeight + Pixels(6));
        RowTemplate.Height = Math.Max(Pixels(ItemHeight), textHeight + Pixels(4));
        foreach (DataGridViewRow row in Rows)
            row.Height = RowTemplate.Height;
        foreach (var (column, size) in columnsAt96Dpi)
        {
            column.MinimumWidth = Pixels(size.Minimum);
            if (column.AutoSizeMode != DataGridViewAutoSizeColumnMode.Fill)
                column.Width = Pixels(size.Width);
        }
    }

    private int Pixels(int units) => (int)Math.Round(units * DeviceDpi / 96f);

    public void AddProgressColumn(int width)
    {
        int column = Columns.Count;
        AddTextColumn("Progress", width);
        CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != column || e.Value is not int percent || e.Graphics is null)
                return;
            e.Paint(e.CellBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground |
                DataGridViewPaintParts.Border);
            int labelWidth = TextRenderer.MeasureText("100%", Font).Width;
            var bar = new Rectangle(e.CellBounds.X + 8, e.CellBounds.Y + (e.CellBounds.Height - 6) / 2,
                Math.Max(0, e.CellBounds.Width - labelWidth - 15), 6);
            using var track = new SolidBrush(DarkTheme.Border);
            using var fill = new SolidBrush(DarkTheme.Mint);
            e.Graphics.FillRectangle(track, bar);
            e.Graphics.FillRectangle(fill, new Rectangle(bar.X, bar.Y, bar.Width * percent / 100, bar.Height));
            var label = new Rectangle(bar.Right + 2, e.CellBounds.Y, labelWidth, e.CellBounds.Height);
            TextRenderer.DrawText(e.Graphics, $"{percent}%", Font, label, DarkTheme.Ink,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            e.Handled = true;
        };
    }
}
