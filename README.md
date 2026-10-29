# Monitor PC (Linux)

App de escritorio en C# + Avalonia (.NET 10) para vigilar tu equipo en vivo.

**Páginas**
- **Panel**: uso de CPU con gráfica, temperatura, RAM libre, disco, red y datos del sistema, más el top de apps por RAM, CPU y actividad de disco.
- **Procesos**: apps agrupadas por nombre (Chrome, Firefox... suman todos sus procesos), ordenables por RAM, CPU o disco (E/S).
- **Disco**: particiones y actividad de lectura/escritura en tiempo real.
- **Espacio**: explorador de almacenamiento para cualquier ruta (ej: `/home/usuario` o `/`) que analiza archivos y carpetas reales ordenados por tamaño (doble clic en una carpeta para explorarla).
- **Ajustes**: 8 temas (Cielo, Medianoche, Neón, Bosque, Atardecer, Grafito, Claro, Sakura), opacidad del personaje de fondo y de las tarjetas, intervalo de actualización y ventana siempre encima.

La ventana se puede reducir hasta ~300 px: la barra lateral pasa a solo iconos y las tarjetas se reacomodan en 1, 2 o 3 columnas.
Los ajustes se guardan en `~/.config/monitorpc/ajustes.json`.

## Ejecutar en desarrollo
```bash
sudo apt install dotnet-sdk-10.0
cd MonitorPC
dotnet run
```

## Crear la versión descargable (un solo ejecutable)
```bash
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish
./publish/monitorpc
```
La carpeta `publish/` no necesita .NET instalado. Para ARM (Raspberry Pi, etc.) usa `-r linux-arm64`.

## Instalarla con icono en el menú de aplicaciones
```bash
sudo mkdir -p /opt/monitorpc
sudo cp publish/monitorpc /opt/monitorpc/
sudo cp Assets/icono.png /opt/monitorpc/icono.png
```
Crea `~/.local/share/applications/monitorpc.desktop`:
```ini
[Desktop Entry]
Name=Monitor PC
Comment=CPU, RAM, temperatura y disco
Exec=/opt/monitorpc/monitorpc
Icon=/opt/monitorpc/icono.png
Terminal=false
Type=Application
Categories=System;Monitor;
```

## Paquete .deb (para compartir)
```bash
dotnet tool install --global dotnet-deb
dotnet deb -c Release -r linux-x64
```
(Alternativas: AppImage con `appimagetool`, o subir el `.zip` de `publish/` a GitHub Releases.)

## Notas
- La temperatura sale "N/D" si el equipo no expone sensores (máquinas virtuales, WSL). Revisa `ls /sys/class/thermal` y `ls /sys/class/hwmon`.
- La actividad de disco por app solo se puede leer para tus propios procesos; los de root no la exponen.
- La RAM por app es la memoria residente (RSS): en apps con varios procesos puede contar memoria compartida más de una vez.
- Si al restaurar paquetes Avalonia da algún aviso, sube las tres versiones del `.csproj` (11.2.3) a la última 11.3.x.
