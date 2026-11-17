// PowerWatch.exe - единый менеджер выключения по графику отключений света.
// Окно с тремя input-полями (канал, черга, запас минут - значения по умолчанию),
// чистая остановка всех открытых программ, окно отсчёта с кнопкой "Отменить",
// интеграция со стеком llama (watchdog -> warmup -> llama-server) и Docker/MySQL.
// Сборка:
//   csc /codepage:65001 /target:winexe /r:System.dll /r:System.Core.dll ^
//       /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
//       /r:System.Web.Extensions.dll /r:System.Management.dll PowerWatch.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace PowerWatch
{
    static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        public const uint WM_CLOSE = 0x0010;
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    // ---------------------------------------------------------------- config --
    public class Config
    {
        public string ChannelUrl { get; set; }
        public string Queue { get; set; }
        public int LeadMinutes { get; set; }
        public int GraceSeconds { get; set; }
        public int IntervalMinutes { get; set; }
        public int CleanupBudget { get; set; }

        public Config()
        {
            ChannelUrl = "https://t.me/s/SvitloKropyvnytskyiMisto";
            Queue = "Черга 4.1";
            LeadMinutes = 15;
            GraceSeconds = 120;      // окно отмены после уборки
            IntervalMinutes = 10;    // опрос канала
            CleanupBudget = 90;      // секунд под чистую остановку программ
        }

        public void Normalize()
        {
            if (string.IsNullOrEmpty(ChannelUrl)) ChannelUrl = "https://t.me/s/SvitloKropyvnytskyiMisto";
            if (string.IsNullOrEmpty(Queue)) Queue = "Черга 4.1";
            if (LeadMinutes < 0) LeadMinutes = 15;
            if (LeadMinutes > 240) LeadMinutes = 240;
            if (GraceSeconds < 30) GraceSeconds = 30;
            if (IntervalMinutes < 1) IntervalMinutes = 10;
            if (CleanupBudget < 15) CleanupBudget = 90;
        }
    }

    public class PState
    {
        public string ArmedFor { get; set; }      // ISO время выключения
        public string OutageStart { get; set; }   // ISO начало отключения света
        public bool CleanupDone { get; set; }
        public string LastFetch { get; set; }
        public string LastSchedule { get; set; }
        public string NextFire { get; set; }      // ISO расчётное время выключения
        public string NextOutage { get; set; }    // ISO ближайшее отключение света
        public PState()
        {
            ArmedFor = null; OutageStart = null; CleanupDone = false;
            LastFetch = null; LastSchedule = null; NextFire = null; NextOutage = null;
        }
    }

    static class Store
    {
        public static readonly string Dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string ConfigPath = Path.Combine(Dir, "powerwatch-config.json");
        public static readonly string StatePath = Path.Combine(Dir, "powerwatch-state.json");
        public static readonly string LogPath = Path.Combine(Dir, "powerwatch.log");
        public static readonly string PausePath = Path.Combine(Dir, "power-watch.pause");

        static readonly object ioLock = new object();
        static readonly JavaScriptSerializer js = new JavaScriptSerializer();

        public static void Log(string msg)
        {
            lock (ioLock)
            {
                try
                {
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine,
                        Encoding.UTF8);
                }
                catch { }
            }
        }

        public static Config LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var c = js.Deserialize<Config>(File.ReadAllText(ConfigPath, Encoding.UTF8));
                    if (c != null) { c.Normalize(); return c; }
                }
            }
            catch (Exception e) { Log("config load failed: " + e.Message); }
            var d = new Config(); d.Normalize(); return d;
        }

        public static void SaveConfig(Config c)
        {
            c.Normalize();
            lock (ioLock) { File.WriteAllText(ConfigPath, js.Serialize(c), Encoding.UTF8); }
        }

        public static PState LoadState()
        {
            try
            {
                if (File.Exists(StatePath))
                {
                    var s = js.Deserialize<PState>(File.ReadAllText(StatePath, Encoding.UTF8));
                    if (s != null) return s;
                }
            }
            catch (Exception e) { Log("state load failed: " + e.Message); }
            return new PState();
        }

        public static void SaveState(PState s)
        {
            lock (ioLock) { File.WriteAllText(StatePath, js.Serialize(s), Encoding.UTF8); }
        }
    }

    // ------------------------------------------------------------- расписание --
    static class Schedule
    {
        public static string FetchHtml(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static string HtmlToText(string html)
        {
            var t = Regex.Replace(html, @"(?i)<br\s*/?>", "\n");
            t = Regex.Replace(t, @"(?s)<[^>]+>", "");
            return WebUtility.HtmlDecode(t);
        }

        // 'dd.MM.yyyy' -> окна @(startH,startM,endH,endM)
        public static Dictionary<string, List<int[]>> Parse(string html, string queue)
        {
            var result = new Dictionary<string, List<int[]>>();
            var queueEsc = Regex.Escape(queue);
            var msgRx = new Regex(@"(?s)<div class=""tgme_widget_message_text[^""]*""[^>]*>(.*?)</div>");
            var dateRx = new Regex(@"\b\d{2}\.\d{2}\.\d{4}\b");
            // дата РАСПИСАНИЯ - после "Укренерго": заголовок может быть вчерашним
            var schedDateRx = new Regex(@"Укренерго""?\s+(\d{2}\.\d{2}\.\d{4})");
            var lineRx = new Regex(queueEsc + @":\s*([^\r\n<]*)");
            var winRx = new Regex(@"(\d{1,2})(?::(\d{2}))?\s*[-–]\s*(\d{1,2})(?::(\d{2}))?");

            foreach (Match m in msgRx.Matches(html))
            {
                var text = HtmlToText(m.Groups[1].Value);
                if (!text.Contains(queue)) continue;

                var dateM = schedDateRx.Match(text);
                if (!dateM.Success) dateM = dateRx.Match(text);
                if (!dateM.Success) continue;
                // schedDateRx хранит дату в группе 1, у dateRx групп нет - берём весь матч
                var dateStr = dateM.Groups.Count > 1 && dateM.Groups[1].Success
                    ? dateM.Groups[1].Value : dateM.Value;

                var lineM = lineRx.Match(text);
                if (!lineM.Success) continue;

                // без переносов строк строка захватывает соседние очереди - обрезаем по "Черга"
                var lineStr = lineM.Groups[1].Value;
                int cut = lineStr.IndexOf("Черга");
                if (cut >= 0) lineStr = lineStr.Substring(0, cut);

                var windows = new List<int[]>();
                foreach (Match w in winRx.Matches(lineStr))
                {
                    int sh, sm, eh, em;
                    if (!int.TryParse(w.Groups[1].Value, out sh)) continue;
                    sm = w.Groups[2].Success && w.Groups[2].Value.Length > 0 ? int.Parse(w.Groups[2].Value) : 0;
                    if (!int.TryParse(w.Groups[3].Value, out eh)) continue;
                    em = w.Groups[4].Success && w.Groups[4].Value.Length > 0 ? int.Parse(w.Groups[4].Value) : 0;
                    if (sh > 23 || sm > 59 || em > 59 || eh > 24) continue;
                    if (eh == 0 && em == 0 && sh == 0 && sm == 0) continue;
                    windows.Add(new int[] { sh, sm, eh, em });
                }

                // последнее сообщение на дату побеждает (превью хронологическое)
                result[dateStr] = windows;
            }
            return result;
        }

        public static DateTime? NextStart(Dictionary<string, List<int[]>> sched, DateTime now)
        {
            DateTime? best = null;
            foreach (var kv in sched)
            {
                DateTime d;
                if (!DateTime.TryParseExact(kv.Key, "dd.MM.yyyy", null,
                    System.Globalization.DateTimeStyles.None, out d)) continue;
                foreach (var w in kv.Value)
                {
                    var start = d.AddHours(w[0]).AddMinutes(w[1]);
                    if (start > now && (best == null || start < best.Value)) best = start;
                }
            }
            return best;
        }
    }

    // ----------------------------------------------------- остановка всего ---
    static class Cleaner
    {
        // Процессы, которые НЕ трогаем (критичные/наши/системные)
        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csrss", "winlogon", "smss", "lsass", "services", "spoolsv", "svchost",
            "System", "Registry", "Memory Compression", "dwm", "fontdrvhost",
            "sihost", "ShellExperienceHost", "StartMenuExperienceHost",
            "SearchHost", "SearchUI", "explorer", "taskhostw", "conhost",
            "PowerWatch", "powershell", "pwsh", "cmd", "WindowsTerminal",
            // opencode/node/Code не трогаем: сессия агента переживёт отмену выключения;
            // при реальном выключении их закроет сама Windows
            "opencode", "node", "Code"
        };

        // WM_CLOSE всем видимым окнам пользовательских приложений; затем ждём и добиваем.
        public static void CloseAllApps(int waitSeconds, Func<bool> cancelled)
        {
            Store.Log("cleanup: закриваю відкриті програми (WM_CLOSE)");
            var victims = new List<Process>();
            int myPid = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == myPid) continue;
                    if (Protected.Contains(p.ProcessName)) continue;
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    if (p.SessionId != Process.GetCurrentProcess().SessionId) continue;  // только наша сессия
                    Native.PostMessage(p.MainWindowHandle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    victims.Add(p);
                    Store.Log("  -> WM_CLOSE: " + p.ProcessName + " (pid " + p.Id + ")");
                }
                catch { }
            }

            var deadline = DateTime.Now.AddSeconds(waitSeconds);
            while (DateTime.Now < deadline)
            {
                if (cancelled != null && cancelled()) { Store.Log("cleanup: скасування - припиняю добивання"); return; }
                Thread.Sleep(1000);
                victims.RemoveAll(p => { try { return p.HasExited; } catch { return true; } });
                if (victims.Count == 0) break;
            }

            foreach (var p in victims)
            {
                try
                {
                    if (!p.HasExited) { p.Kill(); Store.Log("  !! добито: " + p.ProcessName + " (pid " + p.Id + ")"); }
                }
                catch { }
            }
        }

        // Штатная остановка сервисов данных: llama-стек, Kobold, Docker/MySQL.
        public static void StopServices(Func<bool> cancelled)
        {
            // 1. сначала вахтолог, иначе он поднимет llama обратно
            KillByCommandLine("llama-" + "watchdog", "watchdog");
            KillByCommandLine("llama-warmup", "warmup");
            KillProcessByName("llama-server");
            Store.Log("cleanup: llama-стек зупинено");

            if (cancelled != null && cancelled()) return;

            // 2. KoboldCpp
            KillProcessByName("koboldcpp");
            Store.Log("cleanup: KoboldCpp зупинено");

            if (cancelled != null && cancelled()) return;

            // 3. Docker: чистая остановка MySQL через compose down
            string docker = @"C:\Program Files\Docker\Docker\resources\bin\docker.exe";
            if (File.Exists(docker))
            {
                try
                {
                    var psi = new ProcessStartInfo(docker,
                        "compose -f \"E:\\projects\\pyprog\\accon\\docker\\docker-compose.yml\" down");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    var pr = Process.Start(psi);
                    if (pr != null && !pr.WaitForExit(60000)) { try { pr.Kill(); } catch { } }
                    Store.Log("cleanup: docker compose down done");
                }
                catch (Exception e) { Store.Log("cleanup: docker compose error " + e.Message); }

                try
                {
                    var psi2 = new ProcessStartInfo(docker, "shutdown");
                    psi2.UseShellExecute = false; psi2.CreateNoWindow = true;
                    var pr2 = Process.Start(psi2);
                    if (pr2 != null && !pr2.WaitForExit(30000)) { try { pr2.Kill(); } catch { } }
                    Store.Log("cleanup: docker shutdown issued");
                }
                catch (Exception e) { Store.Log("cleanup: docker shutdown error " + e.Message); }
            }
            else Store.Log("cleanup: docker не знайдено - пропускаю");

            Thread.Sleep(5000);
            KillProcessByName("Docker Desktop");
            KillProcessByName("com.docker.backend");
            KillProcessByName("com.docker.build");
            KillProcessByName("vmmemWSL");
            KillProcessByName("vmmem");
            Store.Log("cleanup: залишки Docker добито");
        }

        static void KillProcessByName(string name)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { if (!p.HasExited) { p.Kill(); Store.Log("  killed " + name + " pid=" + p.Id); } }
                catch { }
            }
        }

        // Ищем powershell-процесс с указанным фрагментом в командной строке и убиваем.
        // Фрагмент собирается вызывающим через конкатенацию, чтобы этот shell не убил сам себя.
        static void KillByCommandLine(string needle, string label)
        {
            try
            {
                var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='powershell.exe'");
                foreach (ManagementObject o in searcher.Get())
                {
                    var cl = o["CommandLine"] as string;
                    if (cl != null && cl.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int pid = Convert.ToInt32(o["ProcessId"]);
                        if (pid == Process.GetCurrentProcess().Id) continue;
                        try { Process.GetProcessById(pid).Kill(); Store.Log("  killed " + label + " pid=" + pid); }
                        catch { }
                    }
                }
            }
            catch (Exception e) { Store.Log("KillByCommandLine(" + label + ") error: " + e.Message); }
        }

        // Интеграция с llama: после ОТМЕНЫ выключения поднимаем стек обратно.
        public static void RestartLlamaStack()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
            string sw = Path.Combine(dir, "llama-switch.ps1");
            if (!File.Exists(sw)) { Store.Log("restore: llama-switch.ps1 не знайдено"); return; }
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + sw + "\" -Model qwen25coder");
                psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
                Store.Log("restore: llama-switch -Model qwen25coder запущено (сам підніме прогрів і watchdog)");
            }
            catch (Exception e) { Store.Log("restore: error " + e.Message); }
        }
    }

    // ------------------------------------------------------- армирование ------
    static class Arm
    {
        public static bool Disarm(string reason)
        {
            var st = Store.LoadState();
            if (st.ArmedFor == null) return false;
            try
            {
                var pr = Process.Start(new ProcessStartInfo("shutdown.exe", "/a")
                { UseShellExecute = false, CreateNoWindow = true });
                if (pr != null) pr.WaitForExit(10000);
                Store.Log("скасовую вимкнення (" + reason + "); shutdown /a -> exit=" + pr.ExitCode);
            }
            catch (Exception e) { Store.Log("shutdown /a error: " + e.Message); }
            st.ArmedFor = null; st.OutageStart = null; st.CleanupDone = false;
            Store.SaveState(st);
            return true;
        }

        // Возвращает секунды до выключения (для окна отсчёта).
        public static int Fire(DateTime fireAt, DateTime outageStart)
        {
            var st = Store.LoadState();
            var key = fireAt.ToString("o");
            if (st.ArmedFor == key) return 0;   // уже вооружено

            if (st.ArmedFor != null)
            {
                try
                {
                    var pr = Process.Start(new ProcessStartInfo("shutdown.exe", "/a")
                    { UseShellExecute = false, CreateNoWindow = true });
                    if (pr != null) pr.WaitForExit(10000);
                }
                catch { }
                Store.Log("розклад змінився - старе вимкнення скасовано");
            }

            // бюджет на уборку + grace на отмену
            int secs = Store.LoadConfig().CleanupBudget + Store.LoadConfig().GraceSeconds;
            int untilFire = (int)(fireAt - DateTime.Now).TotalSeconds;
            if (untilFire > secs) secs = untilFire;

            try
            {
                var pr = Process.Start(new ProcessStartInfo("shutdown.exe",
                    "/s /t " + secs + " /c \"PowerWatch: outage " +
                    outageStart.ToString("HH:mm") + " - cancel with: shutdown /a\"")
                { UseShellExecute = false, CreateNoWindow = true });
                if (pr != null) pr.WaitForExit(10000);
                Store.Log("ВИМКЕННЯ ЗАПЛАНОВАНО через " + secs + " с (світло зникає о " +
                    outageStart.ToString("HH:mm") + "); скасування: shutdown /a");
            }
            catch (Exception e) { Store.Log("shutdown arm error: " + e.Message); }

            st.ArmedFor = key;
            st.OutageStart = outageStart.ToString("o");
            Store.SaveState(st);
            return secs;
        }
    }

    // --------------------------------------------------- окно отсчёта --------
    public class CountdownForm : Form
    {
        Label big, outageLbl, statusLbl;
        Button cancelBtn;
        System.Windows.Forms.Timer tick;
        DateTime until;
        public volatile bool Cancelled;
        public readonly ManualResetEvent ClosedEvent = new ManualResetEvent(false);

        public CountdownForm(int seconds, DateTime outageStart)
        {
            Text = "PowerWatch - вимкнення";
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(460, 210);
            Font = new Font("Segoe UI", 9F);

            big = new Label();
            big.Font = new Font("Segoe UI", 20F, FontStyle.Bold);
            big.TextAlign = ContentAlignment.MiddleCenter;
            big.Dock = DockStyle.Top;
            big.Height = 60;

            outageLbl = new Label();
            outageLbl.TextAlign = ContentAlignment.MiddleCenter;
            outageLbl.Dock = DockStyle.Top;
            outageLbl.Height = 22;
            outageLbl.Text = "Вимкнення світла о " + outageStart.ToString("HH:mm");

            statusLbl = new Label();
            statusLbl.TextAlign = ContentAlignment.MiddleCenter;
            statusLbl.Dock = DockStyle.Top;
            statusLbl.Height = 40;
            statusLbl.Text = "Зупиняю програми...";

            cancelBtn = new Button();
            cancelBtn.Text = "СКАСУВАТИ ВИМКНЕННЯ";
            cancelBtn.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            cancelBtn.Dock = DockStyle.Bottom;
            cancelBtn.Height = 56;
            cancelBtn.BackColor = Color.FromArgb(220, 60, 60);
            cancelBtn.ForeColor = Color.White;
            cancelBtn.FlatStyle = FlatStyle.Flat;
            cancelBtn.Click += OnCancelClick;

            Controls.Add(cancelBtn);
            Controls.Add(statusLbl);
            Controls.Add(outageLbl);
            Controls.Add(big);

            until = DateTime.Now.AddSeconds(seconds);
            tick = new System.Windows.Forms.Timer();
            tick.Interval = 500;
            tick.Tick += OnTick;
            tick.Start();
            OnTick(null, null);

            // закрытие крестиком = отмена (безопасное направление: лучше поработать
            // дольше, чем дать питанию умереть при включённом ноутбуке)
            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!Cancelled && DialogResult != DialogResult.OK)
                {
                    e.Cancel = true;
                    OnCancelClick(s, null);
                }
            };
        }

        void OnTick(object s, EventArgs e)
        {
            var left = until - DateTime.Now;
            if (left.TotalSeconds <= 0)
            {
                tick.Stop();
                big.Text = "Вимкнення...";
                cancelBtn.Enabled = false;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }
            int total = (int)Math.Ceiling(left.TotalSeconds);
            big.Text = string.Format("{0:00}:{1:00}", total / 60, total % 60);
        }

        void OnCancelClick(object s, EventArgs e)
        {
            if (Cancelled) return;
            Cancelled = true;
            tick.Stop();
            try
            {
                var pr = Process.Start(new ProcessStartInfo("shutdown.exe", "/a")
                { UseShellExecute = false, CreateNoWindow = true });
                if (pr != null) pr.WaitForExit(10000);
                Store.Log("СКАСУВАННЯ користувачем: shutdown /a виконано");
            }
            catch (Exception ex) { Store.Log("СКАСУВАННЯ: shutdown /a error " + ex.Message); }
            statusLbl.Text = "Вимкнення скасовано";
            DialogResult = DialogResult.Cancel;
            Close();
        }

        public void SetStatus(string s)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action<string>(SetStatus), s); } catch { } return; }
            statusLbl.Text = s;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            ClosedEvent.Set();
        }
    }

    // ------------------------------------------------------- главное окно ----
    public class MainForm : Form
    {
        TextBox urlBox, queueBox, logBox;
        NumericUpDown leadUp;
        Button saveBtn, testBtn, pauseBtn;
        Label status1, status2;
        NotifyIcon tray;
        bool exiting;
        Thread worker;
        volatile bool wake;

        public MainForm()
        {
            Text = "PowerWatch - менеджер вимкнення";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            ClientSize = new Size(560, 470);
            Font = new Font("Segoe UI", 9F);

            int y = 12;
            Controls.Add(MkLabel("Посилання на канал:", 12, y, 520));
            y += 20;
            urlBox = new TextBox { Left = 12, Top = y, Width = 520 };
            Controls.Add(urlBox);
            y += 30;

            Controls.Add(MkLabel("Черга (черга відключень):", 12, y, 260));
            Controls.Add(MkLabel("Вимикати за N хв до відключення:", 290, y, 260));
            y += 20;
            queueBox = new TextBox { Left = 12, Top = y, Width = 260 };
            Controls.Add(queueBox);
            leadUp = new NumericUpDown
            {
                Left = 290, Top = y, Width = 80,
                Minimum = 0, Maximum = 240, Value = 15
            };
            Controls.Add(leadUp);
            y += 34;

            saveBtn = new Button { Left = 12, Top = y, Width = 120, Height = 28, Text = "Зберегти" };
            testBtn = new Button { Left = 140, Top = y, Width = 150, Height = 28, Text = "Перевірити (тест)" };
            pauseBtn = new Button { Left = 298, Top = y, Width = 150, Height = 28, Text = "Пауза" };
            saveBtn.Click += OnSave;
            testBtn.Click += OnTest;
            pauseBtn.Click += OnPause;
            Controls.AddRange(new Control[] { saveBtn, testBtn, pauseBtn });
            y += 38;

            status1 = MkLabel("", 12, y, 520); status1.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            Controls.Add(status1);
            y += 20;
            status2 = MkLabel("", 12, y, 520);
            Controls.Add(status2);
            y += 26;

            Controls.Add(MkLabel("Журнал:", 12, y, 520));
            y += 18;
            logBox = new TextBox
            {
                Left = 12, Top = y, Width = 520, Height = 240,
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 8.5F)
            };
            Controls.Add(logBox);

            // свернуть в трей, а не закрывать (слежка должна жить)
            tray = new NotifyIcon();
            tray.Icon = SystemIcons.Application;
            tray.Text = "PowerWatch";
            tray.Visible = true;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Відкрити", null, delegate { ShowFromTray(); });
            menu.Items.Add("Вихід", null, delegate { exiting = true; tray.Visible = false; Application.Exit(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowFromTray(); };

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (!exiting) { e.Cancel = true; Hide(); tray.ShowBalloonTip(3000, "PowerWatch",
                    "Спостереження продовжує працювати в треї", ToolTipIcon.Info); }
            };

            Load += OnLoad;
        }

        static Label MkLabel(string text, int x, int y, int w)
        {
            return new Label { Text = text, Left = x, Top = y, Width = w, Height = 18, AutoEllipsis = true };
        }

        void ShowFromTray() { Show(); WindowState = FormWindowState.Normal; Native.SetForegroundWindow(Handle); }

        void OnLoad(object s, EventArgs e)
        {
            var cfg = Store.LoadConfig();
            urlBox.Text = cfg.ChannelUrl;
            queueBox.Text = cfg.Queue;
            leadUp.Value = Math.Min(leadUp.Maximum, Math.Max(leadUp.Minimum, cfg.LeadMinutes));
            RefreshLog();
            UpdatePauseBtn();

            worker = new Thread(WorkerLoop);
            worker.IsBackground = true;
            worker.Start();

            // статус и журнал обновляем сами, раз в 10 секунд
            var uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 10000;
            uiTimer.Tick += delegate { RefreshLog(); };
            uiTimer.Start();
        }

        void OnSave(object s, EventArgs e)
        {
            var cfg = Store.LoadConfig();
            cfg.ChannelUrl = urlBox.Text.Trim();
            cfg.Queue = queueBox.Text.Trim();
            cfg.LeadMinutes = (int)leadUp.Value;
            cfg.Normalize();
            Store.SaveConfig(cfg);
            Store.Log("конфіг збережено: url=" + cfg.ChannelUrl + " queue=" + cfg.Queue + " lead=" + cfg.LeadMinutes);
            wake = true;
            RefreshLog();
        }

        void OnTest(object s, EventArgs e)
        {
            testBtn.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg;
                try
                {
                    var cfg = Store.LoadConfig();
                    var html = Schedule.FetchHtml(cfg.ChannelUrl);
                    var sched = Schedule.Parse(html, cfg.Queue);
                    if (sched.Count == 0) msg = "TEST: розклад для черги не знайдено";
                    else
                    {
                        var next = Schedule.NextStart(sched, DateTime.Now);
                        if (next == null) msg = "TEST: найближчих відключень немає";
                        else msg = string.Format("TEST: найближче відключення {0:dd.MM HH:mm}, вимкнення о {1:HH:mm}",
                            next.Value, next.Value.AddMinutes(-cfg.LeadMinutes));
                    }
                }
                catch (Exception ex) { msg = "TEST ERROR: " + ex.Message; }
                Store.Log(msg);
                try { BeginInvoke(new MethodInvoker(delegate { testBtn.Enabled = true; RefreshLog(); })); } catch { }
            });
        }

        void OnPause(object s, EventArgs e)
        {
            if (File.Exists(Store.PausePath))
            {
                try { File.Delete(Store.PausePath); } catch { }
                Store.Log("паузу знято");
            }
            else
            {
                Arm.Disarm("паузу ввімкнено");
                try { File.WriteAllText(Store.PausePath, DateTime.Now.ToString("o")); } catch { }
                Store.Log("ПАУЗА: вимкнення не планую (знімається видаленням файлу power-watch.pause)");
            }
            wake = true;
            UpdatePauseBtn();
            RefreshLog();
        }

        void UpdatePauseBtn()
        {
            pauseBtn.Text = File.Exists(Store.PausePath) ? "Продовжити" : "Пауза";
        }

        void RefreshLog()
        {
            try
            {
                if (!File.Exists(Store.LogPath)) return;
                var lines = File.ReadAllLines(Store.LogPath, Encoding.UTF8);
                int from = Math.Max(0, lines.Length - 14);
                var sb = new StringBuilder();
                for (int i = from; i < lines.Length; i++) sb.AppendLine(lines[i]);
                logBox.Text = sb.ToString();
                logBox.SelectionStart = logBox.Text.Length;
                logBox.ScrollToCaret();
            }
            catch { }
            var st = Store.LoadState();
            var cfg = Store.LoadConfig();
            status1.Text = st.ArmedFor != null
                ? "Вимкнення ЗАПЛАНОВАНО: " + DateTime.Parse(st.ArmedFor).ToString("dd.MM HH:mm:ss")
                : (st.NextFire != null
                    ? "Найближче вимкнення: " + DateTime.Parse(st.NextFire).ToString("dd.MM HH:mm:ss") +
                      " (світло зникає о " + DateTime.Parse(st.NextOutage).ToString("HH:mm") + ")"
                    : "Вимкнення не заплановано: розкладу немає");
            status2.Text = "Перевірка кожні " + cfg.IntervalMinutes + " хв, запас " + cfg.LeadMinutes +
                " хв, остання перевірка: " + (st.LastFetch != null ? DateTime.Parse(st.LastFetch).ToString("HH:mm:ss") : "-");
        }

        // ------------------------------------------------------- рабочий цикл --
        void WorkerLoop()
        {
            Store.Log("=== PowerWatch запущено: lead=" + Store.LoadConfig().LeadMinutes +
                "min interval=" + Store.LoadConfig().IntervalMinutes + "min ===");
            while (!exiting)
            {
                DateTime? fire = null;
                try
                {
                    if (File.Exists(Store.PausePath))
                    {
                        Arm.Disarm("файл паузи присутній");
                        SleepCfg();
                        continue;
                    }

                    var cfg = Store.LoadConfig();
                    var html = Schedule.FetchHtml(cfg.ChannelUrl);
                    var sched = Schedule.Parse(html, cfg.Queue);

                    var st = Store.LoadState();
                    st.LastFetch = DateTime.Now.ToString("o");
                    st.LastSchedule = string.Join(",", new List<string>(sched.Keys).ToArray());
                    Store.SaveState(st);

                    var next = Schedule.NextStart(sched, DateTime.Now);
                    if (next == null)
                    {
                        Store.Log("найближчих відключень не бачу (розклад не опубліковано або відключень немає)");
                        Arm.Disarm("відключень немає");
                        var stn = Store.LoadState();
                        stn.NextFire = null; stn.NextOutage = null;
                        Store.SaveState(stn);
                        SleepCfg();
                        continue;
                    }

                    fire = next.Value.AddMinutes(-cfg.LeadMinutes);
                    double minsToOutage = Math.Round((next.Value - DateTime.Now).TotalMinutes, 1);

                    var st3 = Store.LoadState();
                    st3.NextFire = fire.Value.ToString("o");
                    st3.NextOutage = next.Value.ToString("o");
                    Store.SaveState(st3);

                    if (DateTime.Now >= fire.Value)
                    {
                        Store.Log("до вимкнення світла лишилось " + minsToOutage + " хв - час вимикатися");
                        RunShutdownSequence(fire.Value, next.Value);
                    }
                    else
                    {
                        // график сдвинули - перепривязываем
                        var st2 = Store.LoadState();
                        if (st2.ArmedFor != null)
                        {
                            var armedDt = DateTime.Parse(st2.ArmedFor);
                            if (Math.Abs((armedDt - fire.Value).TotalSeconds) > 60)
                            {
                                Arm.Disarm("розклад змінився");
                                Store.Log("новий розклад: відключення " + next.Value.ToString("HH:mm") +
                                    ", вимкнення " + fire.Value.ToString("HH:mm"));
                            }
                        }
                        Store.Log("ок: відключення " + next.Value.ToString("dd.MM HH:mm") +
                            " (через " + minsToOutage + " хв), вимкнення о " + fire.Value.ToString("HH:mm"));
                    }
                }
                catch (Exception ex)
                {
                    Store.Log("ПОМИЛКА перевірки: " + ex.Message + " - повторю пізніше");
                    fire = null;
                }

                // спим до следующего цикла, будимся ровно к моменту выключения
                int sleepSec = Store.LoadConfig().IntervalMinutes * 60;
                if (fire.HasValue && fire.Value > DateTime.Now)
                {
                    int toFire = (int)Math.Ceiling((fire.Value - DateTime.Now).TotalSeconds);
                    if (toFire < sleepSec) sleepSec = toFire;
                }
                if (sleepSec < 15) sleepSec = 15;
                Sleep(sleepSec);
            }
        }

        void SleepCfg() { Sleep(Store.LoadConfig().IntervalMinutes * 60); }

        void Sleep(int sec)
        {
            int waited = 0;
            while (waited < sec && !exiting)
            {
                if (wake) { wake = false; return; }
                Thread.Sleep(1000);
                waited++;
            }
        }

        // ---------------------------------------------------- сценарий выкл ----
        void RunShutdownSequence(DateTime fireAt, DateTime outageStart)
        {
            // 1. вооружаем выключение сразу (даже если наш процесс умрёт - Windows выключит)
            int secs = Arm.Fire(fireAt, outageStart);
            if (secs <= 0) secs = Store.LoadConfig().CleanupBudget + Store.LoadConfig().GraceSeconds;

            // 2. окно отсчёта с кнопкой отмены (на UI-потоке)
            CountdownForm cd = null;
            try
            {
                Invoke(new MethodInvoker(delegate
                {
                    cd = new CountdownForm(secs, outageStart);
                    cd.Show(this);
                }));
            }
            catch { }

            // 3. чистая остановка: сервисы данных, затем все открытые программы
            try
            {
                if (cd != null) cd.SetStatus("Зупиняю llama/Kobold/Docker...");
                Cleaner.StopServices(delegate { return cd != null && cd.Cancelled; });

                if (cd == null || !cd.Cancelled)
                {
                    if (cd != null) cd.SetStatus("Закриваю відкриті програми...");
                    Cleaner.CloseAllApps(30, delegate { return cd != null && cd.Cancelled; });
                }

                if (cd != null && !cd.Cancelled) cd.SetStatus("Готово. Чекаю на вимкнення...");
                Store.Log("прибирання завершено");
            }
            catch (Exception ex) { Store.Log("прибирання: помилка " + ex.Message); }

            // 4. ждём закрытия окна (отмена или истечение отсчёта)
            if (cd != null)
            {
                cd.ClosedEvent.WaitOne(Store.LoadConfig().CleanupBudget + Store.LoadConfig().GraceSeconds + 60000);
                if (cd.Cancelled)
                {
                    Store.Log("вимкнення скасовано - відновлюю llama-стек");
                    Cleaner.RestartLlamaStack();
                    try { BeginInvoke(new MethodInvoker(RefreshLog)); } catch { }
                    return;
                }
            }
            // дошли до конца - Windows выключает компьютер по armed shutdown
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            exiting = true;
            tray.Visible = false;
            base.OnFormClosed(e);
        }
    }

    // ---------------------------------------------------------- точка входа --
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // тест без UI: PowerWatch.exe -test
            if (args.Length > 0 && args[0] == "-test")
            {
                try
                {
                    var cfg = Store.LoadConfig();
                    var html = Schedule.FetchHtml(cfg.ChannelUrl);
                    var sched = Schedule.Parse(html, cfg.Queue);
                    foreach (var kv in sched)
                    {
                        var parts = new List<string>();
                        foreach (var w in kv.Value) parts.Add(string.Format("{0:D2}:{1:D2}-{2:D2}:{3:D2}", w[0], w[1], w[2], w[3]));
                        Store.Log("TEST  " + kv.Key + ": " + (parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "немає відключень"));
                    }
                    var next = Schedule.NextStart(sched, DateTime.Now);
                    if (next != null)
                        Store.Log(string.Format("TEST  найближче {0:dd.MM HH:mm}, вимкнення {1:HH:mm:ss}",
                            next.Value, next.Value.AddMinutes(-cfg.LeadMinutes)));
                    else Store.Log("TEST  найближчих відключень немає");
                }
                catch (Exception e) { Store.Log("TEST ERROR: " + e.Message); Environment.Exit(1); }
                Environment.Exit(0);
            }

            // тест окна отсчёта БЕЗ вооружения shutdown: PowerWatch.exe -testui
            if (args.Length > 0 && args[0] == "-testui")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Store.Log("TESTUI: відкриваю вікно зворотного відліку на 45 с (shutdown НЕ озброєно)");
                Application.Run(new CountdownForm(45, DateTime.Now.AddMinutes(15)));
                Store.Log("TESTUI: вікно зворотного відліку закрито");
                Environment.Exit(0);
            }

            // один экземпляр
            bool fresh;
            using (var mtx = new Mutex(true, "Global\\PowerWatchSingle", out fresh))
            {
                if (!fresh) { Environment.Exit(0); }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
