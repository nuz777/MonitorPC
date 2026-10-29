using System.Globalization;
using System.Text.RegularExpressions;

namespace MonitorPC;

public readonly record struct ProcInfo(int Pid, string Nombre, long Jiffies, long RssBytes, long IoLeido, long IoEscrito);

public readonly record struct Memoria(long TotalKB, long DisponibleKB, long SwapTotalKB, long SwapLibreKB);

/// <summary>Lee métricas del sistema directamente desde /proc y /sys (solo Linux).</summary>
public static class SystemStats
{
    // ---------- CPU ----------
    public static (long idle, long total) LeerCpu()
    {
        var linea = File.ReadLines("/proc/stat").First();
        var v = linea.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                     .Skip(1).Take(8).Select(long.Parse).ToArray();
        return (v[3] + v[4], v.Sum()); // idle + iowait, total
    }

    public static string ModeloCpu()
    {
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
        long total = 0, disp = 0, swT = 0, swL = 0;
        foreach (var l in File.ReadLines("/proc/meminfo"))
        {
            var p = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) continue;
            switch (p[0])
            {
                case "MemTotal:": total = long.Parse(p[1]); break;
                case "MemAvailable:": disp = long.Parse(p[1]); break;
                case "SwapTotal:": swT = long.Parse(p[1]); break;
                case "SwapFree:": swL = long.Parse(p[1]); break;
            }
        }
        return new Memoria(total, disp, swT, swL);
    }

    // ---------- Disco ----------
    public static (long libre, long total) LeerDiscoRaiz()
    {
        var d = new DriveInfo("/");
        return (d.AvailableFreeSpace, d.TotalSize);
    }

    static readonly Regex DiscoFisico =
        new(@"^(sd[a-z]+|vd[a-z]+|xvd[a-z]+|hd[a-z]+|nvme\d+n\d+|mmcblk\d+)$", RegexOptions.Compiled);

    /// <summary>Bytes acumulados leídos/escritos en todos los discos físicos.</summary>
    public static (long leido, long escrito) LeerIoDisco()
    {
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
        long rx = 0, tx = 0;
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
                rx += long.Parse(p[0]);
                tx += long.Parse(p[8]);
            }
        }
        catch { }
        return (rx, tx);
    }

    // ---------- Sistema ----------
    public static TimeSpan Uptime()
    {
        try
        {
            var s = File.ReadAllText("/proc/uptime").Split(' ')[0];
            return TimeSpan.FromSeconds(double.Parse(s, CultureInfo.InvariantCulture));
        }
        catch { return TimeSpan.Zero; }
    }

    public static string Carga()
    {
        try
        {
            var p = File.ReadAllText("/proc/loadavg").Split(' ');
            return $"{p[0]} · {p[1]} · {p[2]}";
        }
        catch { return "-"; }
    }

    public static string InfoSistema()
    {
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

    public static List<ProcInfo> LeerProcesos()
    {
        var lista = new List<ProcInfo>(512);
        int pagina = Environment.SystemPageSize;

        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            try
            {
                var stat = File.ReadAllText($"{dir}/stat");
                int ini = stat.IndexOf('(');
                int fin = stat.LastIndexOf(')');
                if (ini < 0 || fin < ini) continue;

                var nombre = stat[(ini + 1)..fin];
                var c = stat[(fin + 2)..].Split(' ');
                long jiffies = long.Parse(c[11]) + long.Parse(c[12]); // utime + stime
                long rss = long.Parse(c[21]) * pagina;
                if (rss == 0) continue; // hilos del kernel

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
                catch { leido = escrito = -1; } // sin permiso (procesos de otros usuarios)

                lista.Add(new ProcInfo(pid, nombre, jiffies, rss, leido, escrito));
            }
            catch { /* el proceso terminó mientras lo leíamos */ }
        }
        return lista;
    }
}
