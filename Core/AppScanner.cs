namespace MonitorPC;

public static class AppScanner
{
    public static List<FilaUso> EscanearApps()
    {
        var apps = new Dictionary<string, (long bytes, string origen)>(StringComparer.OrdinalIgnoreCase);

        // 1. Mapear archivos .desktop a nombres legibles
        var desktopANombre = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var carpetasDesktop = new[]
        {
            "/usr/share/applications",
            "/var/lib/snapd/desktop/applications",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/applications"),
            "/var/lib/flatpak/exports/share/applications"
        };

        foreach (var dir in carpetasDesktop)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var archivo in Directory.EnumerateFiles(dir, "*.desktop"))
                {
                    var (nombre, oculto) = ParsearDesktop(archivo);
                    if (!oculto && !string.IsNullOrWhiteSpace(nombre))
                    {
                        desktopANombre[archivo] = nombre;
                    }
                }
            }
            catch { }
        }

        // 2. Apps Snap (JetBrains Rider, Firefox, VLC, Steam, pgAdmin, etc.)
        try
        {
            var snapDesktopDir = "/var/lib/snapd/desktop/applications";
            if (Directory.Exists(snapDesktopDir))
            {
                foreach (var archivo in Directory.EnumerateFiles(snapDesktopDir, "*.desktop"))
                {
                    if (!desktopANombre.TryGetValue(archivo, out var nombreApp)) continue;

                    var archivoBase = Path.GetFileName(archivo);
                    var snapNombre = archivoBase.Split('_')[0];

                    long bytes = 0;
                    var dirSnaps = "/var/lib/snapd/snaps";
                    if (Directory.Exists(dirSnaps))
                    {
                        foreach (var f in Directory.EnumerateFiles(dirSnaps, $"{snapNombre}_*.snap"))
                        {
                            try { bytes += new FileInfo(f).Length; } catch { }
                        }
                    }

                    if (bytes == 0 && Directory.Exists($"/snap/{snapNombre}"))
                    {
                        try
                        {
                            foreach (var sub in Directory.EnumerateDirectories($"/snap/{snapNombre}"))
                            {
                                if (int.TryParse(Path.GetFileName(sub), out _))
                                    bytes += MedirDirectorio(sub);
                            }
                        }
                        catch { }
                    }

                    if (bytes > 5 * 1024 * 1024)
                    {
                        if (!apps.TryGetValue(nombreApp, out var actual) || bytes > actual.bytes)
                            apps[nombreApp] = (bytes, "Paquete Snap");
                    }
                }
            }
        }
        catch { }

        // 3. Apps del sistema (APT / DPKG: VS Code, Antigravity, MongoDB Compass, etc.)
        try
        {
            var statusFile = "/var/lib/dpkg/status";
            var pkgSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(statusFile))
            {
                string? paqueteActual = null;
                foreach (var linea in File.ReadLines(statusFile))
                {
                    if (linea.StartsWith("Package: "))
                    {
                        paqueteActual = linea.Substring(9).Trim();
                    }
                    else if (linea.StartsWith("Installed-Size: ") && paqueteActual != null)
                    {
                        if (long.TryParse(linea.Substring(16).Trim(), out var kb))
                        {
                            pkgSizes[paqueteActual] = kb * 1024;
                        }
                    }
                }
            }

            var dpkgInfoDir = "/var/lib/dpkg/info";
            if (Directory.Exists(dpkgInfoDir))
            {
                foreach (var listFile in Directory.EnumerateFiles(dpkgInfoDir, "*.list"))
                {
                    var baseName = Path.GetFileNameWithoutExtension(listFile);
                    var paquete = baseName.Split(':')[0];

                    if (!pkgSizes.TryGetValue(paquete, out var tamBytes) || tamBytes < 5 * 1024 * 1024)
                        continue;

                    try
                    {
                        foreach (var linea in File.ReadLines(listFile))
                        {
                            var ruta = linea.Trim();
                            if (desktopANombre.TryGetValue(ruta, out var nombreApp))
                            {
                                if (!apps.TryGetValue(nombreApp, out var actual) || tamBytes > actual.bytes)
                                {
                                    apps[nombreApp] = (tamBytes, "Aplicación del sistema");
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 4. Flatpak (si existen apps flatpak)
        try
        {
            var flatpakExports = "/var/lib/flatpak/exports/share/applications";
            if (Directory.Exists(flatpakExports))
            {
                foreach (var archivo in Directory.EnumerateFiles(flatpakExports, "*.desktop"))
                {
                    if (!desktopANombre.TryGetValue(archivo, out var nombreApp)) continue;

                    var appId = Path.GetFileNameWithoutExtension(archivo);
                    var appDir = $"/var/lib/flatpak/app/{appId}";
                    if (Directory.Exists(appDir))
                    {
                        long tam = MedirDirectorio(appDir);
                        if (tam > 5 * 1024 * 1024)
                        {
                            if (!apps.TryGetValue(nombreApp, out var actual) || tam > actual.bytes)
                                apps[nombreApp] = (tam, "Flatpak");
                        }
                    }
                }
            }
        }
        catch { }

        var lista = apps.Select(kv => new { Nombre = kv.Key, Bytes = kv.Value.bytes, Origen = kv.Value.origen })
                        .OrderByDescending(a => a.Bytes)
                        .ToList();

        if (lista.Count == 0) return new List<FilaUso>();

        double max = lista[0].Bytes;
        return lista.Select(a => new FilaUso
        {
            Nombre = a.Nombre,
            Valor = Fmt.Bytes(a.Bytes),
            Detalle = a.Origen,
            Pct = max > 0 ? 100.0 * a.Bytes / max : 0
        }).ToList();
    }

    static (string? nombre, bool oculto) ParsearDesktop(string ruta)
    {
        string? nombreDefault = null;
        string? nombreEs = null;
        bool oculto = false;

        try
        {
            foreach (var l in File.ReadLines(ruta))
            {
                var linea = l.Trim();
                if (linea.StartsWith("[") && linea != "[Desktop Entry]")
                {
                    if (nombreDefault != null) break;
                }
                if (linea.StartsWith("Name[es]=") || linea.StartsWith("Name[es_ES]="))
                {
                    nombreEs = linea.Substring(linea.IndexOf('=') + 1).Trim();
                }
                else if (linea.StartsWith("Name=") && nombreDefault == null)
                {
                    nombreDefault = linea.Substring(5).Trim();
                }
                else if (linea.Equals("NoDisplay=true", StringComparison.OrdinalIgnoreCase) ||
                         linea.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase))
                {
                    oculto = true;
                }
            }
        }
        catch { }

        return (nombreEs ?? nombreDefault, oculto);
    }

    static long MedirDirectorio(string dir)
    {
        long total = 0;
        try
        {
            var opc = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opc))
            {
                try { total += f.Length; } catch { }
            }
        }
        catch { }
        return total;
    }
}
