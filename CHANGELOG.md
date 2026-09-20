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
- **Bug real de Apollo/upstream — perfil H.264 del encoder AMD AMF roto**:
  `src/video.cpp` leía un campo (`cfg.profile`) que `video::config_t`
  nunca ha tenido — nunca se detectó porque ese código Windows-only nunca
  se había compilado en CI. Sustituido por un valor fijo (`"high"`),
  mismo patrón que usan NVENC/QuickSync.
- **Bug real de Apollo/upstream — cuelgue permanente al cerrar cualquier
  sesión de encoder AMD AMF**: `~avcodec_encode_session_t()` intentaba
  vaciar el encoder con una llamada escrita para FFmpeg 8.0, mientras el
  FFmpeg realmente usado (`third-party/build-deps`, fijado a un commit
  de mediados de 2025) es anterior a esa migración. Sin este fix, Apollo
  ni siquiera arrancaba con una GPU AMD activa — se colgaba para siempre
  en `probe_encoders()`. Confirmado en hardware real (iGPU de un
  portátil ASUS). Ver [issue upstream #1588](https://github.com/ClassicOldSong/Apollo/issues/1588).
- **Bug real de Apollo/upstream — SudoVDA nunca funciona en una build
  compilada desde cero**: `drivers/sudovda/install.bat` depende de dos
  binarios (`nefconc.exe`, la herramienta que crea el nodo de
  dispositivo, y `SudoVDA.dll`, el driver UMDF2 en sí) que **ni este
  fork, ni el repo público de ClassicOldSong/Apollo, ni el propio repo
  del driver SudoVDA los incluyen o descargan en ningún sitio**. Sin
  ellos, el dispositivo nunca se crea (o se crea sin driver asociado) y
  la Web UI muestra "Driver status: Uninitialized" para siempre — bug
  ampliamente reportado sin resolver
  ([#1044](https://github.com/ClassicOldSong/Apollo/issues/1044),
  [#1360](https://github.com/ClassicOldSong/Apollo/issues/1360), ambas
  con ese título exacto). Arreglado extrayendo ambos binarios del
  instalador oficial `v0.4.6` (verificado que su `sudovda.cat`/`.cer`
  son idénticos byte a byte a los que ya trae Apollo, para no romper la
  firma del catálogo) y vendorizándolos en
  `src_assets/windows/drivers/sudovda/`. Confirmado en dos PCs Windows
  reales distintos.
- **Rendimiento — trabajo repetido innecesario en el envío de cada frame
  de vídeo** (`src/stream.cpp`): la matriz de paridad Reed-Solomon se
  reconstruía en cada frame (el camino de audio ya la construye una sola
  vez, por comparación se detectó el descuido) — ahora se cachea por
  pareja `(data_shards, parity_shards)`. `concat_and_insert()` asignaba
  un buffer de heap nuevo en cada frame — ahora reutiliza un buffer
  `thread_local`, con cuidado explícito de no filtrar bytes del frame
  anterior en la cabecera. Sin cambio de comportamiento observable,
  verificado compilando en Linux y Windows.

### Añadido
- **CI en GitHub Actions** (`.github/workflows/build-windows.yml`,
  `build-linux.yml`) — compila cualquier branch en runners reales de
  Windows y Linux con un solo clic, sin instalar ningún toolchain en
  local. Recuperados y adaptados de los workflows originales de Apollo
  (borrados en algún punto del historial de upstream).

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

### Documentado (retomado en `feature/amd-amf-adaptive-bitrate`, ver abajo)
- Investigación original de por qué esto no se pudo hacer también para
  AMD en la misma feature (ffmpeg fija el bitrate del encoder AMF solo al
  inicializar, nunca por frame) queda en
  `docs/dev/amd-amf-adaptive-bitrate-future-plan.md` como contexto
  histórico — confirmó que el bitrate de AMF **sí es una propiedad
  dinámica de verdad**, lo que hizo viable la branch de abajo.

## `feature/amd-amf-adaptive-bitrate`

### Añadido
- Bitrate adaptativo también para AMD AMF (H.264/HEVC/AV1), reutilizando
  el mismo mecanismo de `feature/adaptive-bitrate` (informes de pérdida
  de Moonlight → `bitrate_scale_events` → `encode_run()`). A diferencia
  de NVENC (que necesita una llamada explícita de "reconfigure"), aquí
  basta con actualizar `avcodec_ctx->bit_rate` directamente — el parche
  de ffmpeg (ver abajo) recoge el cambio en el siguiente frame por su
  cuenta. Sin flags nuevos: reutiliza `adaptive_bitrate`/
  `adaptive_bitrate_floor_pct`, ya existentes.
- **Parche de ffmpeg** en un fork aparte,
  [`jgrandioso/build-deps`](https://github.com/jgrandioso/build-deps)
  (`patches/FFmpeg/FFmpeg/AMF/01-dynamic-bitrate.patch`) — hace que el
  wrapper de AMF de ffmpeg reaccione a cambios de bitrate en caliente, en
  vez de fijarlo solo una vez al inicializar. Publicado como
  [Release descargable](https://github.com/jgrandioso/build-deps/releases/tag/amf-dynamic-bitrate-windows-amd64),
  al margen del submódulo `third-party/build-deps` de Apollo (que sigue
  fijado a un commit obsoleto de una branch `dist` ya retirada en
  upstream — ver el análisis).

### Corregido (prerrequisito, hereda a todas las branches vía `master`)
- Cuelgue permanente al cerrar cualquier sesión de encoder AMD AMF
  (incluida la sesión de prueba de un frame que usa `probe_encoders()` al
  arrancar) — causado por un desajuste de versión entre un código de
  drenado escrito para FFmpeg 8.0 y el FFmpeg realmente usado (anterior a
  esa migración). Sin este fix no se podía ni arrancar Apollo con AMD.
  Confirmado en hardware real. Ver commit "fix(video): remove
  encoder-teardown drain that hangs forever on AMD AMF" en `master`, e
  [issue upstream #1588](https://github.com/ClassicOldSong/Apollo/issues/1588).

**Estado**: compila limpio de verdad en CI de Windows real (no
simulado). El parche de ffmpeg aplica limpio, verificado con `git apply
--check`. Durante la validación se encontró y arregló un choque de
cabeceras (`ffnvcodec/nvEncodeAPI.h`) que rompía la compilación nativa de
NVENC de Apollo — aislado con una prueba de control antes de arreglarlo.
Ejecución real sobre GPU AMD (`SetProperty` en una sesión AMF activa)
sigue sin confirmarse — necesita hardware Windows real. Detalle:
`docs/dev/amd-amf-adaptive-bitrate.md`.

## `feature/amd-high-motion-quality-boost`

### Añadido
- `amd_high_motion_quality_boost` (Web UI: pestaña "AMD AMF Encoder") —
  mejora la calidad percibida en movimiento rápido (paneos de cámara,
  acción rápida) para los tres codecs AMF (H.264, HEVC, AV1). No
  necesitó tocar ffmpeg — la opción (`high_motion_quality_boost_enable`)
  ya existía en el wrapper de ffmpeg que Apollo ya usa, solo no estaba
  expuesta en Apollo. Sin valor por defecto forzado.

**Estado**: solo la parte multiplataforma (`config.h`/`config.cpp`) se
pudo compilar aquí — el mapeo real en `src/video.cpp` está dentro de un
`#ifdef _WIN32` (igual que HIDMaestro), sin compilar ni probar en este
entorno. Detalle: `docs/dev/amd-high-motion-quality-boost.md`.
