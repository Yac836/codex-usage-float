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
    internal static class SelfTests
    {
        internal static object Run()
        {
            int count = 0;
            Action<bool, string> check = delegate(bool pass, string name)
            {
                if (!pass) throw new Exception("Self-test failed: " + name);
                count++;
            };
            Func<string, UsageSnapshot> parse = text => UsageSnapshot.Parse(Json.Map(Json.Serializer().DeserializeObject(text)));
            var both = parse("{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":12.5,\"windowDurationMins\":300,\"resetsAt\":1793293649},\"secondary\":{\"usedPercent\":7,\"windowDurationMins\":10080,\"resetsAt\":1793880449}}}}");
            check(both.FiveHour.UsedPercent == 12.5 && both.Weekly.UsedPercent == 7, "both windows mapped by duration");
            var reversed = parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":30,\"windowDurationMins\":10080},\"secondary\":{\"usedPercent\":6,\"windowDurationMins\":300}}}");
            check(reversed.FiveHour.UsedPercent == 6 && reversed.Weekly.UsedPercent == 30, "reversed order");
            var partial = parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":0,\"windowDurationMins\":300},\"secondary\":null}}");
            check(partial.Weekly == null && partial.FiveHour.UsedPercent == 0, "missing differs from zero");
            check(DisplayText.Percent(partial.Weekly, false) == "—", "unavailable placeholder");
            check(DisplayText.Percent(both.FiveHour, true) == "87.5%", "remaining is complement");
            bool rejected = false;
            try { parse("{\"rateLimitsByLimitId\":{\"codex\":null},\"rateLimits\":{\"primary\":{\"usedPercent\":50,\"windowDurationMins\":300}}}"); }
            catch (UsageException) { rejected = true; }
            check(rejected, "authoritative keyed null does not reuse legacy data");
            rejected = false;
            try { parse("{\"rateLimits\":{\"limitId\":\"other\",\"primary\":{\"usedPercent\":50,\"windowDurationMins\":300}}}"); }
            catch (UsageException) { rejected = true; }
            check(rejected, "other buckets rejected");
            var clamped = parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":120,\"windowDurationMins\":300}}}");
            check(clamped.FiveHour.UsedPercent == 100, "percent clamped");
            var reset = new QuotaWindow { ResetsAt = 100000, Minutes = 300 };
            check(DisplayText.Countdown(reset, DisplayText.ResetUtc(100001)) == "等待额度更新", "passed reset not assumed refreshed");
            check(DisplayText.Countdown(reset, DisplayText.ResetUtc(100000 - 65 * 60)) == "1时5分后重置", "countdown hours");
            check(DisplayText.Countdown(reset, DisplayText.ResetUtc(100000 - 1500 * 60)) == "1天1时后重置", "countdown days");
            var settings = new Preferences { X = 50, Y = 100, ShowRemaining = true };
            var restored = Json.Serializer().Deserialize<Preferences>(Json.Serializer().Serialize(settings));
            check(restored.X == 50 && restored.Y == 100 && restored.KeepOnTop && restored.ShowRemaining,
                "display preferences round trip without losing defaults");
            var cards = parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":1,\"windowDurationMins\":300}},\"rateLimitResetCredits\":{\"availableCount\":2}}");
            check(cards.ResetCreditsCount == 2, "reset credits count is read");
            cards = parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":1,\"windowDurationMins\":300}},\"rateLimitResetCredits\":{\"availableCount\":0}}");
            check(cards.ResetCreditsCount == 0, "zero reset credits is known zero");
            check(partial.ResetCreditsCount == null, "missing reset credits remains unknown");
            var onlyCards = parse("{\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":[{\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":1794003813}]}}");
            check(onlyCards.FiveHour == null && onlyCards.Weekly == null && onlyCards.ResetCreditsCount == 1, "reset credits can be read without quota windows");
            check(onlyCards.ResetCreditExpiryDates.Count == 1 && onlyCards.ResetCreditExpiryDates[0] == 1794003813, "reset expiry parsed as Unix seconds");
            check(DisplayText.CreditExpiry(onlyCards).Contains(DisplayText.ResetUtc(1794003813).ToLocalTime().ToString("yyyy-MM-dd HH:mm")), "expiry displays local calendar time");
            check(DisplayText.CreditExpiry(cards) == "暂无可用重置卡", "zero credits does not display old expiry");
            var incomplete = parse("{\"rateLimitResetCredits\":{\"availableCount\":2,\"credits\":[{\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":1794003813},{\"status\":\"consumed\",\"resetType\":\"codexRateLimits\",\"expiresAt\":100000}]}}");
            check(incomplete.ResetCreditExpiryDates.Count == 1 && DisplayText.CreditExpiry(incomplete).StartsWith("已知到期"), "consumed credits excluded and incomplete detail identified");
            check(DisplayText.CreditExpiry(parse("{\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":null}}")) == "未提供到期日期", "unavailable date not invented");
            var oldPreferences = Json.Serializer().Deserialize<Preferences>("{\"SizePercent\":100,\"OpacityPercent\":85}");
            check(oldPreferences.ShowFiveHour && oldPreferences.ShowWeekly && oldPreferences.ShowResetCredits, "existing preferences default to showing all three items");
            var multipleCards = parse("{\"rateLimitResetCredits\":{\"availableCount\":2,\"credits\":[{\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":1795003813},{\"status\":\"available\",\"resetType\":\"codexRateLimits\",\"expiresAt\":1794003813}]}}");
            check(multipleCards.ResetCreditsCount == 2 && DisplayText.CreditExpiry(multipleCards) == "最先到期 " + DisplayText.ResetUtc(1794003813).ToLocalTime().ToString("yyyy-MM-dd HH:mm"), "multiple cards keep total count and earliest expiry regardless of response order");
            return new { passed = count, success = true };
        }
    }

}
