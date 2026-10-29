namespace MonitorPC;

/// <summary>Formatos de texto para tamaños, velocidades y tiempos.</summary>
public static class Fmt
{
    public static string Bytes(double b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        if (i == 0) return $"{b:F0} {u[i]}";
        if (b >= 100) return $"{b:F0} {u[i]}";
        if (b >= 10) return $"{b:F1} {u[i]}";
        return $"{b:F2} {u[i]}";
    }

    public static string Rate(double bytesPorSeg) => Bytes(bytesPorSeg) + "/s";

    public static string Tiempo(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays} d {t.Hours} h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        return $"{t.Minutes} min";
    }
}

/// <summary>Una fila genérica para las listas (apps, carpetas, particiones...).</summary>
public sealed class FilaUso
{
    public string Nombre { get; init; } = "";
    public string Valor { get; init; } = "";
    public string Detalle { get; init; } = "";
    public double Pct { get; init; }
    public string? Ruta { get; init; }
    public bool EsCarpeta { get; init; }
    public bool TieneDetalle => Detalle.Length > 0;
}
