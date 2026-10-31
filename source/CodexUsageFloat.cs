using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexUsageFloat
{
    internal static class Json
    {
        internal static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 1024 * 1024, RecursionLimit = 48 };
        }
        internal static Dictionary<string, object> Map(object value)
        {
            return value as Dictionary<string, object>;
        }
        internal static object Get(Dictionary<string, object> map, string key)
        {
            object result;
            return map != null && map.TryGetValue(key, out result) ? result : null;
        }
        internal static double? Number(object value)
        {
            if (value == null || value is bool || value is string) return null;
            try
            {
                double n = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return Double.IsNaN(n) || Double.IsInfinity(n) ? (double?)null : n;
            }
            catch { return null; }
        }
    }

    internal sealed class QuotaWindow
    {
        public double UsedPercent { get; set; }
        public long? ResetsAt { get; set; }
        public int Minutes { get; set; }
    }

    internal sealed class UsageSnapshot
    {
        public QuotaWindow FiveHour { get; set; }
        public QuotaWindow Weekly { get; set; }
        public DateTime RetrievedUtc { get; set; }
        public int? ResetCreditsCount { get; set; }
        public List<long?> ResetCreditExpiryDates { get; set; }
        public bool ResetCreditDetailsProvided { get; set; }

        internal static UsageSnapshot Parse(Dictionary<string, object> response)
        {
            Dictionary<string, object> bucket = null;
            var keyed = Json.Map(Json.Get(response, "rateLimitsByLimitId"));
            if (keyed != null && keyed.ContainsKey("codex"))
                bucket = Json.Map(keyed["codex"]);
            else
                bucket = Json.Map(Json.Get(response, "rateLimits"));
            var limitId = Json.Get(bucket, "limitId") as string;
            if (limitId != null && limitId != "codex") bucket = null;
            var result = new UsageSnapshot { RetrievedUtc = DateTime.UtcNow, ResetCreditExpiryDates = new List<long?>() };
            var credits = Json.Map(Json.Get(response, "rateLimitResetCredits"));
            double? count = Json.Number(Json.Get(credits, "availableCount"));
            if (count.HasValue && count.Value >= 0)
                result.ResetCreditsCount = (int)Math.Min(Int32.MaxValue, Math.Floor(count.Value));
            object details = Json.Get(credits, "credits");
            var rows = details as IEnumerable;
            if (rows != null && !(details is string) && !(details is Dictionary<string, object>))
            {
                result.ResetCreditDetailsProvided = true;
                foreach (object item in rows)
                {
                    var credit = Json.Map(item);
                    if ((Json.Get(credit, "status") as string) != "available" ||
                        (Json.Get(credit, "resetType") as string) != "codexRateLimits") continue;
                    double? expiry = Json.Number(Json.Get(credit, "expiresAt"));
                    result.ResetCreditExpiryDates.Add(expiry.HasValue && expiry > 0 && expiry < 253402300800L
                        ? (long?)Math.Floor(expiry.Value) : null);
                }
            }
            foreach (string key in new[] { "primary", "secondary" })
            {
                var raw = Json.Map(Json.Get(bucket, key));
                double? used = Json.Number(Json.Get(raw, "usedPercent"));
                double? duration = Json.Number(Json.Get(raw, "windowDurationMins"));
                if (!used.HasValue || !duration.HasValue) continue;
                if (duration != 300 && duration != 10080) continue;
                double? reset = Json.Number(Json.Get(raw, "resetsAt"));
                long? resetSeconds = reset.HasValue && reset > 0 && reset < 253402300800L
                    ? (long?)Math.Floor(reset.Value) : null;
                var window = new QuotaWindow
                {
                    UsedPercent = Math.Max(0, Math.Min(100, used.Value)),
                    Minutes = (int)duration.Value,
                    ResetsAt = resetSeconds
                };
                if (window.Minutes == 300) result.FiveHour = window;
                else result.Weekly = window;
            }
            if (result.FiveHour == null && result.Weekly == null && !result.ResetCreditsCount.HasValue && !result.ResetCreditDetailsProvided)
                throw new UsageException("暂未返回 5 小时或每周额度");
            return result;
        }
    }

    internal sealed class UsageException : Exception
    {
        internal UsageException(string message) : base(message) { }
    }

    internal sealed class UsageReader
    {
        private readonly string explicitExecutable;
        private readonly int timeoutMs;
        internal UsageReader(string executable = null, int requestTimeoutMs = 15000)
        {
            explicitExecutable = executable;
            timeoutMs = requestTimeoutMs;
        }

        internal static string ResolveCodex()
        {
            string configured = Environment.GetEnvironmentVariable("CODEX_USAGE_FLOAT_CODEX");
            if (!String.IsNullOrWhiteSpace(configured))
            {
                string candidate = configured.Trim().Trim('"');
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                throw new UsageException("指定的 Codex 路径无效，请检查 CODEX_USAGE_FLOAT_CODEX");
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string bins = Path.Combine(local, "OpenAI", "Codex", "bin");
            if (Directory.Exists(bins))
            {
                var candidates = Directory.GetDirectories(bins)
                    .Select(d => Path.Combine(d, "codex.exe"))
                    .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
                if (candidates.Length > 0) return candidates[0];
            }
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string item in path.Split(Path.PathSeparator))
            {
                try
                {
                    string directory = item.Trim().Trim('"');
                    if (String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) continue;
                    string candidate = Path.Combine(directory, "codex.exe");
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException) { }
            }
            throw new UsageException("未找到 Codex，请先打开桌面版");
        }

        internal UsageSnapshot Read(CancellationToken cancellation)
        {
            string executable = explicitExecutable ?? ResolveCodex();
            var start = new ProcessStartInfo(executable, "app-server --listen stdio://")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            // Bound unsolicited notifications so a noisy helper cannot grow
            // the UI process without limit. Keep the newest pending lines.
            var lines = new BlockingCollection<string>(128);
            using (var job = new OwnedProcessJob())
            using (var process = new Process { StartInfo = start })
            {
                DataReceivedEventHandler output = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null || e.Data.Length > 1024 * 1024) return;
                    try
                    {
                        if (!lines.TryAdd(e.Data))
                        {
                            string discarded;
                            lines.TryTake(out discarded);
                            lines.TryAdd(e.Data);
                        }
                    }
                    catch (InvalidOperationException) { }
                };
                // Drain stderr without saving account details, paths or tokens.
                DataReceivedEventHandler error = delegate { };
                process.OutputDataReceived += output;
                process.ErrorDataReceived += error;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!process.Start()) throw new UsageException("额度读取程序启动失败");
                    job.Attach(process);
                    using (cancellation.Register(delegate { KillOwned(process); }))
                    {
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        Request(process, lines, 1, "initialize", new
                        {
                            clientInfo = new
                            {
                                name = "codex_usage_float",
                                title = "Codex Usage Float",
                                version = "1.0.0"
                            }
                        }, cancellation);
                        Write(process, new { method = "initialized", @params = new { } });
                        var response = Request(process, lines, 2, "account/rateLimits/read", new { }, cancellation);
                        return UsageSnapshot.Parse(response);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (UsageException) { throw; }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new UsageException("无法启动 Codex，请打开桌面版后重试");
                }
                catch (IOException) { throw new UsageException("额度连接已断开，稍后重试"); }
                finally
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.StandardInput.Close();
                            if (!process.WaitForExit(800)) KillOwned(process);
                            process.WaitForExit(1200);
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    process.OutputDataReceived -= output;
                    process.ErrorDataReceived -= error;
                    lines.Dispose();
                }
            }
        }

        private Dictionary<string, object> Request(Process process, BlockingCollection<string> lines,
            int id, string method, object parameters, CancellationToken cancellation)
        {
            Write(process, new { id = id, method = method, @params = parameters });
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                cancellation.ThrowIfCancellationRequested();
                string line;
                if (!lines.TryTake(out line, 100, cancellation))
                {
                    if (process.HasExited) throw new UsageException("额度读取程序已退出");
                    continue;
                }
                Dictionary<string, object> message;
                try { message = Json.Map(Json.Serializer().DeserializeObject(line)); }
                catch (ArgumentException) { continue; }
                catch (InvalidOperationException) { continue; }
                if (Json.Number(Json.Get(message, "id")) != id) continue;
                if (Json.Get(message, "error") != null)
                    throw new UsageException("读取失败，请确认 Codex 已登录且网络可用");
                var result = Json.Map(Json.Get(message, "result"));
                if (result == null) throw new UsageException("Codex 返回的数据暂不可用");
                return result;
            }
            throw new UsageException("读取超时，稍后自动重试");
        }

        private static void Write(Process process, object value)
        {
            process.StandardInput.WriteLine(Json.Serializer().Serialize(value));
            process.StandardInput.Flush();
        }

        private static void KillOwned(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    // Closing this handle also closes only our own helper if the UI crashes
    // or is ended from Task Manager. It never contains the user's Codex app.
    internal sealed class OwnedProcessJob : IDisposable
    {
        private Microsoft.Win32.SafeHandles.SafeFileHandle handle;
        internal OwnedProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid) throw new UsageException("无法创建额度读取进程");
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
            {
                handle.Dispose();
                throw new UsageException("无法初始化额度读取进程");
            }
        }
        internal void Attach(Process process)
        {
            if (!AssignProcessToJobObject(handle, process.Handle))
                throw new UsageException("无法管理额度读取进程，请稍后重试");
        }
        public void Dispose() { if (handle != null) handle.Dispose(); }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            internal long ProcessUserTime, JobUserTime;
            internal uint LimitFlags;
            internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcessLimit;
            internal UIntPtr Affinity;
            internal uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            internal BasicLimits Basic;
            internal IoCounters Io;
            internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job,
            int informationClass, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job, IntPtr process);
    }

    internal sealed class Preferences
    {
        public int? X { get; set; }
        public int? Y { get; set; }
        public int SizePercent { get; set; }
        public int OpacityPercent { get; set; }
        public bool ShowRemaining { get; set; }
        public bool KeepOnTop { get; set; }
        public bool EdgeAutoHide { get; set; }
        public bool CircularStyle { get; set; }
        public bool ShowFiveHour { get; set; }
        public bool ShowWeekly { get; set; }
        public bool ShowResetCredits { get; set; }
        public bool HotkeyEnabled { get; set; }
        public int HotkeyKeys { get; set; }
        internal static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageFloat");
        internal static readonly string FilePath = Path.Combine(Folder, "preferences.json");

        public Preferences()
        {
            SizePercent = 100;
            OpacityPercent = 100;
            KeepOnTop = true;
            EdgeAutoHide = true;
            ShowFiveHour = ShowWeekly = ShowResetCredits = true;
            HotkeyEnabled = true;
            HotkeyKeys = (int)RevealHotkey.Default;
        }
        internal static Preferences Load(string filePath = null)
        {
            string target = filePath ?? FilePath;
            try
            {
                if (File.Exists(target) && new FileInfo(target).Length <= 65536)
                {
                    Preferences p;
                    try
                    {
                        var serializer = Json.Serializer();
                        var raw = Json.Map(serializer.DeserializeObject(File.ReadAllText(target, Encoding.UTF8)));
                        if (raw == null) return new Preferences();
                        foreach (string key in new[] { "KeepOnTop", "EdgeAutoHide", "CircularStyle", "ShowRemaining", "ShowFiveHour", "ShowWeekly", "ShowResetCredits", "HotkeyEnabled" })
                            if (raw.ContainsKey(key) && !(raw[key] is bool)) return new Preferences();
                        foreach (string key in new[] { "SizePercent", "OpacityPercent", "X", "Y", "HotkeyKeys" })
                        {
                            if (!raw.ContainsKey(key) || ((key == "X" || key == "Y") && raw[key] == null)) continue;
                            double? number = Json.Number(raw[key]);
                            if (!number.HasValue || number.Value != Math.Floor(number.Value) || number.Value < Int32.MinValue || number.Value > Int32.MaxValue)
                                return new Preferences();
                        }
                        p = serializer.ConvertToType<Preferences>(raw);
                    }
                    // JavaScriptSerializer can throw a base Exception for a
                    // typed property conversion (for example SizePercent="x").
                    catch (Exception error)
                    {
                        if (error is OutOfMemoryException) throw;
                        return new Preferences();
                    }
                    if (p != null)
                    {
                        if (!new[] { 100, 125, 150 }.Contains(p.SizePercent)) p.SizePercent = 100;
                        if (!new[] { 100, 85, 70 }.Contains(p.OpacityPercent)) p.OpacityPercent = 100;
                        if (!RevealHotkey.IsValid((Keys)p.HotkeyKeys)) p.HotkeyKeys = (int)RevealHotkey.Default;
                        if (!p.X.HasValue || !p.Y.HasValue || Math.Abs((long)p.X.Value) > 1000000 || Math.Abs((long)p.Y.Value) > 1000000)
                            p.X = p.Y = null;
                        return p;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return new Preferences();
        }
        internal void Save(string filePath = null)
        {
            try
            {
                string target = Path.GetFullPath(filePath ?? FilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                string temporary = target + ".tmp";
                File.WriteAllText(temporary, Json.Serializer().Serialize(this), new UTF8Encoding(false));
                if (File.Exists(target))
                    File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static class DisplayText
    {
        internal static DateTime ResetUtc(long seconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }
        internal static string Countdown(QuotaWindow window, DateTime nowUtc)
        {
            if (window == null) return "暂未返回额度";
            if (!window.ResetsAt.HasValue) return "重置时间未知";
            var delta = ResetUtc(window.ResetsAt.Value) - nowUtc;
            if (delta.TotalSeconds <= 0) return "等待额度更新";
            long total = Math.Max(1L, (long)Math.Ceiling(delta.TotalMinutes));
            if (total >= 1440) return String.Format("{0}天{1}时后重置", total / 1440, (total % 1440) / 60);
            if (total >= 60) return String.Format("{0}时{1}分后重置", total / 60, total % 60);
            return String.Format("{0}分后重置", total);
        }
        internal static string Percent(QuotaWindow window, bool remaining)
        {
            if (window == null) return "—";
            return (remaining ? 100 - window.UsedPercent : window.UsedPercent)
                .ToString("0.#", CultureInfo.InvariantCulture) + "%";
        }
        internal static string CreditExpiry(UsageSnapshot snapshot)
        {
            if (snapshot == null) return "正在读取到期日期";
            if (snapshot.ResetCreditsCount == 0) return "暂无可用重置卡";
            var dates = snapshot.ResetCreditExpiryDates ?? new List<long?>();
            var known = dates.Where(d => d.HasValue).Select(d => d.Value).OrderBy(d => d).ToArray();
            if (known.Length == 0) return "未提供到期日期";
            string prefix = snapshot.ResetCreditsCount == 1 && dates.Count == 1 ? "到期 " :
                !snapshot.ResetCreditsCount.HasValue || known.Length < snapshot.ResetCreditsCount.Value ? "已知到期 " : "最先到期 ";
            return prefix + ResetUtc(known[0]).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        internal static string CreditDetails(UsageSnapshot snapshot)
        {
            if (snapshot == null) return "正在读取重置卡信息";
            if (snapshot.ResetCreditsCount == 0) return "重置卡：0 张可用\n暂无可用重置卡\n仅展示信息，不会使用重置卡";
            string count = snapshot.ResetCreditsCount.HasValue ? snapshot.ResetCreditsCount.Value + " 张可用" : "可用数量未提供";
            var dates = snapshot.ResetCreditExpiryDates ?? new List<long?>();
            var text = new List<string> { "重置卡：" + count };
            if (!snapshot.ResetCreditDetailsProvided) text.Add("服务仅返回数量，未提供逐张到期日期");
            else
            {
                for (int i = 0; i < dates.Count; i++)
                    text.Add("第 " + (i + 1) + " 张：" + (dates[i].HasValue ? ResetUtc(dates[i].Value).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " 到期" : "未提供到期日期"));
                if (snapshot.ResetCreditsCount.HasValue && dates.Count < snapshot.ResetCreditsCount.Value)
                    text.Add("其余重置卡的到期明细未返回");
            }
            text.Add("仅展示信息，不会使用重置卡");
            return String.Join("\n", text);
        }
    }

    internal enum DockEdge { None, Left, Right, Top, Bottom }

    internal static class EdgeDocking
    {
        internal static DockEdge Detect(Rectangle window, Rectangle work, int distance)
        {
            var edges = new[] { DockEdge.Left, DockEdge.Right, DockEdge.Top, DockEdge.Bottom };
            var gaps = new[] { Math.Abs(window.Left - work.Left), Math.Abs(window.Right - work.Right),
                Math.Abs(window.Top - work.Top), Math.Abs(window.Bottom - work.Bottom) };
            DockEdge edge = DockEdge.None;
            int nearest = distance + 1;
            for (int i = 0; i < edges.Length; i++)
                if (gaps[i] < nearest) { nearest = gaps[i]; edge = edges[i]; }
            return edge;
        }
        internal static Rectangle Snap(Rectangle window, Rectangle work, DockEdge edge)
        {
            int x = Math.Max(work.Left, Math.Min(window.Left, work.Right - window.Width));
            int y = Math.Max(work.Top, Math.Min(window.Top, work.Bottom - window.Height));
            if (edge == DockEdge.Left) x = work.Left;
            if (edge == DockEdge.Right) x = Math.Max(work.Left, work.Right - window.Width);
            if (edge == DockEdge.Top) y = work.Top;
            if (edge == DockEdge.Bottom) y = Math.Max(work.Top, work.Bottom - window.Height);
            return new Rectangle(x, y, window.Width, window.Height);
        }
        internal static Rectangle Tab(Rectangle window, Rectangle work, DockEdge edge, int thickness, int length)
        {
            if (edge == DockEdge.Left || edge == DockEdge.Right)
            {
                int h = Math.Min(window.Height, length);
                int y = Math.Max(work.Top, Math.Min(window.Top + (window.Height - h) / 2, work.Bottom - h));
                return new Rectangle(edge == DockEdge.Left ? work.Left : work.Right - thickness, y, thickness, h);
            }
            int w = Math.Min(window.Width, length);
            int x = Math.Max(work.Left, Math.Min(window.Left + (window.Width - w) / 2, work.Right - w));
            return new Rectangle(x, edge == DockEdge.Top ? work.Top : work.Bottom - thickness, w, thickness);
        }
    }

    internal sealed partial class UsageForm : Form
    {
        internal const string WindowTitle = "Codex 额度悬浮窗";
        internal const int RevealMessage = 0x8000 + 47;
        private readonly Preferences preferences;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer countdown = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer savePosition = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer dockTimer = new System.Windows.Forms.Timer();
        private readonly ToolTip tip = new ToolTip { AutoPopDelay = 15000, InitialDelay = 300, ReshowDelay = 100 };
        private readonly Label heading = new Label();
        private readonly Label mode = new Label();
        private readonly Label[] names = { new Label(), new Label() };
        private readonly Label[] values = { new Label(), new Label() };
        private readonly Label[] resets = { new Label(), new Label() };
        private readonly Label status = new Label();
        private readonly Label creditName = new Label();
        private readonly Label creditValue = new Label();
        private readonly Label creditExpiry = new Label();
        private readonly Label emptyLabel = new Label();
        private readonly Button refreshButton = new Button();
        private readonly Color background = Color.FromArgb(22, 26, 33);
        private readonly Color muted = Color.FromArgb(144, 156, 174);
        private readonly Color[] accents = { Color.FromArgb(92, 190, 248), Color.FromArgb(183, 154, 251) };
        private List<Font> fonts = new List<Font>();
        private readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();
        private UsageSnapshot snapshot;
        private string readError;
        private bool displayedStale;
        private int reading;
        private bool ready;
        private volatile bool closing;
        private float systemScale;
        private float scale;
        private readonly string preferencePath;
        private readonly Func<CancellationToken, UsageSnapshot> readUsage;
        private readonly SurfaceUpload uploadCircle;
        private Task refreshTask;
        private bool resourcesDisposed;
        private string drawingWarning;
        private ContextMenuStrip context;
        private ToolStripMenuItem cardStyle, circleStyle;
        private DockEdge dockEdge;
        private Rectangle expandedBounds;
        private bool dockCollapsed, adjustingDock, dragging;
        private bool applyingTopmost;
        private DateTime? outsideSince;

        internal UsageForm() : this(null, null) { }

        internal UsageForm(string settingsPath, Func<CancellationToken, UsageSnapshot> reader, SurfaceUpload surfaceUpload = null,
            bool enableRecovery = true, RevealHotkey hotkey = null)
        {
            desktopRecoveryEnabled = enableRecovery;
            preferencePath = settingsPath;
            readUsage = reader ?? (token => new UsageReader().Read(token));
            uploadCircle = surfaceUpload ?? LayeredSurface.Update;
            preferences = Preferences.Load(preferencePath);
            Text = WindowTitle;
            AccessibleName = WindowTitle;
            FormBorderStyle = FormBorderStyle.None;
            // Hide from the taskbar through the native tool-window style.
            // ShowInTaskbar=false creates a hidden owner in WinForms, which
            // obscures the floating window from window-management tools.
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = background;
            ForeColor = Color.FromArgb(236, 241, 248);
            DoubleBuffered = true;
            TopMost = preferences.KeepOnTop;
            Opacity = preferences.CircularStyle ? 1 : preferences.OpacityPercent / 100.0;
            using (var graphics = CreateGraphics()) systemScale = graphics.DpiX / 96f;

            heading.Text = "Codex 额度";
            names[0].Text = "5小时";
            names[1].Text = "每周";
            creditName.Text = "重置卡";
            emptyLabel.Text = "右键选择显示项目";
            foreach (var label in new[] { heading, mode, names[0], names[1], values[0], values[1], resets[0], resets[1], status,
                creditName, creditValue, creditExpiry, emptyLabel })
            {
                label.BackColor = Color.Transparent;
                label.AutoSize = false;
                label.UseMnemonic = false;
                label.TextAlign = ContentAlignment.MiddleLeft;
                label.MouseDown += BeginDrag;
                label.DoubleClick += delegate { StartRefresh(); };
                Controls.Add(label);
            }
            refreshButton.Text = "↻";
            refreshButton.FlatStyle = FlatStyle.Flat;
            refreshButton.FlatAppearance.BorderSize = 0;
            refreshButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(43, 53, 66);
            refreshButton.ForeColor = muted;
            refreshButton.BackColor = background;
            refreshButton.TabStop = true;
            refreshButton.AccessibleName = "立即刷新额度";
            refreshButton.Click += delegate { StartRefresh(); };
            Controls.Add(refreshButton);
            tip.SetToolTip(refreshButton, "立即刷新；双击窗口也可刷新");
            MouseDown += BeginDrag;
            DoubleClick += delegate { StartRefresh(); };

            context = new ContextMenuStrip();
            context.Items.Add("立即刷新", null, delegate { StartRefresh(); });
            var remaining = new ToolStripMenuItem("显示剩余额度") { Checked = preferences.ShowRemaining, CheckOnClick = true };
            remaining.CheckedChanged += delegate
            {
                preferences.ShowRemaining = remaining.Checked;
                preferences.Save(preferencePath);
                UpdateDisplay();
            };
            context.Items.Add(remaining);
            var onTop = new ToolStripMenuItem("保持置顶") { Checked = preferences.KeepOnTop, CheckOnClick = true };
            onTop.CheckedChanged += delegate
            {
                preferences.KeepOnTop = onTop.Checked;
                EnsureTopMost(true);
                preferences.Save(preferencePath);
            };
            context.Items.Add(onTop);
            var autoHide = new ToolStripMenuItem("贴边自动收起") { Checked = preferences.EdgeAutoHide, CheckOnClick = true };
            autoHide.CheckedChanged += delegate
            {
                SetAutoHide(autoHide.Checked);
                SavePosition();
            };
            context.Items.Add(autoHide);
            var itemsMenu = new ToolStripMenuItem("显示项目");
            for (int i = 0; i < 3; i++)
            {
                int itemId = i;
                var item = new ToolStripMenuItem(new[] { "5小时额度", "每周额度", "重置卡和到期日期" }[i])
                    { Checked = ItemVisible(i), CheckOnClick = true };
                item.CheckedChanged += delegate { SetItemVisible(itemId, item.Checked); };
                itemsMenu.DropDownItems.Add(item);
            }
            context.Items.Add(itemsMenu);
            var styleMenu = new ToolStripMenuItem("样式");
            cardStyle = new ToolStripMenuItem("信息卡片") { Checked = !preferences.CircularStyle };
            circleStyle = new ToolStripMenuItem("圆形仪表") { Checked = preferences.CircularStyle };
            cardStyle.Click += delegate
            {
                SetCircularStyle(false);
                cardStyle.Checked = true; circleStyle.Checked = false;
            };
            circleStyle.Click += delegate
            {
                SetCircularStyle(true);
                cardStyle.Checked = false; circleStyle.Checked = true;
            };
            styleMenu.DropDownItems.Add(cardStyle);
            styleMenu.DropDownItems.Add(circleStyle);
            context.Items.Add(styleMenu);
            var sizeMenu = new ToolStripMenuItem("大小");
            foreach (int n in new[] { 100, 125, 150 })
            {
                int chosen = n;
                var item = new ToolStripMenuItem(n + "%") { Checked = preferences.SizePercent == n };
                item.Click += delegate
                {
                    preferences.SizePercent = chosen;
                    foreach (ToolStripMenuItem other in sizeMenu.DropDownItems) other.Checked = other == item;
                    ExpandDock();
                    ApplyLayout();
                    KeepVisible();
                    UpdateDockAnchor(true);
                    SavePosition();
                };
                sizeMenu.DropDownItems.Add(item);
            }
            context.Items.Add(sizeMenu);
            var opacityMenu = new ToolStripMenuItem("透明度");
            foreach (int n in new[] { 100, 85, 70 })
            {
                int chosen = n;
                var item = new ToolStripMenuItem(n + "%") { Checked = preferences.OpacityPercent == n };
                item.Click += delegate
                {
                    preferences.OpacityPercent = chosen;
                    if (preferences.CircularStyle) RefreshCircleSurface(true);
                    else Opacity = chosen / 100.0;
                    EnsureTopMost(true);
                    foreach (ToolStripMenuItem other in opacityMenu.DropDownItems) other.Checked = other == item;
                    preferences.Save(preferencePath);
                };
                opacityMenu.DropDownItems.Add(item);
            }
            context.Items.Add(opacityMenu);
            context.Items.Add("重置位置", null, delegate
            {
                ExpandDock(); dockEdge = DockEdge.None; dockTimer.Stop();
                DefaultLocation(); expandedBounds = Bounds; SavePosition();
            });
            context.Items.Add(new ToolStripSeparator());
            context.Items.Add("退出", null, delegate { Close(); });
            ContextMenuStrip = context;
            foreach (Control control in Controls) control.ContextMenuStrip = context;

            ApplyLayout();
            if (preferences.X.HasValue && preferences.Y.HasValue)
                Location = new Point(preferences.X.Value, preferences.Y.Value);
            else DefaultLocation();
            KeepVisible();
            ready = true;
            expandedBounds = Bounds;
            UpdateDisplay();

            countdown.Interval = 1000;
            countdown.Tick += delegate
            {
                EnsureTopMost(false);
                CheckScreenLayout();
                if (DisplayNeedsUpdate(DateTime.UtcNow)) UpdateDisplay();
            };
            refresh.Interval = 60000;
            refresh.Tick += delegate { StartRefresh(); };
            savePosition.Interval = 500;
            savePosition.Tick += delegate { savePosition.Stop(); SavePosition(); };
            LocationChanged += delegate
            {
                if (ready && !closing && !adjustingDock && !dockCollapsed)
                {
                    expandedBounds = Bounds;
                    savePosition.Stop(); savePosition.Start();
                }
            };
            dockTimer.Interval = 180;
            dockTimer.Tick += delegate
            {
                UpdateDockHover(Cursor.Position, context.Visible || editingShortcut, Control.MouseButtons != MouseButtons.None, DateTime.UtcNow);
            };
            MouseEnter += delegate { if (dockCollapsed) ExpandDock(); };
            context.Opening += delegate { ExpandDock(); outsideSince = null; };
            context.Closed += delegate { outsideSince = DateTime.UtcNow; };
            Shown += delegate
            {
                countdown.Start(); refresh.Start(); StartRefresh();
                UpdateDockAnchor(false);
                EnsureTopMost(true);
            };
            FormClosing += delegate
            {
                closing = true;
                countdown.Stop();
                refresh.Stop();
                savePosition.Stop();
                dockTimer.Stop();
                SavePosition();
                shutdown.Cancel();
            };
            InitializeRecovery(hotkey);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed)
            {
                resourcesDisposed = true;
                closing = true;
                DisposeRecovery();
                shutdown.Cancel();
                countdown.Dispose(); refresh.Dispose(); savePosition.Dispose(); dockTimer.Dispose();
                tip.Dispose();
                if (context != null) context.Dispose();
                if (refreshTask == null || refreshTask.IsCompleted) shutdown.Dispose();
                else refreshTask.ContinueWith(task => shutdown.Dispose(), TaskScheduler.Default);
            }
            base.Dispose(disposing);
            if (disposing)
            {
                foreach (Font font in fonts) font.Dispose();
                fonts.Clear(); fontCache.Clear();
            }
        }

        private bool PostToUi(Action callback)
        {
            if (closing || IsDisposed || !IsHandleCreated) return false;
            try { BeginInvoke(callback); return true; }
            catch (InvalidOperationException) { return false; }
        }

        private int S(int n) { return (int)Math.Round(n * scale); }
        private void EnsureTopMost(bool force)
        {
            if (preferences == null || closing || IsDisposed || !IsHandleCreated || applyingTopmost) return;
            applyingTopmost = true;
            try
            {
                bool desired = preferences.KeepOnTop;
                if (TopMost != desired) TopMost = desired;
                bool actual = (Native.GetWindowLongPtr(Handle, -20).ToInt64() & 8) != 0;
                if (force || actual != desired)
                    Native.SetWindowPos(Handle, new IntPtr(desired ? -1 : -2), 0, 0, 0, 0, 0x13);
            }
            finally { applyingTopmost = false; }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            circleLayerConfigured = circleLayerActive = false;
            circleFrameKey = null;
            EnsureTopMost(true);
            AttachRecoveryHandle();
            if (ready) PostToUi(delegate { RefreshCircleSurface(true); });
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            ReleaseRecoveryHandle();
            circleLayerConfigured = circleLayerActive = false;
            circleFrameKey = null;
            base.OnHandleDestroyed(e);
        }
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            PostToUi(delegate { EnsureTopMost(false); });
        }
        protected override void WndProc(ref Message message)
        {
            if (HandleRecoveryMessage(ref message)) return;
            if (message.Msg == RevealMessage && ready && !closing)
            {
                PostToUi(RevealWindow);
                return;
            }
            base.WndProc(ref message);
        }
        private void RevealWindow()
        {
            if (closing || IsDisposed) return;
            ExpandDock();
            dockEdge = DockEdge.None;
            dockTimer.Stop();
            outsideSince = null;
            WindowState = FormWindowState.Normal;
            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            Bounds = WindowPlacement.Center(Size, work);
            expandedBounds = Bounds;
            Show();
            EnsureTopMost(true);
            BringToFront();
            Activate();
            Native.SetForegroundWindow(Handle);
            SavePosition();
        }
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle = (parameters.ExStyle | 0x80) & ~0x40000;
                return parameters;
            }
        }
        private Font MakeFont(float points, FontStyle style)
        {
            float size = points * preferences.SizePercent / 100f;
            string key = size.ToString("0.###", CultureInfo.InvariantCulture) + ":" + (int)style;
            Font font;
            if (fontCache.TryGetValue(key, out font)) return font;
            font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
            fontCache.Add(key, font);
            fonts.Add(font);
            return font;
        }
        private void Place(Control control, int x, int y, int w, int h, float fontSize, FontStyle style)
        {
            control.Bounds = new Rectangle(S(x), S(y), S(w), S(h));
            control.Font = MakeFont(fontSize, style);
        }
        private void ApplyLayout()
        {
            scale = systemScale * preferences.SizePercent / 100f;
            SuspendLayout();
            int count = VisibleItemCount();
            if (preferences.CircularStyle)
            {
                LayoutCircle();
            }
            else
            {
                ClientSize = new Size(S(382), S(Math.Max(110, 68 + count * 42)));
                heading.Text = "Codex 额度";
                Place(heading, 16, 8, 170, 26, 10, FontStyle.Bold);
                heading.TextAlign = ContentAlignment.MiddleLeft;
                heading.ForeColor = ForeColor;
                Place(mode, 232, 10, 134, 22, 8.5f, FontStyle.Regular);
                mode.TextAlign = ContentAlignment.MiddleRight;
                for (int i = 0; i < 2; i++)
                {
                    int top = 40 + Math.Max(0, VisibleRow(i)) * 42;
                    Place(names[i], 16, top + 1, 56, 28, 10, FontStyle.Regular);
                    names[i].TextAlign = ContentAlignment.MiddleLeft;
                    Place(values[i], 72, top - 3, 79, 36, 17, FontStyle.Bold);
                    Place(resets[i], 232, top + 2, 138, 26, 8.5f, FontStyle.Regular);
                    resets[i].TextAlign = ContentAlignment.MiddleLeft;
                }
                int cardTop = 40 + Math.Max(0, VisibleRow(2)) * 42;
                Place(creditName, 16, cardTop + 1, 56, 28, 10, FontStyle.Regular);
                creditName.TextAlign = ContentAlignment.MiddleLeft;
                Place(creditValue, 72, cardTop - 3, 79, 36, 17, FontStyle.Bold);
                Place(creditExpiry, 172, cardTop + 2, 198, 26, 8.5f, FontStyle.Regular);
                creditExpiry.TextAlign = ContentAlignment.MiddleRight;
                Place(emptyLabel, 16, 41, 310, 24, 9, FontStyle.Regular);
                emptyLabel.TextAlign = ContentAlignment.MiddleLeft;
            }
            mode.ForeColor = muted;
            for (int i = 0; i < 2; i++)
            {
                names[i].ForeColor = muted;
                resets[i].ForeColor = muted;
            }
            creditName.ForeColor = muted;
            creditValue.ForeColor = Color.FromArgb(244, 194, 112);
            creditExpiry.ForeColor = muted;
            emptyLabel.ForeColor = muted;
            int footer = Math.Max(82, 40 + count * 42);
            Place(status, 16, footer, 310, 20, 8, FontStyle.Regular);
            status.ForeColor = muted;
            status.AutoEllipsis = true;
            Place(refreshButton, 342, footer - 3, 27, 26, 14, FontStyle.Regular);
            UpdateControlVisibility();
            UpdateWindowRegion();
            ResumeLayout(false);
            Invalidate();
        }
        private bool ItemVisible(int item)
        {
            return item == 0 ? preferences.ShowFiveHour : item == 1 ? preferences.ShowWeekly : preferences.ShowResetCredits;
        }
        private int VisibleItemCount()
        {
            return (preferences.ShowFiveHour ? 1 : 0) + (preferences.ShowWeekly ? 1 : 0) + (preferences.ShowResetCredits ? 1 : 0);
        }
        private int VisibleRow(int item)
        {
            int row = -1;
            for (int i = 0; i <= item; i++) if (ItemVisible(i)) row++;
            return row;
        }
        private int CircleDesignSize()
        {
            int quotas = (preferences.ShowFiveHour ? 1 : 0) + (preferences.ShowWeekly ? 1 : 0);
            if (quotas == 2) return preferences.ShowResetCredits ? 260 : 220;
            if (quotas == 1 && preferences.ShowResetCredits) return 236;
            return 188;
        }
        private void SetItemVisible(int item, bool visible)
        {
            ExpandDock();
            if (item == 0) preferences.ShowFiveHour = visible;
            else if (item == 1) preferences.ShowWeekly = visible;
            else preferences.ShowResetCredits = visible;
            ApplyLayout();
            KeepVisible();
            UpdateDockAnchor(true);
            UpdateDisplay();
            SavePosition();
        }
        private void SetCircularStyle(bool circular)
        {
            ExpandDock();
            drawingWarning = null;
            preferences.CircularStyle = circular;
            cardStyle.Checked = !circular;
            circleStyle.Checked = circular;
            ApplyLayout();
            KeepVisible();
            UpdateDockAnchor(true);
            UpdateDisplay();
            SavePosition();
        }
        private void UpdateControlVisibility()
        {
            if (preferences.CircularStyle)
            {
                // Text is composed with the same alpha surface as the circle.
                foreach (Control control in Controls) control.Visible = false;
                return;
            }
            foreach (Control control in Controls) control.Visible = !dockCollapsed;
            if (dockCollapsed) return;
            for (int i = 0; i < 2; i++)
                names[i].Visible = values[i].Visible = resets[i].Visible = ItemVisible(i);
            creditName.Visible = creditValue.Visible = creditExpiry.Visible = preferences.ShowResetCredits;
            emptyLabel.Visible = VisibleItemCount() == 0;
            if (!dockCollapsed && preferences.CircularStyle)
            {
                status.Visible = false;
                refreshButton.Visible = false;
            }
        }

        private void DefaultLocation()
        {
            var work = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(work.Right - Width - S(24), work.Top + S(72));
        }
        private void KeepVisible()
        {
            Bounds = WindowPlacement.Fit(Bounds, ReadWorkAreas(), Cursor.Position);
        }
        private void SavePosition()
        {
            var position = dockCollapsed ? expandedBounds.Location : Location;
            preferences.X = position.X;
            preferences.Y = position.Y;
            preferences.Save(preferencePath);
        }
        private void BeginDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ExpandDock();
            dragging = true;
            try
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
            }
            finally
            {
                dragging = false;
                UpdateDockAnchor(false);
                SavePosition();
            }
        }

        private void UpdateWindowRegion()
        {
            ConfigureCircleWindow();
            if (preferences.CircularStyle) return;
            using (var path = new GraphicsPath())
            {
                if (preferences.CircularStyle && !dockCollapsed) path.AddEllipse(0, 0, Width, Height);
                else using (var rounded = Rounded(new Rectangle(0, 0, Width, Height), S(dockCollapsed ? 3 : 13)))
                    path.AddPath(rounded, false);
                var old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }
        private void SetAutoHide(bool enabled)
        {
            preferences.EdgeAutoHide = enabled;
            ExpandDock();
            if (enabled) UpdateDockAnchor(false);
            else { dockEdge = DockEdge.None; outsideSince = null; dockTimer.Stop(); }
        }
        private void UpdateDockAnchor(bool keepEdge)
        {
            if (dockCollapsed) return;
            var work = Screen.FromRectangle(Bounds).WorkingArea;
            if (!preferences.EdgeAutoHide) dockEdge = DockEdge.None;
            else if (!keepEdge || dockEdge == DockEdge.None)
                dockEdge = EdgeDocking.Detect(Bounds, work, S(14));
            adjustingDock = true;
            try
            {
                if (dockEdge != DockEdge.None) Bounds = EdgeDocking.Snap(Bounds, work, dockEdge);
                expandedBounds = Bounds;
            }
            finally { adjustingDock = false; }
            outsideSince = DateTime.UtcNow;
            if (dockEdge == DockEdge.None) dockTimer.Stop(); else dockTimer.Start();
            EnsureTopMost(false);
        }
        private void UpdateDockHover(Point cursor, bool menuOpen, bool buttonDown, DateTime now)
        {
            if (closing || dragging || dockEdge == DockEdge.None || !preferences.EdgeAutoHide) return;
            if (menuOpen || buttonDown) { outsideSince = null; return; }
            bool inside = Bounds.Contains(cursor);
            if (inside && preferences.CircularStyle && !dockCollapsed)
            {
                double x = cursor.X - Left - Width / 2.0;
                double y = cursor.Y - Top - Height / 2.0;
                double radius = Math.Max(1, Width / 2.0 - S(6));
                inside = x * x + y * y <= radius * radius;
            }
            if (inside)
            {
                outsideSince = null;
                if (dockCollapsed) ExpandDock();
                return;
            }
            if (dockCollapsed) return;
            if (!outsideSince.HasValue) outsideSince = now;
            if (now - outsideSince.Value >= TimeSpan.FromMilliseconds(650)) CollapseDock();
        }
        private void CollapseDock()
        {
            if (dockCollapsed || dockEdge == DockEdge.None || !preferences.EdgeAutoHide) return;
            expandedBounds = Bounds;
            var work = Screen.FromRectangle(expandedBounds).WorkingArea;
            dockCollapsed = true;
            adjustingDock = true;
            try
            {
                foreach (Control control in Controls) control.Visible = false;
                Bounds = EdgeDocking.Tab(expandedBounds, work, dockEdge, Math.Max(6, S(8)), S(64));
                UpdateWindowRegion();
            }
            finally { adjustingDock = false; }
            AccessibleDescription = "贴边已收起，鼠标移入展开";
            EnsureTopMost(true);
            Invalidate();
        }
        private void ExpandDock()
        {
            if (!dockCollapsed) return;
            adjustingDock = true;
            dockCollapsed = false;
            try
            {
                Bounds = EdgeDocking.Snap(expandedBounds, Screen.FromRectangle(expandedBounds).WorkingArea, dockEdge);
                expandedBounds = Bounds;
                UpdateControlVisibility();
                UpdateWindowRegion();
            }
            finally { adjustingDock = false; }
            outsideSince = null;
            UpdateDisplay();
            EnsureTopMost(true);
            Invalidate();
        }

        private void StartRefresh()
        {
            if (closing || Interlocked.CompareExchange(ref reading, 1, 0) != 0) return;
            CancellationToken cancellation = shutdown.Token;
            UpdateDisplay();
            refreshTask = Task.Run(delegate
            {
                UsageSnapshot data = null;
                string failure = null;
                try { data = readUsage(cancellation); }
                catch (OperationCanceledException) { Interlocked.Exchange(ref reading, 0); return; }
                catch (UsageException e) { failure = e.Message; }
                catch { failure = "读取失败，稍后自动重试"; }
                // Keep the refresh locked until the UI has committed the
                // result. Otherwise a second read can overtake this callback.
                if (!PostToUi(delegate
                {
                    Interlocked.Exchange(ref reading, 0);
                    if (closing) return;
                    if (data != null)
                    {
                        snapshot = data;
                    }
                    readError = failure;
                    UpdateDisplay();
                })) Interlocked.Exchange(ref reading, 0);
            });
        }

        private bool IsStale(DateTime now) { return readError != null || (snapshot != null && now - snapshot.RetrievedUtc > TimeSpan.FromMinutes(3)); }
        private string ResetText(QuotaWindow window, DateTime now)
        {
            string text = DisplayText.Countdown(window, now);
            return preferences.CircularStyle ? text.Replace("后重置", "重置") : text;
        }
        private bool DisplayNeedsUpdate(DateTime now)
        {
            return displayedStale != IsStale(now) || (snapshot != null &&
                ((preferences.ShowFiveHour && resets[0].Text != ResetText(snapshot.FiveHour, now)) ||
                 (preferences.ShowWeekly && resets[1].Text != ResetText(snapshot.Weekly, now))));
        }
        private void UpdateDisplay()
        {
            if (closing) return;
            DateTime now = DateTime.UtcNow;
            bool stale = displayedStale = IsStale(now);
            mode.Text = preferences.ShowRemaining ? "剩余额度" : "已使用额度";
            if (!preferences.ShowFiveHour && !preferences.ShowWeekly)
                mode.Text = preferences.ShowResetCredits ? "重置卡信息" : "全部已隐藏";
            if (preferences.CircularStyle)
            {
                if (stale) mode.Text = (preferences.ShowRemaining ? "剩余" : "已用") + " · 待更新";
                else if (snapshot == null) mode.Text = "正在读取额度";
            }
            creditValue.Text = snapshot != null && snapshot.ResetCreditsCount.HasValue
                ? (snapshot.ResetCreditsCount.Value > 99 ? "99+" : snapshot.ResetCreditsCount.Value.ToString()) + "张" : "—";
            creditExpiry.Text = DisplayText.CreditExpiry(snapshot);
            creditValue.ForeColor = stale ? muted : Color.FromArgb(244, 194, 112);
            string creditDetails = DisplayText.CreditDetails(snapshot);
            tip.SetToolTip(creditName, creditDetails);
            tip.SetToolTip(creditValue, creditDetails);
            tip.SetToolTip(creditExpiry, creditDetails);
            var windows = new[] { snapshot == null ? null : snapshot.FiveHour, snapshot == null ? null : snapshot.Weekly };
            for (int i = 0; i < 2; i++)
            {
                names[i].Text = new[] { "5小时", "每周" }[i] +
                    (preferences.CircularStyle ? preferences.ShowRemaining ? "剩余" : "已用" : "");
                string value = DisplayText.Percent(windows[i], preferences.ShowRemaining);
                string reset = ResetText(windows[i], now);
                if (values[i].Text != value) { values[i].Text = value; Invalidate(); }
                if (resets[i].Text != reset) resets[i].Text = reset;
                values[i].ForeColor = stale || windows[i] == null ? muted : Tone(i, windows[i].UsedPercent);
                string detail = windows[i] == null ? "本次未返回这项额度" :
                    String.Format("{0}：已用 {1}，剩余 {2}", names[i].Text,
                        DisplayText.Percent(windows[i], false), DisplayText.Percent(windows[i], true));
                if (windows[i] != null && windows[i].ResetsAt.HasValue)
                    detail += "\n重置时间：" + DisplayText.ResetUtc(windows[i].ResetsAt.Value).ToLocalTime().ToString("MM-dd HH:mm:ss");
                if (snapshot != null) detail += "\n数据时间：" + snapshot.RetrievedUtc.ToLocalTime().ToString("HH:mm:ss");
                tip.SetToolTip(values[i], detail);
                tip.SetToolTip(resets[i], detail);
            }
            if (Interlocked.CompareExchange(ref reading, 0, 0) != 0)
                status.Text = readError != null ? "正在重试…" : snapshot == null ? "正在读取本机 Codex 额度…" : "正在刷新…";
            else if (readError != null)
                status.Text = snapshot == null ? readError : "上次 " + snapshot.RetrievedUtc.ToLocalTime().ToString("HH:mm") + " · " + readError;
            else if (snapshot == null) status.Text = "等待额度数据";
            else status.Text = "每分钟更新 · " + snapshot.RetrievedUtc.ToLocalTime().ToString("HH:mm:ss") + (stale ? " · 数据待更新" : "");
            if (drawingWarning != null) status.Text += " · " + drawingWarning;
            status.ForeColor = readError == null ? muted : Color.FromArgb(236, 182, 103);
            tip.SetToolTip(status, status.Text + "\n右键打开菜单；双击立即刷新");
            tip.SetToolTip(heading, status.Text + "\n蓝环：5小时；紫环：每周\n双击刷新，右键选择显示项目");
            tip.SetToolTip(mode, status.Text + "\n重置卡仅展示数量和到期日期，不会自动使用");
            var summary = new List<string>();
            for (int i = 0; i < 2; i++) if (ItemVisible(i)) summary.Add(names[i].Text + " " + values[i].Text + " " + resets[i].Text);
            if (preferences.ShowResetCredits) summary.Add("重置卡 " + creditValue.Text + " " + creditExpiry.Text);
            if (summary.Count == 0) summary.Add(emptyLabel.Text);
            summary.Add(status.Text);
            AccessibleDescription = String.Join("；", summary);
            refreshButton.Enabled = Interlocked.CompareExchange(ref reading, 0, 0) == 0;
            RefreshCircleSurface(false);
        }

        private Color Tone(int row, double used)
        {
            if (used >= 95) return Color.FromArgb(249, 117, 124);
            if (used >= 80) return Color.FromArgb(243, 190, 104);
            return accents[row];
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (preferences != null && preferences.CircularStyle)
            {
                RefreshCircleSurface(false);
                return;
            }
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (dockCollapsed)
            {
                using (var border = new Pen(Color.FromArgb(85, 117, 146)))
                using (var outline = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), S(3)))
                    e.Graphics.DrawPath(border, outline);
                using (var marker = new Pen(accents[0], Math.Max(2, S(2))))
                {
                    marker.StartCap = marker.EndCap = LineCap.Round;
                    if (dockEdge == DockEdge.Left || dockEdge == DockEdge.Right)
                        e.Graphics.DrawLine(marker, Width / 2, Height / 3, Width / 2, Height * 2 / 3);
                    else e.Graphics.DrawLine(marker, Width / 3, Height / 2, Width * 2 / 3, Height / 2);
                }
                return;
            }
            using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), S(13)))
            using (var border = new Pen(Color.FromArgb(53, 63, 77))) e.Graphics.DrawPath(border, path);
            using (var divider = new Pen(Color.FromArgb(40, 48, 59)))
            {
                e.Graphics.DrawLine(divider, S(16), S(35), S(366), S(35));
                e.Graphics.DrawLine(divider, S(16), Height - S(28), S(366), Height - S(28));
            }
            for (int i = 0; i < 2; i++)
            {
                if (!ItemVisible(i)) continue;
                var window = snapshot == null ? null : i == 0 ? snapshot.FiveHour : snapshot.Weekly;
                var rectangle = new Rectangle(S(155), S(53 + VisibleRow(i) * 42), S(65), Math.Max(3, S(5)));
                using (var track = new SolidBrush(Color.FromArgb(46, 55, 68))) e.Graphics.FillRectangle(track, rectangle);
                if (window != null)
                {
                    double percent = preferences.ShowRemaining ? 100 - window.UsedPercent : window.UsedPercent;
                    int width = (int)Math.Round(rectangle.Width * percent / 100);
                    using (var fill = new SolidBrush(readError == null ? Tone(i, window.UsedPercent) : muted))
                        e.Graphics.FillRectangle(fill, rectangle.X, rectangle.Y, width, rectangle.Height);
                }
            }
        }
        private static GraphicsPath Rounded(Rectangle rectangle, int radius)
        {
            var path = new GraphicsPath();
            int diameter = Math.Max(2, radius * 2);
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("wtsapi32.dll", SetLastError = true)] internal static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
        [DllImport("wtsapi32.dll")] internal static extern bool WTSUnRegisterSessionNotification(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    }

    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            bool first;
            string identity = WindowsIdentity.GetCurrent().User.Value;
            using (var mutex = new Mutex(true, @"Local\CodexUsageFloat." + identity, out first))
            {
                if (!first)
                {
                    IntPtr window = Native.FindWindow(null, UsageForm.WindowTitle);
                    if (window != IntPtr.Zero)
                    {
                        uint owner;
                        Native.GetWindowThreadProcessId(window, out owner);
                        Native.AllowSetForegroundWindow(owner);
                        Native.ShowWindow(window, 9);
                        Native.PostMessage(window, UsageForm.RevealMessage, IntPtr.Zero, IntPtr.Zero);
                        Native.SetForegroundWindow(window);
                    }
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new UsageForm());
                mutex.ReleaseMutex();
            }
            return 0;
        }
    }
}
