# Frame Pacing (Fase 3 — cierre de feature)

Este documento resume la feature ya implementada, para consulta rápida sin
tener que releer todo `frame-pacing-analysis.md` (que sigue siendo la
referencia detallada de investigación/diseño). Branch: `feature/frame-pacing`.

## Qué se añadió y por qué

Apollo ya tenía dos mecanismos de pacing (ver análisis), pero ninguno de
los dos era ajustable por el usuario ni daba visibilidad de lo que estaba
pasando. Se añadieron tres cosas, todas detrás de opciones que por defecto
reproducen el comportamiento de siempre:

1. **Tolerancia de snapping configurable** (`frame_pacing_tolerance_pct`,
   Web UI: pestaña Advanced): antes era un valor fijo en el código (25%,
   1/4 del intervalo ideal). Ahora se puede ajustar entre 5-100% sin
   recompilar.
2. **Suavizado opcional de ráfagas de red** (`frame_pacing_smooth_bursts`,
   Web UI: pestaña Advanced): cuando un frame codifica rápido y le sobra
   margen respecto al intervalo ideal, reparte su envío en vez de
   mandarlo lo más rápido posible — menos ráfaga percibida por el
   cliente. Los frames grandes (ej. keyframes) no se ven afectados.
3. **Telemetría de jitter** (solo logs, nivel debug/verbose, sin opción de
   usuario): cuánto se desvía la llegada real de cada frame respecto a la
   cadencia ideal, tanto en captura→codificación como en el envío de red.

## Diff conceptual: modificado vs. nuevo

**Modificado** (comportamiento existente, extendido sin cambiarlo por
defecto):
- `src/video.cpp`: la línea que calculaba `frame_variation_threshold` como
  `encode_frame_threshold / 4` ahora lee `config::video.frame_pacing_tolerance_pct`.
  Con el default (25.0), el cálculo da exactamente el mismo resultado que
  antes.
- `src/stream.cpp`: `ratecontrol_packets_in_1ms` (el presupuesto de envío
  por ms) ahora puede reducirse cuando `frame_pacing_smooth_bursts` está
  activo. Con el flag apagado (default), la línea original se ejecuta sin
  cambios.

**Nuevo** (no existía antes):
- Dos campos en `config::video_t` (`src/config.h`) y su registro en
  `src/config.cpp`.
- Dos loggers de diagnóstico (`capture_jitter_logger` en video.cpp,
  `network_pacing_interval_logger` en stream.cpp), siguiendo el patrón ya
  usado en el proyecto (`min_max_avg_periodic_logger`).
- Los dos campos en la Web UI (`Advanced.vue`), en el manifiesto
  (`config.html`), en las cadenas de texto (`en.json`) y en la
  documentación (`configuration.md`).
- `docs/dev/frame-pacing-analysis.md` (investigación previa) y este mismo
  archivo.

Nada del pipeline de captura, codificación o red se reestructuró — son
extensiones puntuales en los sitios exactos donde ya vivía la lógica.

## Impacto en compatibilidad con upstream (`git merge` futuro)

Riesgo **bajo-medio**, con un punto de atención concreto:

- Los cambios en `video.cpp` y `stream.cpp` son inserciones pequeñas y
  localizadas (una línea sustituida, un bloque nuevo de ~15-20 líneas cada
  uno) en funciones que no son objetivo típico de refactors grandes
  upstream. Un `git merge` debería aplicar limpio en la mayoría de casos.
- **Punto de atención real**: `cmake/packaging/linux.cmake` tiene
  `CPACK_DEBIAN_PACKAGE_SHLIBDEPS OFF` con un comentario admitiendo que
  está roto (ver `building-linux.md`). Si upstream lo arregla algún día,
  nuestros fixes de Docker (`libicu76` manual, etc.) quedarían
  redundantes — no rompería nada, pero convendría revisarlo tras un merge
  grande.
- `docs/configuration.md`, `config.html` y `en.json` son los más
  propensos a conflictos de merge (son archivos que upstream también toca
  a menudo al añadir sus propias opciones), pero los conflictos serían de
  los fáciles de resolver a mano (listas de opciones, no lógica).

## Qué requiere prueba en hardware/Windows real

- **Todo el efecto observable real** de `frame_pacing_smooth_bursts`: si
  de verdad reduce jitter percibido, requiere streaming real con
  Moonlight + una red con jitter/pérdida real. Aquí solo se confirmó que
  compila y que la lógica no introduce backlog en el peor caso (frames
  grandes), por análisis, no por medición.
- Los valores que reporta `capture_jitter_logger` / `network_pacing_interval_logger`
  con contenido real (juegos, movimiento) — aquí solo se generó una
  imagen dummy sin GPU, no hay datos reales que inspeccionar todavía.
- Efecto de ajustar `frame_pacing_tolerance_pct` a valores distintos del
  default (5-100%) sobre la fluidez percibida — puramente subjetivo,
  necesita ojos y un stream real.

## Cómo activar/probar una vez compilado en Windows

1. Compilar el branch `feature/frame-pacing` en Windows (ver
   `docs/building.md` del propio repo para dependencias de Windows —
   MSYS2, no cubierto por nuestro Dockerfile de Linux).
2. Abrir la Web UI de Apollo (`https://localhost:47990` o la IP del host)
   → pestaña **Advanced**.
3. Al final de la pestaña: **"Frame pacing tolerance (%)"** (numérico,
   default 25) y **"Smooth frame pacing bursts"** (checkbox, default
   desactivado).
4. Para probar el suavizado de ráfagas: activar el checkbox, guardar,
   reiniciar el stream desde Moonlight. Para ver el efecto hay que
   comparar percepción de fluidez con el checkbox activado vs.
   desactivado — no hay un indicador numérico en la UI todavía (la
   telemetría solo va a los logs).
5. Para ver la telemetría de jitter: arrancar Apollo con nivel de log
   `debug` o `verbose` (opción `min_log_level` ya existente en Apollo) y
   revisar el log de la app durante un stream activo — busca "Frame
   pacing:".
