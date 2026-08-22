# WPF animation control probe

Minimal opaque WPF window matching the monitor's 637x1392 physical size and visible row count. It has
one cached rotating glyph and one cached opacity pulse, with no WPF-UI, Mica, effects, DataGrid,
bindings, scanners, tray integration, or background threads.

Build without launching:

```powershell
dotnet build .\tools\WpfAnimationProbe\WpfAnimationProbe.csproj -c Release
```

Run one mode at a time from a normal user terminal:

```powershell
.\tools\WpfAnimationProbe\bin\Release\net10.0-windows\WpfAnimationProbe.exe static
.\tools\WpfAnimationProbe\bin\Release\net10.0-windows\WpfAnimationProbe.exe low
.\tools\WpfAnimationProbe\bin\Release\net10.0-windows\WpfAnimationProbe.exe uncapped
```
