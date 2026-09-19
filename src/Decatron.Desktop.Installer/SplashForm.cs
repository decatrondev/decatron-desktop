namespace Decatron.Desktop.Installer;

// Toda la marca del instalador vive aquí: ventana sin chrome de Windows, colores de
// DecatronTheme.axaml, nada que el usuario tenga que clicar; se cierra sola al terminar.
internal sealed class SplashForm : Form
{
    private static readonly Color Graphite = Color.FromArgb(0x0F, 0x11, 0x15);
    private static readonly Color Surface = Color.FromArgb(0x1F, 0x23, 0x2C);
    private static readonly Color Ink = Color.FromArgb(0xEC, 0xEE, 0xF2);
    private static readonly Color InkMuted = Color.FromArgb(0x8B, 0x93, 0xA5);
    private static readonly Color Accent = Color.FromArgb(0x91, 0x46, 0xFF);
    private static readonly Color Danger = Color.FromArgb(0xEF, 0x44, 0x44);

    private readonly Label _status;
    private readonly ProgressBar _progress;

    public SplashForm()
    {
        Text = "Decatron Desktop";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 168);
        BackColor = Graphite;
        ShowInTaskbar = true;
        TopMost = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var brand = new Label
        {
            Text = "Decatron",
            Font = new Font("Segoe UI", 15, FontStyle.Bold),
            ForeColor = Ink, AutoSize = true, Location = new Point(24, 26),
        };
        var brand2 = new Label
        {
            Text = "Desktop",
            Font = new Font("Segoe UI", 15, FontStyle.Regular),
            ForeColor = InkMuted, AutoSize = true, Location = new Point(brand.Right + 118, 26),
        };
        // Acento: una línea morada bajo el título, en vez de un logo que pese.
        var bar = new Panel { BackColor = Accent, Location = new Point(24, 62), Size = new Size(36, 3) };

        _status = new Label
        {
            Text = "Preparando instalación…",
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = InkMuted, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(24, 78), Size = new Size(332, 24),
        };
        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30,
            Location = new Point(24, 114), Size = new Size(332, 6),
        };

        Controls.Add(brand); Controls.Add(brand2); Controls.Add(bar);
        Controls.Add(_status); Controls.Add(_progress);
    }

    public void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(SetStatus, text); return; }
        _status.Text = text;
    }

    public void ShowError(string message)
    {
        if (InvokeRequired) { BeginInvoke(ShowError, message); return; }
        _progress.Visible = false;
        _status.ForeColor = Danger;
        _status.Text = message;
        _status.Size = new Size(332, 48);
        ClientSize = new Size(380, 200);
        var close = new Button
        {
            Text = "Cerrar", Location = new Point(268, 152), Size = new Size(88, 30),
            FlatStyle = FlatStyle.Flat, ForeColor = Ink, BackColor = Surface,
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();
        Controls.Add(close);
    }

    public void CloseFromBackground()
    {
        if (InvokeRequired) { BeginInvoke(CloseFromBackground); return; }
        Close();
    }
}
