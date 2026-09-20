# Changelog del fork

Este archivo documenta los cambios propios de este fork personal de
[Apollo](https://github.com/ClassicOldSong/Apollo) — no reemplaza el
changelog del proyecto original (`docs/changelog.md`), que sigue
reflejando los cambios de LizardByte/ClassicOldSong.

**Importante sobre el estado de estos cambios**: cada feature nació en su
propio branch, partiendo de `master` en el commit `db2e4199`. A partir de
la versión **1.1.0**, todas las features listadas abajo ya están
fusionadas en `master` — las secciones por branch se conservan porque
tienen el detalle técnico y las notas de validación de cada una, pero ya
no representan código "sin publicar". Ver `README-fork.md` para el
estado general del fork.

## `master`

### 1.1.0 — Añadido
- **Fusión completa de `feature/all-in-one-testing`** — el resto de
  features de este fork (listadas abajo con su propia sección, con el
  detalle técnico completo) pasan a estar en `master`: backend de mando
  HIDMaestro, bitrate adaptativo en caliente (NVENC y AMD AMF), AMD AMF
  High Motion Quality Boost, y monitor virtual duplicado con la pantalla
  primaria (`virtual_display_duplicate_primary`). Confirmadas
  funcionales por el usuario en hardware real antes de fusionar — ver la
  sección de cada una para el detalle de qué se validó específicamente.

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
- **Símbolos de teclado equivocados en hosts con layout no-US** (`src/platform/windows/input.cpp`):
  conectando desde clientes que no pueden mapear limpiamente una tecla a
  layout US (confirmado con Moonlight para iOS), símbolos como `@`
  salían como el carácter equivocado (`"` en vez de `@` en un host
  español) — el cliente manda el combo VK+modificador que produciría el
  carácter deseado en un teclado US, marcado como "no normalizado", y
  Windows lo resolvía a través del layout real del host en vez del
  carácter que el usuario realmente quería. Para ese caso concreto
  (teclas de símbolo/dígito, host no-US), se recupera el carácter
  interpretando el combo bajo un layout US real y se inyecta
  directamente como Unicode. Confirmado por el usuario en hardware real
  (iPad + host Windows en español) — primer bug de esta sesión validado
  end-to-end, no solo compilado. Detalle en
  `docs/dev/keyboard-symbol-layout.md`.

### Añadido
- **CI en GitHub Actions** (`.github/workflows/build-windows.yml`,
  `build-linux.yml`) — compila cualquier branch en runners reales de
  Windows y Linux con un solo clic, sin instalar ningún toolchain en
  local. Recuperados y adaptados de los workflows originales de Apollo
  (borrados en algún punto del historial de upstream).

## `feature/frame-pacing` (mergeado en `master`, 1.0.0)

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

## `feature/hidmaestro-backend` (mergeado en `master`, 1.1.0)

### Añadido
- `input_backend` (Web UI: Input) — backend de mando alternativo vía
  [HIDMaestro](https://github.com/hifihedgehog/HIDMaestro), emulando un
  Xbox Series X|S con soporte de rumble de gatillos que ViGEmBus no
  puede ofrecer. Default `vigem` (sin cambios).
- `tools/hidmaestro-bridge/` — proceso .NET auxiliar nuevo que habla con
  el SDK de HIDMaestro (C#-only), controlado por Apollo vía JSON sobre
  stdin/stdout. Build detrás de `-DSUNSHINE_ENABLE_HIDMAESTRO=ON` (OFF
  por defecto).

**Estado**: no se pudo compilar ni ejecutar en el entorno de desarrollo
Linux (Windows-only, necesita .NET 10 SDK + Visual Studio) — compilado y
validado en CI de Windows, y confirmado funcional por el usuario en
hardware real antes de fusionar a `master`. Ver el aviso al principio de
`docs/dev/hidmaestro-backend.md` para la historia completa del
desarrollo (incluye dos correcciones de rumbo importantes).

## `feature/adaptive-bitrate` (mergeado en `master`, 1.1.0)

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
sintéticos de pérdida. Confirmado funcional por el usuario en hardware
real (GPU NVIDIA) antes de fusionar a `master`. El toggle vive en la
pestaña propia del encoder NVIDIA NVENC (no en Audio/Video general).
Detalle: `docs/dev/adaptive-bitrate.md`.

### Documentado (retomado en `feature/amd-amf-adaptive-bitrate`, ver abajo)
- Investigación original de por qué esto no se pudo hacer también para
  AMD en la misma feature (ffmpeg fija el bitrate del encoder AMF solo al
  inicializar, nunca por frame) queda en
  `docs/dev/amd-amf-adaptive-bitrate-future-plan.md` como contexto
  histórico — confirmó que el bitrate de AMF **sí es una propiedad
  dinámica de verdad**, lo que hizo viable la branch de abajo.

## `feature/amd-amf-adaptive-bitrate` (mergeado en `master`, 1.1.0)

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
Confirmado funcional por el usuario en hardware AMD real antes de
fusionar a `master`. El toggle vive tanto en la pestaña del encoder
NVIDIA NVENC como en la de AMD AMF Encoder (mismo valor de config,
compartido entre ambos encoders). Detalle:
`docs/dev/amd-amf-adaptive-bitrate.md`.

## `feature/amd-high-motion-quality-boost` (mergeado en `master`, 1.0.0)

### Añadido
- `amd_high_motion_quality_boost` (Web UI: pestaña "AMD AMF Encoder") —
  mejora la calidad percibida en movimiento rápido (paneos de cámara,
  acción rápida) para los tres codecs AMF (H.264, HEVC, AV1). No
  necesitó tocar ffmpeg — la opción (`high_motion_quality_boost_enable`)
  ya existía en el wrapper de ffmpeg que Apollo ya usa, solo no estaba
  expuesta en Apollo. Sin valor por defecto forzado.

**Estado**: solo la parte multiplataforma (`config.h`/`config.cpp`) se
pudo compilar en el entorno de desarrollo Linux — el mapeo real en
`src/video.cpp` está dentro de un `#ifdef _WIN32` (igual que HIDMaestro).
Compilado en CI de Windows real. Detalle:
`docs/dev/amd-high-motion-quality-boost.md`.

## `feature/virtual-display-duplicate` (mergeado en `master`, 1.1.0)

### Añadido
- `virtual_display_duplicate_primary` (Web UI: Audio/Video) — pone el
  monitor virtual que Apollo crea para cada cliente en modo
  duplicado/clonado con la pantalla física primaria, en vez de añadirlo
  siempre como pantalla extendida independiente (comportamiento
  anterior). **Activado por defecto**, a diferencia de toda otra feature
  de este fork — mutuamente excluyente con `isolated_virtual_display_option`
  ya existente (activar uno desactiva el otro, tanto en la Web UI como
  en tiempo de ejecución).
- `VDISPLAY::duplicateWithPrimaryDisplay()` — usa la misma API real de
  Windows CCD (`QueryDisplayConfig`/`SetDisplayConfig`) que ya usa
  `changeDisplaySettings2` en el mismo fichero, reasignando el
  `sourceInfo` del monitor virtual al de la pantalla primaria — así es
  como Windows representa un grupo de pantallas clonadas.

### Corregido
- **La resolución del monitor virtual se quedaba pegada a la primaria
  para siempre**: activar el modo duplicado cambia la resolución
  compartida del grupo clonado (así funciona un clone group de Windows
  — una sola superficie de origen para todo el grupo), pero nada
  restauraba la resolución original de la pantalla primaria al terminar
  la sesión — el código de limpieza (`terminate()`) se escribió pensando
  en el modo "isolated" anterior, que nunca tocaba la primaria.
  Arreglado capturando la resolución/refresco original antes de activar
  el modo duplicado y restaurándolo explícitamente al terminar la
  sesión (`VDISPLAY::restorePrimaryDisplayMode()`).

**Estado**: código exclusivo de Windows (`src/platform/windows/`), solo
se pudo compilar la parte multiplataforma en este entorno. Compilado en
CI de Windows real, incluido el fix de restauración de resolución.
Confirmado funcional por el usuario en hardware real antes de fusionar
a `master`. Detalle: `docs/dev/virtual-display-duplicate.md`.
