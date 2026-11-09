using System.Diagnostics;
using System.Text.Json;

namespace IbkrManager.TestHost;

/// <summary>
/// Stand-in for IBKR Desktop used by the Windows integration tests. It reproduces the parts the
/// manager interacts with: a process named <c>ntws.exe</c>, a login window whose title classifies as
/// "login", a username field focused on open, Tab order username → password → submit, and a title
/// change to the 2FA/logged-in variants after signing in.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var options = HostOptions.Parse(args);
        ApplicationConfiguration.Initialize();
        Application.Run(new LoginForm(options));
    }
}

internal sealed record HostOptions(
    string OutputPath,
    bool IgnoreClose,
    bool SpawnChild,
    bool LoggedInDirectly)
{
    public static HostOptions Parse(string[] args)
    {
        var output = Path.Combine(Path.GetTempPath(), "ibkr-testhost-" + Environment.ProcessId + ".json");
        var ignoreClose = false;
        var spawnChild = false;
        var loggedIn = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--ignore-close":
                    ignoreClose = true;
                    break;
                case "--spawn-child":
                    spawnChild = true;
                    break;
                case "--logged-in":
                    loggedIn = true;
                    break;
            }
        }

        return new HostOptions(output, ignoreClose, spawnChild, loggedIn);
    }
}

internal sealed class LoginForm : Form
{
    private readonly HostOptions _options;
    private readonly TextBox _username;
    private readonly TextBox _password;

    public LoginForm(HostOptions options)
    {
        _options = options;
        Text = options.LoggedInDirectly ? "IBKR Desktop Portfolio" : "IBKR Desktop - Login";
        Width = 420;
        Height = 220;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(16),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _username = new TextBox { Dock = DockStyle.Fill, TabIndex = 0, AccessibleName = "Username" };
        _password = new TextBox { Dock = DockStyle.Fill, TabIndex = 1, UseSystemPasswordChar = true, AccessibleName = "Password" };
        var submit = new Button { Text = "Sign In", Dock = DockStyle.Fill, TabIndex = 2 };

        layout.Controls.Add(new Label { Text = "Username", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_username, 1, 0);
        layout.Controls.Add(new Label { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_password, 1, 1);
        layout.Controls.Add(submit, 1, 2);

        Controls.Add(layout);
        AcceptButton = submit;
        KeyPreview = true;
        submit.Click += (_, _) => SignIn();
        _username.TextChanged += (_, _) => WritePayload(submitted: false);
        _password.TextChanged += (_, _) => WritePayload(submitted: false);

        if (options.SpawnChild)
        {
            SpawnChild();
        }

        if (options.IgnoreClose)
        {
            FormClosing += (_, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                }
            };
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _username.Focus();
        WritePayload(submitted: false);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        // Keep the username field focused when the window is brought forward, like a browser-style
        // login form does; without a focused control the keystrokes would be dropped.
        if (!_username.Focused && !_password.Focused)
        {
            _username.Focus();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter)
        {
            SignIn();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SignIn()
    {
        WritePayload(submitted: true);

        // Mimic IBKR: the login window turns into the security-code (2FA) prompt.
        Text = "IBKR Desktop Security Code";
    }

    private void WritePayload(bool submitted)
    {
        var payload = new
        {
            username = _username.Text,
            password = _password.Text,
            submitted,
            activeControl = ActiveControl?.Name ?? "none",
            focused = _password.Focused ? "password" : _username.Focused ? "username" : "none",
            childPid = _childPid,
            at = DateTimeOffset.UtcNow,
        };

        try
        {
            File.WriteAllText(_options.OutputPath, JsonSerializer.Serialize(payload));
        }
        catch (IOException)
        {
            // The reader may be mid-write; the next change retries.
        }
    }

    private static int? _childPid;

    private static void SpawnChild()
    {
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c timeout /t 600 /nobreak > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        _childPid = child?.Id;
    }
}
