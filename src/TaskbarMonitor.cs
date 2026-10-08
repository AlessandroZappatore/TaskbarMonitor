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
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
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
    Point pos;

    const string ShowEvent = @"Local\TaskbarMonitor.Show";
    const int HotkeyId = 1, WM_HOTKEY = 0x0312, MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000, VK_M = 0x4D;
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    static System.Threading.EventWaitHandle showEvent;
    static bool StartedByAutostart, OpenSettingsOnStart;
    bool hidden;
    ToolStripMenuItem hideItem;

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
                    run.SetValue(AppName, "\"" + Application.ExecutablePath + "\" --autostart");
                }
            }
            using (var run2 = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                // voce di avvio creata da versioni precedenti: aggiunge --autostart
                var cur0 = run2 == null ? null : run2.GetValue(AppName) as string;
                if (cur0 != null && !cur0.Contains("--autostart"))
                    run2.SetValue(AppName, "\"" + Application.ExecutablePath + "\" --autostart");
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

    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var m = new System.Threading.Mutex(true, AppName, out created))
        {
            if (!created)
            {
                // gia' in esecuzione: riaprire l'app riattiva i widget nascosti
                try { System.Threading.EventWaitHandle.OpenExisting(ShowEvent).Set(); } catch { }
                return;
            }
            showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, ShowEvent);
            StartedByAutostart = Array.IndexOf(args, "--autostart") >= 0;
            OpenSettingsOnStart = Array.IndexOf(args, "--settings") >= 0;
            Migrate();
            if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) SetProcessDPIAware();
            try { S = GetDpiForSystem() / 96f; } catch { S = 1f; }
            Application.EnableVisualStyles();
            Application.Run(new TaskbarMonitor());
        }
    }

    TaskbarMonitor()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Size = new Size(ComputeWidth(), P(40));
        sampler.SetActive(slots);

        var menu = new ContextMenuStrip();
        hidden = StartedByAutostart && HiddenSetting();
        hideItem = new ToolStripMenuItem(hidden ? "Show widgets" : "Hide widgets");
        hideItem.Click += (s, e) => SetHidden(!hidden);
        autoStartItem = new ToolStripMenuItem("Start with Windows");
        autoStartItem.Checked = IsAutoStart();
        autoStartItem.Click += (s, e) => { SetAutoStart(!autoStartItem.Checked); autoStartItem.Checked = IsAutoStart(); };
        var mediaItem = new ToolStripMenuItem("Show now playing");
        mediaItem.Checked = MediaEnabled();
        mediaItem.Click += (s, e) => { mediaItem.Checked = !mediaItem.Checked; SetMediaEnabled(mediaItem.Checked); media.Enabled2 = mediaItem.Checked; };
        var reset = new ToolStripMenuItem("Reposition on taskbar");
        reset.Click += (s, e) => PlaceDefault();
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (s, e) => { tray.Visible = false; Application.Exit(); };
        var settingsItem = new ToolStripMenuItem("Settings...");
        settingsItem.Click += (s, e) => OpenSettings();
        menu.Items.AddRange(new ToolStripItem[] { hideItem, settingsItem, new ToolStripSeparator(), autoStartItem, mediaItem, reset, new ToolStripSeparator(), exit });
        ContextMenuStrip = menu;

        tray.Icon = SystemIcons.Information;
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) SetHidden(!hidden); };
        tray.Visible = true;
        UpdateTrayText();

        var h0 = Handle; TaskbarHost.Attach(this);
        RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_M);
        LoadPosition();
        media = new NowPlayingBar();
        media.Enabled2 = MediaEnabled();
        media.Suppressed = hidden;

        // riaprire l'exe mentre e' gia' in esecuzione fa riapparire i widget
        new System.Threading.Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                try { BeginInvoke(new Action(() => SetHidden(false))); } catch { }
            }
        }) { IsBackground = true }.Start();

        MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragStart = e.Location; } };
        MouseMove += (s, e) => { if (dragging) SetPos(Cursor.Position.X - dragStart.X, Cursor.Position.Y - dragStart.Y); };
        MouseUp += (s, e) => { if (dragging) { dragging = false; SavePosition(); } };

        timer.Interval = 1000;
        timer.Tick += (s, e) => Update1s();
        timer.Start();
        if (OpenSettingsOnStart)
        {
            var once = new Timer { Interval = 800 };
            once.Tick += (s, e) => { once.Stop(); once.Dispose(); OpenSettings(); };
            once.Start();
        }
        Update1s();
    }

    // permette di avviare l'app gia' nascosta (solo l'icona nell'area di notifica)
    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(hidden ? false : value);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId) SetHidden(!hidden);
        base.WndProc(ref m);
    }

    void SetHidden(bool value)
    {
        hidden = value;
        using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue("Hidden", hidden ? 1 : 0);
        hideItem.Text = hidden ? "Show widgets" : "Hide widgets";
        UpdateTrayText();
        media.Suppressed = hidden;
        if (hidden)
        {
            Hide();
            tray.ShowBalloonTip(4000, "Taskbar Monitor", "Widgets hidden. Click this icon or press Ctrl+Alt+M to show them again.", ToolTipIcon.Info);
        }
        else
        {
            Show();
            TaskbarHost.Ensure(this, pos);
            Invalidate();
        }
    }

    void UpdateTrayText()
    {
        tray.Text = hidden ? "Taskbar Monitor (hidden) - click to show" : "Taskbar Monitor - click to hide";
    }

    static bool HiddenSetting()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            return k != null && Convert.ToInt32(k.GetValue("Hidden", 0)) == 1;
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

    void SetPos(int x, int y)
    {
        pos = new Point(x, y);
        TaskbarHost.MoveTo(this, x, y);
    }

    void PlaceDefault()
    {
        var s = Screen.PrimaryScreen;
        Rectangle b = s.Bounds, w = s.WorkingArea;
        int x = b.Right - Width - P(360); // a sinistra dell'area di notifica
        int y;
        if (w.Bottom < b.Bottom) y = w.Bottom + (b.Bottom - w.Bottom - Height) / 2; // taskbar in basso
        else y = b.Bottom - Height - P(4);
        SetPos(x, y);
        SavePosition();
    }

    void LoadPosition()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
        {
            if (k != null && k.GetValue("MY") != null && (k.GetValue("MR") != null || k.GetValue("MX") != null))
            {
                int y = (int)k.GetValue("MY");
                // si salva il bordo destro: cosi' la larghezza puo' cambiare senza finire sopra l'area di notifica
                int right = k.GetValue("MR") != null ? (int)k.GetValue("MR") : (int)k.GetValue("MX") + P(224); // MX: versioni <1.2, larghezza di allora
                var p = new Point(right - Width, y);
                if (SystemInformation.VirtualScreen.Contains(p)) { SetPos(p.X, p.Y); return; }
            }
        }
        PlaceDefault();
    }

    void SavePosition()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
        {
            k.SetValue("MR", pos.X + Width);
            k.SetValue("MY", pos.Y);
            k.DeleteValue("MX", false);
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
            if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\" --autostart");
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

        TaskbarHost.Ensure(this, pos);
        sampler.Down = down; sampler.Up = up;
        sampler.Update();
        Invalidate();
    }

    // ---- dati scelti dall'utente ----
    // Le larghezze delle colonne si misurano sul testo piu' largo possibile di ogni dato scelto:
    // restano fisse mentre i numeri cambiano, ma non c'e' spazio sprecato.
    const int SidePad = 4, ColGap = 8, LabelGap = 4;
    Sampler sampler = new Sampler();
    Metric[] slots = Metrics.Load();
    SettingsForm settings;
    int[] colLabelW = new int[3], colValueW = new int[3], colUnitW = new int[3], colX = new int[3];
    bool[] colUsed = new bool[3];
    int totalWidth;

    // valore e unita' piu' larghi che ogni dato puo' avere
    static void WidestSample(Metric m, out string value, out string unit)
    {
        switch (m)
        {
            case Metric.Down: case Metric.Up: case Metric.DiskRead: case Metric.DiskWrite:
                value = "999.9"; unit = "MB/s"; break;
            case Metric.CpuTemp: value = "100"; unit = "°C"; break;
            case Metric.RamUsed: value = "128.0"; unit = "GB"; break;
            case Metric.DiskFree: value = "1023"; unit = "GB"; break;
            case Metric.Uptime: value = "99d 23"; unit = "h"; break;
            default: value = "100"; unit = "%"; break;
        }
    }

    int TextWidth(string s)
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero))
            return (int)Math.Ceiling(g.MeasureString(s, font, 10000, StringFormat.GenericTypographic).Width);
    }

    void ComputeLayout()
    {
        int x = P(SidePad);
        for (int c = 0; c < 3; c++)
        {
            Metric[] pair = { slots[2 * c], slots[2 * c + 1] };
            colUsed[c] = pair[0] != Metric.None || pair[1] != Metric.None;
            if (!colUsed[c]) continue;
            int lw = 0, vw = 0, uw = 0;
            foreach (var m in pair)
            {
                if (m == Metric.None) continue;
                string v, u;
                WidestSample(m, out v, out u);
                lw = Math.Max(lw, TextWidth(sampler.Get(m).Label));
                vw = Math.Max(vw, TextWidth(v));
                uw = Math.Max(uw, TextWidth(u));
            }
            colLabelW[c] = lw; colValueW[c] = vw; colUnitW[c] = uw;
            colX[c] = x;
            x += lw + P(LabelGap) + vw + P(2) + uw + P(ColGap);
        }
        bool any = colUsed[0] || colUsed[1] || colUsed[2];
        totalWidth = any ? x - P(ColGap) + P(SidePad) : P(64);
    }

    int ComputeWidth()
    {
        ComputeLayout();
        return totalWidth;
    }

    // applica la scelta: il bordo destro resta fermo (accanto all'area di notifica) e il widget cresce/si restringe a sinistra
    void ApplySlots(Metric[] s)
    {
        slots = s;
        Metrics.Save(s);
        sampler.SetActive(slots);
        int oldW = Width, newW = ComputeWidth();
        if (newW != oldW)
        {
            Width = newW;
            SetPos(pos.X + oldW - newW, pos.Y);
            SavePosition();
        }
        Invalidate();
    }

    void OpenSettings()
    {
        if (settings == null || settings.IsDisposed)
            settings = new SettingsForm(slots, ApplySlots);
        settings.Show();
        settings.Activate();
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
        using (var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = al, LineAlignment = StringAlignment.Center })
        {
            sf.FormatFlags |= StringFormatFlags.NoWrap; // tipografico: niente margine interno, il testo si allinea al pixel
            g.DrawString(text, font, br, new RectangleF(x, row * Height / 2f, w, Height / 2f), sf);
        }
    }

    // una posizione: etichetta | valore | unita'. Etichetta e unita' dipendono dal dato scelto.
    void DrawSlot(Graphics g, Metric m, int c, int row, bool light, Color fg, Color dim)
    {
        if (m == Metric.None) return;
        Reading r = sampler.Get(m);
        Color vc = fg;
        if (r.Good) vc = Heat(0, 0, 1, light);
        else if (!double.IsNaN(r.Level)) vc = Heat(r.Level, r.Lo, r.Hi, light);
        bool colored = r.Good || !double.IsNaN(r.Level);
        int x = colX[c];
        int vx = x + colLabelW[c] + P(LabelGap);
        int ux = vx + colValueW[c] + P(r.Unit == "%" || r.Unit == "°C" ? 1 : 3); // % e gradi quasi attaccati al numero
        Cell(g, r.Label, dim, x, colLabelW[c] + P(LabelGap), row, StringAlignment.Near);
        Cell(g, r.Value, vc, vx, colValueW[c], row, StringAlignment.Far);
        Cell(g, r.Unit, colored ? vc : dim, ux, colUnitW[c] + P(4), row, StringAlignment.Near);
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

        bool any = false;
        for (int c = 0; c < 3; c++)
        {
            if (!colUsed[c]) continue; // colonna vuota: sparisce
            any = true;
            DrawSlot(g, slots[2 * c], c, 0, light, fg, dim);
            DrawSlot(g, slots[2 * c + 1], c, 1, light, fg, dim);
        }
        if (!any) Cell(g, "No data", dim, P(SidePad), P(60), 0, StringAlignment.Near);
    }

    protected override void Dispose(bool d)
    {
        if (d) { UnregisterHotKey(Handle, HotkeyId); sampler.Dispose(); tray.Dispose(); font.Dispose(); timer.Dispose(); }
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
    bool playing, busy, enabled, suppressed;
    Point screenPos;
    GlobalSystemMediaTransportControlsSessionManager mgr;
    GlobalSystemMediaTransportControlsSession session;

    public bool Suppressed
    {
        get { return suppressed; }
        set { suppressed = value; if (value) Hide(); }
    }

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
        DoubleBuffered = true;
        Size = new Size(TaskbarMonitor.P(300), TaskbarMonitor.P(40));
        var s = Screen.PrimaryScreen;
        Rectangle b = s.Bounds, w = s.WorkingArea;
        int y = w.Bottom < b.Bottom ? w.Bottom + (b.Bottom - w.Bottom - Height) / 2 : b.Bottom - Height - TaskbarMonitor.P(4);
        screenPos = new Point(b.Left + TaskbarMonitor.P(8), y);
        Cursor = Cursors.Hand;
        var forceHandle = Handle; // serve per BeginInvoke dal thread di polling
        TaskbarHost.Attach(this);
        TaskbarHost.MoveTo(this, screenPos.X, screenPos.Y);
        MouseClick += (o, e) =>
        {
            var se = session;
            if (e.Button != MouseButtons.Left || se == null) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { Wait(se.TryTogglePlayPauseAsync()); } catch { } });
        };
        timer.Interval = 1000;
        timer.Tick += (o, e) => { if (enabled && !suppressed) Poll(); };
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
                if (string.IsNullOrEmpty(t) || !enabled || suppressed) { Hide(); return; }
                if (!Visible) Show();
                TaskbarHost.Ensure(this, screenPos);
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

// Rende la finestra "di proprieta'" della taskbar. Windows tiene sempre una finestra posseduta
// sopra il suo proprietario: quando la taskbar sale (es. si apre Start) salgono anche i riquadri,
// senza dover ripristinare lo z-order di continuo (nessun flicker).
static class TaskbarHost
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetLongPtr(IntPtr h, int i, IntPtr v);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    const int GWLP_HWNDPARENT = -8;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, GW_HWNDPREV = 3;
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    static IntPtr Taskbar { get { return FindWindow("Shell_TrayWnd", null); } }

    public static bool Attach(Form f)
    {
        IntPtr tb = Taskbar, h = f.Handle;
        if (tb == IntPtr.Zero) return false;
        SetLongPtr(h, GWLP_HWNDPARENT, tb);
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        return GetLongPtr(h, GWLP_HWNDPARENT) == tb;
    }

    public static void MoveTo(Form f, int x, int y)
    {
        SetWindowPos(f.Handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static Point ScreenPos(Form f)
    {
        RECT r; GetWindowRect(f.Handle, out r);
        return new Point(r.L, r.T);
    }

    // Riaggancia se Explorer e' stato riavviato; ripristina lo z-order solo se qualcosa e' davvero
    // finito sopra la finestra (altrimenti non fa nulla, quindi nessun flicker).
    public static void Ensure(Form f, Point screenPos)
    {
        IntPtr h = f.Handle, tb = Taskbar;
        if (tb == IntPtr.Zero) return;
        if (GetLongPtr(h, GWLP_HWNDPARENT) != tb)
        {
            if (Attach(f)) MoveTo(f, screenPos.X, screenPos.Y);
            return;
        }
        if (GetWindow(h, GW_HWNDPREV) != IntPtr.Zero)
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
