using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IdmClose
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 1 && args[0] == "--inspect")
            {
                try
                {
                    string path = Store.WriteInspection();
                    Process.Start("notepad.exe", "\"" + path + "\"");
                    return 0;
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "IDM Close 诊断失败"); return 1; }
            }
            if (args.Length != 0) return 2;
            bool first;
            using (var mutex = new Mutex(true, @"Local\IDMClose.Tray", out first))
            {
                if (!first) return 0;
                try { using (var app = new TrayApp()) Application.Run(app); }
                catch (Exception ex)
                {
                    Store.Log("启动失败：" + ex);
                    MessageBox.Show("启动失败：" + ex.Message, "IDM Close");
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }
    }

    internal static class Rules
    {
        internal static bool Matches(string processName, string text)
        {
            if (!string.Equals(processName, "IDMan", StringComparison.OrdinalIgnoreCase)) return false;
            string compact = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
            return compact.IndexOf("InternetDownloadManager", StringComparison.OrdinalIgnoreCase) >= 0
                && compact.Contains("免费试用已经到期");
        }
    }

    internal sealed class WindowInfo
    {
        internal IntPtr Handle;
        internal uint Pid;
        internal uint Thread;
        internal string ClassName;
        internal string Text;
        internal bool Matches;
    }

    internal static class Native
    {
        internal delegate bool EnumProc(IntPtr hwnd, IntPtr param);
        internal delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr hwnd, EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr wparam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        internal static extern IntPtr SendHtmlMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
        [DllImport("oleacc.dll")]
        internal static extern int ObjectFromLresult(IntPtr result, ref Guid iid, IntPtr wparam, [MarshalAs(UnmanagedType.Interface)] out object document);

        internal static string ClassName(IntPtr hwnd)
        {
            var value = new StringBuilder(256);
            GetClassName(hwnd, value, value.Capacity);
            return value.ToString();
        }

        internal static string Caption(IntPtr hwnd)
        {
            var value = new StringBuilder(2048);
            GetWindowText(hwnd, value, value.Capacity);
            return value.ToString();
        }

        internal static string ReadText(IntPtr hwnd)
        {
            var text = new StringBuilder(Caption(hwnd));
            var clock = Stopwatch.StartNew();
            int count = 0;
            EnumChildWindows(hwnd, delegate(IntPtr child, IntPtr unused)
            {
                if (++count > 128 || clock.ElapsedMilliseconds > 180 || text.Length > 16384) return false;
                // Do not collect user input such as URLs, passwords or serial numbers.
                if (ClassName(child).IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                var value = new StringBuilder(1024);
                UIntPtr result;
                if (SendMessageTimeout(child, 0x000D, new UIntPtr((uint)value.Capacity), value, 0x0002 | 0x0020, 25, out result) != IntPtr.Zero)
                    text.AppendLine().Append(value);
                return true;
            }, IntPtr.Zero);
            return text.ToString();
        }

        internal static bool SameWindow(WindowInfo info)
        {
            uint pid;
            return IsWindow(info.Handle) && GetWindowThreadProcessId(info.Handle, out pid) == info.Thread
                && pid == info.Pid && GetAncestor(info.Handle, 2) == info.Handle
                && ClassName(info.Handle) == info.ClassName;
        }
    }

    internal sealed class HtmlReader
    {
        private Thread worker;

        internal string Read(IntPtr root)
        {
            // shortcut: one outstanding COM read; restart the tool if a provider never returns.
            if (worker != null && worker.IsAlive) return "";
            IntPtr browser = IntPtr.Zero;
            int count = 0;
            Native.EnumChildWindows(root, delegate(IntPtr child, IntPtr unused)
            {
                if (++count > 128) return false;
                if (Native.ClassName(child) != "Internet Explorer_Server") return true;
                browser = child;
                return false;
            }, IntPtr.Zero);
            if (browser == IntPtr.Zero) return "";
            string text = "";
            worker = new Thread(() => { text = ReadDocument(browser); }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
            // Discard late results rather than applying stale text to a reused HWND.
            return worker.Join(700) ? text : "";
        }

        private static string ReadDocument(IntPtr browser)
        {
            object document = null, body = null;
            try
            {
                IntPtr result;
                uint message = Native.RegisterWindowMessage("WM_HTML_GETOBJECT");
                if (message == 0 || Native.SendHtmlMessage(browser, message, IntPtr.Zero, IntPtr.Zero, 2, 300, out result) == IntPtr.Zero
                    || result == IntPtr.Zero) return "";
                var iid = new Guid("332C4425-26CB-11D0-B483-00C04FD90119");
                if (Native.ObjectFromLresult(result, ref iid, IntPtr.Zero, out document) != 0 || document == null) return "";
                body = document.GetType().InvokeMember("body", BindingFlags.GetProperty, null, document, null);
                if (body == null) return "";
                string text = body.GetType().InvokeMember("innerText", BindingFlags.GetProperty, null, body, null) as string;
                return text == null ? "" : text.Substring(0, Math.Min(text.Length, 16384));
            }
            catch (Exception) { return ""; }
            finally
            {
                if (body != null && Marshal.IsComObject(body)) Marshal.ReleaseComObject(body);
                if (document != null && Marshal.IsComObject(document)) Marshal.ReleaseComObject(document);
            }
        }
    }

    internal sealed class AutomationReader
    {
        private Task<string> pending;

        internal string Read(IntPtr hwnd)
        {
            // shortcut: one outstanding UIA read; a hung provider disables fallback until it returns.
            if (pending != null && !pending.IsCompleted) return "";
            // Discard late results: HWND values can be reused after a window closes.
            pending = Task.Factory.StartNew(() => ReadNames(hwnd));
            if (!pending.Wait(250)) return "";
            string text = pending.Result;
            pending = null;
            return text;
        }

        private static string ReadNames(IntPtr hwnd)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null) return "";
                var queue = new Queue<AutomationElement>();
                var result = new StringBuilder();
                queue.Enqueue(root);
                int count = 0;
                while (queue.Count > 0 && count++ < 128 && result.Length < 16384)
                {
                    var element = queue.Dequeue();
                    if (element.Current.ControlType == ControlType.Edit) continue;
                    result.AppendLine(element.Current.Name);
                    var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                    while (child != null && queue.Count < 128)
                    {
                        queue.Enqueue(child);
                        child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                    }
                }
                return result.ToString();
            }
            catch (Exception) { return ""; }
        }
    }

    internal sealed class Scanner
    {
        private readonly uint onlyPid;
        private readonly HtmlReader html = new HtmlReader();
        private readonly AutomationReader automation = new AutomationReader();
        private readonly Dictionary<IntPtr, DateTime> lastAutomation = new Dictionary<IntPtr, DateTime>();
        internal Scanner(uint restrictPid = 0) { onlyPid = restrictPid; }

        internal List<WindowInfo> Collect(bool includeHidden)
        {
            var pids = new HashSet<uint>();
            foreach (var process in Process.GetProcessesByName("IDMan"))
                using (process) { if (onlyPid == 0 || onlyPid == (uint)process.Id) pids.Add((uint)process.Id); }
            var result = new List<WindowInfo>();
            var seen = new HashSet<IntPtr>();
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                uint pid;
                uint thread = Native.GetWindowThreadProcessId(hwnd, out pid);
                if (!pids.Contains(pid) || (!includeHidden && !Native.IsWindowVisible(hwnd))) return true;
                seen.Add(hwnd);
                var info = new WindowInfo { Handle = hwnd, Pid = pid, Thread = thread,
                    ClassName = Native.ClassName(hwnd), Text = Native.ReadText(hwnd) };
                if (!Rules.Matches("IDMan", info.Text)) info.Text += "\n" + html.Read(hwnd);
                if (!Rules.Matches("IDMan", info.Text))
                {
                    DateTime last;
                    if (!lastAutomation.TryGetValue(hwnd, out last) || (DateTime.UtcNow - last).TotalSeconds >= 5)
                    {
                        lastAutomation[hwnd] = DateTime.UtcNow;
                        info.Text += "\n" + automation.Read(hwnd);
                    }
                }
                info.Matches = Rules.Matches("IDMan", info.Text);
                result.Add(info);
                return true;
            }, IntPtr.Zero);
            foreach (var hwnd in lastAutomation.Keys.Where(h => !seen.Contains(h)).ToArray()) lastAutomation.Remove(hwnd);
            return result;
        }
    }

    internal sealed class CloseAttempt
    {
        internal WindowInfo Window;
        internal int Attempts;
        internal Stopwatch SinceSent;
        internal bool WasSent;
        internal bool Warned;
    }

    internal sealed class Closer
    {
        private readonly Dictionary<IntPtr, CloseAttempt> pending = new Dictionary<IntPtr, CloseAttempt>();
        private readonly Action<string> error;
        internal int Closed { get; private set; }
        internal Closer(Action<string> onError) { error = onError; }

        internal void Confirm()
        {
            foreach (var pair in pending.ToArray())
            {
                var item = pair.Value;
                bool same = Native.SameWindow(item.Window);
                if (!same || !Native.IsWindowVisible(pair.Key))
                {
                    if (item.WasSent) Closed++;
                    pending.Remove(pair.Key);
                }
                else if (item.WasSent && !item.Warned && item.SinceSent.Elapsed.TotalSeconds >= 2)
                {
                    item.Warned = true;
                    error("弹窗未响应关闭请求；将有限重试，不结束 IDM 进程。");
                }
            }
        }

        internal void Request(WindowInfo info)
        {
            if (!info.Matches || !Native.SameWindow(info) || !Native.IsWindowVisible(info.Handle)) return;
            CloseAttempt item;
            if (!pending.TryGetValue(info.Handle, out item))
            {
                item = new CloseAttempt { Window = info };
                pending.Add(info.Handle, item);
            }
            if (item.Attempts >= 3 || (item.SinceSent != null && item.SinceSent.Elapsed.TotalSeconds < 5)) return;
            item.Attempts++;
            item.SinceSent = Stopwatch.StartNew();
            if (Native.PostMessage(info.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero)) item.WasSent = true;
            else
            {
                int code = Marshal.GetLastWin32Error();
                error(code == 5 ? "权限不足：IDM 可能以管理员身份运行，请让两者使用相同权限。"
                    : "发送关闭请求失败，Windows 错误码：" + code);
            }
        }
    }

    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string Name = "IDMClose";
        internal static string Command { get { return "\"" + Application.ExecutablePath + "\""; } }
        internal static bool Enabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && string.Equals(key.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        internal static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(Name, Command, RegistryValueKind.String);
                else if (string.Equals(key.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(Name, false);
            }
        }
    }

    internal static class Store
    {
        internal static string DirectoryPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IDMClose"); } }
        internal static string LogPath { get { return Path.Combine(DirectoryPath, "errors.log"); } }
        private static readonly object gate = new object();
        internal static void Log(string message)
        {
            try
            {
                lock (gate)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 262144)
                    {
                        string old = LogPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogPath, old);
                    }
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception) { /* Logging must not stop the tray tool. */ }
        }
        internal static string WriteInspection()
        {
            var report = new StringBuilder("IDM Close 只读诊断\r\n" + DateTime.Now.ToString("s") + "\r\n");
            var windows = new Scanner().Collect(true);
            foreach (var info in windows)
            {
                report.AppendFormat("\r\nHWND=0x{0:X} PID={1} Class={2} Visible={3} Match={4}\r\n", info.Handle.ToInt64(), info.Pid,
                    info.ClassName, Native.IsWindowVisible(info.Handle), info.Matches);
                report.AppendLine(info.Text);
            }
            if (windows.Count == 0) report.AppendLine("未发现 IDM 窗口。");
            Directory.CreateDirectory(DirectoryPath);
            string path = Path.Combine(DirectoryPath, "diagnostics.txt");
            File.WriteAllText(path, report.ToString(), Encoding.UTF8);
            return path;
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Control dispatch = new Control();
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem status = new ToolStripMenuItem { Enabled = false };
        private readonly ToolStripMenuItem pause = new ToolStripMenuItem("暂停拦截");
        private readonly ToolStripMenuItem startup = new ToolStripMenuItem("开机启动");
        private readonly ToolStripMenuItem lastError = new ToolStripMenuItem("最近错误：无") { Enabled = false };
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly Scanner scanner;
        private readonly uint onlyPid;
        private readonly Closer closer;
        private readonly Native.WinEventProc callback;
        private readonly List<IntPtr> hooks = new List<IntPtr>();
        private bool paused, scanning, scanAgain, stopped;
        private int generation;
        private string previousError;

        internal TrayApp(uint restrictPid = 0)
        {
            onlyPid = restrictPid;
            scanner = new Scanner(restrictPid);
            var handle = dispatch.Handle;
            closer = new Closer(ReportError);
            menu.Font = SystemFonts.MenuFont;
            menu.ShowImageMargin = false;
            menu.Items.Add(status);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(pause);
            menu.Items.Add(startup);
            menu.Items.Add("只读诊断…", null, (s, e) => Inspect());
            menu.Items.Add("查看错误日志", null, (s, e) => OpenLog());
            menu.Items.Add(lastError);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, (s, e) => ExitThread());
            tray = new NotifyIcon { Icon = SystemIcons.Shield, ContextMenuStrip = menu, Visible = true };
            pause.Click += (s, e) => TogglePause();
            startup.Click += (s, e) => ToggleStartup();
            menu.Opening += (s, e) => RefreshStartup();
            RefreshStartup();
            callback = OnWindowEvent;
            foreach (uint evt in new uint[] { 0x8002, 0x800C })
            {
                var hook = Native.SetWinEventHook(evt, evt, IntPtr.Zero, callback, 0, 0, 0x0002);
                if (hook == IntPtr.Zero) ReportError("窗口事件监听失败，将使用每秒扫描。错误码：" + Marshal.GetLastWin32Error());
                else hooks.Add(hook);
            }
            timer.Tick += (s, e) => { closer.Confirm(); UpdateStatus(); Scan(); };
            timer.Start();
            UpdateStatus();
            Scan();
        }

        private void OnWindowEvent(IntPtr hook, uint evt, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
        {
            if (stopped || paused || hwnd == IntPtr.Zero || objectId != 0 || childId != 0) return;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (onlyPid != 0 && onlyPid != pid) return;
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                    if (!string.Equals(process.ProcessName, "IDMan", StringComparison.OrdinalIgnoreCase)) return;
                Scan();
            }
            catch (ArgumentException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }

        private void Scan()
        {
            if (paused || stopped) return;
            if (scanning) { scanAgain = true; return; }
            scanning = true;
            int version = generation;
            Task.Factory.StartNew(() =>
            {
                List<WindowInfo> found = null;
                Exception failure = null;
                try { found = scanner.Collect(false); }
                catch (Exception ex) { failure = ex; }
                try
                {
                    dispatch.BeginInvoke((Action)(() =>
                    {
                        scanning = false;
                        if (stopped) return;
                        if (!paused && version == generation)
                        {
                            if (failure != null) ReportError("扫描失败：" + failure.Message);
                            else
                            {
                                closer.Confirm();
                                foreach (var info in found) closer.Request(info);
                            }
                        }
                        UpdateStatus();
                        if (scanAgain) { scanAgain = false; Scan(); }
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        private void TogglePause()
        {
            paused = !paused;
            generation++;
            pause.Text = paused ? "恢复拦截" : "暂停拦截";
            UpdateStatus();
            if (!paused) Scan();
        }

        private void RefreshStartup()
        {
            try { startup.Checked = Startup.Enabled(); }
            catch (Exception ex) { ReportError("读取开机启动失败：" + ex.Message); }
        }

        private void ToggleStartup()
        {
            try { Startup.Set(!startup.Checked); startup.Checked = Startup.Enabled(); }
            catch (Exception ex)
            {
                ReportError("更改开机启动失败：" + ex.Message);
                MessageBox.Show("更改失败，原状态已保留。\n" + ex.Message, "IDM Close", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Inspect()
        {
            try { Process.Start(Application.ExecutablePath, "--inspect"); }
            catch (Exception ex) { ReportError("无法启动诊断：" + ex.Message); }
        }

        private void OpenLog()
        {
            try
            {
                if (!File.Exists(Store.LogPath)) { MessageBox.Show("暂无错误记录。", "IDM Close"); return; }
                Process.Start("notepad.exe", "\"" + Store.LogPath + "\"");
            }
            catch (Exception ex) { ReportError("无法打开日志：" + ex.Message); }
        }

        private void ReportError(string message)
        {
            if (previousError != message) Store.Log(message);
            previousError = message;
            lastError.Text = "最近错误：" + (message.Length > 28 ? message.Substring(0, 28) + "…" : message);
            lastError.ToolTipText = message;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            string state = paused ? "已暂停" : "正在拦截";
            status.Text = state + " · 本次已关闭 " + closer.Closed + " 个弹窗";
            if (tray != null) tray.Text = "IDM Close · " + state + "\n已关闭 " + closer.Closed + " 个" + (previousError == null ? "" : " · 有错误，右键查看");
        }

        protected override void ExitThreadCore()
        {
            Stop();
            base.ExitThreadCore();
        }

        private void Stop()
        {
            if (stopped) return;
            stopped = true;
            timer.Stop();
            foreach (var hook in hooks) Native.UnhookWinEvent(hook);
            tray.Visible = false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();
                timer.Dispose();
                tray.Dispose();
                menu.Dispose();
                dispatch.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
