using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MonitorPC;

public partial class MainWindow : Window
{
    const int MaxPuntos = 60;
    static readonly IBrush Verde = Brush.Parse("#4ADE80");
    static readonly IBrush Amarillo = Brush.Parse("#FBBF24");
    static readonly IBrush Rojo = Brush.Parse("#F87171");

    readonly Sampler _sampler = new();
    readonly DispatcherTimer _timer = new();
    readonly Ajustes _aj;
    readonly Dictionary<string, (Control pagina, Button boton)> _paginas = new();
    readonly List<double> _hCpu = new(), _hTemp = new(), _hRam = new(), _hRed = new();

    string _pagina = "panel";
    string _orden = "ram";
    bool _ocupado, _listo, _cargandoParticiones;
    Snapshot? _ultimo;

    public MainWindow()
    {
        InitializeComponent();
        _aj = Ajustes.Cargar();

        _paginas["panel"] = (PgPanel, BtnPanel);
        _paginas["procesos"] = (PgProcesos, BtnProcesos);
        _paginas["disco"] = (PgDisco, BtnDisco);
        _paginas["ajustes"] = (PgAjustes, BtnAjustes);

        ConstruirTemas();
        ConfigurarAjustes();
        AplicarTema();
        AplicarFondo();

        TxtSubPanel.Text = _sampler.InfoSistema;
        TxtCpuModelo.Text = $"{_sampler.ModeloCpu} · {_sampler.Nucleos} núcleos";
        TxtSisInfo.Text = _sampler.InfoSistema;

        _listo = true;
        AjustarDiseno(Width);
        Navegar("panel");

        _timer.Interval = TimeSpan.FromSeconds(_aj.IntervaloSeg);
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    // ------------------------------------------------------------------
    //  Diseño adaptable
    // ------------------------------------------------------------------
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_listo && change.Property == BoundsProperty)
            AjustarDiseno(Bounds.Width);
    }

    void AjustarDiseno(double ancho)
    {
        bool compacta = ancho < 640;
        Sidebar.Width = compacta ? 66 : 196;
        SetClase(Sidebar, "compacta", compacta);

        double contenido = ancho - Sidebar.Width;
        GridTarjetas.Columns = contenido < 560 ? 1 : contenido < 860 ? 2 : 3;
        GridTops.Columns = contenido < 640 ? 1 : contenido < 960 ? 2 : 3;
    }

    static void SetClase(Control c, string clase, bool activa)
    {
        if (activa) { if (!c.Classes.Contains(clase)) c.Classes.Add(clase); }
        else c.Classes.Remove(clase);
    }

    // ------------------------------------------------------------------
    //  Navegación
    // ------------------------------------------------------------------
    void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string t) Navegar(t);
    }

    void Navegar(string pag)
    {
        _pagina = pag;
        foreach (var kv in _paginas)
        {
            kv.Value.pagina.IsVisible = kv.Key == pag;
            SetClase(kv.Value.boton, "active", kv.Key == pag);
        }
        if (_ultimo != null) PintarListas(_ultimo);
        if (pag == "disco") _ = ActualizarParticionesAsync();
    }

    void Orden_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string t) return;
        _orden = t;
        SetClase(BtnOrdRam, "active", t == "ram");
        SetClase(BtnOrdCpu, "active", t == "cpu");
        SetClase(BtnOrdIo, "active", t == "io");
        SetClase(ListaProcesos, "c3", t == "ram");
        SetClase(ListaProcesos, "c1", t == "cpu");
        SetClase(ListaProcesos, "c4", t == "io");
        if (_ultimo != null) PintarProcesos(_ultimo);
    }

    // ------------------------------------------------------------------
    //  Muestreo
    // ------------------------------------------------------------------
    async Task TickAsync()
    {
        if (_ocupado) return;
        _ocupado = true;
        try
        {
            var snap = await Task.Run(() => _sampler.Tomar());
            _ultimo = snap;
            Pintar(snap);
        }
        catch { /* si una lectura falla, se reintenta en el siguiente ciclo */ }
        finally { _ocupado = false; }
    }

    static void Agregar(List<double> h, double v)
    {
        h.Add(v);
        if (h.Count > MaxPuntos) h.RemoveAt(0);
    }

    static void Spark(Canvas lienzo, Polyline linea, Polygon? area, List<double> datos, double max)
    {
        double w = lienzo.Bounds.Width, h = lienzo.Bounds.Height;
        if (w <= 0 || h <= 0 || datos.Count < 2 || max <= 0) return;

        double paso = w / (MaxPuntos - 1);
        double x0 = w - paso * (datos.Count - 1);
        var pts = new Points();
        for (int i = 0; i < datos.Count; i++)
            pts.Add(new Point(x0 + i * paso, h - 1 - Math.Clamp(datos[i] / max, 0, 1) * (h - 3)));
        linea.Points = pts;

        if (area != null)
        {
            var a = new Points();
            foreach (var p in pts) a.Add(p);
            a.Add(new Point(w, h));
            a.Add(new Point(x0, h));
            area.Points = a;
        }
    }

    void Pintar(Snapshot s)
    {
        // CPU
        TxtCpu.Text = $"{s.CpuPct:F0} %";
        TxtFrec.Text = s.FrecGHz is double f ? $"{f:F2} GHz" : "";
        TxtCpuDet.Text = $"{_sampler.Nucleos} núcleos · carga {s.Carga}";
        Agregar(_hCpu, s.CpuPct);
        Spark(LienzoCpu, LineaCpu, AreaCpu, _hCpu, 100);

        // Temperatura
        if (s.TempC is double t)
        {
            TxtTemp.Text = $"{t:F0} °C";
            BarTemp.Value = t;
            if (t < 60) { BarTemp.Foreground = Verde; TxtTempEstado.Text = "Normal"; }
            else if (t < 80) { BarTemp.Foreground = Amarillo; TxtTempEstado.Text = "Caliente"; }
            else { BarTemp.Foreground = Rojo; TxtTempEstado.Text = "Muy alta"; }
            Agregar(_hTemp, t);
            Spark(LienzoTemp, LineaTemp, null, _hTemp, 100);
        }
        else
        {
            TxtTemp.Text = "N/D";
            BarTemp.Value = 0;
            TxtTempEstado.Text = "sin sensor";
        }

        // RAM
        TxtRam.Text = Fmt.Bytes(s.RamLibreBytes);
        BarRam.Value = s.RamUsadaPct;
        TxtRamDet.Text = $"{s.RamUsadaPct:F0} % en uso · {Fmt.Bytes(s.RamTotalBytes - s.RamLibreBytes)} de {Fmt.Bytes(s.RamTotalBytes)}"
                         + (s.SwapTotalBytes > 0 ? $"\nSwap: {Fmt.Bytes(s.SwapUsadoBytes)} de {Fmt.Bytes(s.SwapTotalBytes)}" : "");
        Agregar(_hRam, s.RamUsadaPct);
        Spark(LienzoRam, LineaRam, null, _hRam, 100);

        // Disco
        TxtDisco.Text = Fmt.Bytes(s.DiscoLibreBytes);
        BarDisco.Value = s.DiscoUsadoPct;
        TxtDiscoDet.Text = $"{s.DiscoUsadoPct:F0} % en uso · {Fmt.Bytes(s.DiscoTotalBytes)} en total";
        TxtDiscoIo.Text = $"Lee {Fmt.Rate(s.DiscoLeeBps)} · escribe {Fmt.Rate(s.DiscoEscribeBps)}";

        // Red
        TxtRedBaja.Text = $"↓ {Fmt.Rate(s.RedBajaBps)}";
        TxtRedSube.Text = $"↑ {Fmt.Rate(s.RedSubeBps)}";
        Agregar(_hRed, s.RedBajaBps);
        Spark(LienzoRed, LineaRed, null, _hRed, Math.Max(10_240, _hRed.Max()));

        // Sistema
        TxtUptime.Text = $"Encendido hace {Fmt.Tiempo(s.Uptime)}";

        PintarListas(s);
    }

    void PintarListas(Snapshot s)
    {
        switch (_pagina)
        {
            case "panel":
                ListaTopRam.ItemsSource = FilasRam(s, 5, false);
                ListaTopCpu.ItemsSource = FilasCpu(s, 5, false);
                ListaTopIo.ItemsSource = FilasIo(s, 5, false);
                break;
            case "procesos":
                PintarProcesos(s);
                break;
            case "disco":
                TxtIoLee.Text = $"Lectura: {Fmt.Rate(s.DiscoLeeBps)}";
                TxtIoEscribe.Text = $"Escritura: {Fmt.Rate(s.DiscoEscribeBps)}";
                ListaIo.ItemsSource = FilasIo(s, 8, true);
                _ = ActualizarParticionesAsync();
                break;
        }
    }

    void PintarProcesos(Snapshot s)
    {
        ListaProcesos.ItemsSource = _orden switch
        {
            "cpu" => FilasCpu(s, 25, true),
            "io" => FilasIo(s, 25, true),
            _ => FilasRam(s, 25, true)
        };
        TxtSubProc.Text = $"{s.Apps.Sum(a => a.Cantidad)} procesos · {s.Apps.Count} aplicaciones";
    }

    async Task ActualizarParticionesAsync()
    {
        if (_cargandoParticiones) return;
        _cargandoParticiones = true;
        try
        {
            var ps = await Task.Run(Sampler.LeerParticiones);
            ListaParticiones.ItemsSource = ps.Select(p => new FilaUso
            {
                Nombre = p.Montaje,
                Valor = $"{Fmt.Bytes(p.LibreBytes)} libres",
                Detalle = $"{p.Formato} · {Fmt.Bytes(p.TotalBytes - p.LibreBytes)} usados de {Fmt.Bytes(p.TotalBytes)}",
                Pct = p.TotalBytes > 0 ? 100.0 * (p.TotalBytes - p.LibreBytes) / p.TotalBytes : 0
            }).ToList();
        }
        catch { }
        finally { _cargandoParticiones = false; }
    }

    // ---------- Filas para las listas ----------
    static string Nom(AppUso a) => a.Cantidad > 1 ? $"{a.Nombre} ×{a.Cantidad}" : a.Nombre;

    static List<FilaUso> FilasRam(Snapshot s, int n, bool detalle) =>
        s.Apps.OrderByDescending(a => a.RamBytes).Take(n).Select(a => new FilaUso
        {
            Nombre = Nom(a),
            Valor = Fmt.Bytes(a.RamBytes),
            Pct = s.RamTotalBytes > 0 ? 100.0 * a.RamBytes / s.RamTotalBytes : 0,
            Detalle = detalle ? $"CPU {a.CpuPct:F1} %" : ""
        }).ToList();

    static List<FilaUso> FilasCpu(Snapshot s, int n, bool detalle)
    {
        var top = s.Apps.OrderByDescending(a => a.CpuPct).Take(n).ToList();
        double max = top.Count > 0 ? Math.Max(top[0].CpuPct, 0.1) : 1;
        return top.Select(a => new FilaUso
        {
            Nombre = Nom(a),
            Valor = $"{a.CpuPct:F1} %",
            Pct = 100.0 * a.CpuPct / max,
            Detalle = detalle ? $"RAM {Fmt.Bytes(a.RamBytes)}" : ""
        }).ToList();
    }

    static List<FilaUso> FilasIo(Snapshot s, int n, bool detalle)
    {
        var top = s.Apps.Where(a => a.IoDisponible && a.IoBps > 0)
                        .OrderByDescending(a => a.IoBps).Take(n).ToList();
        if (top.Count == 0)
            return new List<FilaUso> { new() { Nombre = "Sin actividad ahora mismo" } };
        double max = top[0].IoBps;
        return top.Select(a => new FilaUso
        {
            Nombre = Nom(a),
            Valor = Fmt.Rate(a.IoBps),
            Pct = 100.0 * a.IoBps / max,
            Detalle = detalle ? $"lee {Fmt.Rate(a.IoLeeBps)} · escribe {Fmt.Rate(a.IoEscribeBps)}" : ""
        }).ToList();
    }

    // ------------------------------------------------------------------
    //  Temas y ajustes
    // ------------------------------------------------------------------
    void ConstruirTemas()
    {
        foreach (var t in Temas.Todos)
        {
            var puntos = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var hex in new[] { t.Acento1, t.Acento2, t.Acento3 })
                puntos.Children.Add(new Border
                {
                    Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Brush.Parse(hex)
                });

            var muestra = new Border
            {
                Height = 34,
                CornerRadius = new CornerRadius(9),
                Background = Brush.Parse(t.Fondo),
                BorderBrush = Brush.Parse(t.Borde),
                BorderThickness = new Thickness(1),
                Child = new Border
                {
                    Child = puntos,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var contenido = new StackPanel { Spacing = 6 };
            contenido.Children.Add(muestra);
            contenido.Children.Add(new TextBlock
            {
                Text = t.Nombre,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var boton = new Button
            {
                Content = contenido,
                Tag = t.Nombre,
                Width = 112,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            boton.Click += (_, _) =>
            {
                _aj.Tema = t.Nombre;
                AplicarTema();
                _aj.Guardar();
            };
            WrapTemas.Children.Add(boton);
        }
    }

    void AplicarTema()
    {
        var t = Temas.Buscar(_aj.Tema);
        var r = Application.Current!.Resources;

        static Color Hex(string h) => Color.Parse(h);
        static Color Alfa(string h, double a)
        {
            var c = Color.Parse(h);
            return Color.FromArgb((byte)(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);
        }

        r["BgBrush"] = new SolidColorBrush(Hex(t.Fondo));
        r["SideBrush"] = new SolidColorBrush(Alfa(t.Fondo, 0.78));
        r["CardBrush"] = new SolidColorBrush(Alfa(t.Tarjeta, _aj.OpacidadTarjetas));
        r["CardBorderBrush"] = new SolidColorBrush(Alfa(t.Borde, 0.9));
        r["TextBrush"] = new SolidColorBrush(Hex(t.Texto));
        r["MutedBrush"] = new SolidColorBrush(Hex(t.Apagado));
        r["Accent1Brush"] = new SolidColorBrush(Hex(t.Acento1));
        r["Accent2Brush"] = new SolidColorBrush(Hex(t.Acento2));
        r["Accent3Brush"] = new SolidColorBrush(Hex(t.Acento3));
        r["Accent4Brush"] = new SolidColorBrush(Hex(t.Acento4));
        r["Accent1SoftBrush"] = new SolidColorBrush(Alfa(t.Acento1, 0.22));
        r["HoverBrush"] = new SolidColorBrush(t.Oscuro ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0));
        r["TrackBrush"] = new SolidColorBrush(t.Oscuro ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(34, 0, 0, 0));

        Application.Current.RequestedThemeVariant = t.Oscuro ? ThemeVariant.Dark : ThemeVariant.Light;

        // Marca el tema elegido
        foreach (var hijo in WrapTemas.Children)
        {
            if (hijo is not Button b) continue;
            bool sel = b.Tag as string == t.Nombre;
            b.BorderThickness = new Thickness(sel ? 2 : 1);
            b.BorderBrush = sel ? new SolidColorBrush(Hex(t.Acento1)) : Brushes.Transparent;
        }
    }

    void AplicarFondo()
    {
        ImgFondo.IsVisible = _aj.MostrarPersonaje;
        ImgFondo.Opacity = _aj.OpacidadPersonaje;
        Topmost = _aj.SiempreEncima;
    }

    void ActualizarTextosAjustes()
    {
        TxtOpPersonaje.Text = $"Opacidad del personaje: {_aj.OpacidadPersonaje:P0}";
        TxtOpTarjetas.Text = $"Opacidad de las tarjetas: {_aj.OpacidadTarjetas:P0}";
    }

    void ConfigurarAjustes()
    {
        SwPersonaje.IsChecked = _aj.MostrarPersonaje;
        SlPersonaje.Value = _aj.OpacidadPersonaje;
        SlTarjetas.Value = _aj.OpacidadTarjetas;
        SwEncima.IsChecked = _aj.SiempreEncima;
        CmbIntervalo.SelectedIndex = _aj.IntervaloSeg switch { 2 => 1, 5 => 2, _ => 0 };
        ActualizarTextosAjustes();

        SwPersonaje.PropertyChanged += (_, e) =>
        {
            if (e.Property != ToggleButton.IsCheckedProperty) return;
            _aj.MostrarPersonaje = SwPersonaje.IsChecked == true;
            AplicarFondo();
        };
        SwEncima.PropertyChanged += (_, e) =>
        {
            if (e.Property != ToggleButton.IsCheckedProperty) return;
            _aj.SiempreEncima = SwEncima.IsChecked == true;
            AplicarFondo();
        };
        SlPersonaje.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _aj.OpacidadPersonaje = SlPersonaje.Value;
            AplicarFondo();
            ActualizarTextosAjustes();
        };
        SlTarjetas.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _aj.OpacidadTarjetas = SlTarjetas.Value;
            AplicarTema();
            ActualizarTextosAjustes();
        };
        CmbIntervalo.SelectionChanged += (_, _) =>
        {
            _aj.IntervaloSeg = CmbIntervalo.SelectedIndex switch { 1 => 2, 2 => 5, _ => 1 };
            _timer.Interval = TimeSpan.FromSeconds(_aj.IntervaloSeg);
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _aj.Guardar();
        base.OnClosing(e);
    }
}
