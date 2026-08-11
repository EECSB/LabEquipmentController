using System;
using System.Drawing;
using System.Windows.Forms;

namespace LabEquipmentController;

/// <summary>
/// A window that hosts one detached <see cref="InstrumentConsole"/>, so several instruments
/// can be watched side by side instead of one tab at a time.
///
/// The console is *moved* here, not copied: the same control (and therefore the same
/// session, log and history) is reparented out of its tab and back again. That is far more
/// reliable than trying to move a whole TabPage between containers, and it means nothing
/// about the connection is disturbed by detaching.
///
/// The window is created and shown *before* the console is handed to it, and it hands the
/// console back before it closes — so at no point does the control sit unparented. WinForms
/// will service an unparented control with a hidden "parking" window and an extra handle
/// recreation; going straight from one parent to the next skips that. This arrangement is
/// the one verified end to end against the bench.
///
/// Shown as owned by the main form, so it always sits above it in the z-order. The point of
/// detaching is to keep an instrument in view while working in the main window, so letting
/// it fall behind that window would defeat the exercise. Ownership scopes this to the main
/// form only — other applications still come over the top normally — and it means the
/// window also minimises and closes along with its owner.
/// </summary>
public sealed class InstrumentWindow : Form
{
    /// <summary>Raised while this window is closing, asking for the console to go back into
    /// a tab. Handled synchronously, so the console is moved before this window dies.</summary>
    public event EventHandler? ReattachRequested;

    private bool _released;

    /// <summary>The console this window hosts. Null only between construction and
    /// <see cref="AdoptConsole"/>.</summary>
    public InstrumentConsole Console { get; private set; } = null!;

    public InstrumentWindow(string title, Icon? icon)
    {
        // Match the main form's scaling so this window grows correctly on high-DPI displays
        // (a code-built form must set these itself — the designer normally does).
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;

        Text = "Lab Equipment Controller — " + title;
        if (icon != null) Icon = icon;
        // A detached console carries everything a docked one does — quick commands, the tool
        // row, the log and the results pane side by side — and 820×480 left the log and the
        // results a column each. Sized to what the panes actually need.
        //
        // Both numbers are measured, not guessed. Height: the console spends 315px on the
        // header, quick strip and tool row before the split even starts, and the results pane
        // then spends 39 on its tab strip and 119 on the plot's controls — so 620 left the
        // plot canvas 35 pixels to draw in, which is no plot at all.
        //
        // Width was set by the tool row: its six buttons come to 1361px laid end to end, so
        // below about 1375 it wraps to a second line — which is what 1240 was doing, at a
        // cost of 54px off the panes below. Also clears the ~1150 the plot's own controls
        // need to stay on one row.
        //
        // 1440 cleared that and nothing else. The split is even, so it gave the log side 719
        // pixels, and Send, Clear Log, Save Log and the gap between them take about 450 of
        // those — leaving the command box roughly 250 and the separator collapsed to nothing
        // (see InstrumentConsole.SizeCommandSeparator). Widening is what actually fixes that:
        // at 1920 the log side is around 960, the box gets some 430, and the separator is
        // affordable again. OnLoad clamps to the working area, so a smaller screen still gets
        // a window it can reach the bottom of.
        ClientSize = new Size(1920, 840);
        MinimumSize = new Size(560, 360);
        StartPosition = FormStartPosition.Manual;
        Font = new Font("Segoe UI", 9f);

        FormClosing += InstrumentWindow_FormClosing;
    }

    /// <summary>
    /// Never open bigger than the screen it lands on. The size above is what the panes want;
    /// a 1366×768 laptop cannot give it, and a window taller than the desktop puts its own
    /// bottom row — the command box and Send — out of reach.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        Rectangle work = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Math.Max(Width, MinimumSize.Width), work.Width),
                        Math.Min(Math.Max(Height, MinimumSize.Height), work.Height));
    }

    /// <summary>
    /// Take ownership of a console. Call this only once the window is showing, so the
    /// control moves straight from its old parent to this one in a single step.
    /// </summary>
    public void AdoptConsole(InstrumentConsole console)
    {
        Console = console ?? throw new ArgumentNullException(nameof(console));
        console.Dock = DockStyle.Fill;
        Controls.Add(console);          // moves it off the tab page in one operation
        console.SetDetached(true);
    }

    /// <summary>
    /// Note that the console has already been taken elsewhere, so closing this window must
    /// not ask for it back. Used when the caller moves the console itself.
    /// </summary>
    public void MarkReleased() => _released = true;

    /// <summary>
    /// Take the console back out of this window without disposing it. Only needed on paths
    /// that have nowhere to put it yet (app shutdown); prefer moving it directly.
    /// </summary>
    public InstrumentConsole ReleaseConsole()
    {
        if (!_released)
        {
            _released = true;
            Controls.Remove(Console);
            Console.SetDetached(false);
        }
        return Console;
    }

    private void InstrumentWindow_FormClosing(object? sender, FormClosingEventArgs e)
    {
        // Closing this window re-attaches the console rather than dropping the connection —
        // disconnecting is an explicit action (the console's Disconnect button). On app
        // shutdown the main form has already released the console, so there is nothing to do.
        if (_released || e.CloseReason != CloseReason.UserClosing) return;

        _released = true;

        // Synchronous on purpose: the handler moves the console into a tab page that already
        // exists, while this window is still alive to move it *from*.
        ReattachRequested?.Invoke(this, EventArgs.Empty);

        // If nothing took it, detach it by hand so closing this form doesn't dispose it.
        if (Console is { IsDisposed: false } && ReferenceEquals(Console.Parent, this))
            Controls.Remove(Console);
    }
}
