using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

internal static class FakeCodex
{
    private static int Main(string[] args)
    {
        string root = Environment.GetEnvironmentVariable("CODEX_FLOAT_TEST_ROOT");
        if (String.IsNullOrEmpty(root)) return 91;
        if (args.Length > 0 && args[0] == "--descendant")
        {
            File.WriteAllText(Path.Combine(root, "descendant.pid"), Process.GetCurrentProcess().Id.ToString());
            Thread.Sleep(60000); return 0;
        }
        File.WriteAllText(Path.Combine(root, "helper.pid"), Process.GetCurrentProcess().Id.ToString());
        string mode = Environment.GetEnvironmentVariable("CODEX_FLOAT_TEST_MODE");
        var json = new JavaScriptSerializer(); string line;
        while ((line = Console.ReadLine()) != null)
        {
            var request = json.Deserialize<Dictionary<string, object>>(line);
            string method = (string)request["method"];
            File.AppendAllText(Path.Combine(root, "requests.txt"), method + Environment.NewLine);
            if (method != "initialize" && method != "initialized" && method != "account/rateLimits/read") return 92;
            if (!request.ContainsKey("id")) continue;
            int id = Convert.ToInt32(request["id"]);
            if (method == "initialize")
            {
                if (mode == "init-hang") { Thread.Sleep(60000); return 0; }
                Console.WriteLine(json.Serialize(new { id = id, result = new { } })); Console.Out.Flush(); continue;
            }
            if (mode == "early-exit") return 23;
            if (mode == "descendants" || mode == "descend-hang")
            {
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--descendant") { UseShellExecute = false, CreateNoWindow = true });
                var watch = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(root, "descendant.pid")) && watch.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            }
            if (mode == "hang" || mode == "descend-hang") { Thread.Sleep(60000); return 0; }
            if (mode == "noise" || mode == "flood")
            {
                Console.WriteLine("not JSON"); Console.WriteLine("{}"); Console.WriteLine("{\"id\":999,\"result\":{}}");
                int count = mode == "flood" ? 12000 : 2;
                for (int i = 0; i < count; i++) Console.WriteLine("{\"method\":\"notification\",\"params\":{\"n\":" + i + "}}");
            }
            if (mode == "rpc-error") Console.WriteLine(json.Serialize(new { id = id, error = new { code = 401, message = "MOCK_PRIVATE_SENTINEL" } }));
            else Console.WriteLine(json.Serialize(new { id = id, result = new {
                rateLimitsByLimitId = new { codex = new { primary = new { usedPercent = 12, windowDurationMins = 300, resetsAt = 1793339348 }, secondary = new { usedPercent = 5, windowDurationMins = 10080, resetsAt = 1793880449 } } },
                rateLimitResetCredits = new { availableCount = 2, credits = new[] { new { status = "available", resetType = "codexRateLimits", expiresAt = 1794090213L }, new { status = "available", resetType = "codexRateLimits", expiresAt = 1794003813L } } }
            } }));
            Console.Out.Flush();
            if (mode == "stubborn-exit") { Thread.Sleep(60000); return 0; }
        }
        return 0;
    }
}
