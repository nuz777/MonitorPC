using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MonitorPC;

public readonly record struct ProcInfo(int Pid, string Nombre, long Jiffies, long RssBytes, long IoLeido, long IoEscrito);

public readonly record struct Memoria(long TotalKB, long DisponibleKB, long SwapTotalKB, long SwapLibreKB);

/// <summary>Lee métricas del sistema en Linux y Windows.</summary>
public static class SystemStats
{
    // ---------- Win32 P/Invoke ----------
    [StructLayout(LayoutKind.Sequential)]
    struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
        public long ToTicks() => (long)(((ulong)dwHighDateTime << 32) | dwLowDateTime);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---------- CPU ----------
    public static (long idle, long total) LeerCpu()
    {
        if (OperatingSystem.IsWindows())
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long idleTicks = idle.ToTicks();
                long totalTicks = kernel.ToTicks() + user.ToTicks();
                return (idleTicks, totalTicks);
            }
            return (0, 1);
        }

        try
        {
            var linea = File.ReadLines("/proc/stat").First();
            var v = linea.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Skip(1).Take(8).Select(long.Parse).ToArray();
            return (v[3] + v[4], v.Sum()); // idle + iowait, total
        }
        catch
        {
            return (0, 1);
        }
    }

    public static string ModeloCpu()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var val = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string;
                if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
            }
            catch { }
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
        }

        try
        {
            foreach (var l in File.ReadLines("/proc/cpuinfo"))
            {
                if (l.StartsWith("model name") || l.StartsWith("Model") || l.StartsWith("Hardware"))
                {
                    var i = l.IndexOf(':');
                    if (i >= 0) return l[(i + 1)..].Trim();
                }
            }
        }
        catch { }
        return "CPU";
    }

    public static double? FrecuenciaGHz()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var mhz = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "~MHz", null);
                if (mhz is int m && m > 0) return m / 1000.0;
            }
            catch { }
            return null;
        }

        try
        {
            var vals = new List<double>();
            foreach (var l in File.ReadLines("/proc/cpuinfo"))
            {
                if (!l.StartsWith("cpu MHz")) continue;
                var i = l.IndexOf(':');
                if (i >= 0 && double.TryParse(l[(i + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
                    vals.Add(m);
            }
            if (vals.Count > 0) return vals.Average() / 1000.0;
        }
        catch { }
        return null;
    }

    // ---------- Temperatura ----------
    static double? LeerMilis(string ruta)
    {
        try { return long.Parse(File.ReadAllText(ruta).Trim()) / 1000.0; }
        catch { return null; }
    }

    static bool Valida(double? t) => t is > 0 and < 150;

    public static double? LeerTemperatura()
    {
        if (!OperatingSystem.IsLinux()) return null;

        // 1) zonas térmicas que parecen de CPU
        try
        {
            foreach (var zona in Directory.GetDirectories("/sys/class/thermal", "thermal_zone*"))
            {
                string tipo;
                try { tipo = File.ReadAllText(Path.Combine(zona, "type")).Trim().ToLowerInvariant(); }
                catch { continue; }
                if (tipo.Contains("pkg") || tipo.Contains("cpu") || tipo.Contains("k10temp") ||
                    tipo.Contains("x86") || tipo.Contains("soc"))
                {
                    var t = LeerMilis(Path.Combine(zona, "temp"));
                    if (Valida(t)) return t;
                }
            }
        }
        catch { }

        // 2) hwmon (AMD k10temp, Intel coretemp...)
        try
        {
            foreach (var hw in Directory.GetDirectories("/sys/class/hwmon", "hwmon*"))
            {
                string nombre;
                try { nombre = File.ReadAllText(Path.Combine(hw, "name")).Trim(); }
                catch { continue; }
                if (nombre is "k10temp" or "coretemp" or "zenpower" or "cpu_thermal")
                {
                    var t = LeerMilis(Path.Combine(hw, "temp1_input"));
                    if (Valida(t)) return t;
                }
            }
        }
        catch { }

        // 3) último recurso
        var z0 = LeerMilis("/sys/class/thermal/thermal_zone0/temp");
        return Valida(z0) ? z0 : null;
    }

    // ---------- Memoria ----------
    public static Memoria LeerMemoria()
    {
        if (OperatingSystem.IsWindows())
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                long total = (long)(mem.ullTotalPhys / 1024);
                long disp = (long)(mem.ullAvailPhys / 1024);
                long swT = (long)(mem.ullTotalPageFile / 1024);
                long swL = (long)(mem.ullAvailPageFile / 1024);
                return new Memoria(total, disp, swT, swL);
            }
            return new Memoria(0, 0, 0, 0);
        }

        long lTotal = 0, lDisp = 0, lSwT = 0, lSwL = 0;
        try
        {
            foreach (var l in File.ReadLines("/proc/meminfo"))
            {
                var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) continue;
                switch (p[0])
                {
                    case "MemTotal:": lTotal = long.Parse(p[1]); break;
                    case "MemAvailable:": lDisp = long.Parse(p[1]); break;
                    case "SwapTotal:": lSwT = long.Parse(p[1]); break;
                    case "SwapFree:": lSwL = long.Parse(p[1]); break;
                }
            }
        }
        catch { }
        return new Memoria(lTotal, lDisp, lSwT, lSwL);
    }

    // ---------- Disco ----------
    public static (long libre, long total) LeerDiscoRaiz()
    {
        var ruta = OperatingSystem.IsWindows()
            ? (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\")
            : "/";
        var d = new DriveInfo(ruta);
        return (d.AvailableFreeSpace, d.TotalSize);
    }

    static readonly Regex DiscoFisico =
        new(@"^(sd[a-z]+|vd[a-z]+|xvd[a-z]+|hd[a-z]+|nvme\d+n\d+|mmcblk\d+)$", RegexOptions.Compiled);

    /// <summary>Bytes acumulados leídos/escritos en todos los discos físicos.</summary>
    public static (long leido, long escrito) LeerIoDisco()
    {
        if (!OperatingSystem.IsLinux()) return (0, 0);

        long r = 0, w = 0;
        try
        {
            foreach (var l in File.ReadLines("/proc/diskstats"))
            {
                var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 10 || !DiscoFisico.IsMatch(p[2])) continue;
                r += long.Parse(p[5]) * 512;
                w += long.Parse(p[9]) * 512;
            }
        }
        catch { }
        return (r, w);
    }

    // ---------- Red ----------
    public static (long rx, long tx) LeerRed()
    {
        if (OperatingSystem.IsWindows())
        {
            long rx = 0, tx = 0;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var stats = nic.GetIPv4Statistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
            }
            catch { }
            return (rx, tx);
        }

        long lRx = 0, lTx = 0;
        try
        {
            foreach (var l in File.ReadLines("/proc/net/dev").Skip(2))
            {
                var i = l.IndexOf(':');
                if (i < 0) continue;
                var nombre = l[..i].Trim();
                if (nombre == "lo" || nombre.StartsWith("docker") || nombre.StartsWith("veth") || nombre.StartsWith("br-"))
                    continue;
                var p = l[(i + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                lRx += long.Parse(p[0]);
                lTx += long.Parse(p[8]);
            }
        }
        catch { }
        return (lRx, lTx);
    }

    // ---------- Sistema ----------
    public static TimeSpan Uptime()
    {
        return TimeSpan.FromMilliseconds(Environment.TickCount64);
    }

    public static string Carga()
    {
        if (OperatingSystem.IsWindows()) return "-";

        try
        {
            var p = File.ReadAllText("/proc/loadavg").Split(' ');
            return $"{p[0]} · {p[1]} · {p[2]}";
        }
        catch { return "-"; }
    }

    public static string InfoSistema()
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.OSDescription;
        }

        string distro = "Linux", kernel = "";
        try
        {
            foreach (var l in File.ReadLines("/etc/os-release"))
                if (l.StartsWith("PRETTY_NAME="))
                    distro = l["PRETTY_NAME=".Length..].Trim('"');
        }
        catch { }
        try { kernel = File.ReadAllText("/proc/sys/kernel/osrelease").Trim(); } catch { }
        return kernel.Length > 0 ? $"{distro} · kernel {kernel}" : distro;
    }

    // ---------- Procesos ----------
    static bool EsGenerico(string n) =>
        n.StartsWith("python") || n is "java" or "node" or "dotnet" or "ruby" or "perl" or "php" or "deno" or "bun";

    static string MejorarNombre(string dir, string nombre)
    {
        try
        {
            var partes = File.ReadAllText($"{dir}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < partes.Length; i++)
            {
                if (partes[i].StartsWith('-')) continue;
                var f = Path.GetFileName(partes[i]);
                if (f.Length > 0) return $"{nombre} · {f}";
            }
        }
        catch { }
        return nombre;
    }

    static (long utime, long stime, long rss) ParsearCamposStat(ReadOnlySpan<char> span)
    {
        long utime = 0, stime = 0, rss = 0;
        int idx = 0;
        int inicio = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || span[i] == ' ')
            {
                if (i > inicio)
                {
                    var seg = span[inicio..i];
                    if (idx == 11) long.TryParse(seg, NumberStyles.Integer, CultureInfo.InvariantCulture, out utime);
                    else if (idx == 12) long.TryParse(seg, NumberStyles.Integer, CultureInfo.InvariantCulture, out stime);
                    else if (idx == 21) { long.TryParse(seg, NumberStyles.Integer, CultureInfo.InvariantCulture, out rss); break; }
                    idx++;
                }
                inicio = i + 1;
            }
        }
        return (utime, stime, rss);
    }

    public static List<ProcInfo> LeerProcesos()
    {
        if (OperatingSystem.IsWindows())
        {
            var res = new List<ProcInfo>(128);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    long jiffies = p.TotalProcessorTime.Ticks;
                    long rss = p.WorkingSet64;
                    res.Add(new ProcInfo(p.Id, p.ProcessName, jiffies, rss, -1, -1));
                }
                catch { }
            }
            return res;
        }

        var lista = new List<ProcInfo>(256);
        int pagina = Environment.SystemPageSize;

        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            var nombreDir = Path.GetFileName(dir);
            if (!int.TryParse(nombreDir, out var pid)) continue;
            try
            {
                var statContenido = File.ReadAllText($"{dir}/stat");
                int ini = statContenido.IndexOf('(');
                int fin = statContenido.LastIndexOf(')');
                if (ini < 0 || fin < ini || fin + 2 >= statContenido.Length) continue;

                var nombre = statContenido[(ini + 1)..fin];
                var restoSpan = statContenido.AsSpan(fin + 2);
                var (utime, stime, rssPaginas) = ParsearCamposStat(restoSpan);
                long rss = rssPaginas * pagina;
                if (rss == 0) continue;

                if (EsGenerico(nombre)) nombre = MejorarNombre(dir, nombre);

                long leido = -1, escrito = -1;
                try
                {
                    foreach (var l in File.ReadLines($"{dir}/io"))
                    {
                        if (l.StartsWith("read_bytes:")) leido = long.Parse(l.AsSpan(11).Trim());
                        else if (l.StartsWith("write_bytes:")) escrito = long.Parse(l.AsSpan(12).Trim());
                    }
                }
                catch { leido = escrito = -1; }

                lista.Add(new ProcInfo(pid, nombre, utime + stime, rss, leido, escrito));
            }
            catch { }
        }
        return lista;
    }
}
