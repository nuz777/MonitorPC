using System.Text.Json;

namespace MonitorPC;

public sealed class Ajustes
{
    public string Tema { get; set; } = "Cielo";
    public double OpacidadPersonaje { get; set; } = 0.50;
    public double OpacidadTarjetas { get; set; } = 0.74;
    public int IntervaloSeg { get; set; } = 1;
    public bool MostrarPersonaje { get; set; } = true;
    public bool SiempreEncima { get; set; } = false;

    static string Ruta => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "monitorpc", "ajustes.json");

    public static Ajustes Cargar()
    {
        try
        {
            if (File.Exists(Ruta))
                return JsonSerializer.Deserialize<Ajustes>(File.ReadAllText(Ruta)) ?? new Ajustes();
        }
        catch { }
        return new Ajustes();
    }

    public void Guardar()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Ruta)!);
            File.WriteAllText(Ruta, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>Colores de un tema (todos en #RRGGBB). La transparencia de tarjetas se aplica al pintar.</summary>
public sealed record Tema(
    string Nombre, bool Oscuro,
    string Fondo, string Tarjeta, string Borde, string Texto, string Apagado,
    string Acento1, string Acento2, string Acento3, string Acento4);

public static class Temas
{
    public static readonly Tema[] Todos =
    {
        new("Cielo",      true,  "#0A0F1F", "#121A33", "#25315A", "#F2F5FF", "#8E9AC0", "#3D7BFF", "#38D0FF", "#8B7CFF", "#FFB86B"),
        new("Medianoche", true,  "#0D0A1A", "#171230", "#2E2557", "#F4F0FF", "#9A90C4", "#A855F7", "#EC4899", "#6366F1", "#22D3EE"),
        new("Neón",       true,  "#05060C", "#0E1020", "#2A1E4A", "#EAFBFF", "#7FA3B5", "#00F0FF", "#FF2E97", "#B6FF3B", "#FFE600"),
        new("Bosque",     true,  "#08130D", "#102017", "#21402D", "#ECFAF0", "#86AE95", "#34D399", "#A3E635", "#2DD4BF", "#FBBF24"),
        new("Atardecer",  true,  "#170B0E", "#25131A", "#4A2530", "#FFF1EC", "#C79A9A", "#FB7185", "#FB923C", "#FBBF24", "#C084FC"),
        new("Grafito",    true,  "#0F1113", "#1A1D21", "#2C3036", "#F1F3F5", "#8A9099", "#7DD3FC", "#A5B4FC", "#86EFAC", "#FCD34D"),
        new("Claro",      false, "#EEF2FB", "#FFFFFF", "#D5DDF0", "#14193A", "#5C668F", "#2563EB", "#0891B2", "#7C3AED", "#EA580C"),
        new("Sakura",     false, "#FFF1F5", "#FFFFFF", "#F5CFDC", "#3A1626", "#8F5D72", "#EC4899", "#F97316", "#8B5CF6", "#14B8A6"),
    };

    public static Tema Buscar(string nombre) =>
        Todos.FirstOrDefault(t => t.Nombre == nombre) ?? Todos[0];
}
