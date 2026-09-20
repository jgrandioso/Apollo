# Bitrate Adaptativo Real (Fase 3 — cierre de feature)

Branch: `feature/adaptive-bitrate`. Lee primero
`docs/dev/adaptive-bitrate-analysis.md` para la investigación y el diseño
completo.

## Qué se añadió y por qué

Warp Mode ya existía, pero solo corrige el bitrate una vez al arrancar el
stream según el framerate — no reacciona a la red durante la sesión.
Moonlight ya manda informes periódicos de pérdida de paquetes
(`IDX_LOSS_STATS`) que Apollo recibía y tiraba. Esta feature usa ese dato
real para bajar (y recuperar) el bitrate del encoder **mientras el stream
está activo**, sin cortes — algo que hoy no existía en absoluto (el
bitrate se fijaba una sola vez al crear la sesión de encoder).

Detrás de dos flags nuevos: `adaptive_bitrate` (default apagado) y
`adaptive_bitrate_floor_pct` (default 50%, nunca baja de la mitad del
bitrate ya ajustado por Warp Mode).

## Diff conceptual: modificado vs. nuevo

**Modificado**:
- `src/nvenc/nvenc_base.h` / `.cpp`: se guarda una copia del `NV_ENC_CONFIG`
  usado al crear el encoder (antes era una variable local que se perdía),
  para poder reutilizarla en el nuevo método `reconfigure_bitrate()`.
- `src/video.cpp`: `nvenc_encode_session_t` gana un método
  `reconfigure_bitrate()` (mismo patrón que el `invalidate_ref_frames()`
  ya existente); `encode_run()` consume el nuevo evento de mail
  `bitrate_scale` en su bucle principal, igual que ya hacía con
  `idr_events`.
- `src/stream.cpp`: el handler de `IDX_LOSS_STATS` (antes solo logueaba)
  ahora calcula la señal de red y, si corresponde, publica el nuevo
  evento.
- `src/globals.h`, `src/config.h`, `src/config.cpp`: nuevo evento de mail
  y los dos flags de configuración.

**Nuevo**:
- `update_adaptive_bitrate_scale()` en `stream.cpp` — la función que
  convierte la ventana de informes de pérdida en un multiplicador de
  bitrate.
- `nvenc_base::reconfigure_bitrate()` — usa `NvEncReconfigureEncoder`
  (API real de NVIDIA, cabecera vendorizada en `third-party/nv-codec-headers/`)
  para cambiar el bitrate sin destruir la sesión.
- Web UI (`NvidiaNvencEncoder.vue`), `config.html`, `en.json`,
  `configuration.md` — los dos flags expuestos y documentados. Viven en
  la pestaña propia del encoder NVIDIA NVENC, no en Audio/Video general,
  ya que solo funcionan con encoders concretos (2026-09-20, movidos de
  `DisplayModesSettings.vue` tras confusión sobre dónde vivían). Cuando
  se añadió el soporte de AMD AMF (`amd-amf-adaptive-bitrate`) el mismo
  flag pasó a usarlo también ese encoder, así que el checkbox se
  duplicó en `AmdAmfEncoder.vue` también - mismo `v-model`, ambas
  pestañas controlan el mismo valor de config (2026-09-20, se detectó
  porque los usuarios de AMD no veían la opción en ningún sitio).

Nada de esto toca la ruta de los encoders vía ffmpeg (software, VA-API,
QuickSync, AMD AMF) — quedan sin esta feature en este MVP, tal como se
decidió en el análisis previo.

## Impacto en compatibilidad con upstream

**Bajo-medio.** Los cambios en `nvenc_base.h`/`.cpp` son aditivos (nuevos
miembros/método, nada eliminado ni reordenado). `video.cpp`/`stream.cpp`
son inserciones localizadas en funciones concretas. El punto de fricción
más probable en un `git merge` futuro: si upstream reestructura
`NV_ENC_CONFIG`/`NV_ENC_INITIALIZE_PARAMS` en `create_encoder()` (cambia
bastante entre versiones del SDK de NVIDIA), habría que revisar que
`active_encode_config`/`active_init_params` sigan reflejando lo que de
verdad se envió.

## Qué se validó aquí (más que en cualquier otra feature de esta sesión)

A diferencia de HIDMaestro y bandwidth-budget, **todo este código es
multiplataforma** — se compiló de verdad en el Docker de Linux, incluido
`src/nvenc/` (NVENC es cross-platform vía CUDA, no exclusivo de Windows).

- Compila limpio (o revisar el log del build si no — ver más abajo).
- Simulación de pérdida de paquetes real con `netem` en el contenedor
  Docker (ver sección siguiente) para confirmar que `IDX_LOSS_STATS`
  llega y que la lógica de `update_adaptive_bitrate_scale()` reacciona
  como se espera.

## Qué NO se pudo validar aquí

- **`NvEncReconfigureEncoder()` ejecutándose de verdad** — necesita una
  sesión NVENC activa con GPU NVIDIA real, que no existe en este entorno.
  Se compiló y se revisó la lógica contra la cabecera oficial, pero no se
  ejecutó ni una sola vez.
- **Que el multiplicador calculado (umbrales de severidad/inestabilidad)
  sea razonable en la práctica** — los valores (`ADAPTIVE_BITRATE_WINDOW`,
  `ADAPTIVE_BITRATE_SEVERITY_SCALE`, etc. en `stream.cpp`) son un punto de
  partida sin calibrar contra condiciones de red reales.
- **Convivencia real con Warp Mode** durante un stream real (la lógica
  aplica el multiplicador sobre `config.bitrate`, que ya incluye el ajuste
  de Warp, pero no se ha probado end-to-end).

## Cómo activar/probar una vez compilado en el host real (con GPU NVIDIA)

1. Web UI → pestaña **NVIDIA NVENC Encoder** → activar
   **"Adaptive Bitrate"**, y ajustar **"Adaptive Bitrate Floor (%)"** si
   el 50% por defecto no encaja.
2. Iniciar un stream real. Con red estable no debería notarse ningún
   cambio (el multiplicador se queda en 1.0).
3. Para provocar el ajuste: degradar la red del cliente a propósito (ej.
   `tc qdisc`/`netem` en la máquina cliente, o simplemente una red WiFi
   mala) y revisar los logs de Apollo — busca `"Adaptive bitrate:"`.
4. Confirmar que el bitrate sube de nuevo cuando la red mejora (el
   multiplicador recupera hacia 1.0 conforme bajan las pérdidas en la
   ventana móvil).
