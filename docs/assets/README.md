# Project visuals

- `ovi-hero.svg` is the README banner. Its capture scene is an illustration, explicitly labeled inside the artwork.
- `ovi-mark.svg` is the capture-frame app mark. The PNG files are raster exports of these original SVGs.
- `recording-panel.png` and `recording-countdown.png` are rendered by the app's native `--ui-smoke` fixture using generated content. They show real WinUI controls, not a desktop capture.
- `screenshot-preview.png` and `settings.png` show the app's actual WinUI views rendered by a native fixture with generated demo content. They contain no desktop or private screenshots.
- `apps/windows/Assets/Ovi.Screenshot.ico` is the matching Windows executable icon, with 16, 24, 32, 48, 64, 128 and 256 pixel sizes.
- `apps/windows/Assets/capture.wav` is an original 160 ms capture tick. Rebuild it with `python scripts/generate-capture-sound.py`; the generator uses only Python's standard library.

The SVGs contain no external images, scripts or font downloads. Their text uses local font fallbacks. Keep product images representative of the current app and label mockups or generated demonstrations clearly.
