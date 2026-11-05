using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Path = System.IO.Path;
using Sound = System.Media.SoundPlayer;

namespace DshPet
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            ServicePointManager.Expect100Continue = false;
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var pet = new PetWindow(args);
            app.Run(pet);
        }
    }

    internal sealed class BalanceService
    {
        private static readonly HttpClient Http = CreateHttp();
        private readonly object gate = new object();
        private readonly Action<string> log;
        private bool busy;
        private DateTime lastFetch = DateTime.MinValue;
        private string title = "账户余额";
        private string value = "查询中…";
        private string detail = "";

        public BalanceService(Action<string> log)
        {
            this.log = log;
        }

        private static HttpClient CreateHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(9) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) DeepSeek/1.0 Chrome/140.0.0.0 Safari/537.36");
            return client;
        }

        public string Title { get { return title; } }
        public string Value { get { return value; } }
        public string Detail { get { return detail; } }
        public event Action Updated;

        public bool Stale
        {
            get { return DateTime.Now - lastFetch > TimeSpan.FromSeconds(25); }
        }

        public void Refresh()
        {
            lock (gate)
            {
                if (busy) return;
                busy = true;
            }
            Task.Run(() =>
            {
                string t, v, d;
                try
                {
                    Fetch(out t, out v, out d);
                }
                catch (Exception ex)
                {
                    log("balance error " + ex.GetType().Name + ": " + ex.Message);
                    t = "账户余额";
                    v = "读取失败";
                    d = ex is UnauthorizedAccessException ? "请重新登录 DSH" : "网络或登录状态异常";
                }
                lock (gate)
                {
                    title = t;
                    value = v;
                    detail = d;
                    lastFetch = DateTime.Now;
                    busy = false;
                }
                var handler = Updated;
                if (handler != null) handler();
            });
        }

        private void Fetch(out string title, out string value, out string detail)
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string cred = Path.Combine(home, ".dsh", ".credentials.yaml");
            if (!File.Exists(cred))
            {
                title = "账户余额";
                value = "未登录";
                detail = "未找到 DSH 凭据";
                return;
            }
            string text = File.ReadAllText(cred);
            int idx = text.IndexOf("deepseek-account-platform/default:", StringComparison.Ordinal);
            if (idx < 0)
            {
                title = "账户余额";
                value = "未登录";
                detail = "DSH 账户未连接";
                return;
            }
            string section = text.Substring(idx);
            Match tm = Regex.Match(section, @"token:\s*(\S+)");
            Match im = Regex.Match(section, @"issuer:\s*(\S+)");
            if (!tm.Success || !im.Success)
            {
                title = "账户余额";
                value = "未登录";
                detail = "DSH 凭据不完整";
                return;
            }
            string issuer = im.Groups[1].Value.TrimEnd('/');
            string url = issuer + "/api/v0/users/get_user_summary";


            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-dsh-auth-token", tm.Groups[1].Value);
            req.Headers.TryAddWithoutValidation("x-client-bundle-id", "");
            req.Headers.TryAddWithoutValidation("x-client-platform", "desktop-win");
            req.Headers.TryAddWithoutValidation("x-client-version", "1.0.0");
            req.Headers.TryAddWithoutValidation("x-client-locale", "zh_CN");
            int tz = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalSeconds;
            req.Headers.TryAddWithoutValidation("x-client-timezone-offset", tz.ToString(CultureInfo.InvariantCulture));
            req.Headers.TryAddWithoutValidation("accept", "application/json");

            HttpResponseMessage resp;
            try
            {
                resp = Http.SendAsync(req, HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                throw new IOException("network");
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedAccessException("expired");
            }
            if (!resp.IsSuccessStatusCode)
            {
                throw new IOException("http " + (int)resp.StatusCode);
            }
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var ser = new JavaScriptSerializer();
            var root = ser.Deserialize<Dictionary<string, object>>(body);
            var data = root != null && root.ContainsKey("data") ? root["data"] as Dictionary<string, object> : null;
            var biz = data != null && data.ContainsKey("biz_data") ? data["biz_data"] as Dictionary<string, object> : null;
            object walletsObj = null;
            if (biz != null) biz.TryGetValue("normal_wallets", out walletsObj);
            var wallets = walletsObj as System.Collections.IEnumerable;
            if (wallets == null || walletsObj is string)
            {
                title = "账户余额";
                value = "读取失败";
                detail = "返回数据异常";
                return;
            }
            var parts = new List<string>();
            foreach (object item in wallets)
            {
                var w = item as Dictionary<string, object>;
                if (w == null) continue;
                string cur = Convert.ToString(w.ContainsKey("currency") ? w["currency"] : "", CultureInfo.InvariantCulture);
                string bal = Convert.ToString(w.ContainsKey("balance") ? w["balance"] : "", CultureInfo.InvariantCulture);
                decimal amount;
                if (!decimal.TryParse(bal, NumberStyles.Float, CultureInfo.InvariantCulture, out amount)) continue;
                string symbol = cur == "CNY" ? "¥" : cur == "USD" ? "$" : "";
                parts.Add(symbol + amount.ToString("0.00", CultureInfo.InvariantCulture));
            }
            title = "账户余额";
            value = parts.Count > 0 ? string.Join("  ", parts.ToArray()) : "0.00";
            detail = "";
        }
    }

    internal sealed class PetWindow : Window
    {
        private const string AppName = "DSH 桌宠";
        private readonly string dir;
        private readonly string stopFlag;

        private readonly BalanceService balance;
        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly DateTime launched = DateTime.Now;

        private Grid petGroup;
        private Image petImage;
        private Ellipse shadow;
        private ScaleTransform petScale;
        private RotateTransform petRot;
        private TranslateTransform petTrans;
        private BitmapSource[] frames;
        private int frameIndex = -1;

        private BubbleWindow bubble;
        private Forms.NotifyIcon tray;
        private Sound clang;
        private bool soundPlayed;

        private bool pressed;
        private bool dragging;
        private POINT pressCursor;
        private double pressLeft;
        private double pressTop;
        private double totalMove;

        private bool reacting;
        private DateTime reactStart;
        private bool bubbleShownForReaction;
        private DateTime bubbleShownAt;
        private bool bubbleClosing;
        private bool bubbleVisible;

        private readonly bool demo;
        private DateTime lastDemo = DateTime.MinValue;
        private DateTime lastRaise = DateTime.MinValue;
        private bool menuOpen;

        public PetWindow(string[] args)
        {
            dir = AppDomain.CurrentDomain.BaseDirectory;
            stopFlag = Path.Combine(dir, "stopped.flag");
            demo = Array.IndexOf(args, "--demo") >= 0;

            Title = AppName;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            Width = 210;
            Height = 245;
            WindowStartupLocation = WindowStartupLocation.Manual;

            balance = new BalanceService(Log);
            BuildUi();
            balance.Updated += OnBalanceUpdated;

            timer.Interval = TimeSpan.FromMilliseconds(33);
            timer.Tick += OnTick;
            Loaded += OnLoaded;
            Closing += OnClosing;
            Closed += delegate { Application.Current.Shutdown(); };
            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
            MouseRightButtonUp += OnRightClick;

            bubble = new BubbleWindow();
            try
            {
                clang = new Sound(Path.Combine(dir, "clang.wav"));
                clang.Load();
            }
            catch (Exception) { clang = null; }
            balance.Refresh();
            Log("start pid=" + Process.GetCurrentProcess().Id);
        }

        private void BuildUi()
        {
            var root = new Canvas();
            Content = root;

            shadow = new Ellipse();
            shadow.Width = 140;
            shadow.Height = 14;
            shadow.Fill = new RadialGradientBrush(Color.FromArgb(90, 20, 20, 30), Color.FromArgb(0, 20, 20, 30));
            shadow.Effect = new BlurEffect { Radius = 8 };
            shadow.Opacity = 0.5;
            Canvas.SetLeft(shadow, 35);
            Canvas.SetTop(shadow, 226);
            root.Children.Add(shadow);

            petGroup = new Grid();
            petGroup.Width = 190;
            petGroup.Height = 231;
            petGroup.RenderTransformOrigin = new Point(0.5, 0.97);
            petScale = new ScaleTransform(1, 1);
            petRot = new RotateTransform(0);
            petTrans = new TranslateTransform(0, 0);
            var petGroupTransform = new TransformGroup();
            petGroupTransform.Children.Add(petScale);
            petGroupTransform.Children.Add(petRot);
            petGroupTransform.Children.Add(petTrans);
            petGroup.RenderTransform = petGroupTransform;
            Canvas.SetLeft(petGroup, 10);
            Canvas.SetTop(petGroup, 3);
            root.Children.Add(petGroup);

            petImage = new Image();
            petImage.Width = 190;
            petImage.Height = 231;
            petImage.Stretch = Stretch.Fill;
            frames = LoadFrames();
            petImage.Source = frames[0];
            frameIndex = 0;
            petGroup.Children.Add(petImage);

            LoadIcon();
        }

        private BitmapSource[] LoadFrames()
        {
            string sheetPath = Path.Combine(dir, "frames_sheet_n.png");
            if (!File.Exists(sheetPath)) throw new FileNotFoundException("frames_sheet_n.png");
            var sheet = new BitmapImage();
            sheet.BeginInit();
            sheet.CacheOption = BitmapCacheOption.OnLoad;
            sheet.UriSource = new Uri(sheetPath, UriKind.Absolute);
            sheet.EndInit();
            sheet.Freeze();
            const int fw = 240, fh = 292, cols = 8;
            int count = 75;
            var list = new List<BitmapSource>();
            for (int i = 0; i < count; i++)
            {
                var crop = new CroppedBitmap(sheet, new Int32Rect((i % cols) * fw, (i / cols) * fh, fw, fh));
                crop.Freeze();
                list.Add(crop);
            }
            Log("frames " + list.Count);
            return list.ToArray();
        }

        private void LoadIcon()
        {
            try
            {
                string ico = Path.Combine(dir, "pet.ico");
                var icon = new Forms.NotifyIcon();
                icon.Icon = new System.Drawing.Icon(ico);
                icon.Text = AppName;
                icon.Visible = true;

                var menu = new Forms.ContextMenuStrip();
                var show = new Forms.ToolStripMenuItem("显示 / 隐藏");
                show.Click += delegate { ToggleVisible(); };
                var quit = new Forms.ToolStripMenuItem("拜拜鲸鱼娘");
                quit.Click += delegate { Quit(); };
                menu.Items.Add(show);
                menu.Items.Add(new Forms.ToolStripSeparator());
                menu.Items.Add(quit);
                icon.ContextMenuStrip = menu;
                icon.DoubleClick += delegate { ToggleVisible(); };
                tray = icon;
            }
            catch (Exception)
            {
                tray = null;
            }
        }

        private void Log(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(dir, "pet.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + "\r\n");
            }
            catch (Exception) { }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Log("loaded");
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs a)
            {
                Log("unhandled " + a.ExceptionObject);
            };
            Application.Current.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs a)
            {
                Log("dispatcher " + a.Exception);
            };
            AppliedNoActivate();
            LoadPosition();
            RaiseTopmost();
            timer.Start();
            if (demo)
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                t.Tick += delegate
                {
                    t.Stop();
                    React();
                };
                t.Start();
            }
        }

        private void AppliedNoActivate()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }

        private void OnTick(object sender, EventArgs e)
        {
            double total = (DateTime.Now - launched).TotalSeconds;
            if (reacting)
            {
                UpdateReaction((DateTime.Now - reactStart).TotalSeconds);
            }
            else
            {
                UpdateIdle(total);
            }
            UpdateBubble(DateTime.Now);
            if (!menuOpen && (DateTime.Now - lastRaise).TotalMilliseconds > 400)
            {
                lastRaise = DateTime.Now;
                RaiseTopmost();
                if (bubbleVisible) PlaceBubbleBehindPet();
            }
            if (demo && !reacting && (DateTime.Now - lastDemo).TotalSeconds > 6 && total > 3)
            {
                lastDemo = DateTime.Now;
                React();
            }
        }

        private void UpdateIdle(double total)
        {
            double ph = total * 2 * Math.PI / 3.8;
            petTrans.Y = Math.Sin(ph) * 0.8;
            petRot.Angle = Math.Sin(ph + 0.4) * 0.15;
            petScale.ScaleX = 1;
            petScale.ScaleY = 1 + 0.002 * Math.Sin(ph);
            shadow.Opacity = 0.45 + 0.05 * Math.Sin(ph);
            if (frameIndex != 0)
            {
                frameIndex = 0;
                petImage.Source = frames[0];
            }
        }

        private void UpdateReaction(double t)
        {
            petTrans.Y = 0;
            petRot.Angle = 0;
            petScale.ScaleX = 1;
            petScale.ScaleY = 1;
            shadow.Opacity = 0.5;

            int idx = (int)(t * 15.0);
            if (idx >= frames.Length) idx = frames.Length - 1;
            if (idx != frameIndex)
            {
                frameIndex = idx;
                petImage.Source = frames[idx];
            }

            if (t >= 0.30 && !soundPlayed)
            {
                soundPlayed = true;
                if (clang != null) { try { clang.Play(); } catch (Exception) { } }
            }
            if (t >= 0.36 && !bubbleShownForReaction)
            {
                bubbleShownForReaction = true;
                ShowBubble();
            }

            if (t >= 5.08)
            {
                reacting = false;
                frameIndex = 0;
                petImage.Source = frames[0];
                UpdateIdle((DateTime.Now - launched).TotalSeconds);
            }
        }

        private static double Clamp01(double v)
        {
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        private static double EaseOut(double t)
        {
            t = Clamp01(t);
            return 1 - Math.Pow(1 - t, 3);
        }

        private static double EaseIn(double t)
        {
            t = Clamp01(t);
            return t * t * t;
        }

        private static double EaseInOut(double t)
        {
            t = Clamp01(t);
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        private void React()
        {
            if (reacting) return;
            reacting = true;
            reactStart = DateTime.Now;
            bubbleShownForReaction = false;
            soundPlayed = false;
            frameIndex = 0;
            petImage.Source = frames[0];
            balance.Refresh();
        }

        private void ShowBubble()
        {
            bubble.Apply(balance.Title, balance.Value, balance.Detail);
            PositionBubble();
            if (!bubbleVisible)
            {
                bubbleVisible = true;
                bubble.Opacity = 0;
                bubble.Show();
            }
            bubbleShownAt = DateTime.Now;
            bubbleClosing = false;
            bubble.Opacity = 1;
            RaiseTopmost();
            PlaceBubbleBehindPet();
        }

        private void RaiseTopmost()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private void PlaceBubbleBehindPet()
        {
            if (bubble == null || !bubbleVisible) return;
            IntPtr petHwnd = new WindowInteropHelper(this).Handle;
            IntPtr bubbleHwnd = new WindowInteropHelper(bubble).Handle;
            if (petHwnd == IntPtr.Zero || bubbleHwnd == IntPtr.Zero) return;
            SetWindowPos(bubbleHwnd, petHwnd, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        private void UpdateBubble(DateTime now)
        {
            if (!bubbleVisible) return;
            double age = (now - bubbleShownAt).TotalSeconds;
            if (!bubbleClosing)
            {
                if (age > 2.0) bubbleClosing = true;
            }
            if (bubbleClosing)
            {
                double k = (age - 2.0) / 0.20;
                if (k <= 0)
                {
                    bubble.Opacity = 1;
                }
                else if (k >= 1)
                {
                    bubble.Hide();
                    bubbleVisible = false;
                    bubbleClosing = false;
                }
                else
                {
                    bubble.Opacity = 1 - k;
                }
            }
        }

        private void OnBalanceUpdated()
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                bubble.Apply(balance.Title, balance.Value, balance.Detail);
            }));
        }

        private void PositionBubble()
        {
            if (bubble == null) return;
            double left = Left + (Width - bubble.Width) / 2;
            double top = Top - bubble.Height + 51;
            if (top < SystemParameters.VirtualScreenTop + 4)
            {
                top = Top + Height - 24;
            }
            double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - bubble.Width - 4;
            double minLeft = SystemParameters.VirtualScreenLeft + 4;
            bubble.Left = Math.Max(minLeft, Math.Min(maxLeft, left));
            bubble.Top = top;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            pressed = true;
            dragging = false;
            totalMove = 0;
            POINT p;
            GetCursorPos(out p);
            pressCursor = p;
            pressLeft = Left;
            pressTop = Top;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!pressed) return;
            POINT p;
            GetCursorPos(out p);
            double dx = p.X - pressCursor.X;
            double dy = p.Y - pressCursor.Y;
            totalMove += Math.Abs(dx) + Math.Abs(dy);
            if (!dragging && totalMove > 5) dragging = true;
            if (!dragging) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = pressLeft + dx / dpi.DpiScaleX;
            Top = pressTop + dy / dpi.DpiScaleY;
            PositionBubble();
            e.Handled = true;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (!pressed) return;
            pressed = false;
            ReleaseMouseCapture();
            if (!dragging) React();
            else SavePosition();
            dragging = false;
            e.Handled = true;
        }

        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            var home = new MenuItem { Header = "回到默认位置" };
            home.Click += delegate { MoveHome(); };
            var quit = new MenuItem { Header = "拜拜鲸鱼娘" };
            quit.Click += delegate { Quit(); };
            menu.Items.Add(home);
            menu.Items.Add(new Separator());
            menu.Items.Add(quit);
            menu.PlacementTarget = this;
            bool leftSpace = Left > 240;
            menu.Placement = leftSpace ? PlacementMode.Left : PlacementMode.Right;
            menu.HorizontalOffset = leftSpace ? -6 : 6;
            menu.StaysOpen = false;
            menu.Opened += delegate { menuOpen = true; };
            menu.Closed += delegate { menuOpen = false; RaiseTopmost(); };
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void MoveHome()
        {
            double left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width - 16;
            double top = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height - 34;

            // Optional per-install default keeps a user-chosen home position without
            // baking this machine's coordinates into the open-source build.
            string def = Path.Combine(dir, "default_position.json");
            if (File.Exists(def))
            {
                try
                {
                    var ser = new JavaScriptSerializer();
                    var pos = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(def));
                    left = Convert.ToDouble(pos["left"], CultureInfo.InvariantCulture);
                    top = Convert.ToDouble(pos["top"], CultureInfo.InvariantCulture);
                }
                catch (Exception) { }
            }

            Left = left;
            Top = top;
            SavePosition();
            PositionBubble();
        }

        private void LoadPosition()
        {
            string file = Path.Combine(dir, "position.json");
            bool ok = false;
            if (File.Exists(file))
            {
                try
                {
                    var ser = new JavaScriptSerializer();
                    var pos = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                    double left = Convert.ToDouble(pos["left"], CultureInfo.InvariantCulture);
                    double top = Convert.ToDouble(pos["top"], CultureInfo.InvariantCulture);
                    if (OnScreen(left, top))
                    {
                        Left = left;
                        Top = top;
                        ok = true;
                    }
                }
                catch (Exception) { }
            }
            if (!ok) MoveHome();
        }

        private static bool OnScreen(double left, double top)
        {
            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth;
            double vb = vt + SystemParameters.VirtualScreenHeight;
            return left > vl - 100 && top > vt - 100 && left < vr - 60 && top < vb - 60;
        }

        private void SavePosition()
        {
            try
            {
                var ser = new JavaScriptSerializer();
                string json = ser.Serialize(new Dictionary<string, object> { { "left", Left }, { "top", Top } });
                File.WriteAllText(Path.Combine(dir, "position.json"), json);
            }
            catch (Exception) { }
        }

        private void ToggleVisible()
        {
            if (IsVisible)
            {
                Hide();
                bubble.Hide();
                bubbleVisible = false;
            }
            else
            {
                Show();
                Topmost = true;
            }
        }

        private void Quit()
        {
            Log("quit");
            try { File.WriteAllText(stopFlag, DateTime.Now.ToString("o")); }
            catch (Exception) { }
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            Application.Current.Shutdown();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SavePosition();
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
            }
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    }

    internal sealed class BubbleWindow : Window
    {
        private TextBlock label;
        private TextBlock amount;

        public BubbleWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            Width = 160;
            Height = 66;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Build();
            SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int ex = GetWindowLong(hwnd, -20);
                SetWindowLong(hwnd, -20, ex | 0x00000080 | 0x08000000 | 0x00000020);
            };
        }

        private void Build()
        {
            var root = new Canvas();
            root.IsHitTestVisible = false;
            Content = root;

            var tail = new Polygon();
            tail.Points = new PointCollection
            {
                new Point(0, 0), new Point(14, 0), new Point(7, 10)
            };
            tail.Fill = new SolidColorBrush(Color.FromRgb(255, 249, 234));
            tail.Stroke = new SolidColorBrush(Color.FromRgb(24, 35, 78));
            tail.StrokeThickness = 2;
            tail.StrokeLineJoin = PenLineJoin.Round;
            Canvas.SetLeft(tail, 73);
            Canvas.SetTop(tail, 50);
            root.Children.Add(tail);

            var body = new Border();
            body.Width = 160;
            body.Height = 56;
            body.CornerRadius = new CornerRadius(14);
            body.Background = new SolidColorBrush(Color.FromRgb(255, 249, 234));
            body.BorderBrush = new SolidColorBrush(Color.FromRgb(24, 35, 78));
            body.BorderThickness = new Thickness(2);
            body.Effect = new DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 2,
                Opacity = 0.22,
                Color = Colors.Black
            };
            root.Children.Add(body);

            var panel = new StackPanel();
            panel.HorizontalAlignment = HorizontalAlignment.Center;
            panel.VerticalAlignment = VerticalAlignment.Center;
            panel.Margin = new Thickness(6);
            label = new TextBlock();
            label.Text = "余额";
            label.FontFamily = new FontFamily("Microsoft YaHei UI");
            label.FontSize = 9;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Foreground = new SolidColorBrush(Color.FromRgb(134, 117, 86));
            amount = new TextBlock();
            amount.Text = "查询中…";
            amount.FontFamily = new FontFamily("Microsoft YaHei UI");
            amount.FontSize = 16;
            amount.FontWeight = FontWeights.Bold;
            amount.HorizontalAlignment = HorizontalAlignment.Center;
            amount.TextAlignment = TextAlignment.Center;
            amount.Foreground = new SolidColorBrush(Color.FromRgb(24, 35, 78));
            amount.Margin = new Thickness(0, 1, 0, 0);
            panel.Children.Add(label);
            panel.Children.Add(amount);
            body.Child = panel;

            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        }

        public void Apply(string title, string value, string detail)
        {
            label.Text = "余额";
            amount.Text = value;
            amount.FontSize = value.Length > 8 ? 13 : 16;
        }

        public void RaiseTopmost()
        {
            if (!IsVisible) return;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
        }

        private const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    }
}
