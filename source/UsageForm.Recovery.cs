using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexUsageFloat
{
    internal static class WindowPlacement
    {
        internal static Rectangle Center(Size size, Rectangle work)
        {
            return new Rectangle(work.Left + Math.Max(0, (work.Width - size.Width) / 2),
                work.Top + Math.Max(0, (work.Height - size.Height) / 2), size.Width, size.Height);
        }

        internal static Rectangle Fit(Rectangle window, Rectangle[] workAreas, Point preferred)
        {
            var areas = workAreas.Where(area => area.Width > 0 && area.Height > 0).ToArray();
            if (areas.Length == 0 || areas.Any(area => area.Contains(window))) return window;
            // Prefer the screen already containing most of the window. If the
            // old monitor disappeared completely, use the pointer's screen.
            var target = areas.OrderByDescending(area => {
                var overlap = Rectangle.Intersect(area, window);
                return (long)Math.Max(0, overlap.Width) * Math.Max(0, overlap.Height);
            }).ThenByDescending(area => area.Contains(preferred)).First();
            return new Rectangle(Math.Max(target.Left, Math.Min(window.Left, target.Right - window.Width)),
                Math.Max(target.Top, Math.Min(window.Top, target.Bottom - window.Height)), window.Width, window.Height);
        }
    }

    internal sealed class RevealHotkey : IDisposable
    {
        internal const int Id = 0x4346;
        internal const int Message = 0x0312;
        private readonly Keys key;
        private IntPtr window;
        internal bool Registered { get; private set; }
        internal int LastError { get; private set; }

        internal RevealHotkey(Keys virtualKey = Keys.U) { key = virtualKey; }
        internal bool Register(IntPtr handle)
        {
            Unregister();
            window = handle;
            // A system hotkey needs no keyboard hook or polling. NOREPEAT
            // prevents repeated recentering while the user holds the keys.
            Registered = RecoveryNative.RegisterHotKey(handle, Id, 0x4003, (uint)key);
            LastError = Registered ? 0 : Marshal.GetLastWin32Error();
            return Registered;
        }
        internal void Unregister()
        {
            if (Registered) RecoveryNative.UnregisterHotKey(window, Id);
            Registered = false; window = IntPtr.Zero;
        }
        public void Dispose() { Unregister(); }
    }

    internal sealed partial class UsageForm
    {
        private readonly bool desktopRecoveryEnabled;
        private RevealHotkey revealHotkey;
        private NotifyIcon recoveryTray;
        private Icon recoveryIcon;
        private ToolStripMenuItem revealItem, retryHotkeyItem;
        private Rectangle[] lastWorkAreas;
        private bool recoveryQueued, sessionNotifications;

        private void InitializeRecovery(RevealHotkey hotkey)
        {
            if (!desktopRecoveryEnabled) return;
            revealHotkey = hotkey ?? new RevealHotkey();
            revealItem = new ToolStripMenuItem("呼出到当前屏幕", null, delegate { RevealWindow(); });
            retryHotkeyItem = new ToolStripMenuItem("重试快捷键（可能已被占用）", null, delegate { RegisterRevealHotkey(); });
            context.Items.Insert(0, revealItem);
            context.Items.Insert(1, retryHotkeyItem);
            context.Items.Insert(2, new ToolStripSeparator());
            recoveryIcon = CreateRecoveryIcon();
            recoveryTray = new NotifyIcon { Icon = recoveryIcon, ContextMenuStrip = context };
            recoveryTray.MouseDoubleClick += delegate(object sender, MouseEventArgs e) {
                if (e.Button == MouseButtons.Left) RevealWindow();
            };
            recoveryTray.Visible = true;
            lastWorkAreas = ReadWorkAreas();
            if (IsHandleCreated) AttachRecoveryHandle();
        }

        private static Icon CreateRecoveryIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bitmap))
                using (var face = new SolidBrush(Color.FromArgb(22, 26, 33)))
                using (var blue = new Pen(Color.FromArgb(92, 190, 248), 3))
                using (var purple = new Pen(Color.FromArgb(183, 154, 251), 3))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillEllipse(face, 1, 1, 30, 30);
                    blue.StartCap = blue.EndCap = purple.StartCap = purple.EndCap = LineCap.Round;
                    g.DrawArc(blue, 4, 4, 24, 24, -90, 285);
                    g.DrawArc(purple, 10, 10, 12, 12, -90, 220);
                }
                IntPtr iconHandle = bitmap.GetHicon();
                try { using (var borrowed = Icon.FromHandle(iconHandle)) return (Icon)borrowed.Clone(); }
                finally { RecoveryNative.DestroyIcon(iconHandle); }
            }
        }

        private void RegisterRevealHotkey()
        {
            if (revealHotkey == null || closing || !IsHandleCreated) return;
            bool registered = revealHotkey.Register(Handle);
            revealItem.ShortcutKeyDisplayString = registered ? "Ctrl+Alt+U" : "快捷键不可用";
            retryHotkeyItem.Visible = !registered;
            recoveryTray.Text = registered ? "Codex 额度 · Ctrl+Alt+U / 双击呼出" : "Codex 额度 · 双击呼出（快捷键未注册）";
        }

        private void AttachRecoveryHandle()
        {
            if (revealHotkey == null || closing) return;
            RegisterRevealHotkey();
            sessionNotifications = RecoveryNative.WTSRegisterSessionNotification(Handle, 0);
        }

        private void ReleaseRecoveryHandle()
        {
            if (revealHotkey != null) revealHotkey.Unregister();
            if (sessionNotifications && IsHandleCreated) RecoveryNative.WTSUnRegisterSessionNotification(Handle);
            sessionNotifications = false;
        }

        private void DisposeRecovery()
        {
            ReleaseRecoveryHandle();
            if (recoveryTray != null) { recoveryTray.Visible = false; recoveryTray.Dispose(); recoveryTray = null; }
            if (recoveryIcon != null) { recoveryIcon.Dispose(); recoveryIcon = null; }
            if (revealHotkey != null) revealHotkey.Dispose();
        }

        private bool HandleRecoveryMessage(ref Message message)
        {
            if (!desktopRecoveryEnabled || !ready || closing) return false;
            if (message.Msg == RevealHotkey.Message && message.WParam == new IntPtr(RevealHotkey.Id) &&
                revealHotkey != null && revealHotkey.Registered)
            {
                PostToUi(RevealWindow);
                return true;
            }
            if (message.Msg == 0x007E || message.Msg == 0x001A || message.Msg == 0x02B1)
            {
                // DISPLAYCHANGE / SETTINGCHANGE / WTSSESSION_CHANGE may arrive
                // in bursts. Check after WinForms and Windows update the screen
                // information; the existing one-second clock also catches up.
                if (message.Msg != 0x001A) lastWorkAreas = null;
                if (!recoveryQueued)
                {
                    recoveryQueued = PostToUi(delegate { recoveryQueued = false; CheckScreenLayout(); });
                }
            }
            return false;
        }

        private static Rectangle[] ReadWorkAreas() { return Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray(); }

        private void CheckScreenLayout()
        {
            if (!desktopRecoveryEnabled || closing || !ready || dragging) return;
            var areas = ReadWorkAreas();
            if (lastWorkAreas != null && lastWorkAreas.SequenceEqual(areas)) return;
            lastWorkAreas = areas;
            RecoverToVisibleArea(areas, Cursor.Position);
        }

        private void RecoverToVisibleArea(Rectangle[] areas, Point pointer)
        {
            if (closing || !ready || dragging) return;
            var fullBounds = dockCollapsed ? expandedBounds : Bounds;
            var corrected = WindowPlacement.Fit(fullBounds, areas, pointer);
            if (corrected == fullBounds && !dockCollapsed) return;
            // Use the expanded dimensions, never the eight-pixel tab. An
            // automatic repair leaves keyboard focus in the user's current app.
            ExpandDock();
            dockEdge = DockEdge.None; dockTimer.Stop(); outsideSince = null;
            Bounds = corrected;
            expandedBounds = Bounds;
            RefreshCircleSurface(true);
            EnsureTopMost(false);
            SavePosition();
        }
    }

    internal static class RecoveryNative
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("wtsapi32.dll", SetLastError = true)] internal static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
        [DllImport("wtsapi32.dll")] internal static extern bool WTSUnRegisterSessionNotification(IntPtr window);
    }
}
