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
        internal const Keys Default = Keys.Control | Keys.Alt | Keys.U;
        private IntPtr window;
        internal int MessageId { get; private set; }
        internal Keys Shortcut { get; private set; }
        internal bool Registered { get; private set; }
        internal int LastError { get; private set; }

        internal RevealHotkey(Keys shortcut = Default) { Shortcut = shortcut; MessageId = Id + 1; }
        internal static bool IsValid(Keys shortcut)
        {
            Keys key = shortcut & Keys.KeyCode;
            return (shortcut & ~(Keys.Control | Keys.Alt | Keys.Shift | Keys.KeyCode)) == 0 &&
                (shortcut & (Keys.Control | Keys.Alt)) != 0 &&
                ((key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) ||
                 (key >= Keys.F1 && key <= Keys.F24 && key != Keys.F12));
        }
        internal static string Describe(Keys shortcut)
        {
            Keys key = shortcut & Keys.KeyCode;
            string name = key >= Keys.D0 && key <= Keys.D9 ? ((char)key).ToString() : key.ToString();
            return ((shortcut & Keys.Control) != 0 ? "Ctrl+" : "") + ((shortcut & Keys.Alt) != 0 ? "Alt+" : "") +
                ((shortcut & Keys.Shift) != 0 ? "Shift+" : "") + name;
        }
        internal bool Register(IntPtr handle) { return TryChange(handle, Shortcut); }
        internal bool TryChange(IntPtr handle, Keys shortcut)
        {
            if (!IsValid(shortcut)) { LastError = 87; return false; }
            if (Registered && window == handle && Shortcut == shortcut) return true;
            uint modifiers = 0x4000u | ((shortcut & Keys.Control) != 0 ? 2u : 0u) |
                ((shortcut & Keys.Alt) != 0 ? 1u : 0u) | ((shortcut & Keys.Shift) != 0 ? 4u : 0u);
            int candidateId = MessageId == Id ? Id + 1 : Id;
            // Register the replacement first: a conflict must keep the old
            // chord working. NOREPEAT needs no keyboard hook or key polling.
            if (!Native.RegisterHotKey(handle, candidateId, modifiers, (uint)(shortcut & Keys.KeyCode)))
            { LastError = Marshal.GetLastWin32Error(); return false; }
            Unregister();
            window = handle; Shortcut = shortcut; MessageId = candidateId;
            Registered = true; LastError = 0;
            return true;
        }
        internal void Unregister()
        {
            if (Registered) Native.UnregisterHotKey(window, MessageId);
            Registered = false; window = IntPtr.Zero;
        }
        public void Dispose() { Unregister(); }
    }

    internal sealed class ShortcutDialog : Form
    {
        private readonly Keys[] choices = Enumerable.Range((int)Keys.A, 26).Concat(Enumerable.Range((int)Keys.D0, 10))
            .Concat(Enumerable.Range((int)Keys.F1, 24).Where(key => key != (int)Keys.F12)).Select(key => (Keys)key).ToArray();
        internal readonly CheckBox EnabledBox = new CheckBox { Text = "启用呼出快捷键", AutoSize = true };
        internal readonly CheckBox CtrlBox = new CheckBox { Text = "Ctrl", AutoSize = true };
        internal readonly CheckBox AltBox = new CheckBox { Text = "Alt", AutoSize = true };
        internal readonly CheckBox ShiftBox = new CheckBox { Text = "Shift", AutoSize = true };
        internal readonly ComboBox KeyBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 76 };
        internal readonly Label Hint = new Label { AutoSize = true, MaximumSize = new Size(350, 0) };
        internal readonly Button SaveButton = new Button { Text = "保存", AutoSize = true };
        internal readonly Button DefaultButton = new Button { Text = "恢复默认", AutoSize = true };

        internal ShortcutDialog(bool enabled, Keys current, Func<bool, Keys, string> apply)
        {
            Text = "呼出快捷键"; FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
            MaximizeBox = MinimizeBox = false; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);
            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill, MinimumSize = new Size(350, 0) };
            var chord = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            foreach (Keys key in choices) KeyBox.Items.Add(RevealHotkey.Describe(key));
            chord.Controls.AddRange(new Control[] { CtrlBox, AltBox, ShiftBox, KeyBox });
            buttons.Controls.AddRange(new Control[] { cancel, SaveButton, DefaultButton });
            layout.Controls.Add(EnabledBox); layout.Controls.Add(chord); layout.Controls.Add(Hint); layout.Controls.Add(buttons);
            Controls.Add(layout); AcceptButton = SaveButton; CancelButton = cancel;
            Action<Keys> select = delegate(Keys keys) {
                CtrlBox.Checked = (keys & Keys.Control) != 0; AltBox.Checked = (keys & Keys.Alt) != 0;
                ShiftBox.Checked = (keys & Keys.Shift) != 0; KeyBox.SelectedIndex = Array.IndexOf(choices, keys & Keys.KeyCode);
            };
            EnabledBox.Checked = enabled; select(current);
            EnabledBox.CheckedChanged += delegate { chord.Enabled = EnabledBox.Checked; UpdateHint(); };
            DefaultButton.Click += delegate { select(RevealHotkey.Default); EnabledBox.Checked = true; UpdateHint(); };
            SaveButton.Click += delegate {
                Keys shortcut = (CtrlBox.Checked ? Keys.Control : Keys.None) | (AltBox.Checked ? Keys.Alt : Keys.None) |
                    (ShiftBox.Checked ? Keys.Shift : Keys.None) | (KeyBox.SelectedIndex >= 0 ? choices[KeyBox.SelectedIndex] : Keys.None);
                string error = apply(EnabledBox.Checked, shortcut);
                if (error == null) { DialogResult = DialogResult.OK; Close(); }
                else { Hint.Text = error; Hint.ForeColor = Color.Firebrick; }
            };
            chord.Enabled = enabled; UpdateHint();
        }
        private void UpdateHint()
        {
            Hint.ForeColor = SystemColors.GrayText;
            Hint.Text = EnabledBox.Checked ? "选择 Ctrl/Alt（至少一个）和按键，可叠加 Shift。\n软件内部快捷键可能无法检测；可换键或关闭。" : "关闭后仍可双击托盘或再次运行 EXE 找回。";
        }
    }

    internal sealed partial class UsageForm
    {
        private readonly bool desktopRecoveryEnabled;
        private RevealHotkey revealHotkey;
        private NotifyIcon recoveryTray;
        private Icon recoveryIcon;
        private ToolStripMenuItem revealItem, retryHotkeyItem;
        private Rectangle[] lastWorkAreas;
        private bool recoveryQueued, sessionNotifications, editingShortcut;

        private void InitializeRecovery(RevealHotkey hotkey)
        {
            if (!desktopRecoveryEnabled) return;
            revealHotkey = hotkey ?? new RevealHotkey((Keys)preferences.HotkeyKeys);
            revealItem = new ToolStripMenuItem("呼出到当前屏幕", null, delegate { RevealWindow(); });
            retryHotkeyItem = new ToolStripMenuItem("重试快捷键（可能已被占用）", null, delegate { RegisterRevealHotkey(); });
            context.Items.Insert(0, revealItem);
            context.Items.Insert(1, retryHotkeyItem);
            context.Items.Insert(2, new ToolStripMenuItem("设置呼出快捷键…", null, delegate { EditShortcut(); }));
            context.Items.Insert(3, new ToolStripSeparator());
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
                finally { Native.DestroyIcon(iconHandle); }
            }
        }

        private void RegisterRevealHotkey()
        {
            if (revealHotkey == null || closing || !IsHandleCreated) return;
            if (preferences.HotkeyEnabled) revealHotkey.Register(Handle); else revealHotkey.Unregister();
            UpdateShortcutStatus();
        }
        private void UpdateShortcutStatus()
        {
            string state = !preferences.HotkeyEnabled ? "快捷键已关闭" : revealHotkey.Registered ? RevealHotkey.Describe(revealHotkey.Shortcut) : "快捷键不可用";
            revealItem.ShortcutKeyDisplayString = state;
            retryHotkeyItem.Visible = preferences.HotkeyEnabled && !revealHotkey.Registered;
            recoveryTray.Text = "Codex 额度 · 双击呼出 · " + state;
        }
        private string ApplyShortcut(bool enabled, Keys shortcut)
        {
            if (!RevealHotkey.IsValid(shortcut))
            { if (enabled) return "请选择 Ctrl/Alt + 字母、数字或 F 键（F12 除外）。"; shortcut = (Keys)preferences.HotkeyKeys; }
            if (enabled && !revealHotkey.TryChange(Handle, shortcut)) return "快捷键被占用或无法注册，请换一组；原设置已保留。";
            if (!enabled) revealHotkey.Unregister();
            preferences.HotkeyEnabled = enabled; preferences.HotkeyKeys = (int)shortcut;
            preferences.Save(preferencePath); UpdateShortcutStatus();
            return null;
        }
        private void EditShortcut()
        {
            editingShortcut = true;
            try
            {
                RevealWindow();
                using (var dialog = new ShortcutDialog(preferences.HotkeyEnabled, (Keys)preferences.HotkeyKeys, ApplyShortcut))
                    dialog.ShowDialog(this);
            }
            finally { editingShortcut = false; }
        }

        private void AttachRecoveryHandle()
        {
            if (revealHotkey == null || closing) return;
            RegisterRevealHotkey();
            sessionNotifications = Native.WTSRegisterSessionNotification(Handle, 0);
        }

        private void ReleaseRecoveryHandle()
        {
            if (revealHotkey != null) revealHotkey.Unregister();
            if (sessionNotifications && IsHandleCreated) Native.WTSUnRegisterSessionNotification(Handle);
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
            if (message.Msg == RevealHotkey.Message && revealHotkey != null && revealHotkey.Registered &&
                message.WParam == new IntPtr(revealHotkey.MessageId))
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

}
