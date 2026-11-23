using System.Globalization;
using System.Net.Mail;

namespace SurfArchive;

internal sealed class RetentionInput : TableLayoutPanel
{
    private readonly TextBox years = DarkTheme.Input("Minimum years to keep");
    private readonly TextBox project = DarkTheme.Input("Project");
    private readonly TextBox contact = DarkTheme.Input("Contact name");
    private readonly TextBox email = DarkTheme.Input("Contact email");
    private readonly TextBox notes = DarkTheme.Input("Retention notes");
    private readonly int maximumYears;

    public RetentionInput(RetentionMetadata initial)
    {
        Dock = DockStyle.Fill;
        ColumnCount = 4;
        RowCount = 4;
        Margin = Padding.Empty;
        ColumnStyles.Add(new(SizeType.AutoSize));
        ColumnStyles.Add(new(SizeType.Percent, 50));
        ColumnStyles.Add(new(SizeType.AutoSize));
        ColumnStyles.Add(new(SizeType.Percent, 50));
        for (int i = 0; i < 3; i++)
            RowStyles.Add(new(SizeType.AutoSize));
        RowStyles.Add(new(SizeType.Percent, 100));

        maximumYears = initial.ArchivedOn is { } day ? Math.Min(9999 - day.Year, RetentionMetadata.MaximumYears)
            : RetentionMetadata.MaximumYears;
        years.Text = initial.Fields.Years.ToString(CultureInfo.InvariantCulture);
        var date = DarkTheme.Label("", DarkTheme.Small, DarkTheme.Muted);
        date.Anchor = AnchorStyles.Left;
        years.TextChanged += (_, _) => UpdateDate();
        UpdateDate();
        AddField("Minimum years to keep", years, 0, 0);
        years.MaxLength = 3;
        AddField("Keep until", date, 2, 0);
        AddField("Project (optional)", project, 0, 1, span: 3);
        AddField("Contact name", contact, 0, 2);
        AddField("Email", email, 2, 2);
        contact.PlaceholderText = "Optional";
        email.PlaceholderText = "Optional";
        notes.Multiline = true;
        notes.AcceptsReturn = true;
        notes.ScrollBars = ScrollBars.Vertical;
        DarkTheme.Explorer(notes);
        AddField("Notes (optional)", notes, 0, 3, span: 3);
        project.Text = initial.Fields.Project;
        contact.Text = initial.Fields.Contact;
        email.Text = initial.Fields.ContactEmail;
        notes.Text = initial.Fields.Notes.ReplaceLineEndings("\r\n");

        void UpdateDate() => date.Text = TryReadYears(out int value)
            ? initial.ArchivedOn?.AddYears(value).ToString("yyyy-MM-dd") ?? "Archive date unknown"
            : "—";
    }

    public RetentionFields? Read(out string error)
    {
        if (!TryReadYears(out int value))
        {
            error = $"Enter a whole number of years from 1 to {maximumYears}.";
            years.Focus();
            return null;
        }
        string address = email.Text.Trim();
        if (address.Length != 0 && (!MailAddress.TryCreate(address, out var parsed) || parsed.Address != address))
        {
            error = "Enter a valid contact email address or leave it blank.";
            email.Focus();
            return null;
        }
        error = "";
        return new(value, notes.Text.ReplaceLineEndings("\n"), project.Text.Trim(), contact.Text.Trim(), address);
    }

    private bool TryReadYears(out int value) =>
        int.TryParse(years.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= maximumYears;

    private void AddField(string label, Control field, int column, int row, int span = 1)
    {
        var caption = DarkTheme.Label(label, DarkTheme.Small);
        caption.Anchor = row == 3 ? AnchorStyles.Top | AnchorStyles.Left : AnchorStyles.Left;
        Controls.Add(caption, column, row);
        if (field is TextBox)
        {
            field.Dock = row == 3 ? DockStyle.Fill : DockStyle.Top;
            ((TextBox)field).MaxLength = row == 3 ? 16_000 : 500;
        }
        Controls.Add(field, column + 1, row);
        SetColumnSpan(field, span);
    }
}
