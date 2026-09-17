# Análisis: Bitrate Adaptativo para AMD AMF (Fase 1)

Continuación de `docs/dev/amd-amf-adaptive-bitrate-future-plan.md` (el
documento de planificación original, pospuesto por recursos) y de
`docs/dev/adaptive-bitrate-analysis.md` (el MVP de NVENC, del que esta
feature hereda toda la plomería del lado Apollo). Branch:
`feature/amd-amf-adaptive-bitrate`.

## Punto de partida: lo que ya sabíamos

Del documento de planificación anterior, confirmado contra la cabecera
oficial de AMD (`VideoEncoderVCE.h`, sección "Dynamic properties - can be
set at any time"): `AMF_VIDEO_ENCODER_TARGET_BITRATE` y `PEAK_BITRATE` sí
son propiedades dinámicas de verdad sobre un `AMFComponent` ya activo —
más simple incluso que el mecanismo de NVIDIA (no hace falta una llamada
de "reconfigure" especial, solo `SetProperty`).

El bloqueo no era técnico, era de superficie del proyecto: Apollo no
habla con el SDK de AMF directamente (a diferencia de NVENC, que tiene su
propio wrapper nativo en `src/nvenc/`) — pasa por el wrapper genérico de
ffmpeg (`h264_amf`/`hevc_amf`/`av1_amf` en `libavcodec/amfenc*.c`), y ese
wrapper solo fija el bitrate una vez, al inicializar. Tocar eso significa
mantener un parche sobre ffmpeg, no solo código propio de Apollo.

## Código real de ffmpeg (verificado contra el commit exacto que compila `build-deps`)

`third-party/FFmpeg/FFmpeg` en `LizardByte/build-deps` está fijado a
`bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa` (rama `release/9.0`). Se bajó
el contenido real de esa versión exacta (no `master` de ffmpeg, que podría
no coincidir) para diseñar el parche:

- `libavcodec/amfenc_h264.c` (y sus equivalentes `_hevc.c`/`_av1.c`):
  `amf_encode_init_h264()` fija `AMF_VIDEO_ENCODER_TARGET_BITRATE` y
  `PEAK_BITRATE` una sola vez, con `AMF_ASSIGN_PROPERTY_INT64`.
- `libavcodec/amfenc.c`: `ff_amf_receive_packet()`, la función que
  procesa cada frame — **compartida entre los tres codecs** — nunca
  vuelve a mirar `avctx->bit_rate` después del init.
- `libavcodec/amfenc.h`: la struct compartida `AMFEncoderContext` no
  tiene ni un campo de "último bitrate aplicado" ni sabe a qué codec
  pertenece la sesión — pero `avctx->codec_id` (del `AVCodecContext`
  estándar) sí está siempre disponible en `ff_amf_receive_packet`, así
  que sirve para decidir qué nombre de propiedad usar por codec.

## Diseño del parche

1. `AMFEncoderContext` (amfenc.h): nuevo campo `int64_t applied_bitrate`.
2. `amfenc.c`: nueva función estática `amf_apply_dynamic_bitrate()` que
   hace `switch` sobre `avctx->codec_id` (H264/HEVC/AV1) y llama
   `AMF_ASSIGN_PROPERTY_INT64` con el nombre de propiedad correcto para
   cada uno — `TARGET_BITRATE` siempre, `PEAK_BITRATE` solo si
   `rate_control_mode` es CBR (igual que hace cada función de init).
3. En `ff_amf_receive_packet()`, justo tras el check de
   `if (!ctx->encoder)`: si `avctx->bit_rate != ctx->applied_bitrate`,
   llama a la función de arriba y actualiza `applied_bitrate`.

No se tocan las rutas VBR/peak-constrained (`rc_max_rate`) — fuera de
alcance, ya que el bitrate adaptativo de Apollo solo tiene sentido con
CBR (mismo alcance que ya tiene el MVP de NVENC).

## Dónde vive el parche: `patches/FFmpeg/FFmpeg/AMF/`

`LizardByte/build-deps` ya tiene un mecanismo de parches por componente
(`cmake/ffmpeg/amf.cmake` aplica cualquier `.patch` en
`patches/FFmpeg/FFmpeg/AMF/*.patch` vía `git apply`/`patch -p1`) — no
hizo falta tocar su CMake para nada, solo añadir el fichero de parche
siguiendo el mismo patrón que ya usan sus parches de `cbs/`, `vulkan/`,
`nv-codec-headers/`, etc.

## Problema encontrado en el camino: la branch `dist` de la que depende Apollo ya no existe

`.gitmodules` fija `third-party/build-deps` a `branch = dist`, pero esa
branch **ya no existe** en `LizardByte/build-deps` (confirmado:
`git ls-remote --heads` no la lista, y la API devuelve 404 al pedirla
directamente). El commit al que está fijado Apollo (`a9a7f863...`) es un
resto huérfano — solo alcanzable por su hash exacto, no por ninguna
referencia viva. Probablemente LizardByte migró a distribuir los binarios
vía GitHub Releases (el job `release` que vimos y desactivamos en
nuestro fork de `build-deps` hace justo eso).

Esto significa que el submódulo de Apollo no se puede actualizar nunca
más con `git submodule update --remote` — apunta a un canal de
distribución retirado. Es un problema más amplio que esta feature (afecta
también a por qué `build-deps` estaba tan desactualizado que causó el
bug del bloqueo de AMF al cerrar sesión, ver más abajo), pero no se
aborda aquí — se documenta como hallazgo, no se toca el submódulo por
defecto de las demás branches.

**Cómo se evita para esta feature**: la CI de esta branch descarga el
FFmpeg parcheado directamente como asset de un Release público en
`jgrandioso/build-deps`, y sobrescribe `third-party/build-deps/dist/Windows-AMD64/`
con su contenido antes de compilar — sin tocar el pin del submódulo git
en absoluto. Se descartó la opción `-DFFMPEG_PREPARED_BINARIES` de
Apollo (un "escape hatch" que ya existía en su CMake) porque esa ruta usa
una lista de librerías más corta (le faltan `libx264.a`, `libx265.a`,
`libSvtAv1Enc.a`, HDR10+) — rompería el enlazado.

## Prerrequisito descubierto en el camino: AMF se colgaba para siempre al cerrar cualquier sesión de prueba

Sin relación directa con el bitrate adaptativo, pero bloqueaba poder
probar nada de AMD en absoluto: `~avcodec_encode_session_t()` (destructor,
`src/video.cpp:327` en su momento) intentaba vaciar el encoder al
cerrarlo con una llamada escrita asumiendo el comportamiento de FFmpeg
8.0, mientras `third-party/build-deps` seguía fijado a una versión
anterior a esa migración — en AMD AMF esa llamada nunca vuelve
(NVENC sí, por eso nadie lo detectó antes). Arreglado en `master` y
heredado por todas las branches — ver el commit
"fix(video): remove encoder-teardown drain that hangs forever on AMD AMF"
y el issue upstream
[ClassicOldSong/Apollo#1588](https://github.com/ClassicOldSong/Apollo/issues/1588).
Confirmado en hardware real (iGPU AMD de un portátil ASUS) antes de este
parche: el arranque se colgaba para siempre en `probe_encoders()`, nunca
llegaba a levantar la Web UI.

## Alcance confirmado

Igual que el MVP de NVENC: solo el encoder `amdvce` (H264/HEVC/AV1 vía
AMF). Software, VA-API y QuickSync no tienen este parche y no se ven
afectados — `dynamic_cast` + comprobación de `encoder.name` en el lado
Apollo aseguran que la reconfiguración solo se intenta cuando el encoder
activo es AMD.

## Riesgos

- **No se ha ejecutado ni una sola vez sobre una sesión AMF real** en
  este entorno de desarrollo (Linux, sin GPU AMD, y con AMF Windows-only).
  El parche compila y aplica limpio, pero que `SetProperty` de verdad
  cambie el bitrate en vivo sin errores del driver es una incógnita hasta
  probarlo en el host Windows real.
- **Depende de un fork con su propio pipeline** (`jgrandioso/build-deps`)
  que hay que mantener sincronizado si upstream mueve la versión de
  ffmpeg — más superficie de mantenimiento que cualquier otra feature de
  este proyecto hasta ahora.
- El propio driver AMF del hardware de prueba disponible (iGPU de un
  portátil con soporte de fabricante ya descontinuado) ha dado problemas
  independientes de este trabajo — ver `docs/dev/amd-amf-adaptive-bitrate.md`
  para el estado de esa validación.

## Decisión

Se implementa siguiendo exactamente el diseño de arriba: parche mínimo en
`ff_amf_receive_packet`, sin tocar las rutas VBR, mismo mecanismo de
`bitrate_scale_events` que ya existía para NVENC, extendido con una rama
para `avcodec_encode_session_t` cuando `encoder.name == "amdvce"`. Ver
`docs/dev/amd-amf-adaptive-bitrate.md` para el cierre.
