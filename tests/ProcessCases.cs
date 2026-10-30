using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace CodexUsageFloat.Tests
{
    internal static partial class RegressionSuite
    {
        private static void ProcessCases()
        {
            foreach (string item in new[] { "success", "rpc-error", "early-exit", "init-hang", "hang", "cancel", "noise", "flood", "stubborn-exit", "descendants" })
            {
                string mode = item;
                Case("RPC/PROCESS " + mode, delegate {
                    string root = NewDirectory(mode); string oldMode = Environment.GetEnvironmentVariable("CODEX_FLOAT_TEST_MODE");
                    string oldRoot = Environment.GetEnvironmentVariable("CODEX_FLOAT_TEST_ROOT");
                    try
                    {
                        Environment.SetEnvironmentVariable("CODEX_FLOAT_TEST_ROOT", root);
                        Environment.SetEnvironmentVariable("CODEX_FLOAT_TEST_MODE", mode == "cancel" ? "hang" : mode);
                        var watch = Stopwatch.StartNew();
                        using (var cancellation = new CancellationTokenSource())
                        {
                            var reader = new UsageReader(fakeExecutable, mode.Contains("hang") ? 2000 : 8000);
                            if (mode == "cancel")
                            {
                                var pending = System.Threading.Tasks.Task.Run(() => reader.Read(cancellation.Token));
                                try
                                {
                                    Check(WaitUntil(() => SharedText(Path.Combine(root, "requests.txt")).Contains("account/rateLimits/read"), 6000, false), "mock read never started before cancellation");
                                    cancellation.Cancel();
                                    Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
                                }
                                finally { cancellation.Cancel(); }
                            }
                            else if (mode == "rpc-error" || mode == "early-exit" || mode.Contains("hang"))
                            {
                                var error = Throws<UsageException>(() => reader.Read(cancellation.Token));
                                Check(!error.Message.Contains("MOCK_PRIVATE_SENTINEL"), "raw backend error leaked");
                                if (mode.Contains("hang")) Check(error.Message.Contains("超时"), "hang did not report timeout");
                            }
                            else
                            {
                                var snapshot = reader.Read(cancellation.Token);
                                Check(snapshot.FiveHour.UsedPercent == 12 && snapshot.Weekly.UsedPercent == 5, "mock quota incorrect");
                                Check(snapshot.ResetCreditsCount == 2, "mock reset credits incorrect");
                            }
                        }
                        Check(watch.ElapsedMilliseconds < 15000, "read exceeded bounded duration");
                        Check(WaitUntil(() => !PidAlive(root, "helper.pid") && !PidAlive(root, "descendant.pid"), 3000, false), "owned helper survived cleanup");
                        string[] methods = File.Exists(Path.Combine(root, "requests.txt")) ? File.ReadAllLines(Path.Combine(root, "requests.txt")) : new string[0];
                        Check(methods.All(m => new[] { "initialize", "initialized", "account/rateLimits/read" }.Contains(m)), "non-read RPC sent");
                        if (mode == "success") Check(methods.SequenceEqual(new[] { "initialize", "initialized", "account/rateLimits/read" }), "unexpected handshake order");
                    }
                    finally
                    {
                        CleanupFake(root);
                        Environment.SetEnvironmentVariable("CODEX_FLOAT_TEST_MODE", oldMode);
                        Environment.SetEnvironmentVariable("CODEX_FLOAT_TEST_ROOT", oldRoot);
                    }
                });
            }
            Case("PROCESS force-ending owner cleans helper and descendant", delegate {
                string root = NewDirectory("owner-killed");
                var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                    "--owned-probe \"" + fakeExecutable + "\" \"" + root + "\"") { UseShellExecute = false, CreateNoWindow = true };
                using (var lifetime = new OwnedProcessJob())
                using (var owner = Process.Start(start))
                {
                    lifetime.Attach(owner);
                    try
                    {
                        Check(WaitUntil(() => PidAlive(root, "helper.pid") && PidAlive(root, "descendant.pid"), 8000, false), "mock descendants never started");
                        Check(PidAlive(root, "helper.pid") && PidAlive(root, "descendant.pid"), "mock children not alive before kill");
                        owner.Kill(); Check(owner.WaitForExit(5000), "owner did not exit");
                        Check(WaitUntil(() => !PidAlive(root, "helper.pid") && !PidAlive(root, "descendant.pid"), 5000, false), "job did not clean descendants on abrupt owner exit");
                    }
                    finally { if (!owner.HasExited) { owner.Kill(); owner.WaitForExit(5000); } CleanupFake(root); }
                }
            });
        }

        private static bool PidAlive(string root, string name)
        {
            int pid;
            if (!Int32.TryParse(SharedText(Path.Combine(root, name)), out pid)) return false;
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
        private static string SharedText(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
            catch (IOException) { return ""; }
        }
        private static void CleanupFake(string root)
        {
            foreach (string file in new[] { "helper.pid", "descendant.pid" })
            {
                int pid; if (!Int32.TryParse(SharedText(Path.Combine(root, file)), out pid)) continue;
                try
                {
                    using (var process = Process.GetProcessById(pid))
                        if (!process.HasExited && String.Equals(process.MainModule.FileName, fakeExecutable, StringComparison.OrdinalIgnoreCase))
                        { process.Kill(); process.WaitForExit(5000); }
                }
                catch (ArgumentException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }
}
