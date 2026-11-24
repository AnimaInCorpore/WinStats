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
        [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int n, uint dpi);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

        // Power plans (powrprof): active scheme, its processor settings and the Windows 11 power mode overlay.
        [DllImport("powrprof.dll")] public static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] public static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] public static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr setting, byte[] buf, ref uint size);
        [DllImport("powrprof.dll")] public static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] public static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] public static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] public static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] public static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
        [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr p);
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;   // SystemStatusFlag 1 = energy saver on
            public int BatteryLifeTime, BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);

        // DXGI, only to learn each graphics adapter's name, memory size and LUID (the id the GPU counters use).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DXGI_ADAPTER_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
        }
        // The Slot* methods only keep the COM vtable layout; they are never called.
        [ComImport, Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIFactory
        {
            void Slot0(); void Slot1(); void Slot2(); void Slot3();
            [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter adapter);
        }
        [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IDXGIAdapter
        {
            void Slot0(); void Slot1(); void Slot2(); void Slot3(); void Slot4();
            [PreserveSig] int GetDesc(out DXGI_ADAPTER_DESC desc);
        }
        [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(ref Guid iid, out IDXGIFactory factory);

        // GPU temperature from the graphics kernel (what Task Manager shows), opened by the adapter's LUID.
        [StructLayout(LayoutKind.Sequential)] public struct D3DKMT_OPENADAPTERFROMLUID { public uint LuidLow; public int LuidHigh; public uint hAdapter; }
        [StructLayout(LayoutKind.Sequential)] public struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pData; public uint Size; }
        [StructLayout(LayoutKind.Sequential)] public struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }
        [DllImport("gdi32.dll")] public static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID p);
        [DllImport("gdi32.dll")] public static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO p);
        [DllImport("gdi32.dll")] public static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER p);

        // Drive model and temperature via IOCTL_STORAGE_QUERY_PROPERTY; a handle without read/write access is enough.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(IntPtr h, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

        // Battery details (rate, voltage, capacities) through the battery class driver.
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll")] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid cls, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        // Battery level of Bluetooth devices: the device property Windows' own Bluetooth settings show.
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY { public Guid fmtid; public uint pid; public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; } }
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetAllDevices(IntPtr cls, string enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll")] public static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[] buffer, int size, out int required, int flags);

        // Paired classic Bluetooth devices with their connection state and name.
        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_DEVICE_SEARCH_PARAMS
        {
            public int dwSize;
            public int fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
            public byte cTimeoutMultiplier;
            public IntPtr hRadio;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct BLUETOOTH_DEVICE_INFO
        {
            public int dwSize;
            public ulong Address;
            public uint ulClassofDevice;
            public int fConnected, fRemembered, fAuthenticated;
            public long stLastSeen1, stLastSeen2, stLastUsed1, stLastUsed2;   // two SYSTEMTIMEs, unused
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
        }
        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS p, ref BLUETOOTH_DEVICE_INFO info);
        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern bool BluetoothFindNextDevice(IntPtr find, ref BLUETOOTH_DEVICE_INFO info);
        [DllImport("bthprops.cpl")] public static extern bool BluetoothFindDeviceClose(IntPtr find);

        // NVIDIA's management library, installed with the NVIDIA driver; the only driver-free source of GPU power in watts.
        [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] public static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] public static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);

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

        public static string Celsius(float c) { return c.ToString("0") + " °C"; }

        public static string Mhz(double mhz) { return mhz >= 1000 ? (mhz / 1000).ToString("0.00") + " GHz" : mhz.ToString("0") + " MHz"; }
    }

    static class Draw
    {
        public static int TrayStrokeWidth(int size)
        {
            return Math.Max(1, (int)Math.Round(size / 16f, MidpointRounding.AwayFromZero));
        }

        public static GraphicsPath RoundedBox(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Darken the accents on a light taskbar so thin strokes and small type stay visible.
        public static Color TrayAccent(Color accent, Color fg)
        {
            if (fg.GetBrightness() > 0.5f) return accent;
            return Color.FromArgb(accent.R * 2 / 3, accent.G * 2 / 3, accent.B * 2 / 3);
        }

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

        // Like Fit, with a half-size suffix raised to the top of the digits (a superscript "%").
        public static void FitWithSuperscript(Graphics g, string t, string sup, RectangleF r, Color c, bool bold)
        {
            if (string.IsNullOrEmpty(t)) return;
            FontStyle fs = bold ? FontStyle.Bold : FontStyle.Regular;
            const float ratio = 0.6f;
            float em = r.Height * 1.2f;
            using (StringFormat sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
                SizeF m, ms;
                float gap;
                using (Font f = new Font("Segoe UI", em, fs, GraphicsUnit.Pixel))
                using (Font fsup = new Font("Segoe UI", em * ratio, fs, GraphicsUnit.Pixel))
                {
                    m = g.MeasureString(t, f, 10000, sf); ms = g.MeasureString(sup, fsup, 10000, sf);
                }
                gap = em * 0.04f;
                float total = m.Width + gap + ms.Width;
                if (total > r.Width) { float k = r.Width / total; em *= k; m = new SizeF(m.Width * k, m.Height * k); ms = new SizeF(ms.Width * k, ms.Height * k); gap *= k; total = r.Width; }
                float x = r.X + (r.Width - total) / 2, y = r.Y + (r.Height - m.Height) / 2 - em * 0.06f;
                // Segoe UI digits start about 0.37 em below the top of the line; line both tops up.
                float ys = y + 0.37f * (em - em * ratio);
                using (Font f = new Font("Segoe UI", em, fs, GraphicsUnit.Pixel))
                using (Font fsup = new Font("Segoe UI", em * ratio, fs, GraphicsUnit.Pixel))
                using (SolidBrush b = new SolidBrush(c))
                {
                    g.DrawString(t, f, b, x, y, sf);
                    g.DrawString(sup, fsup, b, x + m.Width + gap, ys, sf);
                }
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

    // These values are saved in the registry. Append new styles to preserve existing settings.
    enum WidgetStyle
    {
        Mini, BarWithLetter, LineChart, BarWithSymbol, SymbolWithValue, SegmentedMeters, LoadRings, LargeReadings
    }

    abstract class Module
    {
        public string Name, Label;
        public Color Color;
        public List<float> History = new List<float>();
        public float HistoryMax = 100;      // 0 = scale to the largest visible value
        public float Value;                 // 0..1, used by the bar widget
        public string IconText = "", BigText = "", Tooltip = "";
        public string IconUnit = "";        // "%" when IconText is a percentage
        public static bool PercentSign;     // draw IconUnit as a small superscript next to the value
        public float[] Bars;                // optional row of small bars in the popup (CPU cores)
        public bool WantsProcesses;
        public NotifyIcon Tray;
        public IntPtr HIcon;

        // Optional choice of what the tray icon shows (index 0 is the default), offered under "Icon shows".
        public string[] Readings;
        public int Reading;

        // Puts a temperature on the icon; the bar and ring widgets scale it from 0 to 100 °C.
        protected void ShowTemperature(float c)
        {
            IconUnit = "";
            if (c <= 0) { Value = 0; IconText = "-"; BigText = "n/a"; Push(0); return; }
            Value = U.Clamp01(c / 100f);
            IconText = c.ToString("0") + "°"; BigText = U.Celsius(c);
            Push(c);
        }

        public virtual bool Available { get { return true; } }
        // A module that returns a menu here opens it on left-click instead of the details popup.
        public virtual ContextMenuStrip ClickMenu() { return null; }
        public abstract void Update(double dt);
        public abstract void Details(List<string[]> rows);   // { key, value }; value == null => section header
        // Special rows: { Note, text } wrapped dim text; { Hint, text } wrapped text after a pointing-up hand;
        // { Link, label, id } a clickable line that calls Action(id).
        public const string Note = "\u0001note", Hint = "\u0001hint", Link = "\u0001link";
        public virtual void Action(string id) { }

        protected void Push(float v)
        {
            History.Add(v);
            if (History.Count > 120) History.RemoveAt(0);
        }

        // Symbol for the bar and value widgets, drawn into the square r.
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
            using (GraphicsPath path = Draw.RoundedBox(o, p.Width)) g.DrawPath(p, path);
        }

        protected void DrawValue(Graphics g, RectangleF r, Color fg)
        {
            if (PercentSign && IconUnit.Length > 0) Draw.FitWithSuperscript(g, IconText, IconUnit, r, fg, true);
            else Draw.Fit(g, IconText, r, fg, true);
        }

        protected void PaintSymbol(Graphics g, RectangleF bounds, int size, Color fg)
        {
            using (Pen p = new Pen(fg, Draw.TrayStrokeWidth(size)))
            using (SolidBrush b = new SolidBrush(fg)) DrawSymbol(g, bounds, p, b);
        }

        void DrawSegmentedMeter(Graphics g, int s, Color fg)
        {
            int symbolSize = (int)Math.Round(s * 0.69f);
            PaintSymbol(g, new RectangleF((s - symbolSize) / 2, 0, symbolSize, symbolSize), s, fg);

            const int count = 5;
            int gap = Math.Max(1, s / 16);
            int width = (s - (count - 1) * gap - 2) / count;
            int x = (s - count * width - (count - 1) * gap) / 2;
            int height = Math.Max(2, s / 8);
            int filled = (int)Math.Round(count * U.Clamp01(Value), MidpointRounding.AwayFromZero);
            using (SolidBrush active = new SolidBrush(Draw.TrayAccent(Color, fg)))
            using (SolidBrush rest = new SolidBrush(Color.FromArgb(64, fg)))
                for (int i = 0; i < count; i++)
                    g.FillRectangle(i < filled ? active : rest, x + i * (width + gap), s - height - 1, width, height);
        }

        void DrawLoadRing(Graphics g, int s, Color fg)
        {
            int stroke = Draw.TrayStrokeWidth(s);
            float inset = 1 + stroke / 2f;
            RectangleF ring = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);
            float level = U.Clamp01(Value);
            using (Pen track = new Pen(Color.FromArgb(62, fg), stroke)) g.DrawEllipse(track, ring);
            if (level > 0)
            {
                using (Pen active = new Pen(Draw.TrayAccent(Color, fg), stroke))
                {
                    active.StartCap = active.EndCap = LineCap.Round;
                    if (level == 1) g.DrawEllipse(active, ring);
                    else g.DrawArc(active, ring, -90, 360 * level);
                }
            }

            int symbolSize = (int)Math.Round(s * 0.52f);
            int start = (s - symbolSize) / 2;
            PaintSymbol(g, new RectangleF(start, start, symbolSize, symbolSize), s, fg);
        }

        public virtual void DrawIcon(Graphics g, int s, Color fg, WidgetStyle style)
        {
            if (style == WidgetStyle.LineChart)
            {
                float baseline = s - 1.5f;
                Draw.Chart(g, new RectangleF(1, 2, s - 2, baseline - 2), History, HistoryMax,
                    Draw.TrayAccent(Color, fg), Math.Max(8, s / 2));
                using (Pen p = new Pen(Color.FromArgb(85, fg))) g.DrawLine(p, 1, baseline, s - 1, baseline);
            }
            else if (style == WidgetStyle.SymbolWithValue)
            {
                // Symbol on top, value below.
                float a = (float)Math.Round(s * 0.48f);
                PaintSymbol(g, new RectangleF((float)Math.Floor((s - a) / 2), 0, a, a), s, fg);
                float top = a + Math.Max(1, s / 16);
                DrawValue(g, new RectangleF(0, top, s, s - top), fg);
            }
            else if (style == WidgetStyle.BarWithLetter || style == WidgetStyle.BarWithSymbol)
            {
                float w = (float)Math.Round(s * 0.24f);
                float x = s - w - Math.Max(1, s / 16);
                if (style == WidgetStyle.BarWithSymbol)
                {
                    float a = x - Math.Max(1, s / 16);
                    PaintSymbol(g, new RectangleF(0, (float)Math.Floor((s - a) / 2), a, a), s, fg);
                }
                else Draw.Fit(g, Label.Substring(0, 1), new RectangleF(0, 0, x - 1, s), fg, true);

                float inset = Math.Max(1, (float)Math.Round(s / 8f));
                float trackHeight = s - inset * 2, bottom = s - inset;
                using (GraphicsPath track = Draw.RoundedBox(new RectangleF(x, inset, w, trackHeight), w / 2))
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(65, fg))) g.FillPath(b, track);
                    float fillHeight = trackHeight * U.Clamp01(Value);
                    if (fillHeight > 0)
                    {
                        fillHeight = Math.Max(1, (float)Math.Round(fillHeight));
                        GraphicsState state = g.Save();
                        g.SetClip(new RectangleF(x, bottom - fillHeight, w, fillHeight), CombineMode.Intersect);
                        using (SolidBrush b = new SolidBrush(Draw.TrayAccent(Color, fg))) g.FillPath(b, track);
                        g.Restore(state);
                    }
                }
            }
            else if (style == WidgetStyle.SegmentedMeters) DrawSegmentedMeter(g, s, fg);
            else if (style == WidgetStyle.LoadRings) DrawLoadRing(g, s, fg);
            else if (style == WidgetStyle.LargeReadings)
            {
                DrawValue(g, new RectangleF(0, 0, s, s * 0.68f), fg);
                Draw.Fit(g, Label, new RectangleF(1, s * 0.69f, s - 2, s * 0.3f), Draw.TrayAccent(Color, fg), true);
            }
            else
            {
                Draw.Fit(g, Label, new RectangleF(1, 0, s - 2, s * 0.39f), Color.FromArgb(185, fg), true);
                DrawValue(g, new RectangleF(1, s * 0.4f, s - 2, s * 0.6f), fg);
            }
        }
    }

    // Snapshot of the active power plan's processor settings and the power mode, plus hints when
    // a setting works against the user (e.g. a raised minimum processor state that keeps the CPU hot).
    class PowerInfo
    {
        static Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        static Guid ProcMin = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
        static Guid ProcMax = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
        public const int HighMin = 50;   // a minimum processor state from here on keeps the CPU from slowing down

        public Guid Scheme;
        public string Plan = "";
        public string ModeAc, ModeDc;    // null if unknown
        public bool OnAc = true, HasBattery;
        public int MinAc = -1, MinDc = -1, MaxAc = -1, MaxDc = -1;
        public int SaverAt = -1;         // battery level at which energy saver turns on: 0 = never, 100 = always
        public bool SaverOn;
        static Guid SubEnergySaver = new Guid("de830923-a562-41af-a086-e3a2c6bad2da");
        static Guid SaverThreshold = new Guid("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");

        public string SaverText
        {
            get
            {
                if (SaverOn) return "On";
                if (SaverAt == 0) return "Never turns on";
                if (SaverAt >= 100) return "Always on";
                return SaverAt > 0 ? "Turns on at " + SaverAt + "%" : null;
            }
        }

        public int Min { get { return OnAc ? MinAc : MinDc; } }
        public int Max { get { return OnAc ? MaxAc : MaxDc; } }
        public string Mode { get { return OnAc ? ModeAc : ModeDc; } }

        public static PowerInfo Read()
        {
            PowerInfo p = new PowerInfo();
            PowerStatus ps = SystemInformation.PowerStatus;
            p.OnAc = ps.PowerLineStatus != PowerLineStatus.Offline;
            p.HasBattery = (ps.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) == 0 && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown;
            IntPtr sp;
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out sp) != 0) return null;
            p.Scheme = (Guid)Marshal.PtrToStructure(sp, typeof(Guid));
            Native.LocalFree(sp);
            p.Plan = PlanName(p.Scheme);
            uint v;
            if (Native.PowerReadACValueIndex(IntPtr.Zero, ref p.Scheme, ref SubProcessor, ref ProcMin, out v) == 0) p.MinAc = (int)v;
            if (Native.PowerReadDCValueIndex(IntPtr.Zero, ref p.Scheme, ref SubProcessor, ref ProcMin, out v) == 0) p.MinDc = (int)v;
            if (Native.PowerReadACValueIndex(IntPtr.Zero, ref p.Scheme, ref SubProcessor, ref ProcMax, out v) == 0) p.MaxAc = (int)v;
            if (Native.PowerReadDCValueIndex(IntPtr.Zero, ref p.Scheme, ref SubProcessor, ref ProcMax, out v) == 0) p.MaxDc = (int)v;
            if (Native.PowerReadDCValueIndex(IntPtr.Zero, ref p.Scheme, ref SubEnergySaver, ref SaverThreshold, out v) == 0) p.SaverAt = (int)v;
            Native.SYSTEM_POWER_STATUS st;
            if (Native.GetSystemPowerStatus(out st)) p.SaverOn = (st.SystemStatusFlag & 1) != 0;
            // The power mode chosen in Settings is stored per power source; fall back to the effective one.
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes"))
                {
                    if (k != null)
                    {
                        object ac = k.GetValue("ActiveOverlayAcPowerScheme"), dc = k.GetValue("ActiveOverlayDcPowerScheme");
                        if (ac != null) p.ModeAc = ModeName(new Guid(ac.ToString()));
                        if (dc != null) p.ModeDc = ModeName(new Guid(dc.ToString()));
                    }
                }
            }
            catch { }
            if (p.Mode == null)
            {
                try
                {
                    Guid o;
                    if (Native.PowerGetEffectiveOverlayScheme(out o) == 0)
                    {
                        if (p.OnAc) p.ModeAc = ModeName(o); else p.ModeDc = ModeName(o);
                    }
                }
                catch { }
            }
            return p;
        }

        static string PlanName(Guid g)
        {
            switch (g.ToString())
            {
                case "381b4222-f694-41f0-9685-ff5bb260df2e": return "Balanced";
                case "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c": return "High performance";
                case "a1841308-3541-4fab-bc81-f71556f20b4a": return "Power saver";
                case "e9a42b02-d5df-448d-aa00-03f14749eb61": return "Ultimate performance";
            }
            try
            {
                uint size = 0;
                Native.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size);
                byte[] buf = new byte[size];
                if (size > 0 && Native.PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) == 0)
                    return Encoding.Unicode.GetString(buf).TrimEnd('\0');
            }
            catch { }
            return "Custom";
        }

        static string ModeName(Guid g)
        {
            switch (g.ToString())
            {
                case "961cc777-2547-4f9d-8174-7d86181b8a7a": return "Best power efficiency";
                case "00000000-0000-0000-0000-000000000000": return "Balanced";
                case "3af9b8d9-7c97-431d-ad78-34a8bfea439f": return "Better performance";
                case "ded574b5-45a0-4f42-8737-46345c09c238": return "Best performance";
            }
            return null;
        }

        public static string ModeNote(string mode)
        {
            switch (mode)
            {
                case "Best power efficiency": return "Lower clocks and less boost: the coolest and quietest mode with the longest battery life, but slower under load.";
                case "Balanced": return "Boosts under load and slows down when idle. A good default for most work.";
                case "Better performance": return "Boosts sooner than Balanced, still slows down when idle.";
                case "Best performance": return "Boosts highest and soonest: fastest, but warmest and loudest, and the battery drains fastest.";
            }
            return null;
        }

        // Hints for settings that work against the user, each with an optional fix { text, link label, action id }.
        public List<string[]> Hints(float cpuTemp)
        {
            List<string[]> h = new List<string[]>();
            // High and Ultimate performance keep the minimum at 100% by design; only flag it in other plans.
            bool fullSpeedPlan = Plan == "High performance" || Plan == "Ultimate performance";
            if (MinAc >= HighMin && !fullSpeedPlan)
                h.Add(new[] { "Minimum processor state is " + MinAc + "% when plugged in. The CPU cannot slow down when idle, so it stays hot and the fans keep spinning. Windows' default in the Balanced plan is 5%; laptop control apps sometimes raise it.", "Set to 5% when plugged in", "minac" });
            if (HasBattery && MinDc >= HighMin && !fullSpeedPlan)
                h.Add(new[] { "Minimum processor state is " + MinDc + "% on battery. The CPU cannot slow down when idle, which drains the battery. Windows' default in the Balanced plan is 5%.", "Set to 5% on battery", "mindc" });
            if (Max >= 0 && Max < 100)
                h.Add(new[] { "Maximum processor state is " + Max + "%" + (OnAc ? "" : " on battery") + ". This caps the clock speed: cooler and quieter, but noticeably slower.", "Set to 100%", OnAc ? "maxac" : "maxdc" });
            if (!OnAc && Mode == "Best performance")
                h.Add(new[] { "Best performance on battery drains it fastest. Balanced or Best power efficiency lasts much longer.", "Open power settings", "settings" });
            if (OnAc && Mode == "Best performance" && cpuTemp >= 90)
                h.Add(new[] { "The CPU is at " + cpuTemp.ToString("0") + " °C. Balanced runs cooler and quieter at slightly lower peak speed; the laptop's own control app may offer a quiet profile too.", "Open power settings", "settings" });
            return h;
        }

        // Battery popup: energy saver and the power mode on battery.
        public List<string[]> BatteryHints()
        {
            List<string[]> h = new List<string[]>();
            if (SaverAt == 0)
                h.Add(new[] { "Energy saver never turns on by itself, so a low battery runs at full speed until it is empty. Windows' default is to turn it on at 20% or 30%.", "Open energy saver settings", "saversettings" });
            if (!OnAc && ModeDc == "Best performance")
                h.Add(new[] { "Best performance on battery drains it fastest. Balanced or Best power efficiency lasts much longer.", "Open power settings", "settings" });
            return h;
        }

        public void Apply(string id)
        {
            if (id == "settings") { try { Process.Start("ms-settings:powersleep"); } catch { } return; }
            if (id == "saversettings") { try { Process.Start("ms-settings:batterysaver"); } catch { } return; }
            Guid s = Scheme;
            uint r = 1;
            if (id == "minac") r = Native.PowerWriteACValueIndex(IntPtr.Zero, ref s, ref SubProcessor, ref ProcMin, 5);
            if (id == "mindc") r = Native.PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref SubProcessor, ref ProcMin, 5);
            if (id == "maxac") r = Native.PowerWriteACValueIndex(IntPtr.Zero, ref s, ref SubProcessor, ref ProcMax, 100);
            if (id == "maxdc") r = Native.PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref SubProcessor, ref ProcMax, 100);
            if (r == 0) Native.PowerSetActiveScheme(IntPtr.Zero, ref s);   // re-apply so the change takes effect now
            else MessageBox.Show("Windows refused to change the setting (error " + r + ").", "WinStats", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    class CpuModule : Module
    {
        PowerInfo power;
        DateTime powerRead = DateTime.MinValue;
        PerformanceCounter total;
        PerformanceCounter[] cores;
        PerformanceCounter[] temps = new PerformanceCounter[0];
        float temp = -1;
        int loadPct;
        PerformanceCounter perf, baseFreq;   // clock speed = base frequency x "% Processor Performance", as in Task Manager
        float mhz, baseMhz;

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
            if (temps.Length > 0) Readings = new[] { "Load", "Temperature" };
            try
            {
                perf = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total");
                baseFreq = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total");
                perf.NextValue();
            }
            catch { perf = baseFreq = null; }
        }

        public override void Update(double dt)
        {
            Value = U.Clamp01(total.NextValue() / 100f);
            loadPct = (int)Math.Round(Value * 100);
            for (int i = 0; i < cores.Length; i++)
            {
                try { Bars[i] = U.Clamp01(cores[i].NextValue() / 100f); } catch { Bars[i] = 0; }
            }
            temp = -1;
            foreach (PerformanceCounter t in temps)
            {
                try { temp = Math.Max(temp, t.NextValue() - 273.15f); } catch { }
            }
            if (perf != null)
            {
                try { baseMhz = baseFreq.NextValue(); mhz = baseMhz * perf.NextValue() / 100f; } catch { mhz = 0; }
            }
            if ((DateTime.UtcNow - powerRead).TotalSeconds >= 10)
            {
                powerRead = DateTime.UtcNow;
                try { power = PowerInfo.Read(); } catch { power = null; }
            }
            int pct = loadPct;
            Tooltip = "CPU " + pct + "%" + (mhz > 0 ? ", " + U.Mhz(mhz) : "") + (temp > 0 ? ", " + U.Celsius(temp) : "")
                + (power != null && power.Hints(temp).Count > 0 ? "\n☝ Power hint: click for details" : "");
            if (Reading == 1) { ShowTemperature(temp); return; }
            IconText = pct.ToString(); IconUnit = "%"; BigText = pct + "%";
            Push(pct);
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            // Chip with pins.
            Outline(g, p, r, 0.22f, 0.22f, 0.56f, 0.56f);
            g.FillRectangle(b, Box(r, null, 0.38f, 0.38f, 0.24f, 0.24f));
            foreach (float t in new[] { 0.36f, 0.64f })
            {
                Line(g, p, r, t, 0.05f, t, 0.22f); Line(g, p, r, t, 0.78f, t, 0.95f);
                Line(g, p, r, 0.05f, t, 0.22f, t); Line(g, p, r, 0.78f, t, 0.95f, t);
            }
        }

        public override void Details(List<string[]> rows)
        {
            rows.Add(new[] { "Usage", loadPct + "%" });
            if (mhz > 0) rows.Add(new[] { "Speed", U.Mhz(mhz) + (baseMhz > 0 ? " (base " + U.Mhz(baseMhz) + ")" : "") });
            rows.Add(new[] { "Logical cores", Environment.ProcessorCount.ToString() });
            if (temp > 0) rows.Add(new[] { "Temperature (ACPI)", U.Celsius(temp) });
            rows.Add(new[] { "Uptime", U.Duration(Native.GetTickCount64() / 1000.0) });
            PowerInfo pw = power;
            if (pw != null)
            {
                rows.Add(new[] { "Power", null });
                rows.Add(new[] { "Power plan", pw.Plan });
                if (pw.ModeAc != null) rows.Add(new[] { "Plugged in" + (pw.OnAc ? " (now)" : ""), pw.ModeAc });
                if (pw.HasBattery && pw.ModeDc != null) rows.Add(new[] { "On battery" + (pw.OnAc ? "" : " (now)"), pw.ModeDc });
                if (pw.Min >= 0 && pw.Max >= 0) rows.Add(new[] { "Processor state", pw.Min + "% to " + pw.Max + "%" });
                string note = PowerInfo.ModeNote(pw.Mode);
                if (note != null) rows.Add(new[] { Note, note });
                foreach (string[] h in pw.Hints(temp))
                {
                    rows.Add(new[] { Hint, h[0] });
                    rows.Add(new[] { Link, h[1], h[2] });
                }
            }
            rows.Add(new[] { "Top processes", null });
            foreach (KeyValuePair<string, double> p in Procs.TopCpu) rows.Add(new[] { p.Key, p.Value.ToString("0.0") + "%" });
        }

        public override void Action(string id)
        {
            PowerInfo pw = power;
            if (pw == null) return;
            pw.Apply(id);
            try { power = PowerInfo.Read(); powerRead = DateTime.UtcNow; } catch { }
        }
    }

    // Per-app graphics preference (Settings → Display → Graphics), stored as "GpuPreference=n;" per exe path:
    // 0 = let Windows decide, 1 = power saving, 2 = high performance. On a laptop with two GPUs an everyday app
    // forced to high performance keeps the discrete GPU awake whenever it runs.
    static class GpuPrefs
    {
        const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
        static readonly Dictionary<string, string> Everyday = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "chrome", "Chrome" }, { "msedge", "Edge" }, { "firefox", "Firefox" }, { "brave", "Brave" }, { "opera", "Opera" },
            { "vivaldi", "Vivaldi" }, { "arc", "Arc" }, { "teams", "Teams" }, { "ms-teams", "Teams" }, { "discord", "Discord" },
            { "slack", "Slack" }, { "zoom", "Zoom" }, { "whatsapp", "WhatsApp" }, { "telegram", "Telegram" }, { "spotify", "Spotify" },
            { "code", "VS Code" }, { "outlook", "Outlook" }, { "olk", "Outlook" }, { "explorer", "Explorer" }, { "acrobat", "Acrobat" }
        };

        // Exe paths set to high performance.
        public static List<string> HighPerformance()
        {
            List<string> list = new List<string>();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k == null) return list;
                    foreach (string name in k.GetValueNames())
                    {
                        object v = k.GetValue(name);
                        if (v != null && Regex.IsMatch(v.ToString(), @"(^|;)GpuPreference=2;")) list.Add(name);
                    }
                }
            }
            catch { }
            return list;
        }

        // Everyday apps among them, with a display name.
        public static List<KeyValuePair<string, string>> EverydayOnHighPerformance()
        {
            List<KeyValuePair<string, string>> r = new List<KeyValuePair<string, string>>();
            foreach (string path in HighPerformance())
            {
                string n;
                if (Everyday.TryGetValue(Path.GetFileNameWithoutExtension(path), out n)) r.Add(new KeyValuePair<string, string>(path, n));
            }
            return r;
        }

        public static string Names(List<KeyValuePair<string, string>> apps)
        {
            List<string> n = apps.Select(a => a.Value).Distinct().ToList();
            if (n.Count == 1) return n[0];
            return string.Join(", ", n.Take(n.Count - 1).ToArray()) + " and " + n[n.Count - 1];
        }

        public static string Hint(List<KeyValuePair<string, string>> apps)
        {
            return Names(apps) + (apps.Select(a => a.Value).Distinct().Count() == 1 ? " is" : " are")
                + " set to always use the high-performance GPU. It cannot sleep while "
                + (apps.Count == 1 ? "it is" : "they are") + " open, which drains the battery and adds heat; "
                + "these apps run fine on the integrated GPU. The change applies when the app restarts.";
        }

        // Sets the everyday apps back to "Let Windows decide", keeping any other options in the value.
        public static void Reset()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key, true))
                {
                    if (k == null) return;
                    foreach (KeyValuePair<string, string> a in EverydayOnHighPerformance())
                    {
                        string v = k.GetValue(a.Key).ToString();
                        k.SetValue(a.Key, Regex.Replace(v, @"(^|;)GpuPreference=2;", "$1GpuPreference=0;"));
                    }
                }
            }
            catch { }
        }
    }

    class GpuModule : Module
    {
        PerformanceCounterCategory eng, mem;
        Dictionary<string, CounterSample> prev = new Dictionary<string, CounterSample>();
        SortedDictionary<string, float> util = new SortedDictionary<string, float>();
        SortedDictionary<string, long> used = new SortedDictionary<string, long>();
        SortedDictionary<string, float> temps = new SortedDictionary<string, float>();
        Dictionary<string, Perf> perf = new Dictionary<string, Perf>();
        class Adapter
        {
            public string Luid, Name;
            public long Vram;
            public bool Software;   // Microsoft Basic Render Driver: not a real GPU
        }

        List<Adapter> adapters = new List<Adapter>();
        DateTime adaptersRead;
        Adapter shown;

        // Per-app graphics preferences, read every 10 s; only a concern with an integrated and a discrete GPU.
        public static bool Hybrid;
        DateTime prefsRead = DateTime.MinValue;
        int highPerf;
        List<KeyValuePair<string, string>> everyday = new List<KeyValuePair<string, string>>();
        public List<KeyValuePair<string, string>> EverydayOnHighPerformance { get { return Hybrid ? everyday : new List<KeyValuePair<string, string>>(); } }

        // Name of the adapter the tray icon follows; empty = whichever is busiest.
        // Stored by name because Windows hands out new LUIDs on every boot.
        public string Selected = "";

        public GpuModule()
        {
            Name = "GPU"; Label = "GPU"; Color = Color.FromArgb(176, 124, 255);
            try
            {
                if (PerformanceCounterCategory.Exists("GPU Engine")) eng = new PerformanceCounterCategory("GPU Engine");
                if (PerformanceCounterCategory.Exists("GPU Adapter Memory")) mem = new PerformanceCounterCategory("GPU Adapter Memory");
            }
            catch { }
            ReadAdapters();
            Hybrid = adapters.Count(a => !a.Software) > 1;
            if (adapters.Any(a => !a.Software && Query(a.Luid).Temp > 0)) Readings = new[] { "Load", "Temperature" };
            try
            {
                if (Native.nvmlInit_v2() == 0)
                {
                    uint n;
                    if (Native.nvmlDeviceGetCount_v2(out n) == 0)
                        for (uint i = 0; i < n; i++)
                        {
                            IntPtr d;
                            StringBuilder sb = new StringBuilder(96);
                            if (Native.nvmlDeviceGetHandleByIndex_v2(i, out d) == 0 && Native.nvmlDeviceGetName(d, sb, 96) == 0)
                                nvidia[sb.ToString().Trim()] = d;
                        }
                }
            }
            catch { }   // no NVIDIA driver: nvml.dll is missing
        }

        public override bool Available { get { return eng != null; } }

        class Perf
        {
            public float Temp;               // °C
            public double Clock, MemClock;   // MHz
            public int FanRpm;
            public float Watts, WattsLimit;  // NVIDIA only
            public int FanPercent = -1;      // NVIDIA only
        }
        Dictionary<string, IntPtr> nvidia = new Dictionary<string, IntPtr>();   // NVML device by adapter name

        // What the graphics kernel reports for an adapter (WDDM 2.4+, the values Task Manager uses); zeros where unknown.
        static Perf Query(string luid)
        {
            Perf p = new Perf();
            try
            {
                string[] parts = luid.Split('_');
                Native.D3DKMT_OPENADAPTERFROMLUID open = new Native.D3DKMT_OPENADAPTERFROMLUID();
                open.LuidHigh = Convert.ToInt32(parts[0].Substring(2), 16);
                open.LuidLow = Convert.ToUInt32(parts[1].Substring(2), 16);
                if (Native.D3DKMTOpenAdapterFromLuid(ref open) != 0) return p;
                IntPtr buf = Marshal.AllocHGlobal(64);
                try
                {
                    for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(buf, i, 0);
                    Native.D3DKMT_QUERYADAPTERINFO q = new Native.D3DKMT_QUERYADAPTERINFO();
                    q.hAdapter = open.hAdapter; q.Type = 62; q.pData = buf; q.Size = 64;
                    // D3DKMT_ADAPTER_PERFDATA: MemoryFrequency (Hz) at 8, FanRPM at 48, Temperature (tenths of a degree) at 56.
                    if (Native.D3DKMTQueryAdapterInfo(ref q) == 0)
                    {
                        p.MemClock = Marshal.ReadInt64(buf, 8) / 1e6;
                        p.FanRpm = Marshal.ReadInt32(buf, 48);
                        p.Temp = Marshal.ReadInt32(buf, 56) / 10f;
                    }
                    // D3DKMT_NODE_PERFDATA per engine: Frequency (Hz) at 8; the fastest engine is the GPU clock.
                    q.Type = 61; q.Size = 56;
                    for (int node = 0; node < 16; node++)
                    {
                        for (int i = 0; i < 64; i += 4) Marshal.WriteInt32(buf, i, 0);
                        Marshal.WriteInt32(buf, 0, node);
                        if (Native.D3DKMTQueryAdapterInfo(ref q) != 0) break;
                        p.Clock = Math.Max(p.Clock, Marshal.ReadInt64(buf, 8) / 1e6);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                    Native.D3DKMT_CLOSEADAPTER close = new Native.D3DKMT_CLOSEADAPTER();
                    close.hAdapter = open.hAdapter;
                    Native.D3DKMTCloseAdapter(ref close);
                }
            }
            catch { }
            return p;
        }

        void AddNvidia(Adapter a, Perf p)
        {
            IntPtr d;
            if (a == null || !nvidia.TryGetValue(a.Name, out d)) return;
            uint mw, limit, fan;
            try
            {
                if (Native.nvmlDeviceGetPowerUsage(d, out mw) == 0) p.Watts = mw / 1000f;
                if (Native.nvmlDeviceGetEnforcedPowerLimit(d, out limit) == 0) p.WattsLimit = limit / 1000f;
                if (Native.nvmlDeviceGetFanSpeed(d, out fan) == 0) p.FanPercent = (int)fan;
            }
            catch { }
        }

        void ReadAdapters()
        {
            List<Adapter> list = new List<Adapter>();
            try
            {
                Guid iid = typeof(Native.IDXGIFactory).GUID;
                Native.IDXGIFactory factory;
                if (Native.CreateDXGIFactory1(ref iid, out factory) >= 0 && factory != null)
                {
                    for (uint i = 0; i < 16; i++)
                    {
                        Native.IDXGIAdapter a;
                        if (factory.EnumAdapters(i, out a) < 0 || a == null) break;
                        Native.DXGI_ADAPTER_DESC d;
                        if (a.GetDesc(out d) >= 0)
                        {
                            Adapter ad = new Adapter();
                            ad.Luid = ("0x" + d.LuidHigh.ToString("X8") + "_0x" + d.LuidLow.ToString("X8")).ToUpperInvariant();
                            ad.Name = (d.Description ?? "").Trim();
                            ad.Vram = (long)d.DedicatedVideoMemory.ToUInt64();
                            ad.Software = d.VendorId == 0x1414 && d.DeviceId == 0x8c;
                            int same = list.Count(x => x.Name == ad.Name || x.Name.StartsWith(ad.Name + " #"));
                            if (same > 0) ad.Name += " #" + (same + 1);   // two identical cards
                            if (!list.Any(x => x.Luid == ad.Luid)) list.Add(ad);
                        }
                        Marshal.ReleaseComObject(a);
                    }
                    Marshal.ReleaseComObject(factory);
                }
            }
            catch { }
            adapters = list;
            adaptersRead = DateTime.UtcNow;
        }

        Adapter Find(string luid) { return adapters.FirstOrDefault(a => a.Luid == luid); }

        // Names of the real GPUs, for the selection menu.
        public List<string> AdapterNames()
        {
            lock (App.Sync) return adapters.Where(a => !a.Software).Select(a => a.Name).ToList();
        }

        static string Luid(string instance)
        {
            int a = instance.IndexOf("luid_"), b = instance.IndexOf("_phys");
            return (a < 0 || b < a ? instance : instance.Substring(a + 5, b - a - 5)).ToUpperInvariant();
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
            // A LUID we have no name for means the adapters changed (driver restart, eGPU): read them again, but not constantly.
            if (u.Keys.Any(k => Find(k) == null) && (DateTime.UtcNow - adaptersRead).TotalSeconds > 30) ReadAdapters();

            SortedDictionary<string, float> t = new SortedDictionary<string, float>();
            Dictionary<string, Perf> pf = new Dictionary<string, Perf>();
            foreach (string luid in u.Keys)
            {
                Adapter a = Find(luid);
                if (a != null && a.Software) continue;
                Perf p = Query(luid);
                AddNvidia(a, p);
                pf[luid] = p;
                if (p.Temp > 0) t[luid] = p.Temp;
            }
            util = u; used = m; temps = t; perf = pf;

            // A chosen GPU shows its own values; otherwise the icon shows the busiest load or the highest temperature.
            shown = Selected.Length == 0 ? null : adapters.FirstOrDefault(a => a.Name == Selected);
            float load = 0, temp = 0;
            if (shown != null) { u.TryGetValue(shown.Luid, out load); t.TryGetValue(shown.Luid, out temp); }
            else
            {
                foreach (KeyValuePair<string, float> kv in u)
                {
                    Adapter a = Find(kv.Key);
                    if (a != null && a.Software) continue;
                    load = Math.Max(load, kv.Value);
                }
                if (t.Count > 0) temp = t.Values.Max();
            }
            if ((DateTime.UtcNow - prefsRead).TotalSeconds >= 10)
            {
                prefsRead = DateTime.UtcNow;
                Hybrid = adapters.Count(a => !a.Software) > 1;
                highPerf = GpuPrefs.HighPerformance().Count;
                everyday = GpuPrefs.EverydayOnHighPerformance();
            }
            int pct = (int)Math.Round(load);
            Tooltip = "GPU " + pct + "%" + (temp > 0 ? ", " + U.Celsius(temp) : "") + (shown != null ? " - " + shown.Name : "")
                + (Hybrid && everyday.Count > 0 ? "\n☝ Graphics hint" : "");
            if (Reading == 1) { ShowTemperature(temp); return; }
            Value = load / 100f;
            IconText = pct.ToString(); IconUnit = "%"; BigText = pct + "%";
            Push(pct);
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            // Graphics card with two fans and its slot connector.
            Outline(g, p, r, 0.05f, 0.19f, 0.9f, 0.52f);
            g.FillEllipse(b, Box(r, null, 0.2f, 0.35f, 0.2f, 0.2f));
            g.FillEllipse(b, Box(r, null, 0.6f, 0.35f, 0.2f, 0.2f));
            g.FillRectangle(b, Box(r, null, 0.23f, 0.71f, 0.37f, 0.16f));
        }

        public override void Details(List<string[]> rows)
        {
            rows.Add(new[] { "Icon shows", shown != null ? shown.Name : (Reading == 1 ? "Hottest GPU" : "Busiest GPU") });
            int i = 0;
            foreach (string luid in util.Keys.Union(used.Keys).Distinct().OrderBy(x => x))
            {
                Adapter a = Find(luid);
                if (a != null && a.Software) continue;
                float u; long m;
                util.TryGetValue(luid, out u);
                rows.Add(new[] { a != null ? a.Name : "Adapter " + i, null });
                rows.Add(new[] { "Load", u.ToString("0") + "%" });
                Perf p;
                if (!perf.TryGetValue(luid, out p)) p = new Perf();
                if (p.Clock > 0) rows.Add(new[] { "Clock", U.Mhz(p.Clock) });
                if (p.MemClock > 0) rows.Add(new[] { "Memory clock", U.Mhz(p.MemClock) });
                if (p.Temp > 0) rows.Add(new[] { "Temperature", U.Celsius(p.Temp) });
                if (p.Watts > 0) rows.Add(new[] { "Power", p.Watts.ToString("0.0") + " W" + (p.WattsLimit > 0 ? " of " + p.WattsLimit.ToString("0") + " W" : "") });
                if (p.FanPercent >= 0) rows.Add(new[] { "Fan", p.FanPercent + "%" + (p.FanRpm > 0 ? " (" + p.FanRpm + " rpm)" : "") });
                else if (p.FanRpm > 0) rows.Add(new[] { "Fan", p.FanRpm + " rpm" });
                if (used.TryGetValue(luid, out m))
                    rows.Add(new[] { "Dedicated memory", a != null && a.Vram > 0 ? U.Bytes(m) + " of " + U.Bytes(a.Vram) : U.Bytes(m) });
                i++;
            }
            if (Hybrid)
            {
                rows.Add(new[] { "Graphics preference", null });
                rows.Add(new[] { "Apps set to high performance", highPerf.ToString() });
                if (everyday.Count > 0)
                {
                    rows.Add(new[] { Hint, GpuPrefs.Hint(everyday) });
                    rows.Add(new[] { Link, "Let Windows decide for " + (everyday.Count == 1 ? "this app" : "these apps"), "gpureset" });
                }
                rows.Add(new[] { Link, "Open graphics settings", "gpusettings" });
            }
        }

        public override void Action(string id)
        {
            if (id == "gpusettings") { try { Process.Start("ms-settings:display-advancedgraphics"); } catch { } return; }
            if (id != "gpureset") return;
            GpuPrefs.Reset();
            highPerf = GpuPrefs.HighPerformance().Count;
            everyday = GpuPrefs.EverydayOnHighPerformance();
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
            IconText = pct.ToString(); IconUnit = "%"; BigText = pct + "%";
            Tooltip = "Memory " + pct + "% (" + U.Bytes(ms.ullTotalPhys - ms.ullAvailPhys) + " of " + U.Bytes(ms.ullTotalPhys) + ")";
            Push(pct);
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            // Memory module with contact pins.
            Outline(g, p, r, 0.05f, 0.17f, 0.9f, 0.36f);
            foreach (float t in new[] { 0.2f, 0.4f, 0.6f, 0.8f }) Line(g, p, r, t, 0.53f, t, 0.85f);
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
        PerformanceCounter read, write, idle;
        float r, w, active = -1, sysTemp;
        List<string[]> drives = new List<string[]>();
        List<string[]> drivesTemps = new List<string[]>();
        string sys = Path.GetPathRoot(Environment.SystemDirectory);
        int sysDisk = -1;                                   // physical disk number of the system drive
        List<int> disks = new List<int>();
        Dictionary<int, string> models = new Dictionary<int, string>();
        HashSet<int> noTemp = new HashSet<int>();           // drives that do not report a temperature (most USB drives)

        public DiskModule()
        {
            Name = "Disk"; Label = "SSD"; Color = Color.FromArgb(255, 159, 10); HistoryMax = 0;
            try
            {
                read = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                write = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                // Instances are named like "0 C:" - disk number, then its drive letters.
                string letter = sys.TrimEnd('\\');
                string inst = "_Total";
                foreach (string n in new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames())
                {
                    int num;
                    if (!int.TryParse(n.Split(' ')[0], out num)) continue;
                    disks.Add(num);
                    if (n.Split(' ').Skip(1).Any(x => string.Equals(x, letter, StringComparison.OrdinalIgnoreCase))) { inst = n; sysDisk = num; }
                }
                disks.Sort();
                // "Active time" as in Task Manager: the share of time the disk was busy.
                idle = new PerformanceCounter("PhysicalDisk", "% Idle Time", inst);
                idle.NextValue();
            }
            catch { read = write = idle = null; }
            Readings = sysDisk >= 0 && DriveTemperature(sysDisk) > 0
                ? new[] { "Used space", "Active time", "Temperature" } : new[] { "Used space", "Active time" };
        }

        // Temperature of \\.\PhysicalDriveN in °C (NVMe and most internal SATA drives); 0 if unknown.
        float DriveTemperature(int n)
        {
            if (noTemp.Contains(n)) return 0;
            IntPtr h = Native.CreateFile(@"\\.\PhysicalDrive" + n, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) { noTemp.Add(n); return 0; }
            try
            {
                byte[] query = new byte[12], o = new byte[1024];   // STORAGE_PROPERTY_QUERY
                int ret;
                if (!models.ContainsKey(n))
                {
                    // StorageDeviceProperty: the product id is at the offset stored at byte 16.
                    string model = "Disk " + n;
                    if (Native.DeviceIoControl(h, 0x2D1400, query, query.Length, o, o.Length, out ret, IntPtr.Zero))
                    {
                        int off = BitConverter.ToInt32(o, 16), end = off;
                        while (off > 0 && end < ret && o[end] != 0) end++;
                        if (off > 0 && end > off) model = Encoding.ASCII.GetString(o, off, end - off).Trim();
                    }
                    models[n] = model;
                }
                BitConverter.GetBytes(52).CopyTo(query, 0);   // StorageDeviceTemperatureProperty
                Array.Clear(o, 0, o.Length);
                // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: InfoCount at 12, first sensor's Temperature at 24 + 2.
                if (!Native.DeviceIoControl(h, 0x2D1400, query, query.Length, o, o.Length, out ret, IntPtr.Zero)
                    || ret < 28 || BitConverter.ToUInt16(o, 12) == 0) { noTemp.Add(n); return 0; }
                short c = BitConverter.ToInt16(o, 26);
                return c > 0 && c < 150 ? c : 0;
            }
            finally { Native.CloseHandle(h); }
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
                try { active = Math.Max(0, Math.Min(100, 100 - idle.NextValue())); } catch { active = -1; }
            }
            List<string[]> t = new List<string[]>();
            sysTemp = 0;
            foreach (int n in disks)
            {
                float c = DriveTemperature(n);
                if (c <= 0) continue;
                t.Add(new[] { models[n], U.Celsius(c) });
                if (n == sysDisk) sysTemp = c;
            }
            drivesTemps = t;

            int pct = (int)Math.Round(Value * 100);
            Tooltip = "Disk " + sys + " " + pct + "% used" + (active >= 0 ? ", " + active.ToString("0") + "% active" : "")
                + (sysTemp > 0 ? ", " + U.Celsius(sysTemp) : "");
            HistoryMax = Reading == 0 ? 0 : 100;
            if (Reading == 2) { ShowTemperature(sysTemp); return; }
            if (Reading == 1)
            {
                float a = Math.Max(0, active);
                Value = a / 100f;
                IconText = a.ToString("0"); IconUnit = "%"; BigText = a.ToString("0") + "%";
                Push(a);
                return;
            }
            IconText = pct.ToString(); IconUnit = "%"; BigText = pct + "%";
            Push(r + w);
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            RectangleF top = Box(r, p, 0.17f, 0.08f, 0.66f, 0.34f);
            RectangleF bottom = Box(r, p, 0.17f, 0.58f, 0.66f, 0.34f);
            g.DrawEllipse(p, top);
            g.DrawArc(p, bottom, 0, 180);
            g.DrawLine(p, top.Left, top.Top + top.Height / 2, bottom.Left, bottom.Top + bottom.Height / 2);
            g.DrawLine(p, top.Right, top.Top + top.Height / 2, bottom.Right, bottom.Top + bottom.Height / 2);
        }

        public override void Details(List<string[]> rows)
        {
            rows.Add(new[] { "Read", U.Bytes(r) + "/s" });
            rows.Add(new[] { "Write", U.Bytes(w) + "/s" });
            if (active >= 0) rows.Add(new[] { "Active time " + sys.TrimEnd('\\'), active.ToString("0") + "%" });
            rows.Add(new[] { "Drives", null });
            rows.AddRange(drives);
            if (drivesTemps.Count > 0)
            {
                rows.Add(new[] { "Temperatures", null });
                rows.AddRange(drivesTemps);
            }
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

        public override void DrawIcon(Graphics g, int s, Color fg, WidgetStyle style)
        {
            if (style == WidgetStyle.LineChart) { base.DrawIcon(g, s, fg, style); return; }
            float row = s / 2f;
            DrawSpeed(g, 0, s, U.Short(up), Draw.TrayAccent(Up, fg), true);
            DrawSpeed(g, row, s, U.Short(down), Draw.TrayAccent(Down, fg), false);
        }

        static void DrawSpeed(Graphics g, float y, int size, string speed, Color color, bool upload)
        {
            float rowHeight = size / 2f;
            float width = (float)Math.Round(size * 3 / 16f), height = (float)Math.Round(size * 5 / 16f);
            float stemWidth = Draw.TrayStrokeWidth(size);
            float stemX = (float)Math.Floor((width - stemWidth) / 2);
            float stemHeight = (float)Math.Round(height * 0.4f);
            float top = (float)Math.Floor(y + (rowHeight - height) / 2);
            float bottom = top + height;
            float tip = upload ? top : bottom;
            float shoulder = upload ? bottom - stemHeight : top + stemHeight;
            using (SolidBrush b = new SolidBrush(color))
            {
                g.FillPolygon(b, new[] { new PointF(width / 2, tip), new PointF(0, shoulder), new PointF(width, shoulder) });
                g.FillRectangle(b, stemX, upload ? shoulder : top, stemWidth, stemHeight);
            }
            float textLeft = width + Math.Max(1, size / 16);
            Draw.Fit(g, speed, new RectangleF(textLeft, y, size - textLeft, rowHeight), color, true);
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

        // From the battery driver (first battery only); 0 / int.MinValue where it does not say.
        string path;
        bool relative;                       // capacities in relative units instead of mWh
        int rateMw = int.MinValue, voltageMv;
        long capacity, full, design;
        int cycles;

        static string FindBattery()
        {
            Guid cls = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a");   // GUID_DEVCLASS_BATTERY
            IntPtr set = Native.SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, 0x12);   // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
            if (set == new IntPtr(-1)) return null;
            try
            {
                Native.SP_DEVICE_INTERFACE_DATA data = new Native.SP_DEVICE_INTERFACE_DATA();
                data.cbSize = Marshal.SizeOf(data);
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref cls, 0, ref data)) return null;
                int size;
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);   // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out size, IntPtr.Zero)) return null;
                    return Marshal.PtrToStringUni(new IntPtr(detail.ToInt64() + 4));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
        }

        void ReadDriver()
        {
            if (path == null) path = FindBattery() ?? "";
            if (path.Length == 0) return;
            IntPtr h = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1)) return;
            try
            {
                byte[] tag = new byte[4], none = new byte[4];
                int ret;
                if (!Native.DeviceIoControl(h, 0x294040, none, 4, tag, 4, out ret, IntPtr.Zero)) return;   // IOCTL_BATTERY_QUERY_TAG
                // BATTERY_QUERY_INFORMATION { tag, BatteryInformation, 0 } -> BATTERY_INFORMATION
                byte[] q = new byte[12], info = new byte[36];
                Array.Copy(tag, q, 4);
                if (Native.DeviceIoControl(h, 0x294044, q, q.Length, info, info.Length, out ret, IntPtr.Zero))
                {
                    relative = (BitConverter.ToUInt32(info, 0) & 0x40000000) != 0;
                    design = BitConverter.ToUInt32(info, 12);
                    full = BitConverter.ToUInt32(info, 16);
                    cycles = (int)BitConverter.ToUInt32(info, 32);
                }
                // BATTERY_WAIT_STATUS { tag, timeout, ... } -> BATTERY_STATUS { state, capacity, voltage, rate }
                byte[] w = new byte[20], st = new byte[16];
                Array.Copy(tag, w, 4);
                if (Native.DeviceIoControl(h, 0x29404C, w, w.Length, st, st.Length, out ret, IntPtr.Zero))
                {
                    capacity = BitConverter.ToUInt32(st, 4);
                    voltageMv = (int)BitConverter.ToUInt32(st, 8);
                    rateMw = BitConverter.ToInt32(st, 12);   // negative while discharging; 0x80000000 = unknown
                }
            }
            catch { }
            finally { Native.CloseHandle(h); }
        }

        static string Wh(long mwh) { return (mwh / 1000.0).ToString("0.0") + " Wh"; }

        string Rate
        {
            get
            {
                if (rateMw == int.MinValue || rateMw == 0 || relative) return null;
                return (rateMw > 0 ? "+" : "−") + (Math.Abs(rateMw) / 1000.0).ToString("0.0") + " W";
            }
        }

        // Energy saver, power mode on battery and apps that keep the discrete GPU awake, read every 10 s.
        PowerInfo power;
        DateTime powerRead = DateTime.MinValue;
        List<KeyValuePair<string, string>> everyday = new List<KeyValuePair<string, string>>();

        List<string[]> Hints()
        {
            List<string[]> h = power != null ? power.BatteryHints() : new List<string[]>();
            if (!Plugged && everyday.Count > 0)
                h.Add(new[] { GpuPrefs.Hint(everyday), "Let Windows decide for " + (everyday.Count == 1 ? "this app" : "these apps"), "gpureset" });
            return h;
        }

        public override void Update(double dt)
        {
            ReadDriver();
            if ((DateTime.UtcNow - powerRead).TotalSeconds >= 10)
            {
                powerRead = DateTime.UtcNow;
                try { power = PowerInfo.Read(); } catch { power = null; }
                everyday = GpuModule.Hybrid ? GpuPrefs.EverydayOnHighPerformance() : new List<KeyValuePair<string, string>>();
            }
            Value = U.Clamp01(ps.BatteryLifePercent);
            int pct = (int)Math.Round(Value * 100);
            Color = Plugged ? Color.FromArgb(52, 199, 89) : (pct <= 20 ? Color.FromArgb(255, 90, 82) : Color.FromArgb(255, 204, 0));
            IconText = pct.ToString(); IconUnit = "%"; BigText = pct + "%";
            Tooltip = "Battery " + pct + "%" + (Rate != null ? ", " + Rate : "") + (Plugged ? " (plugged in)" : "")
                + (Hints().Count > 0 ? "\n☝ Battery hint" : "");
            Push(pct);
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            Outline(g, p, r, 0.05f, 0.20f, 0.76f, 0.60f);
            g.FillRectangle(b, Box(r, null, 0.81f, 0.36f, 0.16f, 0.28f));
            if (Plugged)
            {
                PointF[] bolt = {
                    new PointF(0.49f, 0.28f), new PointF(0.25f, 0.55f), new PointF(0.42f, 0.55f),
                    new PointF(0.36f, 0.72f), new PointF(0.62f, 0.44f), new PointF(0.45f, 0.44f)
                };
                for (int i = 0; i < bolt.Length; i++)
                    bolt[i] = new PointF(r.X + bolt[i].X * r.Width, r.Y + bolt[i].Y * r.Height);
                g.FillPolygon(b, bolt);
            }
        }

        public override void Details(List<string[]> rows)
        {
            bool charging = (ps.BatteryChargeStatus & BatteryChargeStatus.Charging) != 0;
            rows.Add(new[] { "Level", BigText });
            rows.Add(new[] { "Power source", Plugged ? "AC adapter" : "Battery" });
            rows.Add(new[] { "State", charging ? "Charging" : (Plugged ? "Not charging" : "Discharging") });
            if (!Plugged && ps.BatteryLifeRemaining > 0) rows.Add(new[] { "Time remaining", U.Duration(ps.BatteryLifeRemaining) });
            if (Rate != null) rows.Add(new[] { rateMw > 0 ? "Into battery" : "Power draw", Rate.Substring(1) });
            if (!relative && full > 0 && capacity > 0) rows.Add(new[] { "Energy", Wh(capacity) + " of " + Wh(full) });
            if (!relative && design > 0 && full > 0)
                rows.Add(new[] { "Health", Math.Round(100.0 * full / design) + "% of " + Wh(design) });
            if (voltageMv > 0) rows.Add(new[] { "Voltage", (voltageMv / 1000.0).ToString("0.0") + " V" });
            if (cycles > 0) rows.Add(new[] { "Cycle count", cycles.ToString() });
            PowerInfo pw = power;
            if (pw != null)
            {
                rows.Add(new[] { "Power", null });
                if (pw.SaverText != null) rows.Add(new[] { "Energy saver", pw.SaverText });
                if (pw.ModeDc != null) rows.Add(new[] { "Power mode on battery", pw.ModeDc });
            }
            foreach (string[] h in Hints())
            {
                rows.Add(new[] { Hint, h[0] });
                rows.Add(new[] { Link, h[1], h[2] });
            }
        }

        public override void Action(string id)
        {
            if (id == "gpureset") GpuPrefs.Reset();
            else if (power != null) power.Apply(id);
            try { power = PowerInfo.Read(); } catch { }
            everyday = GpuModule.Hybrid ? GpuPrefs.EverydayOnHighPerformance() : new List<KeyValuePair<string, string>>();
            powerRead = DateTime.UtcNow;
        }
    }

    // Battery levels of connected Bluetooth devices (headsets, earbuds) as Windows reports them.
    class BluetoothModule : Module
    {
        class Device
        {
            public string Name;
            public int Level = -1;      // percent
            public bool Connected;
        }

        static Native.DEVPROPKEY batteryKey = new Native.DEVPROPKEY("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        static Native.DEVPROPKEY nameKey = new Native.DEVPROPKEY("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);   // FriendlyName
        static readonly Regex AddressPattern = new Regex(@"[&_]([0-9A-F]{12})(_|\\|$)", RegexOptions.IgnoreCase);

        List<Device> devices = new List<Device>();
        DateTime scanned = DateTime.MinValue;

        public BluetoothModule()
        {
            Name = "Bluetooth"; Label = "BT"; Color = Color.FromArgb(10, 132, 255);
            Scan();
        }

        // Shown only when at least one paired device has ever reported a battery level.
        public override bool Available { get { return devices.Count > 0; } }

        // Every paired device that has a battery level, one entry per Bluetooth address.
        void Scan()
        {
            Dictionary<ulong, Device> found = new Dictionary<ulong, Device>();
            IntPtr set = Native.SetupDiGetAllDevices(IntPtr.Zero, null, IntPtr.Zero, 0x6);   // DIGCF_PRESENT | DIGCF_ALLCLASSES
            if (set == new IntPtr(-1)) return;
            try
            {
                Native.SP_DEVINFO_DATA data = new Native.SP_DEVINFO_DATA();
                data.cbSize = Marshal.SizeOf(data);
                StringBuilder id = new StringBuilder(512);
                byte[] buf = new byte[512];
                for (int i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    int req;
                    uint type;
                    if (!Native.SetupDiGetDeviceInstanceId(set, ref data, id, id.Capacity, out req)) continue;
                    string inst = id.ToString();
                    if (!inst.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Native.SetupDiGetDevicePropertyW(set, ref data, ref batteryKey, out type, buf, buf.Length, out req, 0) || req < 1) continue;
                    int level = buf[0];
                    Match m = AddressPattern.Match(inst);
                    if (!m.Success || level > 100) continue;
                    ulong addr = Convert.ToUInt64(m.Groups[1].Value, 16);
                    string name = "";
                    if (Native.SetupDiGetDevicePropertyW(set, ref data, ref nameKey, out type, buf, buf.Length, out req, 0) && req > 2)
                        name = Encoding.Unicode.GetString(buf, 0, req - 2);
                    if (!found.ContainsKey(addr))
                    {
                        Device d = new Device();
                        d.Name = Regex.Replace(name, @"\s+(Hands-Free( AG)?|Stereo|Avrcp.*)$", "", RegexOptions.IgnoreCase).Trim();
                        d.Level = level;
                        d.Connected = true;   // assumed for devices the classic Bluetooth API does not list (Bluetooth LE)
                        found[addr] = d;
                    }
                }
            }
            catch { }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }

            // The battery property keeps its last value after a device disconnects, so ask the Bluetooth stack who is connected.
            try
            {
                Native.BLUETOOTH_DEVICE_SEARCH_PARAMS p = new Native.BLUETOOTH_DEVICE_SEARCH_PARAMS();
                p.dwSize = Marshal.SizeOf(p);
                p.fReturnAuthenticated = p.fReturnRemembered = p.fReturnConnected = 1;
                Native.BLUETOOTH_DEVICE_INFO info = new Native.BLUETOOTH_DEVICE_INFO();
                info.dwSize = Marshal.SizeOf(info);
                IntPtr find = Native.BluetoothFindFirstDevice(ref p, ref info);
                if (find != IntPtr.Zero)
                {
                    do
                    {
                        Device d;
                        if (found.TryGetValue(info.Address, out d))
                        {
                            d.Connected = info.fConnected != 0;
                            if (!string.IsNullOrEmpty(info.szName)) d.Name = info.szName;
                        }
                        info.dwSize = Marshal.SizeOf(info);
                    } while (Native.BluetoothFindNextDevice(find, ref info));
                    Native.BluetoothFindDeviceClose(find);
                }
            }
            catch { }   // no Bluetooth radio

            devices = found.Values.OrderBy(d => d.Name).ToList();
            scanned = DateTime.UtcNow;
        }

        public override void Update(double dt)
        {
            if ((DateTime.UtcNow - scanned).TotalSeconds >= 10) Scan();   // levels change slowly; a scan walks all devices
            List<Device> on = devices.Where(d => d.Connected).ToList();
            if (on.Count == 0)
            {
                Value = 0; IconText = "–"; IconUnit = ""; BigText = "–";
                Tooltip = "Bluetooth: no device connected";
                Push(0);
                return;
            }
            // The icon shows the emptiest connected device.
            Device low = on.OrderBy(d => d.Level).First();
            Value = low.Level / 100f;
            IconText = low.Level.ToString(); IconUnit = "%"; BigText = low.Level + "%";
            Tooltip = "Bluetooth: " + string.Join(", ", on.Select(d => d.Name + " " + d.Level + "%"));
            Push(low.Level);
        }

        protected override void DrawSymbol(Graphics g, RectangleF r, Pen p, Brush b)
        {
            // The Bluetooth rune.
            PointF[] rune = {
                new PointF(0.28f, 0.30f), new PointF(0.72f, 0.70f), new PointF(0.50f, 0.90f), new PointF(0.50f, 0.10f),
                new PointF(0.72f, 0.30f), new PointF(0.28f, 0.70f)
            };
            for (int i = 0; i < rune.Length; i++) rune[i] = new PointF(r.X + rune[i].X * r.Width, r.Y + rune[i].Y * r.Height);
            using (Pen q = (Pen)p.Clone())
            {
                q.LineJoin = LineJoin.Round; q.StartCap = q.EndCap = LineCap.Round;
                g.DrawLines(q, rune);
            }
        }

        public override void Details(List<string[]> rows)
        {
            foreach (Device d in devices)
                rows.Add(new[] { d.Name, d.Connected ? d.Level + "%" : "Not connected" });
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
            Outline(g, p, r, 0.04f, 0.09f, 0.92f, 0.64f);
            Line(g, p, r, 0.5f, 0.73f, 0.5f, 0.9f);
            Line(g, p, r, 0.25f, 0.9f, 0.75f, 0.9f);
        }

        public override void DrawIcon(Graphics g, int s, Color fg, WidgetStyle style)
        {
            if (style == WidgetStyle.Mini || style == WidgetStyle.SymbolWithValue) { base.DrawIcon(g, s, fg, style); return; }
            // No level to chart: a monitor with the active input written on its screen.
            PaintSymbol(g, new RectangleF(0, 0, s, s), s, fg);
            DrawValue(g, new RectangleF(s * 0.12f, s * 0.15f, s * 0.76f, s * 0.52f), fg);
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
            using (Bitmap bmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bmp))
            using (Font norm = NormFont())
            {
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                foreach (string[] r in rows)
                {
                    if (r[0] == Module.Note || r[0] == Module.Hint) h += TextHeight(g, norm, r) / S;
                    else h += r[1] == null ? 26 : 20;
                }
            }
            return (int)(h * S);
        }

        Font NormFont() { return new Font("Segoe UI", 9 * S * 96f / 72f, FontStyle.Regular, GraphicsUnit.Pixel); }

        // Notes and hints wrap; hints leave room on the left for the hand.
        float TextLeft(string[] r) { return r[0] == Module.Hint ? 22 * S : 0; }

        float TextHeight(Graphics g, Font f, string[] r)
        {
            float cw = W - 28 * S - TextLeft(r);
            return (float)Math.Ceiling(g.MeasureString(r[1], f, (int)cw).Height) + 6 * S;
        }

        List<KeyValuePair<RectangleF, string>> links = new List<KeyValuePair<RectangleF, string>>();

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Cursor = links.Any(l => l.Key.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            foreach (KeyValuePair<RectangleF, string> l in links.ToArray())
            {
                if (!l.Key.Contains(e.Location)) continue;
                lock (App.Sync) { try { M.Action(l.Value); } catch { } }
                if (Visible) RefreshData();
                return;
            }
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

                links.Clear();
                Color hand = light ? Color.FromArgb(196, 120, 0) : Color.FromArgb(255, 185, 40);
                foreach (string[] r in rows)
                {
                    if (r[0] == Module.Note || r[0] == Module.Hint)
                    {
                        float th = TextHeight(g, norm, r), left = TextLeft(r);
                        RectangleF tr = new RectangleF(p + left, y + 3 * S, cw - left, th);
                        if (r[0] == Module.Hint)
                        {
                            using (Font sym = new Font("Segoe UI Symbol", 11 * pt, FontStyle.Regular, GraphicsUnit.Pixel))
                            using (SolidBrush bHand = new SolidBrush(hand))
                                g.DrawString("☝", sym, bHand, p - 2 * S, y);
                        }
                        g.DrawString(r[1], norm, r[0] == Module.Hint ? bFg : bDim, tr);
                        y += th;
                    }
                    else if (r[0] == Module.Link)
                    {
                        float left = 22 * S;
                        SizeF sz = g.MeasureString(r[1], norm);
                        RectangleF lr = new RectangleF(p + left, y, Math.Min(sz.Width, cw - left), 20 * S);
                        using (Font ul = new Font(norm, FontStyle.Underline))
                            g.DrawString(r[1], ul, bCol, lr, near);
                        if (r.Length > 2 && r[2] != null) links.Add(new KeyValuePair<RectangleF, string>(lr, r[2]));
                        y += 20 * S;
                    }
                    else if (r[1] == null)
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
        const int SmallIconWidth = 49;   // SM_CXSMICON
        static readonly string[] Styles = {
            "Mini (label + value)", "Bar with letter", "Line chart", "Bar with symbol", "Symbol with value",
            "Segmented meters", "Load rings", "Large readings"
        };

        List<Module> mods;
        HashSet<string> disabled = new HashSet<string>();
        WidgetStyle style;
        int interval = 1;
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
                () => new DiskModule(), () => new NetModule(), () => new BatteryModule(), () => new BluetoothModule(), () => new DisplayModule()
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
                    d = (int)Native.GetDpiForSystem();
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
                int dpi = Dpi;
                try
                {
                    int size = Native.GetSystemMetricsForDpi(SmallIconWidth, (uint)dpi);
                    if (size > 0) return size;
                }
                catch (EntryPointNotFoundException) { return Math.Max(16, Native.GetSystemMetrics(SmallIconWidth)); }
                return Math.Max(16, (int)Math.Round(16 * dpi / 96f));
            }
        }

        static Bitmap Render(Module m, int size, Color fg, WidgetStyle style)
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
                style = (WidgetStyle)Math.Max(0, Math.Min(Styles.Length - 1, Convert.ToInt32(k.GetValue("Style", 0))));
                interval = Math.Max(1, Math.Min(10, Convert.ToInt32(k.GetValue("Interval", 1))));
                Module.PercentSign = Convert.ToInt32(k.GetValue("PercentSign", 0)) != 0;
            }
            mods = CreateModules();
            GpuModule gpu = mods.OfType<GpuModule>().FirstOrDefault();
            if (gpu != null)
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key)) gpu.Selected = Convert.ToString(k.GetValue("Gpu", ""));
            }
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                foreach (Module m in mods.Where(x => x.Readings != null))
                    m.Reading = Math.Max(0, Math.Min(m.Readings.Length - 1, Convert.ToInt32(k.GetValue("Reading." + m.Name, 0))));
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
                k.SetValue("Style", (int)style);
                k.SetValue("Interval", interval);
                k.SetValue("PercentSign", Module.PercentSign ? 1 : 0);
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
                WidgetStyle option = (WidgetStyle)i;
                widget.DropDownItems.Add(new ToolStripMenuItem(Styles[i], null, delegate { style = option; Save(); RefreshUi(); }));
            }
            widget.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem percent = new ToolStripMenuItem("Small % sign", null, delegate { Module.PercentSign = !Module.PercentSign; Save(); RefreshUi(); });
            widget.DropDownItems.Add(percent);

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

            // Which graphics adapter the GPU icon follows; the entries are filled in when the menu opens.
            GpuModule gpu = mods.OfType<GpuModule>().FirstOrDefault();
            ToolStripMenuItem gpuMenu = new ToolStripMenuItem("GPU shown");
            Action fillGpu = delegate
            {
                gpuMenu.DropDownItems.Clear();
                List<string> choices = new List<string>();
                choices.Add("");
                choices.AddRange(gpu.AdapterNames());
                foreach (string c in choices)
                {
                    string name = c;
                    ToolStripMenuItem it = new ToolStripMenuItem(name.Length == 0 ? "Busiest (automatic)" : name);
                    it.Checked = gpu.Selected == name;
                    it.Click += delegate
                    {
                        gpu.Selected = name;
                        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Gpu", name);
                    };
                    gpuMenu.DropDownItems.Add(it);
                }
            };

            // What each icon shows, for modules that offer a choice (e.g. CPU load or temperature).
            ToolStripMenuItem shows = new ToolStripMenuItem("Icon shows");
            foreach (Module m in mods.Where(x => x.Readings != null))
            {
                Module mod = m;
                ToolStripMenuItem sub = new ToolStripMenuItem(mod.Name);
                for (int i = 0; i < mod.Readings.Length; i++)
                {
                    int n = i;
                    ToolStripMenuItem it = new ToolStripMenuItem(mod.Readings[i]);
                    it.Click += delegate
                    {
                        lock (Sync)
                        {
                            mod.Reading = n;
                            mod.History.Clear();   // the old values are in another unit
                            try { mod.Update(0); } catch { }
                        }
                        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Reading." + mod.Name, n);
                        RefreshUi();
                    };
                    sub.DropDownItems.Add(it);
                }
                sub.DropDownOpening += delegate
                {
                    for (int i = 0; i < sub.DropDownItems.Count; i++) ((ToolStripMenuItem)sub.DropDownItems[i]).Checked = i == mod.Reading;
                };
                shows.DropDownItems.Add(sub);
            }

            menu.Items.Add(modules);
            menu.Items.Add(widget);
            if (shows.DropDownItems.Count > 0) menu.Items.Add(shows);
            if (gpu != null) { fillGpu(); menu.Items.Add(gpuMenu); }
            menu.Items.Add(rate);
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Task Manager", null, delegate { try { Process.Start("taskmgr.exe"); } catch { } });
            menu.Items.Add("Quit WinStats", null, delegate { Quit(); });
            menu.Opening += delegate
            {
                foreach (ToolStripMenuItem it in modules.DropDownItems) it.Checked = !disabled.Contains(((Module)it.Tag).Name);
                for (int i = 0; i < Styles.Length; i++) ((ToolStripMenuItem)widget.DropDownItems[i]).Checked = i == (int)style;
                percent.Checked = Module.PercentSign;
                foreach (ToolStripMenuItem it in rate.DropDownItems) it.Checked = (int)it.Tag == interval;
                auto.Checked = Autostart;
                if (gpu != null) fillGpu();
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
            // Previews feed the README, so numbers use English formatting ("3.49 GHz") whatever Windows is set to.
            System.Globalization.CultureInfo en = new System.Globalization.CultureInfo("en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = en;
            Thread.CurrentThread.CurrentCulture = en;
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
                            using (Bitmap ic = Render(mods[i], size, Color.White, (WidgetStyle)st))
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
            // Use the taskbar's physical pixel size when drawing the icons and popup.
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
