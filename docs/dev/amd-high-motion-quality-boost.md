# AMD: High Motion Quality Boost

Branch: `feature/amd-high-motion-quality-boost`. Feature pequeña y
autocontenida — un solo documento en vez del par análisis/cierre habitual.

## Qué se añadió y por qué

Al investigar el bitrate adaptativo para AMD (ver
`docs/dev/amd-amf-adaptive-bitrate-future-plan.md`), se revisó la
cabecera oficial de AMD (`VideoEncoderVCE.h`, sección "Dynamic
properties") y apareció `AMF_VIDEO_ENCODER_HIGH_MOTION_QUALITY_BOOST_ENABLE`
— una propiedad que mejora la calidad percibida en movimiento rápido
(paneos de cámara, acción rápida), justo el caso típico de streaming de
juegos y uno de los más difíciles para el control de tasa. Apollo ya la
exponía para NVENC pero no para AMD.

Se confirmó que ffmpeg ya la expone tal cual para los tres codecs AMF
(`high_motion_quality_boost_enable`, verificado leyendo el código fuente
real de `libavcodec/amfenc_h264.c`, `amfenc_hevc.c` y `amfenc_av1.c` en
github.com/FFmpeg/FFmpeg) — así que esto **no necesita tocar ffmpeg en
absoluto**, solo exponer una opción que ya existe en el wrapper que
Apollo ya usa.

Nuevo flag `amd_high_motion_quality_boost` (Web UI: pestaña "AMD AMF
Encoder"), sin valor por defecto forzado (no existía antes, así que no
hay comportamiento previo que preservar más allá de "no tocar nada" —
técnicamente distinto del patrón "default = comportamiento actual" de
otras features, porque aquí no había comportamiento previo que romper).

## Diff conceptual

**Modificado**: `src/config.h` (nuevo campo `std::optional<int>` en el
struct `amd`), `src/config.cpp` (valor por defecto sin fijar + registro
de parseo), `src/video.cpp` (una línea añadida en cada uno de los tres
bloques de opciones AMF — h264, hevc, av1). Web UI: `AmdAmfEncoder.vue`,
`config.html`, `en.json`, `configuration.md`.

**Nuevo**: nada — es una extensión mínima de código ya existente en
todos los archivos que toca.

## Impacto en compatibilidad con upstream

**Muy bajo.** Cinco líneas insertadas en sitios donde Apollo ya sigue
este mismo patrón repetidamente para otras opciones AMF (`vbaq`,
`preencode`, etc.) — un futuro `git merge` debería aplicar sin fricción.

## Qué se validó aquí / qué no

- **Validado**: la opción existe de verdad en ffmpeg (código fuente real,
  no documentación resumida) para los tres codecs, y la sintaxis del
  cambio en `config.h`/`config.cpp` compila en Linux (multiplataforma).
- **No validado**: el bloque `encoder_t amdvce` en `src/video.cpp` está
  envuelto en `#ifdef _WIN32` — igual que con HIDMaestro, no se compila
  en el Docker de Linux, así que las tres líneas añadidas ahí no se han
  podido compilar ni probar en este entorno. Tampoco se ha podido medir
  el efecto real en calidad/rendimiento (necesita la 7700 XT real
  corriendo un stream).

## Cómo activar/probar una vez compilado en Windows

1. Web UI → pestaña **AMD AMF Encoder** → activar **"AMF High Motion
   Quality Boost"**.
2. Reiniciar el stream. Jugar algo con movimiento rápido (carreras,
   shooters) y comparar percepción de calidad con la opción activada vs.
   desactivada — es un cambio subjetivo, no hay métrica numérica directa
   en Apollo para esto.
3. Si el build falla al compilar `src/video.cpp` en Windows, revisar que
   `high_motion_quality_boost_enable` siga siendo el nombre exacto de la
   opción en la versión de ffmpeg que uses (viene de
   `third-party/build-deps/dist/Windows-AMD64`) — no debería haber
   cambiado, pero no se ha podido confirmar contra ese binario concreto
   desde este entorno.
