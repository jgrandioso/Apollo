# Análisis: Frame Pacing (Fase 1)

Este documento es el análisis previo obligatorio antes de tocar código para
la feature de frame pacing. Objetivo: entender qué hace Apollo hoy en este
área, qué archivos/funciones tocaríamos, y qué riesgos hay — **antes** de
diseñar ni escribir nada nuevo. Necesita tu aprobación (y una decisión de
alcance, ver la pregunta al final) antes de pasar a implementación.

## Glosario rápido (para quien viene de Python/Java)

- **`std::chrono::steady_clock::time_point`**: un timestamp de un reloj que
  solo avanza hacia adelante (no se ve afectado si alguien cambia la hora
  del sistema). Apollo lo usa para medir intervalos de tiempo con precisión,
  como `datetime.monotonic()` en Python.
- **Hilo (`std::thread`) / cola (`queue`)**: Apollo captura pantalla,
  codifica vídeo y envía paquetes de red en **hilos separados** que se
  comunican por colas (`mail::queue`). Es el mismo patrón que un
  productor-consumidor con `queue.Queue` en Python, pero en C++ sin garbage
  collector — hay que tener cuidado con quién posee la memoria.
- **RTP**: el protocolo de red sobre el que viaja el vídeo hacia Moonlight
  (el cliente). Cada paquete RTP lleva un timestamp que el cliente usa para
  saber cuándo "tocaba" mostrar ese frame.

## Dos mecanismos de pacing que ya existen hoy

Es importante entenderlo antes de proponer nada: **"frame pacing" no es una
casilla vacía en Apollo**. Hay dos mecanismos distintos, en dos capas
distintas del pipeline, y cualquier feature nueva tiene que convivir con
ambos (o sustituir uno concreto).

### 1. Pacing de captura→codificación (cuándo se codifica cada frame)

**Archivo:** `src/video.cpp`, función `encode_run()`, líneas ~1943-2063.

Qué hace, en términos simples: cuando la captura de pantalla entrega un
frame nuevo, este código decide **si se codifica ya, se descarta, o se
"ajusta" su timestamp** para que la cadencia de frames que le llega al
codificador sea regular, aunque la captura real sea irregular (los juegos
no siempre entregan frames a intervalos perfectos).

Variables clave (todas calculadas una vez al arrancar la sesión, según el
framerate objetivo del cliente):

- `encode_frame_threshold`: el intervalo "ideal" entre frames codificados
  (`1 segundo / framerate objetivo`).
- `frame_variation_threshold`: un margen de tolerancia (1/4 del intervalo
  ideal) — si un frame llega dentro de ese margen del timestamp esperado,
  se "engancha" a la cadencia ideal en vez de usar su timestamp real.
- `max_frametime`: cuánto se espera como máximo por un frame nuevo antes de
  rendirse — esto da un **FPS mínimo garantizado** (`minimum_fps_target`,
  configurable) para que contenido estático no se vea peor por falta de
  refresco periódico.

La lógica del bucle (líneas 2018-2051): si un frame llega demasiado pronto
respecto al anterior (más rápido que el margen de tolerancia), **se
descarta** (`continue`). Si llega dentro del margen, se "redondea" al
timestamp ideal. Si llega tarde, se acepta su timestamp real tal cual. Esto
es, en esencia, un pacing por **snapping a una rejilla temporal con
tolerancia**, no un pacing por espera activa (`sleep`) — no frena la
codificación, solo decide qué timestamp lleva cada frame y cuáles se tiran.

### 2. Pacing de envío por red (cómo se transmiten los paquetes de un frame)

**Archivo:** `src/stream.cpp`, función `videoBroadcastThread()`, líneas
~1330-1646 (la parte relevante de pacing: ~1462-1627).

Qué hace: una vez que un frame ya está codificado y partido en paquetes UDP
(con redundancia FEC para tolerar pérdida de paquetes), este código **no
los manda todos de golpe**. Calcula un presupuesto de paquetes por
milisegundo (`ratecontrol_packets_in_1ms`, basado en ~80% de 1Gbps fijo en
el código, línea 1464) y usa un temporizador de alta precisión
(`platf::create_high_precision_timer()`) para espaciar los envíos dentro
del tiempo que dura un frame, evitando mandar una ráfaga enorme de golpe
que podría desbordar buffers de red y causar pérdida de paquetes en
cascada.

`ratecontrol_next_frame_start` (línea 1349, actualizada en 1625-1627) lleva
la cuenta de cuándo "debería" empezar a mandarse el siguiente frame, para
no perder el presupuesto sobrante del frame anterior si el siguiente llega
antes de lo esperado.

### 3. "Warp Mode" — mecanismo relacionado, pero es de bitrate, no de pacing

**Archivo:** `src/rtsp.cpp`, líneas 1061-1064.

Cuando el framerate del monitor del host es mucho más bajo que el
framerate que pide el cliente, Apollo multiplica el bitrate configurado por
un `warp_factor` para compensar (más bits por frame ya que hay menos
frames). Esto es relevante para la feature de **bitrate adaptativo** (Fase
2.4), no tanto para pacing — lo menciono aquí porque comparte vecindario de
código y porque el nombre puede confundirse con "frame pacing" al buscar
en el repo.

## Archivos/funciones que tocaríamos (según qué se decida implementar)

| Archivo | Qué hay ahí | Probabilidad de tocarlo |
|---|---|---|
| `src/video.cpp` (`encode_run`, ~1910-2064) | Snapping de timestamps captura→codificación | Alta si tocamos cadencia de codificación |
| `src/stream.cpp` (`videoBroadcastThread`, ~1330-1649) | Pacing de paquetes por red | Alta si tocamos suavizado de envío |
| `src/config.h` / `src/config.cpp` | Definición y parseo de opciones de configuración | Alta — cualquier flag nuevo va aquí |
| `src/video.h` | Struct `config_t` con `encodingFramerate`, etc. | Media, si añadimos campos nuevos al config de sesión |
| `src/confighttp.cpp` | Endpoints HTTP de la Web UI de configuración | Baja, solo si el flag debe ser editable desde la Web UI |

## Riesgos de romper algo existente

- **Es código de la ruta caliente de streaming**: `encode_run()` y
  `videoBroadcastThread()` corren en bucle para **cada frame de cada
  sesión activa**. Un bug aquí no es cosmético — se nota como
  tartamudeo, micro-freezes o pérdida de sincronía audio/vídeo en
  Moonlight, de inmediato.
- **`ratecontrol_packets_in_1ms` asume ~80% de 1Gbps fijo** (línea 1464)
  — si la feature de "presupuesto de banda ancha" (Fase 2.2) toca esto
  más adelante, frame pacing y bandwidth budget van a interactuar en el
  mismo sitio. Vale la pena tenerlo en cuenta ahora para no duplicar
  lógica entre ambas features.
- **No hay tests automatizados para esta ruta** (confirmado en
  `docs/dev/building-linux.md`: `BUILD_TESTS` está OFF por defecto y sin
  forma de activarlo desde el script de build). Cualquier cambio aquí se
  valida solo manualmente — y en este entorno de desarrollo **no podemos
  probar streaming real** (sin GPU ni cliente Moonlight con mando físico),
  así que la validación real tendrá que esperar al host Windows de
  producción.
- **Los dos mecanismos ya interactúan entre sí** vía `packet->frame_timestamp`
  (seteado en video.cpp, leído en stream.cpp para calcular latencia y
  timestamp RTP) — cualquier cambio en uno puede afectar sutilmente al otro.

## Pregunta abierta — necesito tu decisión antes de diseñar nada

Como frame pacing **ya existe** en dos formas distintas, "añadir frame
pacing" no es un hueco vacío que rellenar — es una decisión sobre **qué
comportamiento concreto falta o queremos poder ajustar**. Candidatos que
veo posibles, para que elijas (o propongas otro):

1. **Hacer configurable el margen de tolerancia actual** (`frame_variation_threshold`,
   ahora fijo en 1/4 del intervalo, no expuesto como opción) — permitiría
   afinar cuánto se "snapea" un frame a la rejilla temporal vs. cuánto se
   respeta su timestamp real.
2. **Pacing más uniforme entre frames consecutivos**, no solo dentro de un
   frame — hoy `ratecontrol_next_frame_start` evita perder presupuesto
   sobrante, pero no suaviza activamente ráfagas cuando varios frames
   grandes llegan seguidos (ej. tras una escena con mucho movimiento).
3. **Exponer telemetría de pacing** (jitter real vs. ideal) a la Web UI o
   logs, como base para que la feature de bitrate adaptativo (Fase 2.4)
   tenga datos reales que consumir.
4. Algo distinto que tengas en mente y que no esté cubierto por lo de
   arriba.

Dime cuál (o cuáles) quieres que sea el alcance, y con eso ya preparo el
diseño concreto (qué función se modifica, qué flag de configuración se
añade, cómo se prueba) para tu aprobación antes de tocar código.

---

## Decisión (2026-09-16): combinar 1+2+3, con el 1 configurable en la Web UI

Diseño concreto, listo para implementar tras tu confirmación final.

### Infraestructura de configuración que hay que respetar

Antes de diseñar, encontré algo que cambia cómo hay que tocar `config.h`
/`config.cpp`: existen tests de consistencia (`tests/integration/test_config_consistency.cpp`
y `test_locale_consistency.cpp`) que exigen que **cada opción de
configuración exista en 4 sitios a la vez**: `src/config.cpp` (registro),
`src_assets/common/assets/web/config.html` (manifiesto de opciones por
pestaña), `docs/configuration.md` (una sección `### nombre_opcion`), y
`en.json` (clave de texto). No se pueden ejecutar ahora mismo (BUILD_TESTS
sigue OFF, ver `building-linux.md`), pero seguimos el patrón exacto igualmente
por si se arregla más adelante. Nota al margen: `limit_framerate` (opción
ya existente) **no** está documentada en `configuration.md` hoy — es un
gap preexistente, no lo vamos a arreglar como parte de esta feature (fuera
de alcance), solo lo menciono para que sepas que la inconsistencia ya
existía antes de que tocáramos nada.

### Dos campos de configuración nuevos, en `config::video_t` (`src/config.h`)

```cpp
double frame_pacing_tolerance_pct;  // % del intervalo ideal usado como margen de snapping (item 1)
bool frame_pacing_smooth_bursts;    // activa el suavizado de ráfagas en el envío de red (item 2)
```

- `frame_pacing_tolerance_pct`: default **25.0** — el mismo valor que hoy
  está *hardcodeado* como `encode_frame_threshold / 4` (25% = 1/4). Con
  este default, **el comportamiento no cambia respecto a hoy** hasta que
  el usuario lo edite desde la Web UI. Rango razonable a validar en el
  registro: 5-100.
- `frame_pacing_smooth_bursts`: default **false**. Con el flag apagado,
  el código de red se comporta exactamente igual que hoy.

### Item 1 — tolerancia configurable (`src/video.cpp`, línea 1947)

Cambio mínimo: sustituir

```cpp
auto frame_variation_threshold = encode_frame_threshold / 4;
```

por

```cpp
auto frame_variation_threshold = encode_frame_threshold * (config.video.frame_pacing_tolerance_pct / 100.0);
```

(usa la config global `config::video`, no el `config_t` de sesión — igual
que hace `limit_framerate` en `rtsp.cpp`).

### Item 2 — suavizado de ráfagas en red (`src/stream.cpp`, `videoBroadcastThread`)

Aquí encontré algo importante que simplifica el diseño: `stream.cpp` **no
tiene visibilidad del framerate objetivo hoy** (confirmé con grep — cero
menciones a "fps"/"framerate" en todo el archivo). Añadir esa plomería
sería un cambio más invasivo de lo que esta feature (pensada para ser la
más simple, "validar el flujo de trabajo con bajo riesgo") debería
necesitar.

En vez de eso, **reutilizo el timestamp que el item 1 ya calcula y deja
"enganchado" a la rejilla ideal** (`packet->frame_timestamp`, que ya llega
a `stream.cpp` hoy). Con el flag activado:

1. Se guarda el `frame_timestamp` del paquete anterior en una variable
   local nueva (`previous_frame_timestamp`).
2. Se calcula `ideal_interval = frame_timestamp_actual - previous_frame_timestamp`
   (se ignora/descarta en el primer frame o si el valor es disparatado —
   límites cordura 1-200ms — para no dividir por cero ni pacing absurdo
   tras una pausa del stream).
3. El presupuesto de paquetes por ms pasa de ser siempre el techo fijo
   (`ratecontrol_packets_in_1ms`, ~80% de 1Gbps) a ser:
   ```
   max(ratecontrol_packets_in_1ms_techo, total_paquetes_del_frame / (ideal_interval_ms * 0.9))
   ```
   Es decir: si el frame es pequeño y cabría de sobra en el intervalo
   ideal, se reparte su envío a lo largo de (~90% de) ese intervalo en vez
   de mandarlo lo más rápido posible — menos ráfaga, jitter más bajo en el
   receptor. Si el frame es grande (ej. un keyframe tras cambio de
   escena) y no cabe estirado en el intervalo, se usa el techo de siempre
   — **nunca se envía más lento de lo que se envía hoy**, así que no se
   introduce retraso/backlog nuevo en el peor caso.
4. Con el flag apagado: se salta todo esto y se usa `ratecontrol_packets_in_1ms`
   tal cual, exactamente como hoy.

Esto conecta de forma natural los items 1 y 2: el snapping de timestamps
(item 1) es lo que le da a item 2 una señal de "intervalo ideal" fiable
sin tener que tocar la sesión ni pasar el framerate por sitios nuevos.

### Item 3 — telemetría de jitter (siempre activa, sin flag, solo logging)

Sigue el patrón que ya usa el propio archivo (`logging::min_max_avg_periodic_logger<double>`,
como el ya existente `frame_processing_latency_logger` en `stream.cpp`
línea 1335) — es decir, no es una opción de configuración nueva, es
instrumentación que ya existe en este estilo en el proyecto y solo se ve
si el nivel de log es debug/verbose. No debería necesitar su propia
entrada en config.cpp/docs (no es una opción de usuario).

- En `video.cpp` (`encode_run`): log periódico de `time_diff` real vs.
  `encode_frame_threshold` esperado — jitter en el lado de captura/codificación.
- En `stream.cpp` (`videoBroadcastThread`): log periódico de `ideal_interval`
  real vs. el intervalo objetivo, y si se aplicó suavizado o se usó el
  techo de siempre.

### Web UI (item 1 gráfico)

- `src_assets/common/assets/web/configs/tabs/Advanced.vue`: nuevo `<input type="number">`
  siguiendo el patrón exacto de `qp`/`min_threads` (líneas 24-35), para
  `frame_pacing_tolerance_pct`. El flag `frame_pacing_smooth_bursts` va
  como `<Checkbox>`, igual que `limit_framerate` (líneas 37-43).
- `src_assets/common/assets/web/config.html`: añadir ambas claves al
  manifiesto `options: {...}` de la pestaña `"advanced"` (línea ~247-259),
  con sus valores por defecto.
- `src_assets/common/assets/web/public/assets/locale/en.json`: claves
  `frame_pacing_tolerance_pct` / `_desc` y `frame_pacing_smooth_bursts` /
  `_desc`, siguiendo el patrón de `qp`/`qp_desc`.
- `docs/configuration.md`: nuevas entradas `### frame_pacing_tolerance_pct`
  y `### frame_pacing_smooth_bursts` bajo `## Advanced`, con el mismo
  formato de tabla que usa `### qp`.

### Validación (sin poder probar streaming real aquí)

- Compilar el branch con el mismo Dockerfile ya arreglado en `master`, y
  confirmar que arranca igual que el baseline (Fase 0).
- Revisar a mano, con los defaults, que los logs de startup muestran los
  mismos valores de `frame_variation_threshold`/`encode_frame_threshold`
  que el baseline sin modificar (prueba de que el default no cambia nada).
- **No se puede validar aquí**: el efecto real del suavizado de ráfagas
  (item 2) y si el jitter medido (item 3) realmente baja, porque necesita
  streaming real con Moonlight + red con jitter/pérdida real — eso queda
  pendiente para el host Windows de producción.
- Tests automatizados: no hay infraestructura que se pueda ejecutar aquí
  (`BUILD_TESTS` OFF, confirmado en Fase 0) — se sigue el patrón de
  `tests/unit/test_video.cpp` / `test_stream.cpp` por si en el futuro se
  arregla el flag de tests, pero no se puede confirmar que compilen ni
  pasen desde este entorno.
