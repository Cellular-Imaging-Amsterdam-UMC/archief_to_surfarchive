using Renci.SshNet.Common;

namespace SurfArchive;

internal sealed class LoginDialog : Form
{
    private readonly TextBox username = DarkTheme.Input("SURF username");
    private readonly TextBox password = DarkTheme.Input("SURF password");
    private readonly CheckBox remember = new() { Text = "Remember username", AutoSize = true };
    private readonly Label feedback = DarkTheme.Label("archive.surf.nl  ·  SFTP", DarkTheme.Small, DarkTheme.Muted);
    private readonly Button signIn = DarkTheme.Button("Sign in", ActionIcon.SignIn, primary: true);
    private readonly Button cancel = DarkTheme.Button("Cancel", ActionIcon.Cancel);
    private ArchiveSession? session;
    private bool connecting;
    private string? settingsError;
    private readonly SessionLog? log;

    public LoginDialog(SessionLog? log = null)
    {
        this.log = log;
        DarkTheme.Window(this);
        Text = "Sign in · SURF Data Archive";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 313);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(26, 21, 26, 17)
        };
        foreach (int height in new[] { 45, 24, 21, 37, 21, 37, 33, 69 })
            layout.RowStyles.Add(new(SizeType.Absolute, height));
        Controls.Add(layout);
        var brand = DarkTheme.Row(DarkTheme.Label("▣", DarkTheme.Brand, DarkTheme.Mint),
            DarkTheme.Label("   Sign in to SURF Data Archive", DarkTheme.Brand));
        layout.Controls.Add(brand, 0, 0);
        feedback.Dock = DockStyle.Fill;
        feedback.AutoSize = false;
        feedback.AutoEllipsis = true;
        layout.Controls.Add(feedback, 0, 1);
        layout.Controls.Add(DarkTheme.Label("Username", DarkTheme.Small, DarkTheme.Muted), 0, 2);
        layout.Controls.Add(username, 0, 3);
        layout.Controls.Add(DarkTheme.Label("Password", DarkTheme.Small, DarkTheme.Muted), 0, 4);
        layout.Controls.Add(password, 0, 5);
        foreach (var field in new[] { username, password })
        {
            field.Dock = DockStyle.Top;
            field.Margin = Padding.Empty;
        }

        password.UseSystemPasswordChar = true;
        remember.Margin = new Padding(0, 3, 0, 0);
        layout.Controls.Add(remember, 0, 6);
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = DarkTheme.Row(cancel, signIn);
        buttons.FlowDirection = FlowDirection.RightToLeft;
        layout.Controls.Add(buttons, 0, 7);

        try
        {
            LoginSettings.RemoveLegacyPassword();
            username.Text = LoginSettings.ReadUsername();
            remember.Checked = username.Text.Length > 0;
        }
        catch (Exception error)
        {
            settingsError = "Could not update local login settings: " + error.Message;
            log?.Write(settingsError);
        }

        AcceptButton = signIn;
        CancelButton = cancel;
        signIn.Click += async (_, _) => await Connect();
        FormClosing += (_, e) => e.Cancel = connecting;
        Shown += (_, _) =>
        {
            if (settingsError is not null)
                ShowFeedback(settingsError, DarkTheme.Error);
            ActiveControl = username.Text.Length == 0 ? username : password;
        };
        FormClosed += (_, _) =>
        {
            password.Clear();
            if (DialogResult != DialogResult.OK)
                session?.Dispose();
        };
    }

    private async Task Connect()
    {
        string name = username.Text.Trim();
        if (name.Length == 0 || password.TextLength == 0)
        {
            ShowFeedback("Enter your credentials to continue.", DarkTheme.Error);
            return;
        }

        connecting = true;
        username.Enabled = password.Enabled = remember.Enabled = signIn.Enabled = cancel.Enabled = false;
        ShowFeedback("Connecting to archive.surf.nl…", DarkTheme.Mint);
        log?.Write($"Connecting to {ArchiveSession.Host}.");
        try
        {
            var credentials = new Credentials(name, password.Text);
            session = await Task.Run(() => new ArchiveSession(credentials));
            log?.Write($"Signed in to {ArchiveSession.Host}. Archive root {session.Root}.");
            try { LoginSettings.RememberUsername(remember.Checked ? name : null); }
            catch (Exception error)
            {
                log?.Write($"Could not update the saved username. {error.Message}");
                MessageBox.Show(this, "Could not update the saved username.\n\n" + error.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (SshAuthenticationException)
        {
            log?.Write("SURF authentication failed.");
            ShowFeedback("Sign-in failed. Check your username and password.", DarkTheme.Error);
        }
        catch (Exception error)
        {
            log?.Write($"SURF sign-in failed. {error.GetType().Name}. {error.Message}");
            ShowFeedback(error.Message, DarkTheme.Error);
        }
        finally
        {
            connecting = false;
            password.Clear();
            username.Enabled = password.Enabled = remember.Enabled = signIn.Enabled = cancel.Enabled = true;
        }

        if (session is not null)
            DialogResult = DialogResult.OK;
        else
            password.Focus();
    }

    private void ShowFeedback(string text, Color color)
    {
        feedback.Text = text;
        feedback.ForeColor = color;
        feedback.AccessibleDescription = text;
    }

    public ArchiveSession TakeSession()
    {
        var result = session ?? throw new InvalidOperationException("There is no signed-in session.");
        session = null;
        return result;
    }
}
