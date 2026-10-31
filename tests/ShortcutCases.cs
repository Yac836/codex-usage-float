using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CodexUsageFloat.Tests
{
    internal static partial class RegressionSuite
    {
        private static void ShortcutCases()
        {
            Case("SHORTCUT allowed chords, reserved keys and display names", delegate {
                Check(RevealHotkey.IsValid(RevealHotkey.Default), "default rejected");
                Check(RevealHotkey.IsValid(Keys.Control | Keys.Shift | Keys.K) && RevealHotkey.IsValid(Keys.Alt | Keys.D9), "custom modifiers rejected");
                foreach (Keys invalid in new[] { Keys.None, Keys.U, Keys.Shift | Keys.U, Keys.Control | Keys.F12, Keys.Control | Keys.Delete, (Keys)(-1) })
                    Check(!RevealHotkey.IsValid(invalid), "invalid global chord accepted: " + invalid);
                Check(RevealHotkey.Describe(Keys.Control | Keys.Alt | Keys.Shift | Keys.D9) == "Ctrl+Alt+Shift+9", "custom chord displayed incorrectly");
            });
            Case("SHORTCUT old settings, disabled round trip and invalid fallback", delegate {
                string path = Path.Combine(NewDirectory("shortcut-config"), "preferences.json");
                File.WriteAllText(path, "{}"); var settings = Preferences.Load(path);
                Check(settings.HotkeyEnabled && settings.HotkeyKeys == (int)RevealHotkey.Default, "old settings changed default hotkey");
                settings.HotkeyEnabled = false; settings.HotkeyKeys = (int)(Keys.Control | Keys.Shift | Keys.K); settings.Save(path);
                var restored = Preferences.Load(path);
                Check(!restored.HotkeyEnabled && restored.HotkeyKeys == settings.HotkeyKeys, "disabled setting lost chosen chord");
                File.WriteAllText(path, "{\"X\":123,\"Y\":456,\"HotkeyKeys\":85}"); restored = Preferences.Load(path);
                Check(restored.HotkeyKeys == (int)RevealHotkey.Default && restored.X == 123, "invalid chord did not recover without losing position");
            });
            Case("SHORTCUT conflicting change retains old binding and successful change releases it", delegate {
                Keys replacement = Keys.Alt | Keys.Shift | Keys.F23;
                using (var blockerWindow = new Form()) using (var blocker = new RevealHotkey(replacement))
                using (var contenderWindow = new Form()) using (var contender = new RevealHotkey(TestShortcut))
                using (var form = FormForTest(new Preferences(), null, null, true, new RevealHotkey(TestShortcut)))
                {
                    var binding = (RevealHotkey)Field(form, "revealHotkey"); int originalId = binding.MessageId;
                    Check(blocker.Register(blockerWindow.Handle), "replacement blocker failed");
                    Check(Call(form, "ApplyShortcut", true, replacement) is string, "occupied replacement accepted");
                    Check(binding.Registered && binding.Shortcut == TestShortcut && !contender.Register(contenderWindow.Handle), "failed replacement released old chord");
                    Check(Preferences.Load((string)Field(form, "preferencePath")).HotkeyKeys == (int)TestShortcut, "failed replacement persisted unwanted chord");
                    blocker.Unregister();
                    Check(Call(form, "ApplyShortcut", true, replacement) == null, "free replacement rejected");
                    Check(binding.Registered && binding.Shortcut == replacement && binding.MessageId != originalId, "replacement binding not committed");
                    Check(contender.Register(contenderWindow.Handle) && !blocker.Register(blockerWindow.Handle), "old/new system ownership incorrect");
                    Check(((ToolStripMenuItem)Field(form, "revealItem")).ShortcutKeyDisplayString == "Alt+Shift+F23", "menu still advertises old chord");
                    Check(Call(form, "ApplyShortcut", false, Keys.None) == null && !binding.Registered, "disable failed after invalid draft input");
                    Check(Preferences.Load((string)Field(form, "preferencePath")).HotkeyKeys == (int)replacement, "disable lost last valid chord");
                }
            });
            Case("SHORTCUT disabled startup, handle recreation, ignored message and reenable", delegate {
                using (var form = FormForTest(new Preferences { HotkeyEnabled = false }, null, null, true, new RevealHotkey(TestShortcut)))
                using (var owner = new Form()) using (var probe = new RevealHotkey(TestShortcut))
                {
                    var binding = (RevealHotkey)Field(form, "revealHotkey");
                    Check(!binding.Registered && probe.Register(owner.Handle), "disabled startup reserves chord"); probe.Unregister();
                    Check(!((ToolStripMenuItem)Field(form, "retryHotkeyItem")).Available && ((NotifyIcon)Field(form, "recoveryTray")).Text.Contains("关闭"), "disabled status is misleading");
                    typeof(Control).GetMethod("RecreateHandle", Hidden).Invoke(form, null);
                    Check(!binding.Registered, "handle recreation enabled disabled shortcut");
                    form.Location = new Point(30000, 30000);
                    Native.SendMessage(form.Handle, RevealHotkey.Message, new IntPtr(binding.MessageId), IntPtr.Zero); Application.DoEvents();
                    Check(!form.Visible && form.Left == 30000, "disabled hotkey message revealed window");
                    Check(Call(form, "ApplyShortcut", true, TestShortcut) == null && binding.Registered, "reenable failed");
                    Check(Preferences.Load((string)Field(form, "preferencePath")).HotkeyEnabled, "reenable not saved");
                }
            });
            Case("SHORTCUT editor cancel, save, defaults and visible validation error", delegate {
                int calls = 0; Keys saved = Keys.None; bool enabled = false;
                Func<bool, Keys, string> apply = (on, keys) => { calls++; enabled = on; saved = keys; return null; };
                using (var dialog = new ShortcutDialog(true, TestShortcut, apply))
                { dialog.Show(); dialog.KeyBox.SelectedItem = "K"; dialog.CancelButton.PerformClick(); Check(calls == 0, "cancel applied draft"); }
                using (var dialog = new ShortcutDialog(false, TestShortcut, apply))
                {
                    dialog.Show(); dialog.EnabledBox.Checked = true;
                    dialog.CtrlBox.Checked = false; dialog.AltBox.Checked = dialog.ShiftBox.Checked = true; dialog.KeyBox.SelectedItem = "9";
                    Check(!dialog.KeyBox.Items.Contains("F12"), "editor offers reserved F12");
                    dialog.SaveButton.PerformClick(); Check(calls == 1 && enabled && saved == (Keys.Alt | Keys.Shift | Keys.D9), "editor did not save selected combination");
                }
                using (var dialog = new ShortcutDialog(false, TestShortcut, apply))
                {
                    dialog.Show(); dialog.DefaultButton.PerformClick(); dialog.SaveButton.PerformClick();
                    Check(calls == 2 && enabled && saved == RevealHotkey.Default, "restore default did not restore chord and enable");
                }
                using (var dialog = new ShortcutDialog(true, TestShortcut, (on, keys) => "测试冲突"))
                {
                    dialog.Show(); Application.DoEvents();
                    using (var bitmap = new Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size)); bitmap.Save(Path.Combine(outputRoot, "shortcut-editor.png")); }
                    dialog.SaveButton.PerformClick();
                    Check(dialog.Visible && dialog.Hint.Text == "测试冲突", "editor closed or hid failed registration");
                    dialog.Close();
                }
            });
            foreach (bool circular in new[] { false, true })
            {
                bool circle = circular;
                Case("IDLE unchanged clock, countdown boundary and stale data " + circle, delegate {
                    using (var form = FormForTest(new Preferences { CircularStyle = circle }))
                    {
                        DateTime now = DateTime.UtcNow; var sample = Sample(); sample.RetrievedUtc = now; sample.FiveHour.ResetsAt = Unix(now.AddSeconds(90));
                        Put(form, "snapshot", sample); Call(form, "UpdateDisplay");
                        for (int seconds = 0; seconds < 10; seconds++) Check(!(bool)Call(form, "DisplayNeedsUpdate", now.AddSeconds(seconds)), "unchanged tick needs full UI update");
                        Check((bool)Call(form, "DisplayNeedsUpdate", now.AddSeconds(45)), "countdown boundary skipped");
                        Call(form, "SetItemVisible", 0, false); Call(form, "SetItemVisible", 1, false);
                        Check(!(bool)Call(form, "DisplayNeedsUpdate", now.AddSeconds(45)), "hidden quota causes full UI update");
                        Check((bool)Call(form, "DisplayNeedsUpdate", now.AddMinutes(3).AddSeconds(1)), "stale transition skipped with quotas hidden");
                        Put(form, "readError", "模拟读取失败"); Check((bool)Call(form, "DisplayNeedsUpdate", now), "error transition skipped");
                        Call(form, "UpdateDisplay"); Check(!(bool)Call(form, "DisplayNeedsUpdate", now), "unchanged error causes repeated UI updates");
                    }
                });
            }
            Case("SHORTCUT repeated editor disposal releases native drawing resources", delegate {
                uint before = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);
                for (int i = 0; i < 10; i++)
                    using (var dialog = new ShortcutDialog(true, TestShortcut, (on, keys) => null))
                    { dialog.Show(); Application.DoEvents(); dialog.Close(); }
                Check(GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0) <= before + 4, "editor GDI objects grew after repeated dispose");
            });
        }
    }
}
