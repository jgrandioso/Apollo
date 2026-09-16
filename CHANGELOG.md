# Changelog del fork

Este archivo documenta los cambios propios de este fork personal de
[Apollo](https://github.com/ClassicOldSong/Apollo) — no reemplaza el
changelog del proyecto original (`docs/changelog.md`), que sigue
reflejando los cambios de LizardByte/ClassicOldSong.

**Importante sobre el estado de estos cambios**: cada feature vive en su
propio branch, partiendo de `master` en el commit `db2e4199`. Nada de esto
está fusionado en `master` todavía — este changelog describe qué hay en
cada branch, no qué hay "publicado". Ver `README-fork.md` para el estado
de cada uno.

## `master`

### Corregido
- **Pipeline de build en Linux (`docker/debian-trixie.dockerfile`)**: 4
  bugs de packaging que impedían compilar el Apollo original sin
  modificar en este entorno de desarrollo — permiso perdido en
  `scripts/linux_build.sh`, paso de tests roto (`BUILD_TESTS` sin forma
  de activarse), nombre de artefacto `.deb` desactualizado tras el rename
  Sunshine→Apollo, y una dependencia (`libicu76`) que faltaba en la
  imagen final. Ninguno toca código de la aplicación. Detalle completo en
  `docs/dev/building-linux.md`.

## `feature/frame-pacing`

### Añadido
- `frame_pacing_tolerance_pct` (Web UI: Advanced) — hace configurable la
  tolerancia de "snapping" de timestamps captura→codificación, antes fija
  al 25% en el código. Default 25 reproduce el comportamiento anterior
  exacto.
- `frame_pacing_smooth_bursts` (Web UI: Advanced) — reparte el envío de
  red de frames que codifican rápido a lo largo de más del intervalo
  ideal, en vez de mandarlos lo más rápido posible. Default desactivado.
- Telemetría de jitter de diagnóstico (solo logs, sin opción de usuario).

**Estado**: compilado y validado — arranca idéntico al baseline con los
defaults. Efecto real del suavizado sin confirmar (necesita streaming
real). Detalle: `docs/dev/frame-pacing.md`.

## `feature/bandwidth-budget`

### Investigado y descartado
- Presupuesto de datos por sesión con aviso al cliente al agotarse.
  Bloqueado por una limitación real de protocolo: Apollo no tiene forma
  de mandar un mensaje de texto libre al cliente durante un stream
  activo. Sin código nuevo — solo el análisis queda documentado en
  `docs/dev/bandwidth-budget-analysis.md`.

## `feature/hidmaestro-backend`

### Añadido (experimental, no verificado de extremo a extremo)
- `input_backend` (Web UI: Input) — backend de mando alternativo vía
  [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro), emulando un
  Xbox Series X|S con soporte de rumble de gatillos que ViGEmBus no
  puede ofrecer. Default `vigem` (sin cambios).
- `tools/hidmaestro-bridge/` — proceso .NET auxiliar nuevo que habla con
  el SDK de HIDMaestro (C#-only), controlado por Apollo vía JSON sobre
  stdin/stdout. Build detrás de `-DSUNSHINE_ENABLE_HIDMAESTRO=ON` (OFF
  por defecto).

**Estado**: nada de este código se pudo compilar ni ejecutar en este
entorno (Windows-only, necesita .NET 10 SDK + Visual Studio). Si el
rumble de gatillos llega de verdad vía Windows.Gaming.Input/GameInput
sigue **sin resolver** tras una investigación considerable — ver el aviso
al principio de `docs/dev/hidmaestro-backend.md` para la historia
completa (incluye dos correcciones de rumbo importantes durante el
desarrollo).

## `feature/adaptive-bitrate`

### Añadido
- `adaptive_bitrate` y `adaptive_bitrate_floor_pct` (Web UI: Audio/Video)
  — baja (y recupera) el bitrate del encoder **en caliente, durante el
  stream**, reaccionando a los informes de pérdida de paquetes que
  Moonlight ya manda y que antes se descartaban. Convive con Warp Mode
  (multiplica sobre su valor, no lo sustituye). **Solo NVENC** (NVIDIA) —
  incluye H.264, HEVC y AV1, ya que los tres pasan por la misma sesión de
  encoder.
- `nvenc_base::reconfigure_bitrate()` — primer mecanismo que existe en
  Apollo para cambiar el bitrate de una sesión de encoder ya activa sin
  destruirla (usa `NvEncReconfigureEncoder` de la API real de NVIDIA).

**Estado**: compila limpio en Linux (NVENC es multiplataforma vía CUDA).
El algoritmo de decisión se validó de forma aislada con escenarios
sintéticos de pérdida. `NvEncReconfigureEncoder` en sí no se ha podido
ejecutar (necesita GPU NVIDIA real). Detalle: `docs/dev/adaptive-bitrate.md`.

### Documentado (sin implementar)
- Investigación de por qué esto no se pudo hacer también para AMD en la
  misma feature (ffmpeg fija el bitrate del encoder AMF solo al
  inicializar, nunca por frame) y plan a futuro si se retoma:
  `docs/dev/amd-amf-adaptive-bitrate-future-plan.md`.
