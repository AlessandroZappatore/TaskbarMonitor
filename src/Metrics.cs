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
enum Metric { None, Down, Up, Cpu, CpuTemp, Ram, RamUsed, Disk, DiskRead, DiskWrite, DiskFree, Gpu, Battery, Uptime }

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

    public static readonly Metric[] Choices =
    {
        Metric.Down, Metric.Up, Metric.Cpu, Metric.CpuTemp, Metric.Ram, Metric.RamUsed,
        Metric.Disk, Metric.DiskRead, Metric.DiskWrite, Metric.DiskFree, Metric.Gpu, Metric.Battery, Metric.Uptime
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
                            if (!Enum.TryParse(parts[i].Trim(), out m)) return Default();
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
    PerformanceCounter diskIdle, diskReadC, diskWriteC;
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
        else if (thermal != null) { foreach (var c in thermal) c.Dispose(); thermal = null; temp = double.NaN; }

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
        tick = 0; // rilegge subito lo spazio libero
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
            temp = max;
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
            default: return Make("", "", "");
        }
    }

    public void Dispose()
    {
        if (thermal != null) foreach (var c in thermal) c.Dispose();
        if (diskIdle != null) diskIdle.Dispose();
        if (diskReadC != null) diskReadC.Dispose();
        if (diskWriteC != null) diskWriteC.Dispose();
    }
}
