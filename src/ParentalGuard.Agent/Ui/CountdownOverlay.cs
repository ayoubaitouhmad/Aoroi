using System.Drawing;

namespace ParentalGuard.Agent.Ui;

/// <summary>
/// A small always-on-top banner in the top-right corner that shows how much time is left as the
/// limit approaches. It deliberately:
///   - never takes keyboard focus (so it doesn't interrupt typing),
///   - doesn't appear in the taskbar or Alt+Tab,
///   - can't be closed by the child (the close attempt is swallowed; only the Agent hides it).
/// </summary>
public sealed class CountdownOverlay : Form
{
    private readonly Label _label;

    public CountdownOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(190, 52);
        BackColor = Color.FromArgb(28, 28, 30);

        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        Controls.Add(_label);

        Visible = false;
    }

    // Don't steal focus when shown.
    protected override bool ShowWithoutActivation => true;

    // Add WS_EX_TOOLWINDOW (keep out of Alt+Tab) and WS_EX_NOACTIVATE (never activate).
    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Show (or update) the banner with the minutes remaining.</summary>
    public void ShowRemaining(int minutes, bool bedtimeApproaching)
    {
        _label.Text = bedtimeApproaching
            ? "Bedtime soon"
            : $"{minutes} min left";

        PositionTopRight();

        if (!Visible)
            Show();
        BringToFront();
    }

    public void HideOverlay()
    {
        if (Visible)
            Hide();
    }

    private void PositionTopRight()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 20, area.Top + 20);
    }

    // Swallow the child's attempts to close it; allow a programmatic/app shutdown close.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
