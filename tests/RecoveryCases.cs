using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CodexUsageFloat.Tests
{
    internal static partial class RegressionSuite
    {
        private const Keys TestShortcut = Keys.Control | Keys.Alt | Keys.F24;

        private static void RecoveryCases()
        {
            Case("RECOVERY queued reveal is ignored after form disposal", delegate {
                var form = FormForTest(new Preferences());
                form.Dispose(); Call(form, "RevealWindow");
                Check(form.IsDisposed && !form.IsHandleCreated, "pending reveal revived disposed window");
            });
            Case("RECOVERY monitor removal, partial overlap and negative coordinates", delegate {
                var primary = new Rectangle(0, 0, 1280, 720);
                var secondary = new Rectangle(-1920, -200, 1920, 1040);
                var window = new Rectangle(-1800, 100, 382, 194);
                Check(WindowPlacement.Fit(window, new[] { primary, secondary }, Point.Empty) == window, "valid secondary monitor moved");
                Check(primary.Contains(WindowPlacement.Fit(window, new[] { primary }, Point.Empty)), "removed monitor left widget outside single-screen RDP");
                window = new Rectangle(1200, 650, 382, 194);
                Check(primary.Contains(WindowPlacement.Fit(window, new[] { primary }, Point.Empty)), "partly visible widget not moved fully into work area");
                var left = new Rectangle(-900, -600, 900, 600);
                Check(left.Contains(WindowPlacement.Fit(new Rectangle(5000, 5000, 382, 194), new[] { primary, left }, new Point(-500, -300))), "lost window ignored pointer screen");
                var spanning = new Rectangle(-250, 100, 382, 194);
                Check(secondary.Contains(WindowPlacement.Fit(spanning, new[] { primary, secondary }, new Point(100, 100))), "largest overlap screen was ignored");
                Check(WindowPlacement.Center(new Size(200, 100), secondary).Location == new Point(-1060, 270), "negative-screen centering incorrect");
            });
            Case("RECOVERY small/empty work area and stable placement", delegate {
                var small = new Rectangle(100, -50, 160, 100); var window = new Rectangle(800, 900, 382, 194);
                var fitted = WindowPlacement.Fit(window, new[] { small }, Point.Empty);
                Check(fitted.Location == small.Location && fitted.Size == window.Size, "oversized widget should keep dimensions and expose its top-left");
                Check(WindowPlacement.Fit(window, new Rectangle[0], Point.Empty) == window, "temporary lack of monitors destroyed saved position");
                Check(WindowPlacement.Fit(fitted, new[] { Rectangle.Empty, small }, Point.Empty) == fitted, "placement drifts between checks");
            });
            foreach (bool style in new[] { false, true })
            {
                bool circle = style;
                Case("RECOVERY expanded bounds and settings survive display change " + circle, delegate {
                    using (var form = FormForTest(new Preferences { CircularStyle = circle, ShowRemaining = true, ShowWeekly = false }))
                    {
                        var work = Screen.PrimaryScreen.WorkingArea;
                        form.Location = new Point(work.Right - form.Width, work.Top + 60);
                        Call(form, "UpdateDockAnchor", false); Size fullSize = form.Size;
                        Call(form, "CollapseDock"); Check((bool)Field(form, "dockCollapsed"), "test tab did not collapse");
                        Put(form, "expandedBounds", new Rectangle(30000, 30000, fullSize.Width, fullSize.Height));
                        Call(form, "RecoverToVisibleArea", new[] { work }, new Point(work.Left + 10, work.Top + 10));
                        Check(!(bool)Field(form, "dockCollapsed") && form.Size == fullSize && work.Contains(form.Bounds), "recovery used tab size or left full widget off-screen");
                        Check((DockEdge)Field(form, "dockEdge") == DockEdge.None, "recovered widget immediately re-hides");
                        var preferences = Preferences.Load((string)Field(form, "preferencePath"));
                        Check(preferences.X == form.Left && preferences.Y == form.Top, "corrected location was not saved");
                        Check(preferences.CircularStyle == circle && preferences.ShowRemaining && !preferences.ShowWeekly, "recovery changed display preferences");
                        var stable = form.Bounds;
                        Call(form, "RecoverToVisibleArea", new[] { work }, Point.Empty);
                        Check(form.Bounds == stable, "visible widget moved unnecessarily");
                    }
                });
            }
            Case("RECOVERY native hotkey conflict and release", delegate {
                using (var owner = new Form()) using (var other = new Form())
                using (var first = new RevealHotkey(TestShortcut)) using (var second = new RevealHotkey(TestShortcut))
                {
                    Check(first.Register(owner.Handle), "test chord could not be registered");
                    Check(!second.Register(other.Handle) && second.LastError != 0, "conflicting chord reported as registered");
                    first.Unregister();
                    Check(second.Register(other.Handle), "unregister did not release system chord");
                    second.Dispose(); second.Dispose();
                    Check(first.Register(owner.Handle), "dispose did not release system chord");
                }
            });
            Case("RECOVERY handle recreation preserves hotkey and disposes tray", delegate {
                var hotkey = new RevealHotkey(TestShortcut);
                var form = FormForTest(new Preferences(), null, null, true, hotkey);
                try
                {
                    Check(hotkey.Registered, "form did not bind hotkey");
                    var tray = (NotifyIcon)Field(form, "recoveryTray"); Check(tray.Visible && tray.Icon != null, "tray fallback absent");
                    typeof(Control).GetMethod("RecreateHandle", Hidden).Invoke(form, null);
                    Check(hotkey.Registered && !((ToolStripMenuItem)Field(form, "retryHotkeyItem")).Available, "handle recreation lost hotkey");
                    using (var contender = new Form()) using (var binding = new RevealHotkey(TestShortcut))
                    {
                        Check(!binding.Register(contender.Handle), "recreated handle did not own chord");
                        form.Dispose(); Check(!hotkey.Registered && Field(form, "recoveryTray") == null, "dispose retained recovery resources");
                        Check(binding.Register(contender.Handle), "disposed form kept chord reserved");
                    }
                }
                finally { form.Dispose(); }
            });
            Case("RECOVERY conflict is visible and registration can be retried", delegate {
                using (var owner = new Form()) using (var blocker = new RevealHotkey(TestShortcut))
                {
                    Check(blocker.Register(owner.Handle), "test blocker did not bind");
                    var hotkey = new RevealHotkey(TestShortcut);
                    using (var form = FormForTest(new Preferences(), null, null, true, hotkey))
                    {
                        var retry = (ToolStripMenuItem)Field(form, "retryHotkeyItem");
                        var reveal = (ToolStripMenuItem)Field(form, "revealItem");
                        Check(!hotkey.Registered, "conflicting form reported hotkey success");
                        Check(retry.Available && reveal.ShortcutKeyDisplayString.Contains("不可用"), "menu hides registration failure");
                        var tray = (NotifyIcon)Field(form, "recoveryTray");
                        Check(tray.Visible && tray.ContextMenuStrip == form.ContextMenuStrip && tray.Text.Contains("不可用"), "tray fallback misleading or missing");
                        blocker.Unregister(); retry.PerformClick();
                        Check(hotkey.Registered && !retry.Available && reveal.ShortcutKeyDisplayString == "Ctrl+Alt+F24", "retry did not recover released shortcut");
                    }
                }
            });
        }

        private static void DesktopRecoveryCases()
        {
            Case("DESKTOP custom hotkey message and tray double click reveal on current screen", delegate {
                using (var form = FormForTest(new Preferences(), null, null, true, new RevealHotkey(TestShortcut)))
                {
                    form.Show(); Application.DoEvents();
                    var binding = (RevealHotkey)Field(form, "revealHotkey");
                    Check(Call(form, "ApplyShortcut", true, Keys.Control | Keys.Shift | Keys.F23) == null, "custom shortcut could not be applied");
                    var work = Screen.FromPoint(Cursor.Position).WorkingArea;
                    form.Location = new Point(work.Right - form.Width, work.Top + 60);
                    Call(form, "UpdateDockAnchor", false); Call(form, "CollapseDock");
                    form.WindowState = FormWindowState.Minimized;
                    Native.SendMessage(form.Handle, RevealHotkey.Message, new IntPtr(binding.MessageId), IntPtr.Zero);
                    Check(WaitUntil(() => !(bool)Field(form, "dockCollapsed") && form.WindowState == FormWindowState.Normal, 2000, true), "hotkey did not restore full widget");
                    Check(form.Bounds == WindowPlacement.Center(form.Size, work), "hotkey did not center on pointer screen");
                    form.Location = new Point(30000, 30000);
                    var tray = (NotifyIcon)Field(form, "recoveryTray");
                    typeof(NotifyIcon).GetMethod("OnMouseDoubleClick", Hidden).Invoke(tray, new object[] { new MouseEventArgs(MouseButtons.Left, 2, 0, 0, 0) });
                    Application.DoEvents();
                    Check(form.Visible && form.Bounds == WindowPlacement.Center(form.Size, work), "tray double-click did not reveal");
                    form.Close();
                }
            });
            Case("DESKTOP automatic screen repair preserves foreground and catches messages", delegate {
                using (var form = FormForTest(new Preferences { EdgeAutoHide = false }, null, null, true, new RevealHotkey(TestShortcut)))
                using (var foreground = new Form { Text = "Recovery focus probe" })
                {
                    form.Show(); foreground.Show(); foreground.Activate(); Application.DoEvents();
                    IntPtr active = GetForegroundWindow(); form.Location = new Point(30000, 30000);
                    var work = Screen.FromPoint(Cursor.Position).WorkingArea;
                    Native.SendMessage(form.Handle, 0x007E, IntPtr.Zero, IntPtr.Zero);
                    Check(WaitUntil(() => work.Contains(form.Bounds), 2000, true), "display-change message did not recover");
                    Check(GetForegroundWindow() == active, "automatic repair stole focus");
                    form.Location = new Point(30000, 30000);
                    Native.SendMessage(form.Handle, 0x02B1, new IntPtr(3), IntPtr.Zero);
                    Check(WaitUntil(() => work.Contains(form.Bounds), 2000, true), "remote-connect notification did not recover");
                    Check(GetForegroundWindow() == active, "remote-connect repair stole focus");
                    Put(form, "lastWorkAreas", new[] { new Rectangle(-4000, 0, 100, 100) });
                    form.Location = new Point(30000, 30000); Call(form, "CheckScreenLayout");
                    Check(work.Contains(form.Bounds), "clock fallback did not detect changed layout");
                    form.Close(); foreground.Close();
                }
            });
        }
    }
}
