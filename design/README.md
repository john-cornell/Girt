# Design sources

Editable SVG sources for Girt's brand assets. The compiled/rasterized versions actually used
by the app live under `src/Girt/Assets/` (`app_icon.ico`, `app_icon.png`, `splash.png`) and are
generated from these, not hand-edited.

- `girt_icon.svg` — the app icon (island girt by sea, transparent background so it adapts to
  light/dark taskbars). Rasterize with `cairosvg` at whatever sizes are needed, then rebuild the
  `.ico` from the resulting PNGs.
- `girt_splash.svg` — the startup splash screen (icon + "Girt" wordmark), rasterized to
  `src/Girt/Assets/splash.png` and wired up via the `<SplashScreen>` MSBuild item in
  `Girt.csproj`.

Regenerate with cairosvg + Pillow, e.g.:

```python
import cairosvg
cairosvg.svg2png(url="girt_splash.svg", write_to="../src/Girt/Assets/splash.png", output_width=900, output_height=420)
```
