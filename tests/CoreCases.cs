using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexUsageFloat.Tests
{
    internal static partial class RegressionSuite
    {
        private static void CoreCases()
        {
            Case("DATA built-in mapping, missing values, credits and formatting", delegate {
                var result = Json.Map(Json.Serializer().DeserializeObject(Json.Serializer().Serialize(SelfTests.Run())));
                Check((bool)result["success"] && (int)result["passed"] >= 23, "built-in data suite failed");
                assertions += (int)result["passed"];
            });
            Case("DATA rejects nonnumeric and nonfinite values", delegate {
                foreach (object value in new object[] { "12", true, Double.NaN, Double.PositiveInfinity, Double.NegativeInfinity, null })
                    Check(Json.Number(value) == null, "invalid number accepted");
                Throws<UsageException>(() => UsageSnapshot.Parse(Json.Map(Json.Serializer().DeserializeObject("{\"rateLimits\":{\"primary\":{\"usedPercent\":\"4\",\"windowDurationMins\":300}}}"))));
            });
            Case("DISPLAY distant reset does not overflow to one minute", delegate {
                string text = DisplayText.Countdown(new QuotaWindow { ResetsAt = 253000000000L }, new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc));
                Check(text.Contains("天") && text != "1分后重置", "large timestamp overflowed");
                Check(DisplayText.ResetUtc(1794003813).Kind == DateTimeKind.Utc, "timestamp must stay UTC until display");
            });
            Case("CREDIT earliest is independent of order and local timezone", delegate {
                var sample = Sample();
                Check(sample.ResetCreditsCount == 3, "total changed");
                Check(DisplayText.CreditExpiry(sample) == "最先到期 " + DisplayText.ResetUtc(1794003813).ToLocalTime().ToString("yyyy-MM-dd HH:mm"), "earliest incorrect");
                sample.ResetCreditExpiryDates.Reverse();
                Check(DisplayText.CreditExpiry(sample).Contains(DisplayText.ResetUtc(1794003813).ToLocalTime().ToString("yyyy-MM-dd HH:mm")), "response order changed earliest");
                sample.ResetCreditExpiryDates.RemoveAt(0);
                Check(DisplayText.CreditExpiry(sample).StartsWith("已知到期 "), "partial dates claimed to be complete");
                Check(DisplayText.CreditDetails(sample).Contains("其余重置卡"), "missing details not explained");
            });
            foreach (string malformed in new[] { "{", "null", "[]", "{\"SizePercent\":\"oops\"}", "{\"KeepOnTop\":{}}", "{\"KeepOnTop\":\"false\"}", "{\"OpacityPercent\":99999999999999999999999999}" })
            {
                string input = malformed;
                Case("PREFS malformed " + input, delegate {
                    string path = Path.Combine(NewDirectory("invalid-config"), "preferences.json");
                    File.WriteAllText(path, input, new UTF8Encoding(false));
                    var prefs = Preferences.Load(path);
                    Check(prefs.SizePercent == 100 && prefs.KeepOnTop, "corrupt config did not recover to defaults");
                });
            }
            Case("PREFS old configuration retains defaults and validates ranges", delegate {
                string path = Path.Combine(NewDirectory("old-config"), "preferences.json");
                File.WriteAllText(path, "{\"SizePercent\":123,\"OpacityPercent\":77,\"X\":-2147483648,\"Y\":2147483647}");
                var prefs = Preferences.Load(path);
                Check(prefs.SizePercent == 100 && prefs.OpacityPercent == 100, "invalid enum values retained");
                Check(!prefs.X.HasValue && !prefs.Y.HasValue, "unsafe coordinates retained");
                Check(prefs.ShowFiveHour && prefs.ShowWeekly && prefs.ShowResetCredits, "old settings hid newly added items");
                File.WriteAllText(path, new string(' ', 65537));
                Check(Preferences.Load(path).SizePercent == 100, "oversized settings not ignored");
            });
            Case("PREFS atomic round trip into isolated directory", delegate {
                string path = Path.Combine(NewDirectory("atomic"), "nested", "preferences.json");
                var prefs = new Preferences { X = -1234, Y = 100, SizePercent = 150, OpacityPercent = 85, ShowWeekly = false, CircularStyle = true };
                prefs.Save(path); prefs.ShowFiveHour = false; prefs.Save(path);
                var loaded = Preferences.Load(path);
                Check(loaded.X == -1234 && loaded.SizePercent == 150 && loaded.OpacityPercent == 85, "saved values changed");
                Check(!loaded.ShowWeekly && !loaded.ShowFiveHour && loaded.ShowResetCredits, "independent flags changed");
                Check(!File.Exists(path + ".tmp"), "atomic temporary file remains");
                Check(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 239, 187, 191 }) == false, "unexpected BOM");
                string obstruction = Path.Combine(NewDirectory("unwritable"), "file"); File.WriteAllText(obstruction, "x");
                prefs.Save(Path.Combine(obstruction, "preferences.json"));
                Check(File.ReadAllText(obstruction) == "x", "save damaged an existing file");
            });
            Case("PATH explicit missing executable reports configuration error", delegate {
                string prior = Environment.GetEnvironmentVariable("CODEX_USAGE_FLOAT_CODEX");
                try
                {
                    Environment.SetEnvironmentVariable("CODEX_USAGE_FLOAT_CODEX", System.IO.Path.Combine(NewDirectory("missing-exe"), "missing.exe"));
                    Check(Throws<UsageException>(() => UsageReader.ResolveCodex()).Message.Contains("CODEX_USAGE_FLOAT_CODEX"), "bad override silently fell back");
                    Environment.SetEnvironmentVariable("CODEX_USAGE_FLOAT_CODEX", "\"" + fakeExecutable + "\"");
                    Check(UsageReader.ResolveCodex() == fakeExecutable, "quoted explicit path not normalized");
                }
                finally { Environment.SetEnvironmentVariable("CODEX_USAGE_FLOAT_CODEX", prior); }
            });
        }
    }
}
