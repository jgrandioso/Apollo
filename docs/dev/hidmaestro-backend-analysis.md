# Análisis: HIDMaestro como Backend de Entrada Alternativo (Fase 1)

Análisis previo obligatorio antes de tocar código. Branch: `feature/hidmaestro-backend`.

## Glosario rápido

- **ViGEmBus**: el driver de Windows que Apollo usa hoy para crear mandos
  virtuales (Xbox 360 / DS4) que Windows ve como hardware real.
- **HIDMaestro**: proyecto alternativo (MIT, activo,
  [github.com/hifihedgehog/HIDMaestro](https://github.com/hifihedgehog/HIDMaestro))
  con el mismo objetivo, pero con más perfiles de mando (234, incluyendo
  Xbox Series) y sin necesitar driver de kernel.
- **Rumble de gatillos ("impulse triggers")**: motores de vibración
  adicionales en los gatillos LT/RT de los mandos Xbox Series/One
  Elite/Series — distinto del rumble estándar (2 motores, cuerpo del
  mando) y distinto de los "adaptive triggers" de PS5 (que son
  resistencia variable, no vibración).

## Qué hace Apollo hoy

Todo el código de mandos vive en `src/platform/windows/input.cpp`
(1782 líneas) — **hoy solo existe una implementación, `vigem_t`,
hardcodeada**. No hay abstracción de "backend intercambiable" que ya
exista para enganchar una alternativa.

Puntos de entrada relevantes (los que cualquier backend nuevo necesita
implementar/enganchar):
- `alloc_gamepad(input_t&, ...)` / `free_gamepad(input_t&, ...)`
  (declarados en `src/platform/common.h:821-822`, implementados en
  `input.cpp:1175` y `:1229`) — crean/destruyen un mando virtual cuando
  un cliente Moonlight conecta/desconecta un mando.
- Callbacks de rumble (`x360_notify`/`ds4_notify`, ~línea 410-440) — el
  driver de Windows llama a estas funciones cuando un juego pide vibrar
  el mando; Apollo las reenvía al cliente Moonlight por red.
- `config::input.gamepad` (string `"x360"`/`"ds4"`/`"auto"`, ya
  existente): esto selecciona el **tipo de mando emulado**, no el
  backend/librería — es un eje distinto del `input_backend` que se pide
  en esta feature. Hay que dejarlo intacto y añadir el nuevo flag al
  lado.

**Hallazgo clave — por qué esta feature tiene sentido de verdad:** el
protocolo de red de Apollo hacia el cliente **ya soporta rumble de
gatillos** (`gamepad_feedback_msg_t::make_rumble_triggers()` y los
campos `left_trigger`/`right_trigger`, `src/platform/common.h:123-167`).
Pero `vigem_t` (el único backend que existe) **nunca usa esos campos** —
solo implementa el rumble estándar de 2 motores (`rumble(target,
largeMotor, smallMotor)`). Esto no es un descuido: **ViGEmBus emula un
mando Xbox 360 clásico, que no tiene motores de gatillo a nivel de API**
— es una limitación estructural del propio ViGEmBus, no de Apollo. Por
eso hace falta un backend distinto (HIDMaestro, con perfil real de Xbox
Series) para que esta funcionalidad exista de verdad.

## Qué es HIDMaestro (resumen de la investigación)

- SDK en **C#/.NET 10**, driver UMDF2 en modo usuario (sin driver de
  kernel, sin certificado EV, sin reinicios). MIT license.
- 234 perfiles de mando en JSON, incluyendo **Xbox Series** con gatillos.
- **No existe API nativa en C/C++, ni COM, ni named pipe documentados**
  — confirmé esto en `hidmaestro.org/docs/` explícitamente. El único
  protocolo interno (memoria compartida SDK↔driver) está documentado
  como detalle de implementación, no como interfaz pública estable —
  reimplementarlo nosotros sería depender de algo que puede cambiar sin
  aviso en cualquier versión. Descartado.
- El propio autor tiene otro proyecto, **PadForge**
  ([github.com/hifihedgehog/PadForge](https://github.com/hifihedgehog/PadForge)),
  que ya consume el evento `OutputDecoded` del SDK para pasar rumble de
  gatillos a mandos Xbox reales — es una referencia real de que el flujo
  funciona, útil como ejemplo al implementar nuestro lado C#.

## Decisión de arquitectura (ya confirmada contigo): proceso auxiliar

Apollo (C++) no puede llamar directamente al SDK de HIDMaestro (C#) sin
mezclar código nativo/managed (`C++/CLI`), algo que descartamos por la
complejidad que añade al build y lo difícil que es de mantener/depurar
sin experiencia previa en C++. En su lugar:

- Un **proceso `.exe` en .NET aparte** (nuevo, lo escribimos nosotros)
  que enlaza el SDK de HIDMaestro, crea el mando virtual con el perfil
  Xbox Series, y expone una interfaz mínima propia que Apollo controla
  por completo (no reimplementamos nada interno de HIDMaestro).
- Apollo lo lanza como subproceso y le habla por un canal simple.
- Ventaja añadida: si el proceso auxiliar crashea, no se lleva Apollo
  con él — se puede reiniciar de forma aislada.

## Diseño concreto propuesto (para tu aprobación)

### Nuevo proceso auxiliar

- Proyecto .NET nuevo, ej. `tools/hidmaestro-bridge/` (fuera de `src/`,
  ya que no es C++) — un ejecutable de consola pequeño.
- Protocolo IPC: **líneas JSON por stdin/stdout** del subproceso (Apollo
  ya sabe lanzar procesos hijos — ver `src/process.cpp` — y leer/escribir
  pipes es estándar en C++). Elegido sobre named pipes/sockets por ser lo
  más simple de depurar a mano (se puede probar el bridge por separado
  tecleando JSON en una terminal, sin ni tocar Apollo) y lo más fácil de
  razonar viniendo de Python.
- Mensajes mínimos que necesita entender:
  - `{"cmd":"create_pad","profile":"xbox-series"}` → responde con éxito/error.
  - `{"cmd":"set_state", ...datos del mando...}` → aplica el estado (botones/ejes).
  - `{"cmd":"destroy_pad"}` → limpieza al desconectar.
  - Eventos que el bridge manda sin que se los pidan: `{"event":"rumble", "left":.., "right":..}` y `{"event":"trigger_rumble", "left":.., "right":..}` (del `OutputDecoded` del SDK).

### Lado Apollo (C++)

- Nueva clase `hidmaestro_t` en `input.cpp`, análoga a `vigem_t` en forma
  (mismos puntos de enganche: `alloc_gamepad_internal`, rumble callback),
  pero por dentro lanza/gestiona el subproceso y traduce mensajes JSON en
  vez de llamar a `vigem_*()`.
- Nuevo flag `config::input.input_backend` (string, valores `"vigem"`
  default / `"hidmaestro"`), registrado igual que `config::input.gamepad`
  ya existente. En `alloc_gamepad`/`free_gamepad`/callbacks de rumble: un
  `if` que despacha a `vigem_t` o `hidmaestro_t` según el flag.
- Con el flag en su default (`"vigem"`), cero cambio de comportamiento.

## Riesgos

- **Nada de esto se puede compilar aquí.** A diferencia de frame pacing
  (que tocaba archivos multiplataforma), todo esto vive en
  `src/platform/windows/`, que nuestro Dockerfile de Linux **ni siquiera
  compila** (CMake solo incluye esos archivos en build de Windows). No
  hay forma de detectar errores de sintaxis/tipos hasta compilar en el
  host Windows real.
- **Nueva dependencia de runtime en el host de producción**: .NET 10
  tiene que estar instalado en la máquina Windows para que el proceso
  auxiliar arranque. Hay que documentarlo como requisito, igual que
  ViGEmBus ya lo es hoy.
- **Gestión de ciclo de vida del subproceso**: qué pasa si el bridge
  tarda en arrancar, crashea a mitad de sesión, o el `.exe` no está
  presente — hay que decidir el comportamiento (¿degradar a sin mando?
  ¿fallar el stream?). Pendiente de diseño detallado en implementación.
- **HIDMaestro es un proyecto externo relativamente nuevo**: si cambia su
  SDK/NuGet en una versión futura, nuestro bridge podría romperse — hay
  que fijar una versión concreta del paquete, no `latest`.
- **Sin infraestructura de test aplicable**: ni los tests de C++ (mismo
  motivo que siempre, `BUILD_TESTS` OFF) ni forma de testear el bridge
  .NET desde este entorno Linux.

## Qué requiere validación en Windows (todo, en este caso)

Absolutamente todo el comportamiento funcional — compilación del lado
C++, compilación/ejecución del bridge .NET, instalación de HIDMaestro,
creación real del mando virtual Xbox Series, y el rumble de gatillos
llegando de verdad a un mando físico a través de Moonlight. Nada de esto
es verificable en este entorno de desarrollo.

## Preguntas antes de implementar

1. **Alcance del MVP**: ¿solo el perfil Xbox Series (motivo original de
   la feature), o desde el principio soporte genérico para varios
   perfiles de HIDMaestro? Recomiendo empezar solo por Xbox Series y
   dejar la generalización para después, dado que ya es una feature con
   mucha superficie nueva.
2. **¿El bridge .NET se compila como parte del build de Apollo (un paso
   más en `scripts/linux_build.sh`/el equivalente Windows), o es un
   proyecto separado que se compila y distribuye aparte?** Afecta a cómo
   se empaqueta el `.exe` junto al instalador de Apollo.
3. **Comportamiento si HIDMaestro/el bridge no están disponibles**: ¿caer
   automáticamente a ViGEm, o fallar visiblemente para que el usuario
   sepa que algo falta?

Con tus respuestas preparo el diseño final y empiezo a implementar.
