using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CodexUsageFloat.Tests
{
    internal static partial class RegressionSuite
    {
        private static readonly List<object> results = new List<object>();
        private static string outputRoot, fakeExecutable;
        private static int passed, failed, assertions;

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length == 3 && args[0] == "--owned-probe")
            {
                Environment.SetEnvironmentVariable("CODEX_FLOAT_TEST_MODE", "descend-hang");
                Environment.SetEnvironmentVariable("CODEX_FLOAT_TEST_ROOT", args[2]);
                new UsageReader(args[1], 30000).Read(CancellationToken.None);
                return 2;
            }
            if (args.Length < 2) { Console.Error.WriteLine("Usage: tests.exe <report-directory> <fake-codex.exe> [--desktop]"); return 2; }
            outputRoot = Path.GetFullPath(args[0]);
            fakeExecutable = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(outputRoot);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            CoreCases(); ProcessCases(); LayoutCases(); AsyncCases();
            if (args.Contains("--desktop")) DesktopCases();
            string json = Json.Serializer().Serialize(new {
                success = failed == 0, passed = passed, failed = failed, assertions = assertions,
                desktopChecksRequested = args.Contains("--desktop"), cases = results
            });
            File.WriteAllText(Path.Combine(outputRoot, "results.json"), json, new UTF8Encoding(false));
            Console.WriteLine("Passed {0}; failed {1}; assertions {2}.", passed, failed, assertions);
            return failed == 0 ? 0 : 1;
        }

        private static void Case(string name, Action body)
        {
            var watch = Stopwatch.StartNew(); string error = null;
            try { body(); passed++; }
            catch (Exception e)
            {
                while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
                error = e.GetType().Name + ": " + e.Message;
                failed++; Console.Error.WriteLine("FAIL {0}: {1}", name, error);
            }
            results.Add(new { name = name, success = error == null, elapsedMs = watch.ElapsedMilliseconds, error = error });
        }

        private static void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception(message);
        }

        private static T Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T e) { assertions++; return e; }
            throw new Exception("Expected " + typeof(T).Name);
        }

        private static string NewDirectory(string name)
        {
            string path = Path.Combine(outputRoot, "work", name + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }

        private static bool WaitUntil(Func<bool> condition, int milliseconds, bool pump)
        {
            var watch = Stopwatch.StartNew();
            do { if (pump) Application.DoEvents(); if (condition()) return true; Thread.Sleep(15); }
            while (watch.ElapsedMilliseconds < milliseconds);
            return condition();
        }

        private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Field(UsageForm form, string name) { return typeof(UsageForm).GetField(name, Hidden).GetValue(form); }
        private static object Call(UsageForm form, string name, params object[] args) { return typeof(UsageForm).GetMethod(name, Hidden).Invoke(form, args); }
        private static void Put(UsageForm form, string name, object value) { typeof(UsageForm).GetField(name, Hidden).SetValue(form, value); }

        private static UsageSnapshot Sample()
        {
            return new UsageSnapshot {
                FiveHour = new QuotaWindow { UsedPercent = 37.5, Minutes = 300, ResetsAt = Unix(DateTime.UtcNow.AddHours(3)) },
                Weekly = new QuotaWindow { UsedPercent = 11, Minutes = 10080, ResetsAt = Unix(DateTime.UtcNow.AddDays(4)) },
                RetrievedUtc = DateTime.UtcNow, ResetCreditsCount = 3, ResetCreditDetailsProvided = true,
                ResetCreditExpiryDates = new List<long?> { 1794090213, 1794003813, 1794176613 }
            };
        }
        private static long Unix(DateTime date) { return (long)(date.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
        private static UsageForm FormForTest(Preferences preferences, Func<CancellationToken, UsageSnapshot> reader = null, SurfaceUpload upload = null)
        {
            string path = Path.Combine(NewDirectory("preferences"), "preferences.json");
            preferences.Save(path);
            var form = new UsageForm(path, reader ?? (token => Sample()), upload);
            form.Text = "Codex Usage Float regression " + Guid.NewGuid().ToString("N");
            return form;
        }
    }
}
