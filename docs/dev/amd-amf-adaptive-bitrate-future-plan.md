# Plan a futuro: bitrate adaptativo para AMD (AMF nativo)

**Estado: no iniciado.** Esto es un documento de planificación, no una
feature en marcha — no hay branch creado todavía. Se escribe ahora
(2026-09-16) tras terminar `feature/adaptive-bitrate` (NVENC), a petición
tuya, para dejar constancia de por dónde habría que empezar si algún día
se decide abordarlo.

## Por qué no se hizo junto con NVENC

Investigado hoy mismo, con el código fuente real de ffmpeg
(`libavcodec/amfenc.c` y `amfenc_h264.c`, descargados de
github.com/FFmpeg/FFmpeg):

- Apollo **no habla con el SDK de AMD (AMF) directamente** para su
  encoder `amdvce` — pasa por el wrapper genérico de ffmpeg/libavcodec
  (`h264_amf`, `hevc_amf`, `av1_amf`).
- Ese wrapper de ffmpeg **fija el bitrate una sola vez, en la
  inicialización** (`amf_encode_init_h264`). La función que procesa cada
  frame (`ff_amf_receive_packet`) no vuelve a tocar el bitrate nunca.
- El propio SDK de AMD probablemente sí soporta cambiar el bitrate en
  caliente sobre un componente ya activo (es un patrón conocido de su
  interfaz tipo-COM, `AMFPropertyStorage::SetProperty` — el propio código
  de ffmpeg tiene un comentario `// Dynamic parameters` cerca de donde fija
  el rate control), pero **ffmpeg no lo expone**, así que no sirve de nada
  para nosotros a través de esa vía.

Conclusión: no es una extensión del trabajo de NVENC, es un proyecto
aparte del mismo tamaño (o mayor).

## Restricción de entorno importante (igual que HIDMaestro)

`amdvce` en Apollo usa `AV_HWDEVICE_TYPE_D3D11VA` (DirectX 11) — **es
Windows-only**, confirmado en `src/video.cpp` (registrado solo bajo
`#ifdef _WIN32`). En Linux, las GPU de AMD pasan por el encoder `vaapi`
genérico, no por AMF. Esto significa que, igual que con HIDMaestro,
**este trabajo futuro no se podría compilar ni probar en este entorno de
desarrollo Linux** — necesitaría el host Windows real con una GPU AMD.

## Las dos vías reales (mismo dilema que evaluamos antes de elegir NVENC hoy)

1. **Parchear ffmpeg** para que su wrapper AMF acepte cambios de bitrate
   post-init (llamar `SetProperty` de nuevo cuando `avctx->bit_rate`
   cambia). Pros: cambio pequeño y localizado si el SDK realmente lo
   soporta como sugiere el comentario "Dynamic parameters". Contras:
   Apollo tendría que mantener un fork/parche de ffmpeg, no solo de su
   propio código — mucho más superficie de mantenimiento a largo plazo
   que nada de lo que hemos tocado hasta ahora.
2. **Wrapper nativo del SDK AMF**, paralelo a `src/nvenc/nvenc_base.*`,
   sin pasar por ffmpeg en absoluto (igual que NVENC no pasa por ffmpeg).
   Pros: control total, mismo patrón ya validado hoy. Contras: hay que
   vendorizar las cabeceras del SDK de AMD (equivalente a
   `third-party/nv-codec-headers/`, que hoy no existe en el repo para
   AMD) y escribir la creación/gestión de sesión de encoder desde cero —
   básicamente repetir `nvenc_base.cpp` (700+ líneas) para un SDK
   distinto.

## Investigación previa necesaria antes de diseñar (Fase 1, cuando toque)

1. **Licencia y disponibilidad de las cabeceras del SDK AMF de AMD**
   (repo público: `github.com/GPUOpen-LibrariesAndSDKs/AMF`) — confirmar
   que se pueden vendorizar igual que se hizo con
   `third-party/nv-codec-headers/` para NVIDIA.
2. **Confirmar de verdad** (leyendo la documentación/cabeceras reales del
   SDK, no asumiendo por el comentario de ffmpeg) que
   `AMF_VIDEO_ENCODER_TARGET_BITRATE` es realmente una propiedad
   "dinámica" modificable sin recrear el `AMFComponent`.
3. Decidir entre las dos vías de arriba — probablemente la (2), por
   coherencia con cómo se hizo NVENC y para no depender de mantener un
   fork de ffmpeg.
4. Repetir el mismo proceso de esta sesión: análisis, documento de
   diseño, tu aprobación, implementación, documentación — igual que con
   las cuatro features de hoy.

## Actualización (2026-09-16): confirmado técnicamente, pospuesto por recursos

- **Confirmado con la cabecera oficial de AMD**
  (`amf/public/include/components/VideoEncoderVCE.h`, sección
  `// Dynamic properties - can be set at any time`): `TARGET_BITRATE` y
  `PEAK_BITRATE` sí son propiedades dinámicas de verdad — más simple
  incluso que el mecanismo de NVIDIA (no hace falta una llamada de
  "reconfigure" especial). La vía (1) del análisis original (parchear
  ffmpeg) es técnicamente viable.
- **Por qué se pospone**: el pipeline de `LizardByte/build-deps` compila
  el ffmpeg de Windows en un **runner de Windows real de GitHub Actions**
  (`windows-2022`), no por cross-compilación desde Linux — confirmado
  revisando su `.github/workflows/ci.yml`. El build en sí no consumiría
  disco de este servidor si se hace vía GitHub Actions (el plan gratuito
  incluye runners Windows), pero sigue siendo un proyecto aparte con su
  propio repo, su propio pipeline y su propio tiempo de configuración —
  no encaja ahora mismo con el margen de disco ni el tiempo disponible.
- **Para cuando se retome**: no haría falta montar nada de esto en este
  servidor — fork de `build-deps`, el parche, y dejar que la CI de GitHub
  (gratuita) compile los `.a` de Windows. Solo haría falta descargar el
  artefacto final (pequeño, ~46MB a juzgar por el tamaño actual de
  `third-party/build-deps/dist/Windows-AMD64`) y apuntar Apollo a él.

## Aviso

Esto no está priorizado ni comprometido a ningún plazo — es solo el
punto de partida documentado para cuando decidas retomarlo.
