using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

// Dati che si possono mostrare nel widget (massimo 6, scelti dall'utente).
enum Metric
{
    None, Down, Up, Cpu, CpuTemp, Ram, RamUsed, Disk, DiskRead, DiskWrite, DiskFree, Gpu, Battery, Uptime,
    CpuPower, CpuFreq, CpuLimit, Wifi, GpuMem, BatteryCycles
}

// Un dato pronto da disegnare: etichetta, valore e unita' cambiano in base al tipo di dato.
struct Reading
{
    public string Label, Value, Unit;
    public double Level, Lo, Hi; // Level = NaN: nessuna scala di colore
    public bool Good;            // sempre verde (es. batteria collegata)
}

static class Metrics
{
    public const int SlotCount = 6; // 3 colonne x 2 righe
    const string SettingsKey = @"Software\TaskbarMonitor";

    // Categorie mostrate nella finestra Impostazioni, nell'ordine in cui compaiono.
    public class Group
    {
        public string Title;
        public Metric[] Items;
        public Group(string title, params Metric[] items) { Title = title; Items = items; }
    }

    public static readonly Group[] Groups =
    {
        new Group("Network", Metric.Down, Metric.Up, Metric.Wifi),
        new Group("CPU", Metric.Cpu, Metric.CpuTemp, Metric.CpuPower, Metric.CpuFreq, Metric.CpuLimit),
        new Group("GPU", Metric.Gpu, Metric.GpuMem),
        new Group("Memory", Metric.Ram, Metric.RamUsed),
        new Group("Disk", Metric.Disk, Metric.DiskRead, Metric.DiskWrite, Metric.DiskFree),
        new Group("Battery and system", Metric.Battery, Metric.BatteryCycles, Metric.Uptime)
    };

    public static string Name(Metric m)
    {
        switch (m)
        {
            case Metric.Down: return "Download speed";
            case Metric.Up: return "Upload speed";
            case Metric.Cpu: return "CPU usage";
            case Metric.CpuTemp: return "CPU temperature";
            case Metric.Ram: return "RAM usage (%)";
            case Metric.RamUsed: return "RAM used (GB)";
            case Metric.Disk: return "Disk activity";
            case Metric.DiskRead: return "Disk read speed";
            case Metric.DiskWrite: return "Disk write speed";
            case Metric.DiskFree: return "Disk free space";
            case Metric.Gpu: return "GPU usage";
            case Metric.Battery: return "Battery";
            case Metric.Uptime: return "PC uptime";
            case Metric.CpuPower: return "CPU power (W)";
            case Metric.CpuFreq: return "CPU frequency";
            case Metric.CpuLimit: return "CPU thermal limit";
            case Metric.Wifi: return "Wi-Fi signal";
            case Metric.GpuMem: return "GPU memory";
            case Metric.BatteryCycles: return "Battery cycles";
            default: return "(empty)";
        }
    }

    public static Metric[] Default()
    {
        return new Metric[] { Metric.Cpu, Metric.Ram, Metric.CpuTemp, Metric.None, Metric.Down, Metric.Up };
    }

    public static Metric[] Load()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                var s = k == null ? null : k.GetValue("Slots") as string;
                if (s != null)
                {
                    var parts = s.Split(',');
                    var res = new Metric[SlotCount];
                    var seen = new HashSet<Metric>();
                    if (parts.Length == SlotCount)
                    {
                        for (int i = 0; i < SlotCount; i++)
                        {
                            Metric m;
                            if (!Enum.TryParse(parts[i].Trim(), out m) || !Enum.IsDefined(typeof(Metric), m)) return Default(); // "99" non e' un dato
                            if (m != Metric.None && !seen.Add(m)) return Default(); // niente doppioni
                            res[i] = m;
                        }
                        return res;
                    }
                }
            }
        }
        catch { }
        return Default();
    }

    public static void Save(Metric[] slots)
    {
        var parts = new string[slots.Length];
        for (int i = 0; i < slots.Length; i++) parts[i] = slots[i].ToString();
        using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey)) k.SetValue("Slots", string.Join(",", parts));
    }
}

// Legge solo i dati richiesti dalle posizioni scelte, quindi un dato non selezionato non costa nulla.
class Sampler : IDisposable
{
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    struct MEMSTATUS { public uint len, load; public ulong totalPhys, availPhys, tpf, apf, tv, av, aev; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTATUS m);
    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

    // rete (impostata dal widget)
    public double Down, Up;

    long pIdle, pKernel, pUser;
    double cpu, ram, ramUsedGb, temp = double.NaN, diskPct, diskRead, diskWrite, diskFreeGb = double.NaN, gpu;
    bool gpuOk;
    int tick;
    HashSet<Metric> wanted = new HashSet<Metric>();

    PerformanceCounter[] thermal;
    const int TempWindow = 5; // secondi su cui si fa la media della temperatura
    Queue<double> tempWin = new Queue<double>();
    PerformanceCounter diskIdle, diskReadC, diskWriteC;
    PerformanceCounter powerCtr, freqCtr, perfCtr;
    PerformanceCounter[] limitCtr, gpuMemCtr;
    double powerW = double.NaN, freqGhz = double.NaN, limitPct = double.NaN, gpuMemGb = double.NaN;
    int wifiQuality = -1, batteryCycles = -1;
    IntPtr wlanHandle = IntPtr.Zero;
    PerformanceCounterCategory gpuCat;
    Dictionary<string, CounterSample> gpuPrev = new Dictionary<string, CounterSample>();

    public Sampler()
    {
        GetSystemTimes(out pIdle, out pKernel, out pUser);
    }

    public void SetActive(Metric[] slots)
    {
        wanted = new HashSet<Metric>(slots);

        if (wanted.Contains(Metric.CpuTemp))
        {
            if (thermal == null)
            {
                try
                {
                    var l = new List<PerformanceCounter>();
                    foreach (var n in new PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames())
                        l.Add(new PerformanceCounter("Thermal Zone Information", "Temperature", n));
                    thermal = l.ToArray();
                }
                catch { thermal = new PerformanceCounter[0]; }
            }
        }
        else if (thermal != null) { foreach (var c in thermal) c.Dispose(); thermal = null; temp = double.NaN; tempWin.Clear(); }

        bool needDisk = wanted.Contains(Metric.Disk) || wanted.Contains(Metric.DiskRead) || wanted.Contains(Metric.DiskWrite);
        if (needDisk)
        {
            try
            {
                if (diskIdle == null) diskIdle = new PerformanceCounter("PhysicalDisk", "% Idle Time", "_Total");
                if (diskReadC == null) diskReadC = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                if (diskWriteC == null) diskWriteC = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            }
            catch { }
        }
        else
        {
            if (diskIdle != null) { diskIdle.Dispose(); diskIdle = null; }
            if (diskReadC != null) { diskReadC.Dispose(); diskReadC = null; }
            if (diskWriteC != null) { diskWriteC.Dispose(); diskWriteC = null; }
        }

        if (!wanted.Contains(Metric.Gpu)) { gpuPrev.Clear(); gpuOk = false; }

        // potenza del pacchetto CPU (Intel RAPL esposto da Windows, in milliwatt)
        if (wanted.Contains(Metric.CpuPower))
        {
            if (powerCtr == null)
                try { powerCtr = new PerformanceCounter("Energy Meter", "Power", "RAPL_Package0_PKG"); } catch { powerCtr = null; }
        }
        else if (powerCtr != null) { powerCtr.Dispose(); powerCtr = null; powerW = double.NaN; }

        // frequenza reale = frequenza base x % prestazioni, come Gestione attivita'
        if (wanted.Contains(Metric.CpuFreq))
        {
            if (freqCtr == null)
            {
                try
                {
                    freqCtr = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
                    perfCtr = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
                }
                catch { freqCtr = null; perfCtr = null; }
            }
        }
        else if (freqCtr != null) { freqCtr.Dispose(); perfCtr.Dispose(); freqCtr = null; perfCtr = null; freqGhz = double.NaN; }

        // limite imposto dal calore (100% = nessuna limitazione)
        if (wanted.Contains(Metric.CpuLimit))
        {
            if (limitCtr == null)
            {
                try
                {
                    var l = new List<PerformanceCounter>();
                    foreach (var n in new PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames())
                        l.Add(new PerformanceCounter("Thermal Zone Information", "% Passive Limit", n));
                    limitCtr = l.ToArray();
                }
                catch { limitCtr = new PerformanceCounter[0]; }
            }
        }
        else if (limitCtr != null) { foreach (var c in limitCtr) c.Dispose(); limitCtr = null; limitPct = double.NaN; }

        // memoria GPU usata (dedicata + condivisa, tutte le schede)
        if (wanted.Contains(Metric.GpuMem))
        {
            if (gpuMemCtr == null)
            {
                try
                {
                    var l = new List<PerformanceCounter>();
                    foreach (var n in new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames())
                    {
                        l.Add(new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", n));
                        l.Add(new PerformanceCounter("GPU Adapter Memory", "Shared Usage", n));
                    }
                    gpuMemCtr = l.ToArray();
                }
                catch { gpuMemCtr = new PerformanceCounter[0]; }
            }
        }
        else if (gpuMemCtr != null) { foreach (var c in gpuMemCtr) c.Dispose(); gpuMemCtr = null; gpuMemGb = double.NaN; }

        if (!wanted.Contains(Metric.Wifi)) { CloseWlan(); wifiQuality = -1; }
        if (!wanted.Contains(Metric.BatteryCycles)) batteryCycles = -1;
        tick = 0; // rilegge subito spazio libero e cicli batteria
    }

    public void Update()
    {
        long i, k, u;
        if (GetSystemTimes(out i, out k, out u))
        {
            long dIdle = i - pIdle, dTotal = (k - pKernel) + (u - pUser); // kernel include l'idle
            if (dTotal > 0) cpu = Math.Max(0, Math.Min(100, 100.0 * (dTotal - dIdle) / dTotal));
            pIdle = i; pKernel = k; pUser = u;
        }

        var m = new MEMSTATUS { len = (uint)Marshal.SizeOf(typeof(MEMSTATUS)) };
        if (GlobalMemoryStatusEx(ref m))
        {
            ram = m.load;
            ramUsedGb = (m.totalPhys - m.availPhys) / 1073741824.0;
        }

        if (thermal != null)
        {
            double max = double.NaN;
            foreach (var c in thermal)
            {
                try { double v = c.NextValue() - 273.15; if (v > 0 && v < 150 && (double.IsNaN(max) || v > max)) max = v; } catch { }
            }
            // Il sensore ACPI di molti portatili oscilla di decine di gradi da un secondo all'altro
            // (picchi di boost): si mostra la media degli ultimi secondi invece del campione istantaneo.
            if (double.IsNaN(max)) { tempWin.Clear(); temp = double.NaN; }
            else
            {
                tempWin.Enqueue(max);
                while (tempWin.Count > TempWindow) tempWin.Dequeue();
                double sum = 0;
                foreach (var t in tempWin) sum += t;
                temp = sum / tempWin.Count;
            }
        }

        if (diskIdle != null)
        {
            try
            {
                diskPct = Math.Max(0, Math.Min(100, 100 - diskIdle.NextValue()));
                diskRead = diskReadC.NextValue();
                diskWrite = diskWriteC.NextValue();
            }
            catch { }
        }

        if (wanted.Contains(Metric.Gpu)) ReadGpu();

        if (powerCtr != null)
        {
            try { powerW = powerCtr.NextValue() / 1000.0; } catch { powerW = double.NaN; }
        }
        if (freqCtr != null)
        {
            try { freqGhz = freqCtr.NextValue() * perfCtr.NextValue() / 100000.0; } catch { freqGhz = double.NaN; }
        }
        if (limitCtr != null)
        {
            double min = double.NaN;
            foreach (var c in limitCtr)
            {
                try { double v = c.NextValue(); if (double.IsNaN(min) || v < min) min = v; } catch { }
            }
            limitPct = min;
        }
        if (gpuMemCtr != null)
        {
            double sum = 0; bool ok = false;
            foreach (var c in gpuMemCtr)
            {
                try { sum += c.NextValue(); ok = true; } catch { }
            }
            gpuMemGb = ok ? sum / 1073741824.0 : double.NaN;
        }
        if (wanted.Contains(Metric.Wifi)) ReadWifi();
        // la prima query WMI dura circa 1 s: fuori dal thread dell'interfaccia
        if (wanted.Contains(Metric.BatteryCycles) && tick % 60 == 0) System.Threading.ThreadPool.QueueUserWorkItem(_ => ReadBatteryCycles());

        if (wanted.Contains(Metric.DiskFree) && tick % 10 == 0)
        {
            try
            {
                var di = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                diskFreeGb = di.AvailableFreeSpace / 1073741824.0;
            }
            catch { diskFreeGb = double.NaN; }
        }
        tick++;
    }

    // Come Gestione attivita': somma per tipo di motore (3D, Compute, ...) e prende il piu' alto.
    void ReadGpu()
    {
        try
        {
            if (gpuCat == null) gpuCat = new PerformanceCounterCategory("GPU Engine");
            var coll = gpuCat.ReadCategory()["Utilization Percentage"];
            var cur = new Dictionary<string, CounterSample>();
            var perType = new Dictionary<string, double>();
            foreach (DictionaryEntry de in coll)
            {
                string name = (string)de.Key;
                var sample = ((InstanceData)de.Value).Sample;
                cur[name] = sample;
                CounterSample prev;
                if (!gpuPrev.TryGetValue(name, out prev)) continue;
                int p = name.IndexOf("engtype_", StringComparison.Ordinal);
                string type = p >= 0 ? name.Substring(p) : name;
                double v = CounterSampleCalculator.ComputeCounterValue(prev, sample), acc;
                perType.TryGetValue(type, out acc);
                perType[type] = acc + v;
            }
            gpuPrev = cur;
            double best = 0;
            foreach (var kv in perType) best = Math.Max(best, kv.Value);
            gpu = Math.Min(100, best);
            gpuOk = true;
        }
        catch { gpuOk = false; }
    }

    // ---- Wi-Fi: qualita' del segnale (0-100) dalla connessione attiva, via Native Wifi API ----
    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid guid, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    const int WlanOpcodeCurrentConnection = 7;
    const int WlanInterfaceStateConnected = 1;
    const int WlanInterfaceEntrySize = 532;   // GUID (16) + descrizione (512) + stato (4)
    const int WlanSignalQualityOffset = 576;  // WLAN_CONNECTION_ATTRIBUTES.wlanAssociationAttributes.wlanSignalQuality

    void ReadWifi()
    {
        int quality = -1;
        try
        {
            if (wlanHandle == IntPtr.Zero)
            {
                uint negotiated;
                if (WlanOpenHandle(2, IntPtr.Zero, out negotiated, out wlanHandle) != 0) { wlanHandle = IntPtr.Zero; wifiQuality = -1; return; }
            }
            IntPtr list;
            if (WlanEnumInterfaces(wlanHandle, IntPtr.Zero, out list) != 0) CloseWlan(); // servizio riavviato: la maniglia non vale piu', alla prossima lettura se ne apre una nuova
            else
            {
                int count = Marshal.ReadInt32(list, 0);
                for (int i = 0; i < count && quality < 0; i++)
                {
                    IntPtr entry = IntPtr.Add(list, 8 + i * WlanInterfaceEntrySize);
                    if (Marshal.ReadInt32(entry, 16 + 512) != WlanInterfaceStateConnected) continue;
                    var gb = new byte[16];
                    Marshal.Copy(entry, gb, 0, 16);
                    var guid = new Guid(gb);
                    int size; IntPtr data;
                    if (WlanQueryInterface(wlanHandle, ref guid, WlanOpcodeCurrentConnection, IntPtr.Zero, out size, out data, IntPtr.Zero) == 0)
                    {
                        if (size > WlanSignalQualityOffset + 4) quality = Marshal.ReadInt32(data, WlanSignalQualityOffset);
                        WlanFreeMemory(data);
                    }
                }
                WlanFreeMemory(list);
            }
        }
        catch { quality = -1; CloseWlan(); }
        wifiQuality = quality;
    }

    void CloseWlan()
    {
        if (wlanHandle != IntPtr.Zero) { try { WlanCloseHandle(wlanHandle, IntPtr.Zero); } catch { } wlanHandle = IntPtr.Zero; }
    }

    // ---- cicli di carica della batteria (WMI, letti una volta al minuto) ----
    void ReadBatteryCycles()
    {
        int cycles = -1;
        try
        {
            using (var s = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount"))
                foreach (System.Management.ManagementObject o in s.Get())
                {
                    cycles = Convert.ToInt32(o["CycleCount"]);
                    o.Dispose();
                    break;
                }
        }
        catch { cycles = -1; }
        batteryCycles = cycles;
    }

    public static void Split(double bps, out string num, out string unit)
    {
        if (bps >= 1024 * 1024) { num = (bps / 1048576).ToString("0.00"); unit = "MB/s"; }
        else if (bps >= 1024) { num = (bps / 1024).ToString("0.0"); unit = "KB/s"; }
        else { num = bps.ToString("0"); unit = "B/s"; }
    }

    static Reading Make(string label, string value, string unit)
    {
        return new Reading { Label = label, Value = value, Unit = unit, Level = double.NaN };
    }

    static Reading Pct(string label, double v, double lo, double hi)
    {
        var r = Make(label, v.ToString("0"), "%");
        r.Level = v; r.Lo = lo; r.Hi = hi;
        return r;
    }

    static Reading Rate(string label, double bps)
    {
        string n, u; Split(bps, out n, out u);
        return Make(label, n, u);
    }

    public Reading Get(Metric m)
    {
        switch (m)
        {
            case Metric.Down: return Rate("↓", Down);
            case Metric.Up: return Rate("↑", Up);
            case Metric.Cpu: return Pct("CPU", cpu, 40, 95);
            case Metric.Ram: return Pct("RAM", ram, 50, 95);
            case Metric.Disk: return Pct("DISK", diskPct, 50, 98);
            case Metric.DiskRead: return Rate("READ", diskRead);
            case Metric.DiskWrite: return Rate("WRITE", diskWrite);
            case Metric.CpuTemp:
                {
                    if (double.IsNaN(temp)) return Make("TEMP", "--", "");
                    var r = Make("TEMP", temp.ToString("0"), "°C");
                    r.Level = temp; r.Lo = 55; r.Hi = 95;
                    return r;
                }
            case Metric.RamUsed:
                {
                    var r = Make("RAM", ramUsedGb.ToString("0.0"), "GB");
                    r.Level = ram; r.Lo = 50; r.Hi = 95;
                    return r;
                }
            case Metric.DiskFree:
                {
                    if (double.IsNaN(diskFreeGb)) return Make("FREE", "--", "");
                    return diskFreeGb >= 1000 ? Make("FREE", (diskFreeGb / 1024).ToString("0.0"), "TB")
                                              : Make("FREE", diskFreeGb.ToString("0"), "GB");
                }
            case Metric.Gpu:
                return gpuOk ? Pct("GPU", gpu, 40, 95) : Make("GPU", "--", "");
            case Metric.Battery:
                {
                    var ps = SystemInformation.PowerStatus;
                    if ((ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0 || ps.BatteryLifePercent > 1f)
                        return Make("BAT", "--", "");
                    double pct = ps.BatteryLifePercent * 100;
                    var r = Make("BAT", pct.ToString("0"), "%");
                    if (ps.PowerLineStatus == PowerLineStatus.Online) r.Good = true;
                    else { r.Level = 100 - pct; r.Lo = 50; r.Hi = 90; } // scarica = rosso
                    return r;
                }
            case Metric.Uptime:
                {
                    var t = TimeSpan.FromMilliseconds(GetTickCount64());
                    return t.TotalDays >= 1
                        ? Make("UP", ((int)t.TotalDays) + "d " + t.Hours, "h")
                        : Make("UP", t.Hours + ":" + t.Minutes.ToString("00"), "h");
                }
            case Metric.CpuPower:
                {
                    if (double.IsNaN(powerW)) return Make("PWR", "--", "");
                    var r = Make("PWR", powerW.ToString(powerW < 10 ? "0.0" : "0"), "W");
                    r.Level = powerW; r.Lo = 10; r.Hi = 35;
                    return r;
                }
            case Metric.CpuFreq:
                return double.IsNaN(freqGhz) ? Make("FREQ", "--", "") : Make("FREQ", freqGhz.ToString("0.00"), "GHz");
            case Metric.CpuLimit:
                {
                    if (double.IsNaN(limitPct)) return Make("LIMIT", "--", "");
                    var r = Make("LIMIT", Math.Min(100, limitPct).ToString("0"), "%");
                    r.Level = 100 - Math.Min(100, limitPct); r.Lo = 0; r.Hi = 40; // 100% = verde, limitato = rosso
                    return r;
                }
            case Metric.Wifi:
                {
                    if (wifiQuality < 0) return Make("WIFI", "--", "");
                    var r = Make("WIFI", wifiQuality.ToString(), "%");
                    r.Level = 100 - wifiQuality; r.Lo = 25; r.Hi = 70; // segnale debole = rosso
                    return r;
                }
            case Metric.GpuMem:
                {
                    if (double.IsNaN(gpuMemGb)) return Make("VRAM", "--", "");
                    return gpuMemGb < 1 ? Make("VRAM", (gpuMemGb * 1024).ToString("0"), "MB")
                                        : Make("VRAM", gpuMemGb.ToString("0.0"), "GB");
                }
            case Metric.BatteryCycles:
                return batteryCycles < 0 ? Make("CYCLES", "--", "") : Make("CYCLES", batteryCycles.ToString(), "");
            default: return Make("", "", "");
        }
    }

    public void Dispose()
    {
        CloseWlan();
        if (powerCtr != null) powerCtr.Dispose();
        if (freqCtr != null) freqCtr.Dispose();
        if (perfCtr != null) perfCtr.Dispose();
        if (limitCtr != null) foreach (var c in limitCtr) c.Dispose();
        if (gpuMemCtr != null) foreach (var c in gpuMemCtr) c.Dispose();
        if (thermal != null) foreach (var c in thermal) c.Dispose();
        if (diskIdle != null) diskIdle.Dispose();
        if (diskReadC != null) diskReadC.Dispose();
        if (diskWriteC != null) diskWriteC.Dispose();
    }
}
