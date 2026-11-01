using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IdmClose
{
    internal static class Tests
    {
        private const string Expired = "Internet Download Manager 的免费试用已经到期";
        private static int assertions;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--fixture")
            {
                Application.EnableVisualStyles();
                Application.Run(new Fixture(args[1]));
                return 0;
            }
            try
            {
                Application.EnableVisualStyles();
                RuleTests();
                StartupTests();
                SingletonTest();
                WindowTests();
                Console.WriteLine("PASS: " + assertions + " assertions");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        private static void Check(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception("FAIL: " + message);
            Console.WriteLine("PASS: " + message);
        }

        private static void RuleTests()
        {
            Check(Rules.Matches("IDMan", Expired), "screenshot heading matches");
            Check(Rules.Matches("idman", "Internet Download\r\nManager 的 免费 试用 已经 到期"), "whitespace and process case");
            Check(!Rules.Matches("Other", Expired), "same text in another process is ignored");
            Check(!Rules.Matches("IDMan", "Internet Download Manager 6.43"), "main window is ignored");
            Check(!Rules.Matches("IDMan", "Internet Download Manager 下载完成"), "download completion is ignored");
            Check(!Rules.Matches("IDMan", "免费试用已经到期"), "both text fragments are required");
            Check(!Rules.Matches("IDMan", null), "null text is ignored");
        }

        private static void StartupTests()
        {
            const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using (var key = Registry.CurrentUser.CreateSubKey(run))
            {
                object original = key.GetValue(Startup.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                RegistryValueKind kind = original == null ? RegistryValueKind.String : key.GetValueKind(Startup.Name);
                try
                {
                    Startup.Set(true);
                    Check(Startup.Enabled(), "startup enables current executable");
                    Startup.Set(false);
                    Check(!Startup.Enabled() && key.GetValue(Startup.Name) == null, "startup disables only own entry");
                    key.SetValue(Startup.Name, "\"F:\\different\\IDMClose.exe\"");
                    Startup.Set(false);
                    Check(key.GetValue(Startup.Name) != null, "different path is not deleted");
                }
                finally
                {
                    if (original == null) key.DeleteValue(Startup.Name, false);
                    else key.SetValue(Startup.Name, original, kind);
                }
                Check(object.Equals(key.GetValue(Startup.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), original), "original startup setting restored");
            }
        }

        private static void SingletonTest()
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\IDMClose.Tray", out first))
            {
                if (!first) { Console.WriteLine("SKIP: singleton test, tool is already running"); return; }
                try
                {
                    string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "IDMClose.exe");
                    using (var child = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }))
                        Check(child.WaitForExit(4000) && child.ExitCode == 0, "second instance exits without scanning");
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static void Pump(int ms)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); }
        }

        private static void Until(Func<bool> predicate, string description, int timeout = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!predicate() && clock.ElapsedMilliseconds < timeout) Pump(20);
            Check(predicate(), description);
        }

        private static void Invoke(object target, string method)
        {
            target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, null);
        }

        private static void WindowTests()
        {
            string folder = Path.Combine(Path.GetTempPath(), "IDMClose-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string fixtureExe = Path.Combine(folder, "IDMan.exe");
            string stateFile = Path.Combine(folder, "state.txt");
            File.Copy(Assembly.GetExecutingAssembly().Location, fixtureExe);
            Process fixture = null;
            try
            {
                fixture = Process.Start(new ProcessStartInfo(fixtureExe, "--fixture \"" + stateFile + "\"") { UseShellExecute = false });
                Until(() => File.Exists(stateFile), "fixture ready");
                IntPtr main = new IntPtr(long.Parse(ReadState(stateFile)[0]));
                uint fixturePid = (uint)fixture.Id;
                var scanner = new Scanner(fixturePid);
                Func<List<WindowInfo>> collect = () => scanner.Collect(false);
                Func<int> requests = () => int.Parse(ReadState(stateFile)[2]);
                Action<int> command = n =>
                {
                    int sequence = int.Parse(ReadState(stateFile)[3]);
                    Native.PostMessage(main, (uint)(0x8000 + n), IntPtr.Zero, IntPtr.Zero);
                    Until(() => int.Parse(ReadState(stateFile)[3]) > sequence, "fixture command " + n + " acknowledged");
                };

                command(1);
                var windows = collect();
                if (!windows.Any(w => w.Matches))
                {
                    Console.WriteLine("Fixture PID=" + fixturePid + " state=" + string.Join(",", ReadState(stateFile)));
                    foreach (var w in windows) Console.WriteLine("HWND=" + w.Handle + " class=" + w.ClassName + " text=" + w.Text);
                    foreach (var p in Process.GetProcessesByName("IDMan")) using (p) Console.WriteLine("IDMan process " + p.Id + " " + p.ProcessName);
                }
                var target = windows.Single(w => w.Matches);
                Check(windows.Any(w => w.Handle == main && !w.Matches), "scanner excludes fixture download window");
                Check(target.Text.Contains("免费试用已经到期"), "native child text is readable");
                var automation = new AutomationReader();
                Until(() => Rules.Matches("IDMan", automation.Read(target.Handle)), "UI Automation reads heading text");
                var errors = new List<string>();
                var closer = new Closer(errors.Add);
                closer.Request(target);
                closer.Request(target);
                Until(() => !Native.IsWindow(target.Handle), "matching dialog closes");
                closer.Confirm();
                Check(closer.Closed == 1 && requests() == 1, "confirmed count and duplicate suppression");
                Check(Native.IsWindowVisible(main), "normal download window survives");

                command(2);
                target = collect().Single(w => w.Matches);
                int before = requests();
                var resistant = new Closer(errors.Add);
                for (int i = 0; i < 10; i++) resistant.Request(target);
                Until(() => requests() == before + 1, "resistant dialog receives only one immediate request");
                Pump(2100);
                resistant.Confirm();
                Check(errors.Count == 1 && resistant.Closed == 0, "unresponsive close is reported without success count");
                Pump(3100);
                resistant.Request(target);
                Until(() => requests() == before + 2, "second attempt after backoff (count=" + requests() + ", baseline=" + before + ", same=" + Native.SameWindow(target) + ")");
                Pump(5100);
                resistant.Request(target);
                Until(() => requests() == before + 3, "third attempt after backoff (count=" + requests() + ", baseline=" + before + ", same=" + Native.SameWindow(target) + ")");
                Pump(5100);
                resistant.Request(target);
                Pump(150);
                Check(requests() == before + 3 && Native.IsWindowVisible(main), "retry limit preserves IDM process");

                command(3);
                Until(() => !Native.IsWindow(target.Handle), "fixture target destroyed");
                var vanished = new Closer(errors.Add);
                vanished.Request(target);
                vanished.Confirm();
                Check(vanished.Closed == 0, "window destroyed before request is ignored");

                command(6);
                var embedded = new IntPtr(long.Parse(ReadState(stateFile)[1]));
                Check(!Rules.Matches("IDMan", Native.ReadText(embedded)), "embedded HTML has no native heading text (real failure pattern)");
                var htmlReader = new HtmlReader();
                Until(() => Rules.Matches("IDMan", htmlReader.Read(embedded)), "HTML reader retrieves embedded heading");
                target = collect().Single(w => w.Handle == embedded);
                Check(target.Matches, "shared scanner matches untitled embedded HTML popup");
                closer.Request(target);
                Until(() => !Native.IsWindow(embedded), "embedded HTML popup closes through normal closer");

                // Tests restrict scanning to the fixture PID, never real IDM windows.
                using (var app = new TrayApp(fixturePid))
                {
                    Invoke(app, "TogglePause");
                    command(1);
                    var popup = new IntPtr(long.Parse(ReadState(stateFile)[1]));
                    Pump(1300);
                    Check(Native.IsWindowVisible(popup), "paused app leaves popup alone");
                    Invoke(app, "TogglePause");
                    Until(() => !Native.IsWindow(popup), "resume immediately scans existing popup");
                    var timer = (System.Windows.Forms.Timer)typeof(TrayApp).GetField("timer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(app);
                    timer.Stop();
                    command(1);
                    popup = new IntPtr(long.Parse(ReadState(stateFile)[1]));
                    Until(() => !Native.IsWindow(popup), "show event closes popup with fallback timer disabled");
                    command(4);
                    popup = new IntPtr(long.Parse(ReadState(stateFile)[1]));
                    Pump(400);
                    Check(Native.IsWindowVisible(popup), "non-matching dialog is preserved");
                    command(5);
                    Until(() => !Native.IsWindow(popup), "name-change event rechecks dialog with timer disabled");
                    Invoke(app, "ExitThreadCore");
                    command(1);
                    popup = new IntPtr(long.Parse(ReadState(stateFile)[1]));
                    Pump(1300);
                    Check(Native.IsWindowVisible(popup), "exit unhooks and stops scanning");
                }

                command(3);
                command(1);
                IntPtr existing = new IntPtr(long.Parse(ReadState(stateFile)[1]));
                using (var app = new TrayApp(fixturePid))
                    Until(() => !Native.IsWindow(existing), "startup scans already visible popup");
            }
            finally
            {
                if (fixture != null)
                {
                    if (!fixture.HasExited)
                    {
                        try { Native.PostMessage(new IntPtr(long.Parse(ReadState(stateFile)[0])), 0x8010, IntPtr.Zero, IntPtr.Zero); }
                        catch (Exception) { }
                        if (!fixture.WaitForExit(3000)) fixture.Kill();
                    }
                    fixture.WaitForExit();
                    fixture.Dispose();
                }
                foreach (string file in new[] { fixtureExe, stateFile }) if (File.Exists(file)) File.Delete(file);
                Directory.Delete(folder);
            }
        }

        private static string[] ReadState(string path)
        {
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    var lines = File.ReadAllLines(path);
                    if (lines.Length == 4) return lines;
                }
                catch (IOException) { }
                Thread.Sleep(10);
            }
            throw new Exception("Fixture state is unavailable.");
        }

        private sealed class QuietForm : Form
        {
            internal QuietForm()
            {
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-20000, -20000);
                Size = new Size(200, 80);
            }
            protected override bool ShowWithoutActivation { get { return true; } }
        }

        private sealed class Fixture : Form
        {
            private readonly string path;
            private Form popup;
            private bool resistant;
            private int requests;
            private int sequence;
            internal Fixture(string statePath)
            {
                path = statePath;
                Text = "Internet Download Manager 6.43";
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(-20000, -20000);
                Shown += (s, e) => Save();
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            private void Save()
            {
                File.WriteAllLines(path, new[] { Handle.ToInt64().ToString(),
                    popup == null || popup.IsDisposed ? "0" : popup.Handle.ToInt64().ToString(), requests.ToString(), sequence.ToString() });
            }
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x8006)
                {
                    popup = new QuietForm { Text = "" };
                    var browser = new WebBrowser { Dock = DockStyle.Fill, ScriptErrorsSuppressed = true };
                    popup.Controls.Add(browser);
                    bool ready = false;
                    browser.DocumentCompleted += (s, e) =>
                    {
                        if (ready || browser.Document == null || browser.Document.Body == null
                            || !(browser.Document.Body.InnerText ?? "").Contains("免费试用已经到期")) return;
                        ready = true;
                        sequence++;
                        Save();
                    };
                    popup.Show();
                    browser.DocumentText = "<html><head><meta charset='utf-8'></head><body><h1>" + Expired + "</h1></body></html>";
                    return;
                }
                if (message.Msg == 0x8001 || message.Msg == 0x8002 || message.Msg == 0x8004)
                {
                    resistant = false;
                    if (popup != null && !popup.IsDisposed) popup.Close();
                    resistant = message.Msg == 0x8002;
                    popup = new QuietForm { Text = "Internet Download Manager" };
                    popup.Controls.Add(new Label { Text = message.Msg == 0x8004 ? "普通提示" : "的免费试用已经到期", Dock = DockStyle.Fill });
                    popup.FormClosing += (s, e) => { requests++; e.Cancel = resistant; Save(); };
                    popup.Show();
                    sequence++;
                    Save();
                    return;
                }
                if (message.Msg == 0x8003)
                {
                    resistant = false;
                    if (popup != null && !popup.IsDisposed) popup.Close();
                    sequence++;
                    Save();
                    return;
                }
                if (message.Msg == 0x8005)
                {
                    popup.Text = Expired;
                    sequence++;
                    Save();
                    return;
                }
                if (message.Msg == 0x8010)
                {
                    resistant = false;
                    if (popup != null) popup.Dispose();
                    Close();
                    return;
                }
                base.WndProc(ref message);
            }
        }
    }
}
