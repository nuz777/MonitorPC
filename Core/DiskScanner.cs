using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace MonitorPC;

public sealed record ItemEspacio(string Nombre, string Ruta, long Bytes, bool EsCarpeta);

/// <summary>Analiza el espacio real ocupado por archivos y carpetas en una ruta.</summary>
public static class DiskScanner
{
    static readonly HashSet<string> OmitirRutas = new(StringComparer.OrdinalIgnoreCase)
    {
        "/proc", "/sys", "/dev", "/run", "/snap"
    };

    [StructLayout(LayoutKind.Sequential)]
    struct StatStruct
    {
        public ulong st_dev, st_ino, st_nlink;
        public uint st_mode, st_uid, st_gid;
        public int __pad0;
        public ulong st_rdev;
        public long st_size, st_blksize, st_blocks;
        public long st_atime;
        public ulong st_atime_nsec;
        public long st_mtime;
        public ulong st_mtime_nsec;
        public long st_ctime;
        public ulong st_ctime_nsec;
        public long __unused0, __unused1, __unused2;
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    static extern int stat(string path, out StatStruct buf);

    static readonly EnumerationOptions Opciones = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    /// <summary>Obtiene el tamaño real en disco de un archivo (manejando sparse files y enlaces).</summary>
    public static long TamanoRealArchivo(FileInfo fi)
    {
        try
        {
            if (fi.LinkTarget != null || fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return 0; // No contar dos veces archivos enlazados

            if (OperatingSystem.IsLinux() && stat(fi.FullName, out var st) == 0 && st.st_blocks > 0)
            {
                long bytesEnBloques = st.st_blocks * 512;
                // Si el tamaño en bloques es menor que el tamaño aparente, es un archivo sparse (ej: VMs, Docker.raw)
                return Math.Min(fi.Length, bytesEnBloques);
            }
            return fi.Length;
        }
        catch
        {
            return fi.Length;
        }
    }

    /// <summary>Calcula el tamaño real acumulado de un directorio.</summary>
    public static long TamanoDirectorio(string dir, CancellationToken ct)
    {
        long total = 0;
        var pila = new Stack<string>();
        pila.Push(dir);

        while (pila.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var actual = pila.Pop();

            try
            {
                var dInfo = new DirectoryInfo(actual);
                if (dInfo.LinkTarget != null || dInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                foreach (var e in dInfo.EnumerateFileSystemInfos("*", Opciones))
                {
                    if (e is DirectoryInfo di)
                    {
                        if (di.LinkTarget == null && !di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            pila.Push(di.FullName);
                    }
                    else if (e is FileInfo fi)
                    {
                        total += TamanoRealArchivo(fi);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* sin permiso o archivo temporal */ }
        }

        return total;
    }

    /// <summary>Escanea una ruta y devuelve todas las carpetas y archivos ordenados por peso.</summary>
    public static List<ItemEspacio> EscanearRuta(string ruta, CancellationToken ct, Action<int>? progreso = null)
    {
        var resultado = new ConcurrentBag<ItemEspacio>();
        var subdirectorios = new List<DirectoryInfo>();

        var dirInfo = new DirectoryInfo(ruta);
        foreach (var e in dirInfo.EnumerateFileSystemInfos("*", Opciones))
        {
            ct.ThrowIfCancellationRequested();

            if (e is DirectoryInfo di)
            {
                if (ruta == "/" && OmitirRutas.Contains(di.FullName))
                    continue;

                if (di.LinkTarget == null && !di.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    subdirectorios.Add(di);
            }
            else if (e is FileInfo fi)
            {
                long tam = TamanoRealArchivo(fi);
                resultado.Add(new ItemEspacio(fi.Name, fi.FullName, tam, false));
            }
        }

        int completadas = 0;
        Parallel.ForEach(subdirectorios,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
            di =>
            {
                long tam = TamanoDirectorio(di.FullName, ct);
                resultado.Add(new ItemEspacio(di.Name, di.FullName, tam, true));
                progreso?.Invoke(Interlocked.Increment(ref completadas));
            });

        return resultado.OrderByDescending(r => r.Bytes).ToList();
    }
}
