using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Media.Control;

class TaskbarMonitor : Form
{
    const string AppName = "TaskbarMonitor";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string SettingsKey = @"Software\TaskbarMonitor";

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    public static float S = 1f;
    public static int P(int v) { return (int)Math.Round(v * S); }
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOMOVE = 2, SWP_NOSIZE = 1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    Timer timer = new Timer();
    NotifyIcon tray = new NotifyIcon();
    ToolStripMenuItem autoStartItem;
    NetworkInterface[] nics = new NetworkInterface[0];
    int tick;
    long lastRx, lastTx;
    double down, up;
    bool first = true, dragging;
    Point dragStart;
    Font font = new Font("Segoe UI", 9f, FontStyle.Regular);
    NowPlayingBar media;

    [STAThread]
    // porta avvio automatico e posizione dalla vecchia versione "InternetSpeedMeter"
    static void Migrate()
    {
        try
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (run != null && run.GetValue("InternetSpeedMeter") != null)
                {
                    run.DeleteValue("InternetSpeedMeter", false);
                    run.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                }
            }
            using (var old = Registry.CurrentUser.OpenSubKey(@"Software\InternetSpeedMeter"))
            using (var cur = Registry.CurrentUser.CreateSubKey(SettingsKey))
            {
                if (old != null && cur.GetValue("MX") == null)
                    foreach (var n in old.GetValueNames()) cur.SetValue(n, old.GetValue(n));
            }
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\InternetSpeedMeter", false);
        }
        catch { }
    }

    static void Main()
    {
        bool created;
        using (var m = new System.Threading.Mutex(true, AppName, out created))
        {
            if (!created) return;
            Migrate();
            SetProcessDPIAware();
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;
            Application.EnableVisualStyles();
            Application.Run(new TaskbarMonitor());
        }
    }

    TaskbarMonitor()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        Size = new Size(P(224), P(40));
        InitStats();

        var menu = new ContextMenuStrip();
        autoStartItem = new ToolStripMenuItem("Avvia con Windows");
        autoStartItem.Checked = IsAutoStart();
        autoStartItem.Click += (s, e) => { SetAutoStart(!autoStartItem.Checked); autoStartItem.Checked = IsAutoStart(); };
        var mediaItem = new ToolStripMenuItem("Mostra brano in riproduzione");
        mediaItem.Checked = MediaEnabled();
        mediaItem.Click += (s, e) => { mediaItem.Checked = !mediaItem.Checked; SetMediaEnabled(mediaItem.Checked); media.Enabled2 = mediaItem.Checked; };
        var reset = new ToolStripMenuItem("Riposiziona sulla taskbar");
        reset.Click += (s, e) => PlaceDefault();
        var exit = new ToolStripMenuItem("Esci");
        exit.Click += (s, e) => { tray.Visible = false; Application.Exit(); };
        menu.Items.AddRange(new ToolStripItem[] { autoStartItem, mediaItem, reset, new ToolStripSeparator(), exit });
        ContextMenuStrip = menu;

        tray.Icon = SystemIcons.Information;
        tray.Text = "Taskbar Monitor";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;

        LoadPosition();
        media = new NowPlayingBar();
        media.Enabled2 = MediaEnabled();

        MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragStart = e.Location; } };
        MouseMove += (s, e) => { if (dragging) Location = new Point(Left + e.X - dragStart.X, Top + e.Y - dragStart.Y); };
        MouseUp += (s, e) => { if (dragging) { dragging = false; SavePosition(); } };

        timer.Interval = 1000;
        timer.Tick += (s, e) => Update1s();
        timer.Start();
        Update1s();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x08000000; // TOOLWINDOW | NOACTIVATE
            return cp;
        }
    }

    void PlaceDefault()
    {
        var s = Screen.PrimaryScreen;
        Rectangle b = s.Bounds, w = s.WorkingArea;
        int x = b.Right - Width - P(360); // a sinistra dell'area di notifica
        int y;
        if (w.Bottom < b.Bottom) y = w.Bottom + (b.Bottom - w.Bottom - Height) / 2; // taskbar in basso
        else y = b.Bottom - Height - P(4);
        Location = new Point(x, y);
        SavePosition();
    }

    void LoadPosition()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            if (k != null && k.GetValue("MX") != null)
            {
                var p = new Point((int)k.GetValue("MX"), (int)k.GetValue("MY"));
                if (SystemInformation.VirtualScreen.Contains(p)) { Location = p; return; }
            }
        }
        PlaceDefault();
    }

    void SavePosition()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
        {
            k.SetValue("MX", Left);
            k.SetValue("MY", Top);
        }
    }

    static bool MediaEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            return k == null || Convert.ToInt32(k.GetValue("Media", 1)) == 1;
    }

    static void SetMediaEnabled(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue("Media", on ? 1 : 0);
    }

    static bool IsAutoStart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(AppName) != null;
    }

    static void SetAutoStart(bool on)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
            else k.DeleteValue(AppName, false);
        }
    }

    static bool LightTaskbar()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
        }
        catch { return false; }
    }

    void Update1s()
    {
        if (tick++ % 10 == 0)
        {
            var list = new System.Collections.Generic.List<NetworkInterface>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                if (n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    list.Add(n);
            nics = list.ToArray();
            first = true; // evita picchi quando cambia l'insieme di interfacce
        }

        long rx = 0, tx = 0;
        foreach (var n in nics)
        {
            try { var st = n.GetIPv4Statistics(); rx += st.BytesReceived; tx += st.BytesSent; }
            catch { }
        }
        if (first) { first = false; down = up = 0; }
        else
        {
            down = Math.Max(0, rx - lastRx);
            up = Math.Max(0, tx - lastTx);
        }
        lastRx = rx; lastTx = tx;

        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        ReadStats();
        tray.Text = "↓ " + Fmt(down) + "  ↑ " + Fmt(up);
        Invalidate();
    }

    // ---- CPU / RAM / temperatura ----
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    struct MEMSTATUS { public uint len, load; public ulong tp, ap, tpf, apf, tv, av, aev; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTATUS m);

    long pIdle, pKernel, pUser;
    double cpuPct, ramPct, tempC = double.NaN;
    PerformanceCounter[] thermal = new PerformanceCounter[0];

    void InitStats()
    {
        GetSystemTimes(out pIdle, out pKernel, out pUser);
        try
        {
            var cat = new PerformanceCounterCategory("Thermal Zone Information");
            var l = new System.Collections.Generic.List<PerformanceCounter>();
            foreach (var n in cat.GetInstanceNames()) l.Add(new PerformanceCounter("Thermal Zone Information", "Temperature", n));
            thermal = l.ToArray();
        }
        catch { }
    }

    void ReadStats()
    {
        long i, k, u;
        if (GetSystemTimes(out i, out k, out u))
        {
            long dIdle = i - pIdle, dTotal = (k - pKernel) + (u - pUser); // kernel include l'idle
            if (dTotal > 0) cpuPct = Math.Max(0, Math.Min(100, 100.0 * (dTotal - dIdle) / dTotal));
            pIdle = i; pKernel = k; pUser = u;
        }
        var m = new MEMSTATUS { len = (uint)Marshal.SizeOf(typeof(MEMSTATUS)) };
        if (GlobalMemoryStatusEx(ref m)) ramPct = m.load;
        double max = double.NaN;
        foreach (var c in thermal)
        {
            try { double v = c.NextValue() - 273.15; if (v > 0 && v < 150 && (double.IsNaN(max) || v > max)) max = v; } catch { }
        }
        tempC = max;
    }

    static void Split(double bps, out string num, out string unit)
    {
        if (bps >= 1024 * 1024) { num = (bps / 1048576).ToString("0.00"); unit = "MB/s"; }
        else if (bps >= 1024) { num = (bps / 1024).ToString("0.0"); unit = "KB/s"; }
        else { num = bps.ToString("0"); unit = "B/s"; }
    }

    static string Fmt(double bps)
    {
        string n, u; Split(bps, out n, out u);
        return n + " " + u;
    }

    // verde -> giallo -> rosso in base al valore (lo = tutto verde, hi = tutto rosso)
    static Color Heat(double v, double lo, double hi, bool light)
    {
        Color g = light ? Color.FromArgb(46, 125, 50) : Color.FromArgb(76, 217, 100);
        Color y = light ? Color.FromArgb(214, 140, 0) : Color.FromArgb(255, 204, 0);
        Color r = light ? Color.FromArgb(211, 47, 47) : Color.FromArgb(255, 69, 58);
        double t = Math.Max(0, Math.Min(1, (v - lo) / (hi - lo)));
        return t < 0.5 ? Lerp(g, y, t * 2) : Lerp(y, r, (t - 0.5) * 2);
    }

    static Color Lerp(Color a, Color b, double t)
    {
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // disegna un testo in una cella a posizione e larghezza fisse
    void Cell(Graphics g, string text, Color c, int x, int w, int row, StringAlignment al)
    {
        using (var br = new SolidBrush(c))
        using (var sf = new StringFormat { Alignment = al, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(text, font, br, new RectangleF(P(x), row * Height / 2f, P(w), Height / 2f), sf);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool light = LightTaskbar();
        Color bg = light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
        Color fg = light ? Color.FromArgb(20, 20, 20) : Color.White;
        Color dim = light ? Color.FromArgb(100, 100, 100) : Color.FromArgb(160, 160, 160);
        var g = e.Graphics;
        g.Clear(bg);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var L = StringAlignment.Near; var R = StringAlignment.Far;

        // colonna statistiche: etichetta | valore | temperatura
        Cell(g, "CPU", dim, 6, 32, 0, L);
        Cell(g, cpuPct.ToString("0") + "%", Heat(cpuPct, 40, 95, light), 38, 38, 0, R);
        Cell(g, double.IsNaN(tempC) ? "--" : tempC.ToString("0") + "°C", double.IsNaN(tempC) ? dim : Heat(tempC, 55, 95, light), 80, 40, 0, R);
        Cell(g, "RAM", dim, 6, 32, 1, L);
        Cell(g, ramPct.ToString("0") + "%", Heat(ramPct, 50, 95, light), 38, 38, 1, R);

        // colonna velocita': freccia | numero | unita'
        string dn, du, un, uu;
        Split(down, out dn, out du); Split(up, out un, out uu);
        Cell(g, "↓", dim, 128, 14, 0, L); Cell(g, dn, fg, 142, 40, 0, R); Cell(g, du, dim, 186, 34, 0, L);
        Cell(g, "↑", dim, 128, 14, 1, L); Cell(g, un, fg, 142, 40, 1, R); Cell(g, uu, dim, 186, 34, 1, L);
    }

    protected override void Dispose(bool d)
    {
        if (d) { tray.Dispose(); font.Dispose(); timer.Dispose(); }
        base.Dispose(d);
    }
}

class NowPlayingBar : Form
{
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    Timer timer = new Timer();
    Font f1 = new Font("Segoe UI", 9f, FontStyle.Bold), f2 = new Font("Segoe UI", 8f);
    string title = "", artist = "", coverKey = "";
    Image cover;
    bool playing, busy, enabled;
    GlobalSystemMediaTransportControlsSessionManager mgr;
    GlobalSystemMediaTransportControlsSession session;

    public bool Enabled2
    {
        get { return enabled; }
        set { enabled = value; if (!value) Hide(); }
    }

    public NowPlayingBar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        Size = new Size(TaskbarMonitor.P(300), TaskbarMonitor.P(40));
        var s = Screen.PrimaryScreen;
        Rectangle b = s.Bounds, w = s.WorkingArea;
        int y = w.Bottom < b.Bottom ? w.Bottom + (b.Bottom - w.Bottom - Height) / 2 : b.Bottom - Height - TaskbarMonitor.P(4);
        Location = new Point(b.Left + TaskbarMonitor.P(8), y);
        Cursor = Cursors.Hand;
        var forceHandle = Handle; // serve per BeginInvoke dal thread di polling
        MouseClick += (o, e) =>
        {
            var se = session;
            if (e.Button != MouseButtons.Left || se == null) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { Wait(se.TryTogglePlayPauseAsync()); } catch { } });
        };
        timer.Interval = 1000;
        timer.Tick += (o, e) => { if (enabled) Poll(); };
        timer.Start();
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }
    }

    static T Wait<T>(Windows.Foundation.IAsyncOperation<T> op)
    {
        while (op.Status == Windows.Foundation.AsyncStatus.Started) System.Threading.Thread.Sleep(10);
        return op.GetResults();
    }

    static Image LoadCover(GlobalSystemMediaTransportControlsSessionMediaProperties p)
    {
        try
        {
            if (p.Thumbnail == null) return null;
            var stream = Wait(p.Thumbnail.OpenReadAsync());
            uint size = (uint)stream.Size;
            var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0));
            Wait<uint>(reader.LoadAsync(size));
            var bytes = new byte[size];
            reader.ReadBytes(bytes);
            using (var ms = new System.IO.MemoryStream(bytes))
            using (var img = Image.FromStream(ms))
                return new Bitmap(img);
        }
        catch { return null; }
    }

    void Poll()
    {
        if (busy) return;
        busy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(_ => PollWork());
    }

    void PollWork()
    {
        try
        {
            if (mgr == null) mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            GlobalSystemMediaTransportControlsSession pick = null;
            foreach (var se in mgr.GetSessions())
            {
                var st = se.GetPlaybackInfo().PlaybackStatus;
                if (st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) { pick = se; break; }
                if (pick == null && st == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused) pick = se;
            }
            session = pick;
            if (pick == null) { BeginInvoke(new Action(Hide)); return; }
            var p = Wait(pick.TryGetMediaPropertiesAsync());
            string t = p.Title ?? "", a = p.Artist ?? "";
            bool pl = pick.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Image newCover = null; bool coverChanged = false;
            if (t + "|" + a != coverKey) { coverChanged = true; newCover = LoadCover(p); }
            BeginInvoke(new Action(() =>
            {
                if (coverChanged) { var old = cover; cover = newCover; coverKey = t + "|" + a; if (old != null) old.Dispose(); Invalidate(); }
                if (t != title || a != artist || pl != playing) { title = t; artist = a; playing = pl; Invalidate(); }
                if (string.IsNullOrEmpty(t) || !enabled) { Hide(); return; }
                if (!Visible) Show();
                SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 1 | 2 | 0x10 | 0x40);
            }));
        }
        catch { }
        finally { busy = false; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool light = false;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                light = k != null && Convert.ToInt32(k.GetValue("SystemUsesLightTheme", 0)) == 1;
        }
        catch { }
        Color bg = light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
        Color fg = light ? Color.FromArgb(20, 20, 20) : Color.White;
        Color dim = light ? Color.FromArgb(90, 90, 90) : Color.FromArgb(170, 170, 170);
        e.Graphics.Clear(bg);
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (var b1 = new SolidBrush(playing ? fg : dim))
        using (var b2 = new SolidBrush(dim))
        using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
        {
            float x = TaskbarMonitor.P(6);
            if (cover != null)
            {
                e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(cover, new Rectangle(TaskbarMonitor.P(4), TaskbarMonitor.P(4), Height - 2 * TaskbarMonitor.P(4), Height - 2 * TaskbarMonitor.P(4)));
                x = Height;
            }
            e.Graphics.DrawString((playing ? "\u266A " : "\u23F8 ") + title, f1, b1, new RectangleF(x, 0, Width - x - TaskbarMonitor.P(4), Height / 2f), sf);
            e.Graphics.DrawString(artist, f2, b2, new RectangleF(x, Height / 2f, Width - x - TaskbarMonitor.P(4), Height / 2f), sf);
        }
    }
}
