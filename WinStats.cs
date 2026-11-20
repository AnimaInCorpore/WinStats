// WinStats - a tiny system monitor for the Windows 11 taskbar, inspired by Stats for macOS
// (https://github.com/exelban/stats). One tray icon per module, click for a details popup.
// Builds with the C# compiler that ships with Windows (see build.cmd) - no SDK required.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinStats
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int n, uint dpi);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

        // Monitor control over DDC/CI (the same channel the monitor's own vendor tools use).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
        }
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
        [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
        [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);
        [DllImport("dxva2.dll")] public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint current, out uint max);
        [DllImport("dxva2.dll")] public static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
        [DllImport("dxva2.dll")] public static extern bool GetCapabilitiesStringLength(IntPtr h, out uint length);
        [DllImport("dxva2.dll", CharSet = CharSet.Ansi)] public static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, StringBuilder caps, uint length);
    }

    static class U
    {
        public static string Bytes(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < 4) { b /= 1024; i++; }
            return b.ToString(i == 0 || b >= 100 ? "0" : (b >= 10 ? "0.0" : "0.00")) + " " + u[i];
        }

        // At most 4 characters, for the tray icon.
        public static string Short(double b)
        {
            b /= 1024; if (b < 1000) return b.ToString("0") + "K";
            b /= 1024; if (b < 10) return b.ToString("0.0") + "M";
            if (b < 1000) return b.ToString("0") + "M";
            b /= 1024; return b.ToString(b < 10 ? "0.0" : "0") + "G";
        }

        public static string Duration(double seconds)
        {
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            if (t.TotalDays >= 1) return (int)t.TotalDays + "d " + t.Hours + "h " + t.Minutes + "m";
            return t.Hours + "h " + t.Minutes + "m";
        }

        public static bool Light(string value)
        {
            try
            {
                object o = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", value, 0);
                return o is int && (int)o != 0;
            }
            catch { return false; }
        }

        public static float Clamp01(float v) { return float.IsNaN(v) ? 0 : Math.Max(0, Math.Min(1, v)); }
    }

    static class Draw
    {
        // Draws text as large as fits into the rectangle, centered.
        public static void Fit(Graphics g, string t, RectangleF r, Color c, bool bold)
        {
            if (string.IsNullOrEmpty(t)) return;
            FontStyle fs = bold ? FontStyle.Bold : FontStyle.Regular;
            float em = r.Height * 1.2f;
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
                Font f = new Font("Segoe UI", em, fs, GraphicsUnit.Pixel);
                SizeF m = g.MeasureString(t, f, 10000, sf);
                if (m.Width > r.Width)
                {
                    em = em * r.Width / m.Width;
                    f.Dispose();
                    f = new Font("Segoe UI", em, fs, GraphicsUnit.Pixel);
                    m = g.MeasureString(t, f, 10000, sf);
                }
                using (SolidBrush b = new SolidBrush(c))
                    g.DrawString(t, f, b, r.X + (r.Width - m.Width) / 2, r.Y + (r.Height - m.Height) / 2 - em * 0.06f, sf);
                f.Dispose();
            }
        }

        public static void Chart(Graphics g, RectangleF r, List<float> hist, float max, Color c, int points)
        {
            int n = Math.Min(points, hist.Count);
            if (n < 2) return;
            int start = hist.Count - n;
            if (max <= 0)
            {
                max = 1;
                for (int i = start; i < hist.Count; i++) max = Math.Max(max, hist[i]);
            }
            float step = r.Width / (points - 1);
            PointF[] pts = new PointF[n + 2];
            for (int i = 0; i < n; i++)
            {
                float v = U.Clamp01(hist[start + i] / max);
                pts[i + 1] = new PointF(r.Right - (n - 1 - i) * step, r.Bottom - v * (r.Height - 1) - 0.5f);
            }
            pts[0] = new PointF(pts[1].X, r.Bottom);
            pts[n + 1] = new PointF(r.Right, r.Bottom);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(90, c))) g.FillPolygon(b, pts);
            PointF[] line = new PointF[n];
            Array.Copy(pts, 1, line, 0, n);
            using (Pen p = new Pen(c, Math.Max(1f, r.Height / 24f))) g.DrawLines(p, line);
        }
    }

    // Per-process CPU and memory, only sampled while a popup that shows it is open.
    static class Procs
    {
        static Dictionary<int, TimeSpan> prev = new Dictionary<int, TimeSpan>();
        static HashSet<int> denied = new HashSet<int>();
        public static List<KeyValuePair<string, double>> TopCpu = new List<KeyValuePair<string, double>>();
        public static List<KeyValuePair<string, double>> TopMem = new List<KeyValuePair<string, double>>();

        public static void Sample(double dt)
        {
            Dictionary<string, double> cpu = new Dictionary<string, double>();
            Dictionary<string, double> mem = new Dictionary<string, double>();
            Dictionary<int, TimeSpan> cur = new Dictionary<int, TimeSpan>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    int id = p.Id;
                    if (id == 0) continue;
                    string n = p.ProcessName;
                    double v;
                    mem.TryGetValue(n, out v);
                    mem[n] = v + p.WorkingSet64;
                    if (denied.Contains(id)) continue;
                    TimeSpan t;
                    try { t = p.TotalProcessorTime; }
                    catch { denied.Add(id); continue; }
                    cur[id] = t;
                    TimeSpan o;
                    if (dt > 0 && prev.TryGetValue(id, out o))
                    {
                        cpu.TryGetValue(n, out v);
                        cpu[n] = v + (t - o).TotalSeconds / dt / Environment.ProcessorCount * 100;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            prev = cur;
            TopCpu = cpu.OrderByDescending(k => k.Value).Take(5).ToList();
            TopMem = mem.OrderByDescending(k => k.Value).Take(5).ToList();
        }
    }

    abstract class Module
    {
        public string Name, Label;
        public Color Color;
        public List<float> History = new List<float>();
        public float HistoryMax = 100;      // 0 = scale to the largest visible value
        public float Value;                 // 0..1, used by the bar widget
        public string IconText = "", BigText = "", Tooltip = "";
        public float[] Bars;                // optional row of small bars in the popup (CPU cores)
        public bool WantsProcesses;
        public NotifyIcon Tray;
        public IntPtr HIcon;

        public virtual bool Available { get { return true; } }
        // A module that returns a menu here opens it on left-click instead of the details popup.
        public virtual ContextMenuStrip ClickMenu() { return null; }
        public abstract void Update(double dt);
        public abstract void Details(List<string[]> rows);   // { key, value }; value == null => section header

        protected void Push(float v)
        {
            History.Add(v);
            if (History.Count > 120) History.RemoveAt(0);
        }

        // Symbol for the "bar with symbol" widget, drawn into the square r. Default: the first letter.
        protected virtual void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            Draw.Fit(g, Label.Substring(0, 1), r, ((SolidBrush)b).Color, true);
        }

        // The symbol helpers map unit-square coordinates into r and snap them to the pixel grid,
        // so strokes cover whole pixels and stay sharp at tray-icon sizes.
        static float Snap(float v, Pen p)
        {
            bool odd = p == null ? false : ((int)Math.Round(p.Width)) % 2 == 1;
            return odd ? (float)Math.Floor(v) + 0.5f : (float)Math.Round(v);
        }

        // p == null: snap to whole pixels (for fills); otherwise to stroke centres.
        protected static RectangleF Box(RectangleF r, Pen p, float x, float y, float w, float h)
        {
            float x1 = Snap(r.X + x * r.Width, p), y1 = Snap(r.Y + y * r.Height, p);
            float x2 = Snap(r.X + (x + w) * r.Width, p), y2 = Snap(r.Y + (y + h) * r.Height, p);
            return new RectangleF(x1, y1, x2 - x1, y2 - y1);
        }

        protected static void Line(Graphics g, Pen p, RectangleF r, float x1, float y1, float x2, float y2)
        {
            g.DrawLine(p, Snap(r.X + x1 * r.Width, p), Snap(r.Y + y1 * r.Height, p), Snap(r.X + x2 * r.Width, p), Snap(r.Y + y2 * r.Height, p));
        }

        protected static void Outline(Graphics g, Pen p, RectangleF r, float x, float y, float w, float h)
        {
            RectangleF o = Box(r, p, x, y, w, h);
            g.DrawRectangle(p, o.X, o.Y, o.Width, o.Height);
        }

        // style: 0 = mini (label + value), 1 = bar, 2 = line chart, 3 = bar with symbol, 4 = symbol with value
        public virtual void DrawIcon(Graphics g, int s, Color fg, int style)
        {
            if (style == 2)
            {
                Draw.Chart(g, new RectangleF(1, 1, s - 2, s - 2), History, HistoryMax, Color, Math.Max(8, s / 2));
                using (Pen p = new Pen(Color.FromArgb(130, fg))) g.DrawRectangle(p, 0.5f, 0.5f, s - 1, s - 1);
            }
            else if (style == 4)
            {
                // Symbol on top, value below.
                float a = (float)Math.Round(s * 0.48f);
                using (Pen p = new Pen(fg, Math.Max(1, s / 16)))
                using (SolidBrush b = new SolidBrush(fg))
                    DrawSymbol(g, new RectangleF((float)Math.Floor((s - a) / 2), 0, a, a), p, b);
                Draw.Fit(g, IconText, new RectangleF(0, a, s, s - a), fg, true);
            }
            else if (style == 1 || style == 3)
            {
                float x = (float)Math.Round(s * 0.58f);
                if (style == 3)
                {
                    float a = (float)Math.Round(s * 0.68f);
                    x = a + Math.Max(1, s / 16);
                    using (Pen p = new Pen(fg, Math.Max(1, s / 16)))   // whole-pixel stroke width
                    using (SolidBrush b = new SolidBrush(fg))
                        DrawSymbol(g, new RectangleF(0, (float)Math.Floor((s - a) / 2), a, a), p, b);
                }
                else Draw.Fit(g, Label.Substring(0, 1), new RectangleF(0, s * 0.15f, s * 0.52f, s * 0.7f), fg, true);
                float w = s - x - 1, h = (s - 2) * U.Clamp01(Value);
                using (SolidBrush b = new SolidBrush(Color)) g.FillRectangle(b, x, s - 1 - h, w, h);
                using (Pen p = new Pen(Color.FromArgb(160, fg))) g.DrawRectangle(p, x - 0.5f, 0.5f, w + 1, s - 1);
            }
            else
            {
                Draw.Fit(g, Label, new RectangleF(0, 0, s, s * 0.36f), Color.FromArgb(210, fg), false);
                Draw.Fit(g, IconText, new RectangleF(0, s * 0.38f, s, s * 0.62f), fg, true);
            }
        }
    }

    class CpuModule : Module
    {
        PerformanceCounter total;
        PerformanceCounter[] cores;
        PerformanceCounter[] temps = new PerformanceCounter[0];
        float temp = -1;

        public CpuModule()
        {
            Name = "CPU"; Label = "CPU"; Color = Color.FromArgb(76, 154, 255); WantsProcesses = true;
            try
            {
                // "% Processor Utility" is what Task Manager shows.
                PerformanceCounterCategory cat = new PerformanceCounterCategory("Processor Information");
                string[] names = cat.GetInstanceNames().Where(x => !x.Contains("_Total"))
                    .OrderBy(x => int.Parse(x.Split(',')[0])).ThenBy(x => int.Parse(x.Split(',')[1])).ToArray();
                total = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total");
                total.NextValue();
                cores = names.Select(x => new PerformanceCounter("Processor Information", "% Processor Utility", x)).ToArray();
            }
            catch
            {
                total = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                cores = Enumerable.Range(0, Environment.ProcessorCount)
                    .Select(i => new PerformanceCounter("Processor", "% Processor Time", i.ToString())).ToArray();
            }
            Bars = new float[cores.Length];
            try
            {
                PerformanceCounterCategory tz = new PerformanceCounterCategory("Thermal Zone Information");
                temps = tz.GetInstanceNames().Select(x => new PerformanceCounter("Thermal Zone Information", "Temperature", x)).ToArray();
            }
            catch { }
        }

        public override void Update(double dt)
        {
            Value = U.Clamp01(total.NextValue() / 100f);
            for (int i = 0; i < cores.Length; i++)
            {
                try { Bars[i] = U.Clamp01(cores[i].NextValue() / 100f); } catch { Bars[i] = 0; }
            }
            temp = -1;
            foreach (PerformanceCounter t in temps)
            {
                try { temp = Math.Max(temp, t.NextValue() - 273.15f); } catch { }
            }
            int pct = (int)Math.Round(Value * 100);
            IconText = pct.ToString(); BigText = pct + "%"; Tooltip = "CPU " + pct + "%";
            Push(pct);
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            // Chip with pins.
            Outline(g, p, r, 0.2f, 0.2f, 0.6f, 0.6f);
            g.FillRectangle(b, Box(r, null, 0.38f, 0.38f, 0.24f, 0.24f));
            foreach (float t in new[] { 0.35f, 0.65f })
            {
                Line(g, p, r, t, 0.02f, t, 0.2f); Line(g, p, r, t, 0.8f, t, 0.98f);
                Line(g, p, r, 0.02f, t, 0.2f, t); Line(g, p, r, 0.8f, t, 0.98f, t);
            }
        }

        public override void Details(List<string[]> rows)
        {
            rows.Add(new[] { "Usage", BigText });
            rows.Add(new[] { "Logical cores", Environment.ProcessorCount.ToString() });
            if (temp > 0) rows.Add(new[] { "Thermal zone", temp.ToString("0") + " °C" });
            rows.Add(new[] { "Uptime", U.Duration(Native.GetTickCount64() / 1000.0) });
            rows.Add(new[] { "Top processes", null });
            foreach (KeyValuePair<string, double> p in Procs.TopCpu) rows.Add(new[] { p.Key, p.Value.ToString("0.0") + "%" });
        }
    }

    class GpuModule : Module
    {
        PerformanceCounterCategory eng, mem;
        Dictionary<string, CounterSample> prev = new Dictionary<string, CounterSample>();
        SortedDictionary<string, float> util = new SortedDictionary<string, float>();
        SortedDictionary<string, long> used = new SortedDictionary<string, long>();
        List<string> names = new List<string>();

        public GpuModule()
        {
            Name = "GPU"; Label = "GPU"; Color = Color.FromArgb(176, 124, 255);
            try
            {
                if (PerformanceCounterCategory.Exists("GPU Engine")) eng = new PerformanceCounterCategory("GPU Engine");
                if (PerformanceCounterCategory.Exists("GPU Adapter Memory")) mem = new PerformanceCounterCategory("GPU Adapter Memory");
            }
            catch { }
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher("select Name from Win32_VideoController"))
                    foreach (ManagementBaseObject o in s.Get()) names.Add(Convert.ToString(o["Name"]));
            }
            catch { }
        }

        public override bool Available { get { return eng != null; } }

        static string Luid(string instance)
        {
            int a = instance.IndexOf("luid_"), b = instance.IndexOf("_phys");
            return a < 0 || b < a ? instance : instance.Substring(a + 5, b - a - 5);
        }

        public override void Update(double dt)
        {
            // Like Task Manager: per adapter, sum each engine over all processes and take the busiest engine.
            Dictionary<string, float> sums = new Dictionary<string, float>();
            Dictionary<string, CounterSample> cur = new Dictionary<string, CounterSample>();
            SortedDictionary<string, float> u = new SortedDictionary<string, float>();
            SortedDictionary<string, long> m = new SortedDictionary<string, long>();

            InstanceDataCollectionCollection data = eng.ReadCategory();
            if (data.Contains("Utilization Percentage"))
            {
                foreach (InstanceData d in data["Utilization Percentage"].Values)
                {
                    string inst = d.InstanceName;
                    cur[inst] = d.Sample;
                    string luid = Luid(inst);
                    if (!u.ContainsKey(luid)) u[luid] = 0;
                    CounterSample p;
                    if (!prev.TryGetValue(inst, out p)) continue;
                    float v = CounterSample.Calculate(p, d.Sample);
                    if (float.IsNaN(v) || float.IsInfinity(v)) continue;
                    int e = inst.IndexOf("_eng_");
                    string key = luid + "|" + (e < 0 ? "" : inst.Substring(e));
                    float old;
                    sums.TryGetValue(key, out old);
                    sums[key] = old + v;
                }
            }
            prev = cur;
            foreach (KeyValuePair<string, float> s in sums)
            {
                string luid = s.Key.Substring(0, s.Key.IndexOf('|'));
                u[luid] = Math.Max(u[luid], Math.Min(100, s.Value));
            }
            if (mem != null)
            {
                try
                {
                    InstanceDataCollectionCollection md = mem.ReadCategory();
                    if (md.Contains("Dedicated Usage"))
                        foreach (InstanceData d in md["Dedicated Usage"].Values) m[Luid(d.InstanceName)] = d.RawValue;
                }
                catch { }
            }
            util = u; used = m;

            float max = u.Count == 0 ? 0 : u.Values.Max();
            Value = max / 100f;
            int pct = (int)Math.Round(max);
            IconText = pct.ToString(); BigText = pct + "%"; Tooltip = "GPU " + pct + "%";
            Push(pct);
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            // Graphics card with two fans and its slot connector.
            Outline(g, p, r, 0.04f, 0.22f, 0.92f, 0.46f);
            g.FillEllipse(b, Box(r, null, 0.17f, 0.32f, 0.26f, 0.26f));
            g.FillEllipse(b, Box(r, null, 0.57f, 0.32f, 0.26f, 0.26f));
            g.FillRectangle(b, Box(r, null, 0.16f, 0.68f, 0.44f, 0.14f));
        }

        public override void Details(List<string[]> rows)
        {
            int i = 0;
            foreach (string luid in util.Keys.Union(used.Keys).Distinct().OrderBy(x => x))
            {
                float u; long m;
                util.TryGetValue(luid, out u);
                string v = u.ToString("0") + "%";
                if (used.TryGetValue(luid, out m)) v += "  ·  " + U.Bytes(m);
                rows.Add(new[] { "Adapter " + i++, v });
            }
            if (names.Count > 0)
            {
                rows.Add(new[] { "Installed", null });
                foreach (string n in names) rows.Add(new[] { n, "" });
            }
        }
    }

    class RamModule : Module
    {
        Native.MEMORYSTATUSEX ms;

        public RamModule()
        {
            Name = "Memory"; Label = "RAM"; Color = Color.FromArgb(52, 199, 89); WantsProcesses = true;
        }

        public override void Update(double dt)
        {
            ms.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            Native.GlobalMemoryStatusEx(ref ms);
            Value = ms.ullTotalPhys == 0 ? 0 : 1f - (float)ms.ullAvailPhys / ms.ullTotalPhys;
            int pct = (int)Math.Round(Value * 100);
            IconText = pct.ToString(); BigText = pct + "%";
            Tooltip = "Memory " + pct + "% (" + U.Bytes(ms.ullTotalPhys - ms.ullAvailPhys) + " of " + U.Bytes(ms.ullTotalPhys) + ")";
            Push(pct);
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            // Memory module with contact pins.
            Outline(g, p, r, 0.04f, 0.26f, 0.92f, 0.36f);
            foreach (float t in new[] { 0.2f, 0.4f, 0.6f, 0.8f }) Line(g, p, r, t, 0.62f, t, 0.84f);
        }

        public override void Details(List<string[]> rows)
        {
            rows.Add(new[] { "Used", U.Bytes(ms.ullTotalPhys - ms.ullAvailPhys) });
            rows.Add(new[] { "Available", U.Bytes(ms.ullAvailPhys) });
            rows.Add(new[] { "Total", U.Bytes(ms.ullTotalPhys) });
            rows.Add(new[] { "Committed", U.Bytes(ms.ullTotalPageFile - ms.ullAvailPageFile) + " / " + U.Bytes(ms.ullTotalPageFile) });
            rows.Add(new[] { "Top processes", null });
            foreach (KeyValuePair<string, double> p in Procs.TopMem) rows.Add(new[] { p.Key, U.Bytes(p.Value) });
        }
    }

    class DiskModule : Module
    {
        PerformanceCounter read, write;
        float r, w;
        List<string[]> drives = new List<string[]>();
        string sys = Path.GetPathRoot(Environment.SystemDirectory);

        public DiskModule()
        {
            Name = "Disk"; Label = "SSD"; Color = Color.FromArgb(255, 159, 10); HistoryMax = 0;
            try
            {
                read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            }
            catch { read = write = null; }
        }

        public override void Update(double dt)
        {
            List<string[]> list = new List<string[]>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string name = d.Name.TrimEnd('\\');
                    if (!string.IsNullOrEmpty(d.VolumeLabel)) name += " " + d.VolumeLabel;
                    list.Add(new[] { name, U.Bytes(d.TotalFreeSpace) + " free of " + U.Bytes(d.TotalSize) });
                    if (string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase))
                        Value = 1f - (float)d.TotalFreeSpace / d.TotalSize;
                }
                catch { }
            }
            drives = list;
            if (read != null)
            {
                try { r = read.NextValue(); w = write.NextValue(); } catch { }
            }
            int pct = (int)Math.Round(Value * 100);
            IconText = pct.ToString(); BigText = pct + "%";
            Tooltip = "Disk " + sys + " " + pct + "% used";
            Push(r + w);
        }

        protected override void DrawSymbol(Graphics g, RectangleF q, Pen p, Brush b)
        {
            // Storage cylinder.
            g.DrawEllipse(p, Box(q, p, 0.12f, 0.08f, 0.76f, 0.3f));
            g.DrawArc(p, Box(q, p, 0.12f, 0.35f, 0.76f, 0.3f), 0, 180);
            g.DrawArc(p, Box(q, p, 0.12f, 0.62f, 0.76f, 0.3f), 0, 180);
            Line(g, p, q, 0.12f, 0.23f, 0.12f, 0.77f);
            Line(g, p, q, 0.88f, 0.23f, 0.88f, 0.77f);
        }

        public override void Details(List<string[]> rows)
        {
            rows.Add(new[] { "Read", U.Bytes(r) + "/s" });
            rows.Add(new[] { "Write", U.Bytes(w) + "/s" });
            rows.Add(new[] { "Drives", null });
            rows.AddRange(drives);
        }
    }

    class NetModule : Module
    {
        static readonly Color Up = Color.FromArgb(255, 90, 82), Down = Color.FromArgb(76, 154, 255);
        Dictionary<string, long[]> prev = new Dictionary<string, long[]>();
        double up, down, totalUp, totalDown;
        string iface = "-", ip = "-";

        public NetModule()
        {
            Name = "Network"; Label = "NET"; Color = Down; HistoryMax = 0;
        }

        public override void Update(double dt)
        {
            long dUp = 0, dDown = 0;
            Dictionary<string, long[]> cur = new Dictionary<string, long[]>();
            string name = "-", addr = "-";
            foreach (NetworkInterface n in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props = n.GetIPProperties();
                    if (props.GatewayAddresses.Count == 0) continue;   // only adapters that actually route traffic
                    IPv4InterfaceStatistics st = n.GetIPv4Statistics();
                    long[] now = { st.BytesSent, st.BytesReceived };
                    cur[n.Id] = now;
                    long[] old;
                    if (prev.TryGetValue(n.Id, out old))
                    {
                        dUp += Math.Max(0, now[0] - old[0]);
                        dDown += Math.Max(0, now[1] - old[1]);
                    }
                    if (name == "-")
                    {
                        name = n.Name;
                        foreach (UnicastIPAddressInformation a in props.UnicastAddresses)
                            if (a.Address.AddressFamily == AddressFamily.InterNetwork) { addr = a.Address.ToString(); break; }
                    }
                }
                catch { }
            }
            prev = cur; iface = name; ip = addr;
            totalUp += dUp; totalDown += dDown;
            up = dt > 0 ? dUp / dt : 0;
            down = dt > 0 ? dDown / dt : 0;
            BigText = U.Bytes(down) + "/s";
            Tooltip = "Network  ↓ " + U.Bytes(down) + "/s  ↑ " + U.Bytes(up) + "/s";
            Push((float)(down + up));
        }

        public override void DrawIcon(Graphics g, int s, Color fg, int style)
        {
            if (style == 2) { base.DrawIcon(g, s, fg, style); return; }
            Draw.Fit(g, U.Short(up), new RectangleF(0, 0, s, s * 0.5f), Up, true);
            Draw.Fit(g, U.Short(down), new RectangleF(0, s * 0.5f, s, s * 0.5f), Down, true);
        }

        public override void Details(List<string[]> rows)
        {
            rows.Add(new[] { "Download", U.Bytes(down) + "/s" });
            rows.Add(new[] { "Upload", U.Bytes(up) + "/s" });
            rows.Add(new[] { "Interface", iface });
            rows.Add(new[] { "Local IP", ip });
            rows.Add(new[] { "Since start", null });
            rows.Add(new[] { "Downloaded", U.Bytes(totalDown) });
            rows.Add(new[] { "Uploaded", U.Bytes(totalUp) });
        }
    }

    class BatteryModule : Module
    {
        PowerStatus ps = SystemInformation.PowerStatus;

        public BatteryModule()
        {
            Name = "Battery"; Label = "BAT"; Color = Color.FromArgb(52, 199, 89);
        }

        public override bool Available { get { return (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0; } }

        bool Plugged { get { return ps.PowerLineStatus == PowerLineStatus.Online; } }

        public override void Update(double dt)
        {
            Value = U.Clamp01(ps.BatteryLifePercent);
            int pct = (int)Math.Round(Value * 100);
            Color = Plugged ? Color.FromArgb(52, 199, 89) : (pct <= 20 ? Color.FromArgb(255, 90, 82) : Color.FromArgb(255, 204, 0));
            IconText = pct.ToString(); BigText = pct + "%";
            Tooltip = "Battery " + pct + "%" + (Plugged ? " (plugged in)" : "");
            Push(pct);
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            // Battery, with a bolt-like centre line while plugged in.
            Outline(g, p, r, 0.04f, 0.28f, 0.8f, 0.44f);
            g.FillRectangle(b, Box(r, null, 0.84f, 0.38f, 0.12f, 0.24f));
            if (Plugged) { Line(g, p, r, 0.52f, 0.36f, 0.36f, 0.52f); Line(g, p, r, 0.36f, 0.52f, 0.52f, 0.48f); Line(g, p, r, 0.52f, 0.48f, 0.36f, 0.64f); }
        }

        public override void Details(List<string[]> rows)
        {
            bool charging = (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
            rows.Add(new[] { "Level", BigText });
            rows.Add(new[] { "Power source", Plugged ? "AC adapter" : "Battery" });
            rows.Add(new[] { "State", charging ? "Charging" : (Plugged ? "Not charging" : "Discharging") });
            if (!Plugged && ps.BatteryLifeRemaining > 0) rows.Add(new[] { "Time remaining", U.Duration(ps.BatteryLifeRemaining) });
        }
    }

    // Switches the input source of external monitors (VCP feature 0x60 over DDC/CI).
    class DisplayModule : Module
    {
        const byte InputSelect = 0x60;

        class Mon
        {
            public IntPtr H;
            public string Name;
            public int Current;
            public List<int> Inputs = new List<int>();
        }

        List<Mon> mons = new List<Mon>();
        Dictionary<string, string> capsCache = new Dictionary<string, string>();
        volatile bool scanning, scanned;
        ContextMenuStrip menu = new ContextMenuStrip();

        // Input values from the VESA MCCS standard. Some monitors use their own codes (often for USB-C).
        static string InputName(int v)
        {
            switch (v)
            {
                case 0x01: return "VGA 1";
                case 0x02: return "VGA 2";
                case 0x03: return "DVI 1";
                case 0x04: return "DVI 2";
                case 0x05: return "Composite 1";
                case 0x06: return "Composite 2";
                case 0x07: return "S-Video 1";
                case 0x08: return "S-Video 2";
                case 0x09: return "Tuner 1";
                case 0x0A: return "Tuner 2";
                case 0x0B: return "Tuner 3";
                case 0x0C: return "Component 1";
                case 0x0D: return "Component 2";
                case 0x0E: return "Component 3";
                case 0x0F: return "DisplayPort 1";
                case 0x10: return "DisplayPort 2";
                case 0x11: return "HDMI 1";
                case 0x12: return "HDMI 2";
                default: return "Input 0x" + v.ToString("X2");
            }
        }

        static string InputShort(int v)
        {
            switch (v)
            {
                case 0x01: case 0x02: return "VG" + v;
                case 0x03: case 0x04: return "DV" + (v - 2);
                case 0x0F: case 0x10: return "DP" + (v - 0x0E);
                case 0x11: case 0x12: return "HD" + (v - 0x10);
                default: return v.ToString("X2");
            }
        }

        public DisplayModule()
        {
            Name = "Display"; Label = "MON"; Color = Color.FromArgb(90, 200, 250); IconText = "…";
            Scan(false);
            SystemEvents.DisplaySettingsChanged += delegate { Scan(false); };
        }

        void Scan(bool forgetCapabilities)
        {
            if (scanning) return;
            scanning = true;
            Thread t = new Thread(delegate()
            {
                try { ScanNow(forgetCapabilities); } catch { } finally { scanning = false; scanned = true; }
            });
            t.IsBackground = true;
            t.Start();
        }

        static string ReadCaps(IntPtr h)
        {
            for (int attempt = 0; attempt < 2; attempt++)   // DDC/CI replies get lost now and then
            {
                uint len;
                if (!Native.GetCapabilitiesStringLength(h, out len) || len == 0) continue;
                StringBuilder sb = new StringBuilder((int)len + 1);
                if (Native.CapabilitiesRequestAndCapabilitiesReply(h, sb, len)) return sb.ToString();
            }
            return "";
        }

        // Finds every monitor that answers the input-select query. Slow (seconds), so never on the UI thread.
        public void ScanNow(bool forgetCapabilities)
        {
            if (forgetCapabilities) capsCache.Clear();
            List<IntPtr> hmons = new List<IntPtr>();
            Native.MonitorEnumProc cb = delegate(IntPtr hm, IntPtr dc, IntPtr rc, IntPtr d) { hmons.Add(hm); return true; };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);

            List<Mon> found = new List<Mon>();
            for (int i = 0; i < hmons.Count; i++)
            {
                uint n;
                if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hmons[i], out n) || n == 0) continue;
                Native.PHYSICAL_MONITOR[] arr = new Native.PHYSICAL_MONITOR[n];
                if (!Native.GetPhysicalMonitorsFromHMONITOR(hmons[i], n, arr)) continue;
                for (int j = 0; j < arr.Length; j++)
                {
                    IntPtr h = arr[j].hPhysicalMonitor;
                    uint cur, max;
                    if (!Native.GetVCPFeatureAndVCPFeatureReply(h, InputSelect, IntPtr.Zero, out cur, out max))
                    {
                        Native.DestroyPhysicalMonitor(h);   // laptop panels and some docks do not speak DDC/CI
                        continue;
                    }
                    Mon m = new Mon();
                    m.H = h; m.Current = (int)(cur & 0xFF); m.Name = arr[j].szPhysicalMonitorDescription;

                    string key = i + "|" + j + "|" + m.Name, caps;
                    if (!capsCache.TryGetValue(key, out caps))
                    {
                        caps = ReadCaps(h);
                        if (caps.Length > 0) capsCache[key] = caps;
                    }
                    Match model = Regex.Match(caps, @"model\(([^)]+)\)");
                    if (model.Success) m.Name = model.Groups[1].Value.Trim();
                    Match inputs = Regex.Match(caps, @"(?<![0-9A-Fa-f])60\s*\(([0-9A-Fa-f\s]+)\)");
                    if (inputs.Success)
                    {
                        foreach (string s in inputs.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int v;
                            if (int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out v) && !m.Inputs.Contains(v)) m.Inputs.Add(v);
                        }
                    }
                    if (m.Inputs.Count == 0) m.Inputs.AddRange(new[] { 0x0F, 0x10, 0x11, 0x12 });   // monitor did not say; offer the usual ones
                    if (!m.Inputs.Contains(m.Current)) m.Inputs.Add(m.Current);
                    found.Add(m);
                }
            }
            List<Mon> old;
            lock (App.Sync) { old = mons; mons = found; }
            foreach (Mon m in old) Native.DestroyPhysicalMonitor(m.H);
        }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            foreach (Mon m in mons)
                sb.AppendLine(m.Name + ": current " + InputName(m.Current) + "; available " + string.Join(", ", m.Inputs.Select(x => InputName(x)).ToArray()));
            return sb.Length == 0 ? "No monitor with DDC/CI input control found." : sb.ToString();
        }

        public override void Update(double dt)
        {
            if (mons.Count == 0)
            {
                IconText = scanned ? "–" : "…";
                Tooltip = scanned ? "Display: no monitor with input control (DDC/CI)" : "Display: looking for monitors…";
            }
            else
            {
                IconText = InputShort(mons[0].Current);
                Tooltip = "Display: " + string.Join(", ", mons.Select(m => m.Name + " on " + InputName(m.Current)).ToArray());
            }
            BigText = IconText;
        }

        public override ContextMenuStrip ClickMenu()
        {
            List<Mon> list;
            lock (App.Sync) list = mons;
            menu.Items.Clear();
            if (list.Count == 0)
            {
                menu.Items.Add(scanning ? "Looking for monitors…" : "No monitor with input control (DDC/CI) found").Enabled = false;
            }
            foreach (Mon m in list)
            {
                Mon mon = m;
                uint cur, max;
                if (Native.GetVCPFeatureAndVCPFeatureReply(mon.H, InputSelect, IntPtr.Zero, out cur, out max)) mon.Current = (int)(cur & 0xFF);
                menu.Items.Add(mon.Name).Enabled = false;
                foreach (int input in mon.Inputs)
                {
                    int value = input;
                    ToolStripMenuItem it = new ToolStripMenuItem(InputName(value));
                    it.Checked = value == mon.Current;
                    it.Click += delegate
                    {
                        if (value == mon.Current) return;
                        Thread t = new Thread(delegate()
                        {
                            if (Native.SetVCPFeature(mon.H, InputSelect, (uint)value)) mon.Current = value;
                        });
                        t.IsBackground = true;
                        t.Start();
                    };
                    menu.Items.Add(it);
                }
                menu.Items.Add(new ToolStripSeparator());
            }
            menu.Items.Add("Rescan monitors", null, delegate { Scan(true); });
            return menu;
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            // Monitor on a stand.
            Outline(g, p, r, 0.04f, 0.12f, 0.92f, 0.58f);
            Line(g, p, r, 0.5f, 0.7f, 0.5f, 0.88f);
            Line(g, p, r, 0.26f, 0.88f, 0.74f, 0.88f);
        }

        public override void DrawIcon(Graphics g, int s, Color fg, int style)
        {
            if (style == 0 || style == 4) { base.DrawIcon(g, s, fg, style); return; }
            // No level to chart: a monitor with the active input written on its screen.
            using (Pen p = new Pen(fg, Math.Max(1, s / 16)))
            using (SolidBrush b = new SolidBrush(fg))
                DrawSymbol(g, new RectangleF(0, 0, s, s), p, b);
            Draw.Fit(g, IconText, new RectangleF(s * 0.14f, s * 0.22f, s * 0.72f, s * 0.38f), fg, true);
        }

        public override void Details(List<string[]> rows)
        {
            foreach (Mon m in mons) rows.Add(new[] { m.Name, InputName(m.Current) });
        }
    }

    class Popup : Form
    {
        public Module M;
        public DateTime HiddenAt;
        public float S = 1;
        List<string[]> rows = new List<string[]>();
        Point anchor;

        public Popup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            AutoScaleMode = AutoScaleMode.None;
            int round = 2;
            try { Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { }
            Deactivate += delegate { HiddenAt = DateTime.UtcNow; Hide(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Hide(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80;         // WS_EX_TOOLWINDOW: keep out of Alt+Tab
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
                return cp;
            }
        }

        public int W { get { return (int)(300 * S); } }

        public int SetModule(Module m)
        {
            M = m;
            S = App.Dpi / 96f;
            rows = new List<string[]>();
            m.Details(rows);
            float h = 14 + 36 + 64 + (m.Bars != null ? 36 : 0) + 10;
            foreach (string[] r in rows) h += r[1] == null ? 26 : 20;
            return (int)(h * S);
        }

        public void ShowFor(Module m, Point at)
        {
            anchor = at;
            Place(SetModule(m));
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
            Invalidate();
        }

        public void RefreshData()
        {
            int h = SetModule(M);
            if (h != Height) Place(h);
            Invalidate();
        }

        void Place(int h)
        {
            Rectangle wa = Screen.FromPoint(anchor).WorkingArea;
            int m = (int)(8 * S);
            int x = Math.Max(wa.Left + m, Math.Min(anchor.X - W / 2, wa.Right - W - m));
            int y = anchor.Y - h - 2 * m;
            if (y < wa.Top + m) y = Math.Max(wa.Top + m, anchor.Y + 2 * m);
            y = Math.Max(wa.Top + m, Math.Min(y, wa.Bottom - h - m));
            Bounds = new Rectangle(x, y, W, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (M != null) lock (App.Sync) PaintTo(e.Graphics, ClientSize.Width, ClientSize.Height);
        }

        public void PaintTo(Graphics g, int w, int h)
        {
            bool light = U.Light("AppsUseLightTheme");
            Color bg = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(32, 32, 32);
            Color fg = light ? Color.FromArgb(20, 20, 20) : Color.FromArgb(240, 240, 240);
            Color dim = light ? Color.FromArgb(105, 105, 105) : Color.FromArgb(155, 155, 155);
            Color panel = light ? Color.FromArgb(234, 234, 234) : Color.FromArgb(46, 46, 46);
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float p = 14 * S, y = p, cw = w - 2 * p;

            float pt = S * 96f / 72f;   // point sizes in pixels at the taskbar's DPI
            using (Font title = new Font("Segoe UI", 11 * pt, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font big = new Font("Segoe UI", 15 * pt, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font norm = new Font("Segoe UI", 9 * pt, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font hdr = new Font("Segoe UI", 7.5f * pt, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush bFg = new SolidBrush(fg), bDim = new SolidBrush(dim), bPanel = new SolidBrush(panel), bCol = new SolidBrush(M.Color))
            using (StringFormat near = new StringFormat(StringFormatFlags.NoWrap), far = new StringFormat(StringFormatFlags.NoWrap))
            {
                near.Trimming = far.Trimming = StringTrimming.EllipsisCharacter;
                near.LineAlignment = far.LineAlignment = StringAlignment.Center;
                far.Alignment = StringAlignment.Far;

                RectangleF head = new RectangleF(p, y, cw, 30 * S);
                g.DrawString(M.Name, title, bFg, head, near);
                g.DrawString(M.BigText, big, bCol, head, far);
                y += 36 * S;

                RectangleF cr = new RectangleF(p, y, cw, 56 * S);
                g.FillRectangle(bPanel, cr);
                Draw.Chart(g, cr, M.History, M.HistoryMax, M.Color, 60);
                y += 64 * S;

                if (M.Bars != null)
                {
                    int n = M.Bars.Length;
                    float bw = cw / n, bh = 28 * S, gap = bw > 4 ? 1.5f * S : 0;
                    for (int i = 0; i < n; i++)
                    {
                        float v = bh * U.Clamp01(M.Bars[i]);
                        g.FillRectangle(bPanel, p + i * bw, y, bw - gap, bh);
                        g.FillRectangle(bCol, p + i * bw, y + bh - v, bw - gap, v);
                    }
                    y += 36 * S;
                }

                foreach (string[] r in rows)
                {
                    if (r[1] == null)
                    {
                        g.DrawString(r[0].ToUpper(), hdr, bDim, new RectangleF(p, y + 6 * S, cw, 20 * S), near);
                        y += 26 * S;
                    }
                    else
                    {
                        RectangleF rr = new RectangleF(p, y, cw, 20 * S);
                        g.DrawString(r[0], norm, r[1].Length == 0 ? bFg : bDim, rr, near);
                        g.DrawString(r[1], norm, bFg, rr, far);
                        y += 20 * S;
                    }
                }
            }
            using (Pen border = new Pen(Color.FromArgb(light ? 200 : 70, light ? 200 : 70, light ? 200 : 70)))
                g.DrawRectangle(border, 0, 0, w - 1, h - 1);
        }
    }

    class App : ApplicationContext
    {
        public static readonly object Sync = new object();
        const string Key = @"Software\WinStats";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly string[] Styles = { "Mini (label + value)", "Bar with letter", "Line chart", "Bar with symbol", "Symbol with value" };

        List<Module> mods;
        HashSet<string> disabled = new HashSet<string>();
        int style, interval = 1;
        Control ui = new Control();
        Popup popup = new Popup();
        ContextMenuStrip menu = new ContextMenuStrip();
        Thread worker;
        volatile bool running = true, wantProcs;

        public static List<Module> CreateModules()
        {
            List<Module> list = new List<Module>();
            Func<Module>[] makers = {
                () => new CpuModule(), () => new GpuModule(), () => new RamModule(),
                () => new DiskModule(), () => new NetModule(), () => new BatteryModule(), () => new DisplayModule()
            };
            foreach (Func<Module> make in makers)
            {
                try { Module m = make(); if (m.Available) list.Add(m); } catch { }
            }
            return list;
        }

        // DPI of the taskbar that hosts the tray, read live so a changed display scale is picked up.
        public static int Dpi
        {
            get
            {
                try
                {
                    IntPtr h = Native.FindWindow("Shell_TrayWnd", null);
                    int d = h == IntPtr.Zero ? 0 : (int)Native.GetDpiForWindow(h);
                    if (d > 0) return d;
                }
                catch { }
                return 96;
            }
        }

        // Exact pixel size of a tray icon at that DPI (SM_CXSMICON: 16 px at 100%, 24 px at 150%, 32 px at 200%).
        static int IconSize
        {
            get
            {
                try { return Math.Max(16, Native.GetSystemMetricsForDpi(49, (uint)Dpi)); }
                catch { return Math.Max(16, Native.GetSystemMetrics(49)); }
            }
        }

        static Bitmap Render(Module m, int size, Color fg, int style)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;   // pixel centres at .5, so snapped strokes fill whole pixels
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                m.DrawIcon(g, size, fg, style);
            }
            return bmp;
        }

        public App()
        {
            IntPtr force = ui.Handle;   // needed for BeginInvoke from the worker
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
            {
                foreach (string s in Convert.ToString(k.GetValue("Disabled", "")).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) disabled.Add(s);
                style = Math.Max(0, Math.Min(Styles.Length - 1, Convert.ToInt32(k.GetValue("Style", 0))));
                interval = Math.Max(1, Math.Min(10, Convert.ToInt32(k.GetValue("Interval", 1))));
            }
            mods = CreateModules();
            BuildMenu();
            foreach (Module m in mods)
            {
                Module mod = m;
                try { mod.Update(0); } catch { }
                mod.Tray = new NotifyIcon();
                mod.Tray.ContextMenuStrip = menu;
                mod.Tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Toggle(mod); };
            }
            RefreshUi();
            foreach (Module m in mods) m.Tray.Visible = !disabled.Contains(m.Name);
            worker = new Thread(Loop);
            worker.IsBackground = true;
            worker.Start();
        }

        void Loop()
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (running)
            {
                Thread.Sleep(interval * 1000);
                double dt = sw.Elapsed.TotalSeconds;
                sw.Restart();
                lock (Sync)
                {
                    foreach (Module m in mods)
                    {
                        if (disabled.Contains(m.Name)) continue;
                        try { m.Update(dt); } catch { }
                    }
                    if (wantProcs) { try { Procs.Sample(dt); } catch { } }
                }
                try { ui.BeginInvoke(new Action(RefreshUi)); } catch { }
            }
        }

        void RefreshUi()
        {
            if (!running) return;
            Color fg = U.Light("SystemUsesLightTheme") ? Color.Black : Color.White;
            int size = IconSize;
            lock (Sync)
            {
                foreach (Module m in mods)
                {
                    if (disabled.Contains(m.Name)) continue;
                    using (Bitmap bmp = Render(m, size, fg, style))
                    {
                        IntPtr h = bmp.GetHicon();
                        Icon old = m.Tray.Icon;
                        m.Tray.Icon = Icon.FromHandle(h);
                        if (old != null) old.Dispose();
                        if (m.HIcon != IntPtr.Zero) Native.DestroyIcon(m.HIcon);
                        m.HIcon = h;
                    }
                    m.Tray.Text = m.Tooltip.Length > 63 ? m.Tooltip.Substring(0, 63) : m.Tooltip;
                }
                wantProcs = popup.Visible && popup.M != null && popup.M.WantsProcesses;
                if (popup.Visible) popup.RefreshData();
            }
        }

        void Toggle(Module m)
        {
            ContextMenuStrip own = m.ClickMenu();
            if (own != null)
            {
                Native.SetForegroundWindow(own.Handle);   // so the menu closes when clicking elsewhere
                own.Show(Cursor.Position);
                return;
            }
            if (popup.Visible && popup.M == m) { popup.Hide(); return; }
            // The click that closed the popup (via deactivation) should not reopen it.
            if (popup.M == m && (DateTime.UtcNow - popup.HiddenAt).TotalMilliseconds < 400) return;
            lock (Sync)
            {
                if (m.WantsProcesses) { try { Procs.Sample(0); } catch { } }
                wantProcs = m.WantsProcesses;
                popup.ShowFor(m, Cursor.Position);
            }
        }

        void Save()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
            {
                k.SetValue("Disabled", string.Join(",", disabled.ToArray()));
                k.SetValue("Style", style);
                k.SetValue("Interval", interval);
            }
        }

        static bool Autostart
        {
            get { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("WinStats") != null; }
            set
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("WinStats", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("WinStats", false);
                }
            }
        }

        void BuildMenu()
        {
            ToolStripMenuItem modules = new ToolStripMenuItem("Modules");
            foreach (Module m in mods)
            {
                Module mod = m;
                ToolStripMenuItem it = new ToolStripMenuItem(mod.Name);
                it.Tag = mod;
                it.Click += delegate
                {
                    lock (Sync)
                    {
                        if (disabled.Contains(mod.Name)) disabled.Remove(mod.Name);
                        else if (mods.Count(x => !disabled.Contains(x.Name)) > 1) disabled.Add(mod.Name);   // keep at least one icon
                    }
                    Save();
                    RefreshUi();
                    mod.Tray.Visible = !disabled.Contains(mod.Name);
                };
                modules.DropDownItems.Add(it);
            }

            ToolStripMenuItem widget = new ToolStripMenuItem("Widget");
            for (int i = 0; i < Styles.Length; i++)
            {
                int n = i;
                widget.DropDownItems.Add(new ToolStripMenuItem(Styles[i], null, delegate { style = n; Save(); RefreshUi(); }));
            }

            int[] secs = { 1, 2, 5 };
            ToolStripMenuItem rate = new ToolStripMenuItem("Update interval");
            foreach (int sec in secs)
            {
                int n = sec;
                ToolStripMenuItem it = new ToolStripMenuItem(n + " s", null, delegate { interval = n; Save(); });
                it.Tag = n;
                rate.DropDownItems.Add(it);
            }

            ToolStripMenuItem auto = new ToolStripMenuItem("Start with Windows", null, delegate { Autostart = !Autostart; });

            menu.Items.Add(modules);
            menu.Items.Add(widget);
            menu.Items.Add(rate);
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Task Manager", null, delegate { try { Process.Start("taskmgr.exe"); } catch { } });
            menu.Items.Add("Quit WinStats", null, delegate { Quit(); });
            menu.Opening += delegate
            {
                foreach (ToolStripMenuItem it in modules.DropDownItems) it.Checked = !disabled.Contains(((Module)it.Tag).Name);
                for (int i = 0; i < widget.DropDownItems.Count; i++) ((ToolStripMenuItem)widget.DropDownItems[i]).Checked = i == style;
                foreach (ToolStripMenuItem it in rate.DropDownItems) it.Checked = (int)it.Tag == interval;
                auto.Checked = Autostart;
            };
        }

        void Quit()
        {
            running = false;
            foreach (Module m in mods)
            {
                m.Tray.Visible = false;
                m.Tray.Dispose();
                if (m.HIcon != IntPtr.Zero) Native.DestroyIcon(m.HIcon);
            }
            ExitThread();
        }

        // "WinStats.exe --preview out.png": renders all widgets and popups into an image (for checking the drawing code).
        // With a module name ("--preview out.png CPU") it samples for a while and renders only that module's popup.
        public static void Preview(string path, string only)
        {
            List<Module> mods = CreateModules();
            if (only != null) mods = mods.Where(m => string.Equals(m.Name, only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mods.Count == 0) return;
            for (int i = 0; i < (only == null ? 4 : 45); i++)
            {
                foreach (Module m in mods) { try { m.Update(i == 0 ? 0 : 1); } catch { } }
                Procs.Sample(i == 0 ? 0 : 1);
                Thread.Sleep(1000);
            }
            if (only != null)
            {
                using (Popup one = new Popup())
                {
                    int h = one.SetModule(mods[0]);
                    using (Bitmap bmp = new Bitmap(one.W, h))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        one.PaintTo(g, one.W, h);
                        bmp.Save(path, ImageFormat.Png);
                    }
                }
                return;
            }
            int size = IconSize, zoom = 4, cell = size * zoom + 12;
            using (Popup popup = new Popup())
            {
                int maxH = mods.Max(m => popup.SetModule(m));   // also sets the popup's scale
                int pw = popup.W, top = Styles.Length * cell + 12;
                using (Bitmap bmp = new Bitmap(Math.Max(mods.Count * (pw + 12) + 12, 400), top + maxH + 12))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(28, 28, 28));
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    for (int st = 0; st < Styles.Length; st++)
                        for (int i = 0; i < mods.Count; i++)
                            using (Bitmap ic = Render(mods[i], size, Color.White, st))
                                g.DrawImage(ic, new Rectangle(12 + i * cell, 12 + st * cell, size * zoom, size * zoom));
                    g.PixelOffsetMode = PixelOffsetMode.Default;
                    for (int i = 0; i < mods.Count; i++)
                    {
                        int h = popup.SetModule(mods[i]);
                        g.ResetTransform();
                        g.TranslateTransform(12 + i * (pw + 12), top);
                        g.SetClip(new Rectangle(0, 0, pw, h));
                        popup.PaintTo(g, pw, h);
                        g.ResetClip();
                    }
                    bmp.Save(path, ImageFormat.Png);
                }
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Per-monitor DPI aware, so Windows never bitmap-stretches the icons or the popup.
            bool pm = false;
            try { pm = Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            if (!pm) Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length >= 2 && args[0] == "--preview") { App.Preview(args[1], args.Length > 2 ? args[2] : null); return; }
            if (args.Length == 2 && args[0] == "--monitors")   // read-only: lists monitors and their inputs into a text file
            {
                DisplayModule d = new DisplayModule();
                d.ScanNow(true);
                File.WriteAllText(args[1], d.Describe());
                return;
            }
            bool created;
            using (Mutex mx = new Mutex(true, "WinStats.SingleInstance", out created))
            {
                if (!created) return;
                Application.Run(new App());
            }
        }
    }
}
