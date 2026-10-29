namespace MonitorPC;

public sealed class AppUso
{
    public string Nombre = "";
    public int Cantidad;
    public double CpuPct;          // % del CPU total de la máquina
    public long RamBytes;
    public bool IoDisponible;      // hay permiso para leer /proc/<pid>/io
    public double IoLeeBps;
    public double IoEscribeBps;
    public double IoBps => IoLeeBps + IoEscribeBps;
}

public sealed class Particion
{
    public string Montaje = "";
    public string Formato = "";
    public long TotalBytes;
    public long LibreBytes;
}

public sealed class Snapshot
{
    public double CpuPct;
    public double? TempC;
    public double? FrecGHz;

    public double RamTotalBytes, RamLibreBytes, SwapTotalBytes, SwapUsadoBytes;
    public double RamUsadaPct => RamTotalBytes > 0 ? 100.0 * (RamTotalBytes - RamLibreBytes) / RamTotalBytes : 0;

    public double DiscoTotalBytes, DiscoLibreBytes;
    public double DiscoUsadoPct => DiscoTotalBytes > 0 ? 100.0 * (DiscoTotalBytes - DiscoLibreBytes) / DiscoTotalBytes : 0;
    public double DiscoLeeBps, DiscoEscribeBps;

    public double RedBajaBps, RedSubeBps;

    public TimeSpan Uptime;
    public string Carga = "";

    public List<AppUso> Apps = new();
}

/// <summary>Toma muestras del sistema; guarda la lectura anterior para calcular velocidades y porcentajes.</summary>
public sealed class Sampler
{
    long _idle, _total;
    long _rx, _tx, _dr, _dw;
    Dictionary<int, (long jif, long rd, long wr)> _prev = new();
    readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    TimeSpan _ultimoT;

    public int Nucleos => Environment.ProcessorCount;
    public string ModeloCpu { get; } = SystemStats.ModeloCpu();
    public string InfoSistema { get; } = SystemStats.InfoSistema();

    public Sampler()
    {
        (_idle, _total) = SystemStats.LeerCpu();
        (_rx, _tx) = SystemStats.LeerRed();
        (_dr, _dw) = SystemStats.LeerIoDisco();
        foreach (var p in SystemStats.LeerProcesos())
            _prev[p.Pid] = (p.Jiffies, p.IoLeido, p.IoEscrito);
        _ultimoT = _sw.Elapsed;
    }

    public Snapshot Tomar()
    {
        var ahora = _sw.Elapsed;
        double dt = Math.Max(0.05, (ahora - _ultimoT).TotalSeconds);
        _ultimoT = ahora;

        var s = new Snapshot();

        // CPU
        var (idle, total) = SystemStats.LeerCpu();
        double dTotal = total - _total, dIdle = idle - _idle;
        s.CpuPct = dTotal > 0 ? Math.Clamp(100.0 * (1 - dIdle / dTotal), 0, 100) : 0;
        _idle = idle; _total = total;
        s.TempC = SystemStats.LeerTemperatura();
        s.FrecGHz = SystemStats.FrecuenciaGHz();

        // Memoria
        var m = SystemStats.LeerMemoria();
        s.RamTotalBytes = m.TotalKB * 1024.0;
        s.RamLibreBytes = m.DisponibleKB * 1024.0;
        s.SwapTotalBytes = m.SwapTotalKB * 1024.0;
        s.SwapUsadoBytes = (m.SwapTotalKB - m.SwapLibreKB) * 1024.0;

        // Disco
        try
        {
            var (libre, tot) = SystemStats.LeerDiscoRaiz();
            s.DiscoLibreBytes = libre; s.DiscoTotalBytes = tot;
        }
        catch { }
        var (dr, dw) = SystemStats.LeerIoDisco();
        s.DiscoLeeBps = Math.Max(0, dr - _dr) / dt;
        s.DiscoEscribeBps = Math.Max(0, dw - _dw) / dt;
        _dr = dr; _dw = dw;

        // Red
        var (rx, tx) = SystemStats.LeerRed();
        s.RedBajaBps = Math.Max(0, rx - _rx) / dt;
        s.RedSubeBps = Math.Max(0, tx - _tx) / dt;
        _rx = rx; _tx = tx;

        s.Uptime = SystemStats.Uptime();
        s.Carga = SystemStats.Carga();

        // Procesos agrupados por nombre (así Chrome, Firefox, etc. suman todos sus procesos)
        var procs = SystemStats.LeerProcesos();
        var nuevo = new Dictionary<int, (long jif, long rd, long wr)>(procs.Count);
        var grupos = new Dictionary<string, AppUso>();
        foreach (var p in procs)
        {
            nuevo[p.Pid] = (p.Jiffies, p.IoLeido, p.IoEscrito);
            if (!grupos.TryGetValue(p.Nombre, out var g))
                grupos[p.Nombre] = g = new AppUso { Nombre = p.Nombre };
            g.Cantidad++;
            g.RamBytes += p.RssBytes;

            if (_prev.TryGetValue(p.Pid, out var a))
            {
                long dj = p.Jiffies - a.jif;
                if (dj > 0 && dTotal > 0) g.CpuPct += 100.0 * dj / dTotal;
                if (p.IoLeido >= 0 && a.rd >= 0)
                {
                    g.IoDisponible = true;
                    g.IoLeeBps += Math.Max(0, p.IoLeido - a.rd) / dt;
                    g.IoEscribeBps += Math.Max(0, p.IoEscrito - a.wr) / dt;
                }
            }
        }
        _prev = nuevo;
        s.Apps = grupos.Values.ToList();

        return s;
    }

    public static List<Particion> LeerParticiones()
    {
        var res = new List<Particion>();
        var vistos = new HashSet<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network)) continue;
                var nombre = d.Name;
                if (nombre.StartsWith("/snap") || nombre.StartsWith("/var/lib/")) continue;
                if (d.TotalSize < 100L * 1024 * 1024) continue;
                if (!vistos.Add($"{d.TotalSize}-{d.TotalFreeSpace}-{d.DriveFormat}")) continue;
                res.Add(new Particion
                {
                    Montaje = nombre,
                    Formato = d.DriveFormat,
                    TotalBytes = d.TotalSize,
                    LibreBytes = d.AvailableFreeSpace
                });
            }
            catch { }
        }
        return res;
    }
}
