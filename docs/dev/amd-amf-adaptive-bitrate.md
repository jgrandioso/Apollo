# Bitrate Adaptativo para AMD AMF (Fase 3 — cierre de feature)

Branch: `feature/amd-amf-adaptive-bitrate` (parte de
`feature/adaptive-bitrate`, el MVP de NVENC). Lee primero
`docs/dev/amd-amf-adaptive-bitrate-analysis.md` para la investigación y
el diseño completo.

## Qué se añadió y por qué

El MVP de bitrate adaptativo (`feature/adaptive-bitrate`) solo funciona
con NVENC — los encoders vía ffmpeg (software, VA-API, QuickSync, AMF)
quedaron fuera porque el wrapper de ffmpeg para AMF solo fija el bitrate
una vez, al inicializar el encoder, y nunca lo vuelve a mirar. Esta
feature cierra ese hueco específicamente para AMD, para que el bitrate
adaptativo reaccione a la pérdida de paquetes real también en hardware
AMD, no solo NVIDIA.

Requiere dos piezas nuevas que no existían en ninguna feature anterior de
este fork: un **parche de ffmpeg** (mantenido en un fork aparte,
`jgrandioso/build-deps`) y una CI que lo compile y lo entregue a Apollo
sin depender del submódulo git roto.

## Diff conceptual: modificado vs. nuevo

**Repo nuevo**: `jgrandioso/build-deps` (fork de `LizardByte/build-deps`).
- `patches/FFmpeg/FFmpeg/AMF/01-dynamic-bitrate.patch` — el parche en sí.
  Añade `applied_bitrate` a `AMFEncoderContext` (`amfenc.h`) y una función
  `amf_apply_dynamic_bitrate()` + su llamada desde
  `ff_amf_receive_packet()` (`amfenc.c`). Verificado con `git apply --check`
  contra el commit exacto de ffmpeg que compila build-deps
  (`bf1b838f`, `release/9.0`).
- `.github/workflows/ci.yml`: matriz recortada de 10 plataformas a solo
  `Windows-AMD64` (lo único que necesita Apollo); quitados los jobs
  `release-setup`/`release` (el primero se rompe con `workflow_dispatch` —
  su script asume un evento `push` real con lista de commits — y el
  segundo ya estaba condicionado a `startsWith(github.repository, 'LizardByte/')`,
  nunca iba a ejecutarse en un fork de todas formas).
- Publicado como [Release público](https://github.com/jgrandioso/build-deps/releases/tag/amf-dynamic-bitrate-windows-amd64)
  (no como branch `dist` — esa branch ya no existe en absoluto en
  upstream, ver el análisis — ni vía la opción `FFMPEG_PREPARED_BINARIES`
  del CMake de Apollo, que usa una lista de librerías incompleta).

**Modificado en Apollo**:
- `src/video.cpp`: el bucle de `bitrate_scale_events` en `encode_run()`
  (ya existente para NVENC) gana una rama `else if (encoder.name == "amdvce")`
  que hace `dynamic_cast<avcodec_encode_session_t *>` y actualiza
  `avcodec_ctx->bit_rate` directamente (en bps, `* 1000` desde el kbps que
  usa `config.bitrate`). No hace falta ningún método de "reconfigure" como
  en NVENC — el parche de ffmpeg ya recoge el cambio en el siguiente
  frame por su cuenta.
- `.github/workflows/build-windows.yml` (solo en esta branch): nuevo paso
  que descarga el asset del Release de `jgrandioso/build-deps` y
  sobrescribe `third-party/build-deps/dist/Windows-AMD64/` antes de
  invocar cmake — el submódulo real sigue fijado a su commit obsoleto
  (branch `dist` retirada), sin tocarlo.

**Nuevo**: nada más en Apollo — a diferencia del MVP de NVENC (que
necesitó un método nuevo `reconfigure_bitrate()` y persistir el
`NV_ENC_CONFIG`), aquí la propiedad dinámica de AMF hace que el cambio
sea mucho más simple: una sola línea en el punto de dispatch existente.

## Impacto en compatibilidad con upstream

**Medio.** El cambio en `video.cpp` es una rama `else if` aislada,
aditiva. El riesgo real está en `jgrandioso/build-deps`: si
`LizardByte/build-deps` avanza su propio pin de ffmpeg en el futuro
(cosa que hará, dado lo desactualizado que está el que usa Apollo hoy),
el parche habría que reaportarlo contra el nuevo código de
`amfenc.c`/`amfenc_h264.c` — no es un fork descartable de una vez, es
algo que mantener activamente si upstream se mueve.

## Bug encontrado y arreglado en el camino (no es parte de esta feature, pero la bloqueaba)

`~avcodec_encode_session_t()` se colgaba para siempre al destruir
cualquier sesión de encoder AMF (incluida la sesión de prueba de un solo
frame que usa `probe_encoders()` al arrancar) — causado por un desajuste
de versión entre el código de drenado (escrito para FFmpeg 8.0) y el
FFmpeg realmente pinneado por `build-deps` (anterior a esa migración).
Arreglado en `master` y heredado por todas las branches — no es
específico de esta feature, pero sin él no se podía compilar ni arrancar
nada relacionado con AMD AMF en absoluto. Detalle completo del bug y su
causa raíz (confirmado contra el propio issue upstream): commit
"fix(video): remove encoder-teardown drain that hangs forever on AMD AMF"
en `master`.

## Qué se validó aquí

- **Compila limpio de verdad**, confirmado en CI de Windows real (no
  simulado): run exitosa en `jgrandioso/Apollo`, con el ffmpeg parcheado
  descargado y enlazado correctamente.
- **El parche de ffmpeg aplica limpio** contra el commit exacto que
  compila `build-deps`, verificado con `git apply --check` antes de
  publicarlo (no se asumió que aplicaría, se comprobó).
- **Bug real encontrado durante la validación**: un choque de cabeceras
  (`ffnvcodec/nvEncodeAPI.h`, presente tanto en el FFmpeg descargado como
  en el submódulo `nv-codec-headers` propio de Apollo, con rutas de
  búsqueda del compilador en el orden equivocado) rompía la compilación
  nativa de NVENC de Apollo. Se aisló la causa con una prueba de control
  (relanzar `feature/adaptive-bitrate` sin tocar, que sí compiló bien,
  descartando una deriva de entorno) antes de aplicar el fix — borrar la
  copia de ffmpeg del directorio descargado, ya que Apollo no la necesita
  para nada.

## Qué NO se pudo validar aquí

- **`SetProperty()` ejecutándose de verdad sobre una sesión AMF activa**
  — necesita GPU AMD real con AMF funcionando, que no existe en este
  entorno de desarrollo Linux.
- **Funcionamiento en hardware real todavía no confirmado por otra vía**:
  el hardware de prueba disponible (iGPU de un portátil ASUS) tuvo
  problemas propios primero con el cuelgue de arriba, y su estado de AMF
  tras solucionarlo (driver reinstalado) seguía sin confirmarse al cierre
  de esta feature — ver la conversación de depuración para el detalle,
  no es un problema de esta feature en sí sino del entorno de prueba
  disponible.
- Todo lo demás que ya estaba sin validar en el MVP de NVENC (calibración
  de los umbrales de severidad/inestabilidad, convivencia real con Warp
  Mode end-to-end) aplica igual aquí, al compartir la misma lógica de
  `stream.cpp`.

## Cómo activar/probar una vez en el host real (con GPU AMD)

1. Igual que NVENC: Web UI → **Audio/Video** → activar **"Adaptive
   Bitrate"**, ajustar **"Adaptive Bitrate Floor (%)"** si hace falta.
2. Confirmar en el log que el encoder activo es `amdvce` (busca
   `Creating encoder [h264_amf]` o equivalente HEVC/AV1) — si el sistema
   cae a `software` por cualquier motivo, esta feature no hace nada (a
   propósito, mismo comportamiento que NVENC con encoders no soportados).
3. Degradar la red del cliente a propósito y revisar los logs — busca
   `"Adaptive bitrate:"`, igual que con NVENC.
4. Si algo va mal específicamente con AMF (no con la lógica de
   bitrate en sí), revisar primero si es el mismo tipo de problema de
   driver/runtime AMF documentado durante esta sesión, antes de asumir
   que es un bug de esta feature.
