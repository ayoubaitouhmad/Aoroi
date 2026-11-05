using System.Drawing;

namespace ParentalGuard.Agent.Ui;

/// <summary>
/// A full-screen "time's up / bedtime" screen shown the moment the Service orders a Lock. It
/// covers every monitor (the whole virtual desktop) and can't be closed by the child.
///
/// It works alongside the real workstation lock: the overlay makes the reason clear and stays
/// up after the child unlocks, so if they're still over the limit the next Lock just keeps it
/// in place. It is not, by itself, the hard enforcement — <c>LockWorkStation()</c> is. (A
/// standard user can still Alt+Tab under this overlay; that gap is closed properly by the
/// workstation lock and, later, by the Service-side fallback in the hardening milestone.)
/// </summary>
public sealed class TimesUpOverlay : Form
{
    private readonly Label _label;

    public TimesUpOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.92;
        Bounds = SystemInformation.VirtualScreen;

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 28F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_label);

        Visible = false;
    }

    // Disable the window's close box (CS_NOCLOSE). Alt+F4 still raises WM_CLOSE, which we also
    // cancel in OnFormClosing — belt and braces.
    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_NOCLOSE = 0x200;
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_NOCLOSE;
            return cp;
        }
    }

    public void ShowOverlay(string reason)
    {
        _label.Text = reason + "\n\nAsk a parent if you think this is a mistake.";
        // Recompute bounds in case monitors changed since construction.
        Bounds = SystemInformation.VirtualScreen;

        if (!Visible)
            Show();
        BringToFront();
        Activate();
    }

    public void HideOverlay()
    {
        if (Visible)
            Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }
}
