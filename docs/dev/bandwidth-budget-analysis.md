# Análisis: Presupuesto de Banda Ancha / Datos por Sesión (Fase 1)

Análisis previo obligatorio antes de tocar código. Branch: `feature/bandwidth-budget`.

## Qué hay hoy (poco, a diferencia de frame pacing)

Busqué contadores de bytes por sesión, endpoints de estadísticas de uso, y
cualquier noción de "presupuesto" — **no existe nada de esto hoy en
Apollo**. Lo único relacionado con banda ancha es un **techo de bitrate
instantáneo**, no un presupuesto de datos acumulado:

- **`config::video.max_bitrate`** (`src/config.h:146`, opción ya existente
  en Web UI): un techo en kbps que limita el bitrate que el cliente puede
  solicitar. Se aplica en `src/rtsp.cpp:1052-1054`, durante la negociación
  RTSP al iniciar el stream. Es un límite de *tasa* (velocidad
  instantánea), no de *consumo total* — un stream de 2 horas al límite de
  `max_bitrate` puede consumir cientos de GB sin que nada lo impida.
- **`ratecontrol_packets_in_1ms`** (`src/stream.cpp`, tocado también en la
  feature de frame pacing): asume un ~80% de 1Gbps fijo como techo de
  envío por red, otro límite de tasa, no de consumo acumulado.
- No hay ningún contador de bytes enviados que persista o se acumule por
  sesión. `session_t` (`src/stream.cpp:350`) no tiene ningún campo de este
  tipo.
- No hay ningún endpoint HTTP de estadísticas de uso (revisé todas las
  rutas registradas en `confighttp.cpp:1525-1558` — hay gestión de
  clientes/apps/config, pero nada de "uso de datos").

## Dónde viviría la lógica nueva

- **Contar bytes**: el sitio natural es donde ya se conoce el tamaño de
  cada paquete enviado — `videoBroadcastThread()` (`src/stream.cpp`,
  ~línea 1330 en adelante, la misma función que tocamos en frame pacing)
  y su equivalente `audioBroadcastThread()` (~línea 1651). Ambas ya
  iteran sobre cada paquete que sale por red.
- **Guardar el contador en la sesión**: `session_t` (`src/stream.cpp:350`)
  es la estructura con el ciclo de vida correcto — vive mientras dura la
  conexión del cliente.
- **Aplicar el límite / cortar la sesión**: `stop(session_t &session)`
  (`src/stream.cpp:1981`) es donde ya se termina una sesión hoy; sería el
  punto de enganche si el presupuesto agotado debe forzar desconexión.
- **Config**: mismo patrón que `max_bitrate` — nuevo campo en
  `config::video_t` (`src/config.h`), registro en `src/config.cpp`, Web UI
  en `Advanced.vue` (o una pestaña propia si el usuario final lo prefiere
  más visible — ver preguntas abajo).
- **Persistencia entre reinicios**: si el presupuesto debe ser "por mes"
  en vez de "por sesión", hay que guardar el contador en disco. Apollo ya
  tiene un mecanismo de estado persistente para clientes emparejados
  (`config::nvhttp.file_state`, gestionado en `file_handler.cpp`) que
  podría servir de plantilla, pero sería una pieza nueva, no una
  extensión de algo existente.

## Riesgos

- **Es una feature nueva de cero**, no una extensión — más superficie
  nueva de código que frame pacing, pero también menos riesgo de romper
  comportamiento existente (no hay lógica previa que se pueda desviar).
- **Ruta caliente compartida con frame pacing**: si ambas features tocan
  `videoBroadcastThread()`, hay que tener cuidado de que los cambios de
  una no interfieran con los de la otra al fusionar ambos branches más
  adelante (esto NO es un problema ahora, mientras cada feature vive en
  su propio branch, pero sí lo será cuando se junten).
- **Decisión de producto, no solo técnica**: qué pasa cuando se agota el
  presupuesto (¿cortar la sesión de golpe es una mala experiencia para
  quien está jugando?) es una decisión que afecta directamente la
  experiencia de usuario — no es algo que deba asumir yo.
- **Sin tests de esta área tampoco**: mismo panorama que frame pacing,
  `BUILD_TESTS` OFF (ver `building-linux.md`).

## Preguntas de alcance — necesito tu decisión antes de diseñar

1. **¿Qué cuenta como "presupuesto"?** ¿Solo tráfico de vídeo, o
   vídeo + audio + overhead de red (FEC, cabeceras)? Lo más simple de
   medir con precisión es vídeo+audio (ambos hilos ya cuentan bytes que
   salen); FEC/cabeceras se podrían estimar pero con menos exactitud.
2. **¿Ventana de tiempo del presupuesto?**
   - (a) **Por sesión**: se resetea cada vez que un cliente se conecta
     (más simple, no necesita persistir en disco).
   - (b) **Acumulado con reset periódico** (ej. "10GB al mes"): necesita
     guardar el contador en disco y saber quardar cuando "empieza" cada
     periodo (¿día de reinicio de Apollo? ¿día 1 del mes calendario?).
   - (c) **Ambas**, como dos límites independientes y configurables.
3. **¿Qué pasa al agotar el presupuesto?**
   - (a) Cortar la sesión inmediatamente (como un `disconnect` forzado).
   - (b) Bajar el bitrate agresivamente (casi al mínimo) en vez de cortar,
     dejando jugar pero muy degradado.
   - (c) Solo avisar (log / notificación en la Web UI) sin actuar,
     dejando la decisión al usuario.
4. **¿Por sesión global, o por cliente emparejado?** Apollo ya distingue
   clientes emparejados individualmente (`getClients`/`updateClient` en
   `confighttp.cpp`) — el presupuesto podría ser un único número global
   para todo el host, o configurable por cliente. Un presupuesto por
   cliente es más flexible pero bastante más trabajo (persistencia por
   cliente, UI para editar cada uno).
5. **¿Visibilidad en la Web UI?** ¿Basta con un campo de configuración
   (cuánto), o también quieres ver cuánto llevas consumido (necesitaría
   un endpoint nuevo tipo `/api/bandwidth-usage`, más superficie de
   código)?

Con tus respuestas preparo el diseño concreto (igual que hicimos con frame
pacing) para tu aprobación antes de tocar código.

---

## Decisión (2026-09-16): feature descartada

Respuestas recibidas a las 5 preguntas: solo vídeo, presupuesto por
sesión, aviso al cliente al agotarse, configurable por cliente
emparejado, con visibilidad de consumo.

Al investigar el punto 3 (aviso al cliente) apareció una limitación real
de protocolo, no de implementación: **Apollo no tiene manera de mandar un
mensaje de texto libre al cliente durante un stream activo**. Revisé los
tipos de paquete que Apollo ya añade sobre el protocolo de Sunshine
(`Execute Server Command`, `Set Clipboard`, `File transfer` —
`src/stream.cpp:52-76`) y los tres van **cliente→host**, no al revés. El
único mensaje host→cliente fuera de vídeo/audio/mando es la terminación
de sesión (`src/stream.cpp:1201-1229`), y usa un **código numérico**
cuyo texto final lo decide el propio Moonlight, no Apollo.

Presenté las alternativas reales (cortar sesión con el mecanismo de
terminación existente, degradar vídeo como aviso implícito, o ambas) y la
decisión del usuario fue **no implementar esta feature**: un aviso al
usuario sobre su propio consumo de datos es responsabilidad natural del
cliente (Moonlight/Artemis), no algo que el host deba forzar sin ese
soporte del lado cliente. Cita: *"creo que sería más una funcionalidad de
moonlight que de sunshine como tal [...] limitar algo por parte del host
que depende enteramente del cliente no es cómodo."*

**Estado:** branch `feature/bandwidth-budget` se cierra sin código nuevo,
solo este análisis como registro de la investigación y la decisión. No se
retoma salvo que cambien las circunstancias (ej. si Artemis añade soporte
de protocolo para mensajes host→cliente en el futuro).
