# Análisis: Bitrate Adaptativo Real (Fase 1)

Análisis previo obligatorio antes de tocar código. Branch: `feature/adaptive-bitrate`.

## Qué hace Apollo hoy

### Warp Mode (lo que ya existe, y por qué no es "bitrate adaptativo real")

**Archivo:** `src/rtsp.cpp`, líneas ~1052-1064, dentro de la negociación RTSP
(se ejecuta **una vez**, al arrancar el stream, no durante la sesión).

```cpp
size_t warp_factor = std::round((float)config.monitor.framerate * 1000 / session.fps);
if (config::video.limit_framerate && warp_factor >= 2) {
  configuredBitrateKbps *= warp_factor;
}
```

Qué hace: si el framerate del monitor del host es mucho más bajo que el
framerate que pide el cliente, multiplica el bitrate por ese factor para
compensar (menos frames, cada uno con más bits). **Esto no reacciona a la
red en absoluto** — es una corrección de framerate calculada una vez al
principio y nunca revisada. No es lo que pide esta feature.

### Dato que ya llega y hoy se descarta: informes de pérdida de paquetes

**Archivo:** `src/stream.cpp`, líneas 943-957.

Moonlight manda periódicamente un paquete `IDX_LOSS_STATS` con: cuántos
paquetes se perdieron desde el último informe, cuánto tiempo pasó, y el
número del último frame bueno recibido. Apollo **ya lo recibe y lo
parsea**, pero solo lo escribe en el log a nivel `verbose` — no hace nada
con el dato:

```cpp
server->map(packetTypes[IDX_LOSS_STATS], [&](session_t *session, const std::string_view &payload) {
  int32_t *stats = (int32_t *) payload.data();
  auto count = stats[0];              // pérdidas desde el último informe
  std::chrono::milliseconds t {stats[1]};  // tiempo desde el último informe
  auto lastGoodFrame = stats[3];
  BOOST_LOG(verbose) << ...;  // y aquí se tira
});
```

Esto es exactamente la materia prima que pide la feature ("pérdida
detectada"). No hace falta inventar un mecanismo de medición nuevo — hace
falta **usar** uno que ya existe.

### Lo que NO existe: una señal de "jitter" real

Busqué cualquier medición de RTT (round-trip time) o varianza de tiempo de
llegada de paquetes — no hay ninguna. El único mecanismo de "ping"
existente (`recv_ping`, `IDX_PERIODIC_PING`) es un keepalive de liveness
(¿sigue ahí el cliente?), no mide latencia. Para tener jitter real
necesitaríamos que el **cliente** (Moonlight) lo calculara y nos lo
mandara — igual que con el aviso de presupuesto de banda ancha, esto se
sale del lado que controlamos (el host). **Por ahora, la única señal de
red real y disponible es la pérdida de paquetes (`IDX_LOSS_STATS`), no el
jitter.**

## El problema de fondo: no hay forma de cambiar el bitrate en caliente

Busqué cualquier función tipo `set_bitrate`/`update_bitrate`/
`reconfigure` en `src/video.cpp` — no existe ninguna. El bitrate se lee
una sola vez al crear la sesión de codificación (línea 1756:
`auto bitrate = config.bitrate * 1000;`) y se queda fijo.

Lo único parecido que existe es `reinit_event`
(`src/video.cpp:1342,1399`), pero es **pesado**: destruye y vuelve a crear
la captura de pantalla completa Y el encoder, pensado para cambios de
pantalla/resolución — no para ajustes finos y frecuentes. Usarlo para
cada informe de pérdida causaría cortes visibles (posible parpadeo,
frame-freeze) cada vez que se ajuste.

Para un ajuste de bitrate de verdad "en caliente" (sin cortes) haría
falta reconfigurar el encoder ya activo, lo cual depende de cada backend:

- **NVENC**: sí tiene una API real para esto (`NvEncReconfigureEncoder`)
  — no investigado en detalle aún, pero existe como mecanismo.
- **Encoders vía ffmpeg/libavcodec** (software, VA-API, QuickSync, AMF):
  soporte inconsistente/parcial para cambiar bitrate sin reinicializar el
  codec — no confirmado que funcione de forma fiable para todos.

**Esto confirma lo que ya sospechabas al pedir esta feature**: no es una
extensión pequeña de Warp Mode, es una pieza nueva, y bastante más
profunda que las tres anteriores — toca código específico de cada
encoder que todavía no hemos explorado (`src/nvenc/`, rutas ffmpeg).

## Archivos/funciones que tocaríamos

| Archivo | Qué hay ahí | Rol en esta feature |
|---|---|---|
| `src/stream.cpp` (~943-957) | Recibe `IDX_LOSS_STATS`, hoy lo descarta | Punto de entrada de la señal de red |
| `src/video.cpp` (~1756, `encode_run`) | Bitrate fijo al crear sesión; `reinit_event` para reinicios pesados | Donde se aplicaría el ajuste (uno de los dos mecanismos, ver preguntas) |
| `src/nvenc/` (sin explorar aún) | Wrapper de la API de NVENC | Si se persigue reconfiguración en caliente para NVENC |
| `src/config.h` / `src/config.cpp` | — | Nuevos flags de configuración |
| Web UI (`Advanced.vue` o pestaña propia) | — | Exponer el flag y quizá umbrales |

## Riesgos

- **Es la feature con más riesgo técnico de las cuatro.** Tocar el
  bitrate del encoder en tiempo real, si se hace mal, puede degradar la
  calidad de forma visible o causar inestabilidad en el encoder (algunos
  backends no toleran bien cambios de bitrate no diseñados para ello).
- **Superficie nueva no explorada**: `src/nvenc/` no se ha mirado en
  ningún análisis anterior. Riesgo de subestimar la complejidad hasta
  entrar ahí de verdad.
- **Interacción con Warp Mode**: ambos tocan el bitrate configurado.
  Habría que decidir cómo conviven (¿Warp Mode sigue aplicando su
  multiplicador base y el bitrate adaptativo ajusta sobre eso? ¿Se
  excluyen mutuamente?).
- **Nada de esto se puede probar aquí**: cualquier prueba real necesita
  streaming activo con pérdida de paquetes real (o simulada con `tc`/
  `netem` en Linux, que si se podría montar en este entorno de forma
  aislada — ver pregunta 4 abajo).
- **Sin tests de esta área tampoco** (mismo motivo de siempre).

## Preguntas antes de diseñar

1. **¿Solo pérdida de paquetes, o también quieres que investigue una
   proxy de jitter en el lado host** (ej. varianza en el tiempo de
   llegada de los `IDX_LOSS_STATS`, o en el intervalo de red de
   `stream.cpp` que ya tocamos en frame pacing)? Sería una aproximación,
   no jitter real del cliente.
2. **¿Qué mecanismo de ajuste prefieres para el MVP?**
   - (a) **Reinit pesado** (reutiliza `reinit_event`, ya probado y
     estable, pero con corte visible cada vez que ajusta — solo viable
     con umbrales altos/poco frecuentes).
   - (b) **Reconfiguración en caliente solo para NVENC** (sin cortes,
     pero deja el resto de encoders sin esta feature hasta una fase
     futura, y necesita que investigue la API de NVENC a fondo primero).
   - (c) **Ambos**: reconfiguración en caliente donde el encoder lo
     soporte, reinit pesado como fallback para el resto.
3. **¿Cómo debe convivir con Warp Mode?** ¿El bitrate adaptativo ajusta
   por encima del valor que Warp Mode ya calculó, o lo sustituye
   mientras esté activo?
4. **¿Quieres que monte una prueba con pérdida de paquetes simulada**
   (`tc qdisc` / `netem` en el contenedor Docker) como parte de la
   validación, ya que sí es algo que podemos reproducir en este entorno
   Linux, a diferencia de las últimas dos features? Sería la primera
   validación "real" (aunque simulada) que conseguimos para una feature
   desde frame pacing.

Con tus respuestas preparo el diseño concreto.

---

## Decisión (2026-09-16): aproximar jitter, reconfiguración en caliente (NVENC), convive con Warp Mode, con simulación de prueba

### Mecanismo de reconfiguración en caliente — confirmado con la API real

`third-party/nv-codec-headers/include/ffnvcodec/nvEncodeAPI.h` (cabecera
oficial de NVIDIA, vendorizada en este mismo repo) expone
`NvEncReconfigureEncoder(void *encoder, NV_ENC_RECONFIGURE_PARAMS*)` —
permite cambiar el bitrate de una sesión de encoder **ya activa**, sin
recrearla, pasando un `NV_ENC_CONFIG` actualizado con
`resetEncoder=0` (para no perder el estado del control de tasa) y
`forceIDR=0` (sin forzar keyframe).

**Problema encontrado**: `nvenc_base::create_encoder()`
(`src/nvenc/nvenc_base.cpp:235`) construye el `NV_ENC_CONFIG` como
variable local — no se guarda como miembro de la clase, así que hoy no
hay nada que reconfigurar después. Hace falta:

1. Guardar `enc_config` (o al menos `rcParams`) como miembro de
   `nvenc_base` al final de `create_encoder()`.
2. Nuevo método público `bool reconfigure_bitrate(uint32_t bitrate_kbps)`
   en `nvenc_base` que actualiza `averageBitRate`/`maxBitRate` sobre esa
   copia guardada y llama a `nvEncReconfigureEncoder`.
3. Punto de acceso ya existe: `platf::nvenc_encode_device_t::nvenc` es
   un puntero público (`src/platform/common.h:454`) — `nvenc_encode_session_t`
   (la sesión que usa `encode_run()`) ya lo tiene, no hace falta plomería
   nueva para llegar hasta ahí.

**Alcance confirmado**: solo NVENC en este MVP. Los encoders vía ffmpeg
(software, VA-API, QuickSync, AMF) no tendrán esta feature por ahora — se
documentará como limitación conocida, no como bug.

### Señal de red: pérdida + aproximación de jitter (ambas en el host, del lado servidor)

Todo se calcula dentro del handler de `IDX_LOSS_STATS`
(`src/stream.cpp:943`), manteniendo una ventana móvil (ej. últimos 10
informes) de `count/t` (pérdidas por segundo):

- **Severidad** = media de pérdidas/segundo en la ventana.
- **Aproximación de jitter** = varianza de esa misma serie — una red
  estable con pérdida constante puntúa bajo en esto aunque la pérdida no
  sea cero; una red que alterna entre "perfecta" y "muy mala" puntúa
  alto. **Importante ser honesto en la documentación**: esto NO es jitter
  real (varianza en el tiempo de llegada de paquetes) — es una
  aproximación basada en cuánto varía la tasa de pérdida informada, que
  es la única señal que tenemos sin tocar el cliente Moonlight.

### Convivencia con Warp Mode

Warp Mode calcula `configuredBitrateKbps` una vez al negociar el stream
(`rtsp.cpp`). El bitrate adaptativo **no lo sustituye** — aplica un
multiplicador (ej. 1.0 a 0.5) sobre ese valor ya calculado, con un suelo
configurable para no degradar la calidad más allá de cierto punto. Si
Warp Mode decidió doblar el bitrate por framerate bajo, el adaptativo
seguirá escalando relativo a ese valor doblado, no al original sin
ajustar.

### Anti-oscilación

Para no reconfigurar el encoder en cada informe de pérdida (chatter):
- Solo se dispara una reconfiguración real si el multiplicador objetivo
  se aleja del aplicado actualmente más de un umbral (ej. 10%).
- Cooldown mínimo entre reconfiguraciones (ej. 2 segundos).

### Config nueva

- `adaptive_bitrate` (bool, default `false`) — flag maestro.
- `adaptive_bitrate_floor_pct` (int, 10-100, default 50) — nunca baja de
  este % del bitrate ya ajustado por Warp Mode.

Con los defaults, `adaptive_bitrate=false` = cero cambio de
comportamiento.

### Plomería: nuevo evento mail, siguiendo el patrón ya existente

`src/stream.cpp` no toca hoy la sesión de encoder — el patrón ya
existente para esto son los `mail::idr` / `invalidate_ref_frames` events
(`safe::mail_t`, consumidos dentro de `encode_run()`). Se añade un
`mail::bitrate_update` del mismo estilo: el handler de `IDX_LOSS_STATS`
calcula el nuevo multiplicador y lo publica; `encode_run()` ya tiene un
bucle que revisa varios eventos por iteración (`idr_events`,
`invalidate_ref_frames_events`) — se añade la comprobación de este nuevo
evento ahí mismo, y si el encoder activo es NVENC, se llama a
`reconfigure_bitrate()`.

### Validación: simulación con pérdida de paquetes real (netem)

A diferencia de bandwidth-budget y HIDMaestro, **esto sí se puede probar
de verdad en este entorno Linux**: `tc qdisc add ... netem loss X%` en la
interfaz del contenedor Docker simula pérdida de paquetes real a nivel de
red. Plan: levantar el contenedor baseline + esta feature, aplicar
`netem` con distintos porcentajes de pérdida, y confirmar en los logs que
`IDX_LOSS_STATS` se recibe y que el multiplicador de bitrate calculado
responde como se espera — aunque sin GPU real no podremos confirmar que
`NvEncReconfigureEncoder` se ejecuta sin error (necesita una sesión NVENC
activa de verdad).
