# WPF-UI FluentWindow composition probe

Matches the standard WPF probe's size, rows, and two cached 10 FPS glyphs while adding only WPF-UI
4.3's `FluentWindow` and `TitleBar`. Modes independently toggle animation and Mica:

```powershell
.\tools\WpfUiAnimationProbe\bin\Release\net10.0-windows\WpfUiAnimationProbe.exe none-static
.\tools\WpfUiAnimationProbe\bin\Release\net10.0-windows\WpfUiAnimationProbe.exe none-low
.\tools\WpfUiAnimationProbe\bin\Release\net10.0-windows\WpfUiAnimationProbe.exe mica-static
.\tools\WpfUiAnimationProbe\bin\Release\net10.0-windows\WpfUiAnimationProbe.exe mica-low
```
