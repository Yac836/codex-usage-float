using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexUsageFloat.Tests
{
    internal static partial class RegressionSuite
    {
        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

        private static bool OwnVisible(Control control)
        {
            return (bool)typeof(Control).GetMethod("GetState", Hidden).Invoke(control, new object[] { 2 });
        }
        private static bool NativeTop(Form form) { return (Native.GetWindowLongPtr(form.Handle, -20).ToInt64() & 8) != 0; }

        private static void LayoutCases()
        {
            foreach (bool circleItem in new[] { false, true }) foreach (int sizeItem in new[] { 100, 125, 150 }) foreach (int maskItem in Enumerable.Range(0, 8))
            {
                bool circle = circleItem; int size = sizeItem, mask = maskItem;
                Case("LAYOUT " + (circle ? "circle" : "card") + " size=" + size + " items=" + mask, delegate {
                    using (var form = FormForTest(new Preferences { SizePercent = size, CircularStyle = circle, ShowRemaining = true, ShowFiveHour = (mask & 1) != 0, ShowWeekly = (mask & 2) != 0, ShowResetCredits = (mask & 4) != 0 }))
                    {
                        Put(form, "snapshot", Sample()); Call(form, "UpdateDisplay");
                        var names = (Label[])Field(form, "names"); var values = (Label[])Field(form, "values");
                        var count = (Label)Field(form, "creditValue"); var expiry = (Label)Field(form, "creditExpiry");
                        Check(form.AccessibleDescription.Contains("每周") == ((mask & 2) != 0), "hidden weekly item leaked into description");
                        Check(form.AccessibleDescription.Contains("重置卡") == ((mask & 4) != 0), "hidden credits leaked into description");
                        Check(values[0].Text == "62.5%" && values[1].Text == "89%", "remaining percentages incorrect");
                        if (circle)
                        {
                            Check(form.Width == form.Height && form.Region == null, "circle has hard clipping or non-square proportions");
                            Check(form.Controls.Cast<Control>().All(c => !OwnVisible(c)), "native child overlaps alpha surface");
                            if ((mask & 1) != 0) Check(names[0].Text == "5小时剩余", "quota mode not part of caption");
                            using (var bitmap = form.RenderCircleBitmap()) using (var graphics = Graphics.FromImage(bitmap))
                            using (var format = new StringFormat(StringFormat.GenericTypographic))
                            {
                                format.FormatFlags |= StringFormatFlags.NoWrap;
                                Check(bitmap.GetPixel(0, 0).A == 0, "outside corner not transparent");
                                Check(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A == 255, "circle face not opaque");
                                if ((mask & 4) != 0)
                                {
                                    var extent = graphics.MeasureString(expiry.Text, expiry.Font, 10000, format);
                                    var number = graphics.MeasureString(count.Text, count.Font, 10000, format);
                                    Check(extent.Width <= expiry.Width && extent.Height <= expiry.Height, "full earliest date clipped");
                                    Check(number.Width <= count.Width && number.Height <= count.Height, "credit count clipped");
                                }
                                if (size == 100 && mask == 5) bitmap.Save(System.IO.Path.Combine(outputRoot, "circle-example.png"), ImageFormat.Png);
                            }
                        }
                        else
                        {
                            Check(form.Region != null && form.Width > form.Height, "card shape incorrect");
                            Check(OwnVisible(values[0]) == ((mask & 1) != 0) && OwnVisible(values[1]) == ((mask & 2) != 0), "card quota visibility incorrect");
                            Check(OwnVisible(count) == ((mask & 4) != 0), "card credit visibility incorrect");
                            Check(OwnVisible((Control)Field(form, "emptyLabel")) == (mask == 0), "empty hint visibility incorrect");
                        }
                    }
                });
            }
            Case("PAINT layered antialias, repeated upload and style switch", delegate {
                using (var form = FormForTest(new Preferences { CircularStyle = true }))
                {
                    Put(form, "snapshot", Sample()); Call(form, "UpdateDisplay");
                    using (var bitmap = form.RenderCircleBitmap())
                    {
                        var alphas = new System.Collections.Generic.HashSet<byte>();
                        for (int x = 0; x < bitmap.Width; x++) for (int y = 0; y < bitmap.Height; y++)
                        { byte alpha = bitmap.GetPixel(x, y).A; if (alpha > 0 && alpha < 255) alphas.Add(alpha); }
                        Check(alphas.Count > 20, "edge is not smoothly antialiased");
                    }
                    for (int i = 0; i < 3; i++) Call(form, "RefreshCircleSurface", true);
                    uint before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                    for (int i = 0; i < 20; i++) Call(form, "RefreshCircleSurface", true);
                    Check(GetGuiResources(Process.GetCurrentProcess().Handle, 0) <= before + 4, "GDI handles grew during redraw");
                    Call(form, "SetCircularStyle", false); Check(form.Region != null, "card region missing after switch");
                    Call(form, "SetCircularStyle", true); Check(form.Region == null, "hard clipping returned after switch");
                }
            });
            Case("DISPOSE directly disposing a form releases fonts", delegate {
                var form = FormForTest(new Preferences()); var font = ((System.Collections.Generic.List<Font>)Field(form, "fonts"))[0];
                form.Dispose(); Throws<ArgumentException>(() => font.GetHeight()); form.Dispose();
            });
            Case("PAINT new snapshot invalidates frame even if rounded labels match", delegate {
                int uploads = 0;
                SurfaceUpload recorder = delegate(IntPtr w, Bitmap b, Point p, byte opacity, ref bool configured) { uploads++; configured = true; };
                using (var form = FormForTest(new Preferences { CircularStyle = true, ShowRemaining = true }, null, recorder))
                {
                    var first = Sample(); first.FiveHour.UsedPercent = 79.96;
                    var second = Sample(); second.FiveHour.UsedPercent = 80.04; second.FiveHour.ResetsAt = first.FiveHour.ResetsAt;
                    second.Weekly = first.Weekly; second.RetrievedUtc = first.RetrievedUtc.AddTicks(1);
                    Put(form, "snapshot", first); Call(form, "UpdateDisplay");
                    int previous = uploads; var values = (Label[])Field(form, "values"); string text = values[0].Text; var color = values[0].ForeColor;
                    Put(form, "snapshot", second); Call(form, "UpdateDisplay");
                    Check(values[0].Text == text && values[0].ForeColor != color, "test did not cross a color threshold while keeping rounded text");
                    Check(uploads > previous, "new data left cached colors on screen");
                }
            });
            Case("PAINT stale quota ring is muted with its value", delegate {
                using (var form = FormForTest(new Preferences { CircularStyle = true, ShowWeekly = false, ShowResetCredits = false }))
                {
                    var sample = Sample(); sample.RetrievedUtc = DateTime.UtcNow.AddMinutes(-5);
                    Put(form, "snapshot", sample); Call(form, "UpdateDisplay");
                    using (var bitmap = form.RenderCircleBitmap())
                    {
                        var pixel = bitmap.GetPixel(bitmap.Width / 2, (int)Math.Round(18 * (float)Field(form, "scale")));
                        Check(pixel.B > 70 && pixel.B - pixel.R < 100, "stale ring kept active blue coloring");
                    }
                }
            });
            DockCases();
        }

        private static void AsyncCases()
        {
            Case("REFRESH lock stays held until UI commits the result", delegate {
                int calls = 0;
                using (var form = FormForTest(new Preferences(), token => { Interlocked.Increment(ref calls); return Sample(); }))
                {
                    Call(form, "StartRefresh");
                    Check(((Task)Field(form, "refreshTask")).Wait(3000), "mock reader did not return");
                    Call(form, "StartRefresh");
                    Check(calls == 1 && (int)Field(form, "reading") == 1, "second refresh overtook pending UI commit");
                    Application.DoEvents();
                    Check((int)Field(form, "reading") == 0 && Field(form, "snapshot") != null, "result was not committed");
                }
            });
            Case("REFRESH dispose during read cancels cleanly", delegate {
                using (var entered = new ManualResetEventSlim(false))
                {
                    var form = FormForTest(new Preferences(), token => { entered.Set(); token.WaitHandle.WaitOne(5000); token.ThrowIfCancellationRequested(); return Sample(); });
                    Call(form, "StartRefresh"); Check(entered.Wait(3000), "mock read did not start");
                    var task = (Task)Field(form, "refreshTask"); form.Dispose();
                    Check(task.Wait(3000), "dispose left read blocked"); Application.DoEvents();
                    Check((int)Field(form, "reading") == 0, "cancel left refresh lock held");
                }
            });
            Case("PAINT native upload failure falls back to usable card", delegate {
                SurfaceUpload failure = delegate(IntPtr w, Bitmap b, Point p, byte opacity, ref bool configured) { throw new Win32Exception(87); };
                using (var form = FormForTest(new Preferences { CircularStyle = true }, null, failure))
                {
                    Application.DoEvents();
                    Check(!((Preferences)Field(form, "preferences")).CircularStyle && form.Region != null, "failed alpha surface left an invisible circle");
                    Check(form.AccessibleDescription.Contains("已切换为卡片"), "fallback reason not exposed");
                    var styles = form.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "样式").DropDownItems.OfType<ToolStripMenuItem>().ToArray();
                    Check(styles[0].Checked && !styles[1].Checked, "fallback left wrong style selected in menu");
                }
            });
            Case("REFRESH error keeps prior data and later success recovers", delegate {
                int calls = 0;
                using (var form = FormForTest(new Preferences(), token => { if (++calls == 1) throw new UsageException("模拟读取失败"); return Sample(); }))
                {
                    var prior = Sample(); Put(form, "snapshot", prior);
                    Call(form, "StartRefresh"); Check(WaitUntil(() => (int)Field(form, "reading") == 0, 3000, true), "error read did not finish");
                    Check(Object.ReferenceEquals(prior, Field(form, "snapshot")) && (string)Field(form, "readError") == "模拟读取失败", "failure erased previous data");
                    Call(form, "StartRefresh"); Check(WaitUntil(() => (int)Field(form, "reading") == 0, 3000, true), "recovery did not finish");
                    Check(Field(form, "readError") == null && !Object.ReferenceEquals(prior, Field(form, "snapshot")), "successful read did not recover");
                }
            });
        }

        private static void DockCases()
        {
            Case("DOCK geometry on four edges and negative-coordinate display", delegate {
                var work = new Rectangle(-1920, 0, 1920, 1080); var bounds = new Rectangle(-1916, 100, 320, 180);
                Check(EdgeDocking.Detect(bounds, work, 14) == DockEdge.Left, "left edge not detected");
                foreach (DockEdge edge in new[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom })
                {
                    var snapped = EdgeDocking.Snap(bounds, work, edge); var tab = EdgeDocking.Tab(snapped, work, edge, 8, 64);
                    Check(work.Contains(tab), "collapsed tab outside work area");
                    Check(edge == DockEdge.Left || edge == DockEdge.Right ? tab.Width == 8 : tab.Height == 8, "tab thickness incorrect");
                    Check(EdgeDocking.Detect(snapped, work, 14) == edge, "snapped edge not preserved");
                }
            });
            foreach (bool style in new[] { false, true })
            {
                bool circle = style;
                Case("DOCK hover, menu, drag, disable and restore " + circle, delegate {
                    using (var form = FormForTest(new Preferences { CircularStyle = circle }))
                    {
                        var work = Screen.PrimaryScreen.WorkingArea; form.Location = new Point(work.Right - form.Width, work.Top + 100);
                        Call(form, "UpdateDockAnchor", false); var original = form.Bounds;
                        var outside = new Point(work.Left + 20, work.Bottom - 20); var now = DateTime.UtcNow;
                        Call(form, "UpdateDockHover", outside, true, false, now.AddSeconds(2)); Check(!(bool)Field(form, "dockCollapsed"), "menu triggered hiding");
                        Call(form, "UpdateDockHover", outside, false, true, now.AddSeconds(2)); Check(!(bool)Field(form, "dockCollapsed"), "button press triggered hiding");
                        Put(form, "dragging", true); Call(form, "UpdateDockHover", outside, false, false, now.AddSeconds(3));
                        Check(!(bool)Field(form, "dockCollapsed"), "drag triggered hiding"); Put(form, "dragging", false);
                        Call(form, "UpdateDockHover", outside, false, false, now.AddSeconds(4));
                        Call(form, "UpdateDockHover", outside, false, false, now.AddSeconds(5)); Check((bool)Field(form, "dockCollapsed"), "pointer outside did not collapse");
                        Check(work.Contains(form.Bounds), "tab outside screen");
                        Call(form, "UpdateDockHover", new Point(form.Left + form.Width / 2, form.Top + form.Height / 2), false, false, now.AddSeconds(6));
                        Check(!(bool)Field(form, "dockCollapsed") && form.Bounds == original, "hover failed to restore full bounds");
                        Call(form, "CollapseDock"); Call(form, "SavePosition"); var prefs = (Preferences)Field(form, "preferences");
                        Check(prefs.X == original.X && prefs.Y == original.Y, "collapsed tab coordinates were saved");
                        Call(form, "SetAutoHide", false); Check(!(bool)Field(form, "dockCollapsed") && form.Bounds == original, "disabling auto-hide failed to expand");
                    }
                });
            }
        }

        private static void DesktopCases()
        {
            Case("DESKTOP shown window starts refresh and stays responsive", delegate {
                int calls = 0;
                using (var form = FormForTest(new Preferences { EdgeAutoHide = false }, token => { Interlocked.Increment(ref calls); token.WaitHandle.WaitOne(150); token.ThrowIfCancellationRequested(); return Sample(); }))
                {
                    var timer = (System.Windows.Forms.Timer)Field(form, "refresh");
                    Check(timer.Interval == 60000, "production refresh period changed");
                    timer.Interval = 200; form.Show(); Application.DoEvents();
                    Check(WaitUntil(() => calls >= 2 && Field(form, "snapshot") != null, 3000, true), "automatic refresh did not run");
                    timer.Stop(); Check(WaitUntil(() => (int)Field(form, "reading") == 0, 3000, true), "read remained busy");
                    int prior = calls;
                    typeof(Control).GetMethod("OnDoubleClick", Hidden).Invoke(form, new object[] { EventArgs.Empty });
                    Check(WaitUntil(() => calls > prior, 2000, true), "double-click did not refresh");
                    var watch = Stopwatch.StartNew(); form.Location = new Point(form.Left + 1, form.Top); form.ContextMenuStrip.Show(form, new Point(8, 8));
                    Application.DoEvents(); form.ContextMenuStrip.Close();
                    Check(watch.ElapsedMilliseconds < 1000, "background read blocked UI"); form.Close();
                }
            });
            foreach (bool style in new[] { false, true })
            {
                bool circle = style;
                Case("DESKTOP native topmost lifecycle " + circle, delegate {
                    using (var form = FormForTest(new Preferences { CircularStyle = circle, EdgeAutoHide = false }))
                    {
                        form.Show(); Application.DoEvents(); Call(form, "EnsureTopMost", true);
                        Check(form.TopMost && NativeTop(form), "native topmost missing");
                        var bounds = form.Bounds; var foreground = GetForegroundWindow();
                        Native.SetWindowPos(form.Handle, new IntPtr(-2), 0, 0, 0, 0, 0x13);
                        Check(!NativeTop(form), "lost native state not simulated"); Call(form, "EnsureTopMost", false);
                        Check(NativeTop(form) && form.Bounds == bounds, "repair failed or moved the window");
                        Check(GetForegroundWindow() == foreground, "topmost repair stole focus");
                        var prefs = (Preferences)Field(form, "preferences"); prefs.KeepOnTop = false; Call(form, "EnsureTopMost", true);
                        Check(!form.TopMost && !NativeTop(form), "disabled topmost ignored");
                        prefs.KeepOnTop = true; Call(form, "EnsureTopMost", true);
                        typeof(Control).GetMethod("RecreateHandle", Hidden).Invoke(form, null); Application.DoEvents();
                        Check(NativeTop(form), "handle recreation lost topmost");
                        prefs.OpacityPercent = 70; if (circle) Call(form, "RefreshCircleSurface", true); else form.Opacity = .7;
                        Call(form, "EnsureTopMost", true); Check(NativeTop(form), "opacity change lost topmost");
                        Check(!circle || (int)Field(form, "circleSurfaceError") == 0, "native layered upload failed");
                        Call(form, "SetAutoHide", true); var work = Screen.PrimaryScreen.WorkingArea;
                        form.Location = new Point(work.Right - form.Width, work.Top + 100); Call(form, "UpdateDockAnchor", false);
                        Call(form, "CollapseDock"); Check(NativeTop(form), "collapsed tab lost topmost");
                        Call(form, "RevealWindow"); Check(!form.IsDisposed && !(bool)Field(form, "dockCollapsed") && NativeTop(form), "reveal failed");
                        form.Close();
                    }
                });
            }
        }
    }
}
