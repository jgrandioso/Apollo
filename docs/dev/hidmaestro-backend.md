# HIDMaestro Backend de Input (Fase 3 — cierre de feature)

Branch: `feature/hidmaestro-backend`. Lee primero
`docs/dev/hidmaestro-backend-analysis.md` para el contexto completo de la
investigación y las decisiones de arquitectura.

## Actualización (2026-09-20): primer test en hardware real

Confirmado por el usuario en Windows real, mando conectado, backend
`hidmaestro` activo:

1. **Los ejes verticales de ambos sticks salían invertidos.** Causa:
   `NormalizeStick()` en `Program.cs` traducía linealmente el valor
   crudo de Apollo (convención XInput, positivo = arriba) al rango
   `[0..1]` que espera `HMGamepadStateHelpers.StandardAxes`, sin tener
   en cuenta que el eje Y genérico de HID (lo que usa el perfil Xbox
   Series X|S de HIDMaestro) sigue la convención contraria — igual que
   DirectInput, positivo/mayor valor = abajo. Arreglado con
   `NormalizeStickY()`, que invierte el resultado solo para los ejes Y
   (los X no cambian, misma convención en ambos lados).
2. **El rumble no llegaba en absoluto** — ni siquiera el motor principal
   (no solo el de gatillos, que ya estaba marcado como sin verificar).
   `HandleOutputReceived()` descarta silenciosamente cualquier paquete
   que no tenga exactamente la forma que se supuso sin confirmar (13
   bytes, `data[5] == 0x0F`) — si la forma real es distinta, nunca se
   ve ni un solo evento de rumble, sin ningún rastro en el log. Añadido
   logging incondicional del paquete crudo (vía el mecanismo de `error`
   ya existente, prefijado `[diag]`) para poder ver la forma real la
   próxima vez que se pruebe, y ajustar el parseo con datos reales en
   vez de otra suposición. **Sigue sin arreglar** — solo instrumentado.

## Actualización (2026-09-20): segundo test — causa real del rumble encontrada

El logging de diagnóstico dio sus frutos: capturado un paquete real del
test de vibración de Steam:

```
source=HidOutput len=7 bytes=00000000FF00EB
```

**La causa real era más simple de lo que parecía**: el código original
solo aceptaba `HMOutputSource.XInput` y descartaba silenciosamente
cualquier otra fuente — pero Steam manda el rumble como `HidOutput` (un
report HID genérico), no como `XInputSetState`. Por eso nunca llegaba
absolutamente nada, sin necesidad siquiera de que el formato de bytes
importase.

Arreglado: `HandleOutputReceived()` ahora maneja `HidOutput` (7 bytes,
`[LT, RT, LM, RM, 0xFF, 0x00, 0xEB]` — la cola coincide exactamente con
el layout de 13 bytes que se había supuesto antes, con la cabecera GIP
recortada) y, por separado, `XInput` con el formato que la propia
documentación del SDK indica textualmente (5 bytes: cmd + tamaño + motor
bajo + motor alto + reservado), en vez de la suposición cruzada de otro
proyecto que se usaba antes.

**Sin confirmar todavía**: la muestra capturada era toda ceros
(LT=RT=LM=RM=0), así que el ORDEN de esos 4 bytes dentro del payload de
`HidOutput` es una inferencia razonada (coincide con el orden del layout
de 13 bytes ya usado), no una confirmación — si el rumble llega pero al
motor equivocado, hace falta una captura con valores distintos de cero
para corregir el orden. El logging `[diag]` se mantiene por si acaso.

## Actualización (2026-09-20): límite arquitectónico real de Windows, no un bug nuestro

Tras el fix anterior, una prueba con `Windows.Gaming.Input.Gamepad.Vibration`
(vía una herramienta de test hecha para esta sesión) seguía sin producir
ningún byte de motor real — el paquete capturado seguía siendo el mismo
`00000000FF00EB` de siempre, sin importar qué intensidad se pidiera.

Investigando el propio repo de HIDMaestro se encontró
`docs/investigations/wgi-silent-sink-2026-04/` — una investigación suya,
extensa y ya cerrada con conclusión firme, que confirma exactamente este
problema: **`Windows.Gaming.Input.Gamepad.put_Vibration` (y todo lo que
se construye encima: GameInput, la vibración de navegadores tipo Chrome/
Edge, y muy probablemente el test de vibración de Steam) nunca entrega
bytes de motor a un mando virtual enumerado bajo ROOT** como el de
HIDMaestro. La sonda de enumeración llega bien al driver, pero el
despacho real de vibración está condicionado a que el dispositivo tenga
un padre USB real — algo que un mando virtual sin driver en modo kernel,
por diseño de HIDMaestro, no puede tener. Confirmado por el propio equipo
de HIDMaestro con reportes a Microsoft; no es algo arreglable desde nuestro
lado (bridge o Apollo).

**La vía que sí funciona, según su misma investigación**: `XInputSetState`
clásico (`xinput1_4.dll`, la API que usan la mayoría de juegos de PC para
rumble) **sí llega correctamente** a este mismo mando virtual — ya
verificado por ellos. Es exactamente el camino que ya arreglamos antes en
este mismo documento (`HMOutputSource.XInput`, formato de 5 bytes).

**Consecuencia importante**: el rumble de gatillos (impulse triggers) —
la razón original de usar HIDMaestro en vez de ViGEm — depende
exclusivamente de WGI/GameInput, que es justo el camino confirmado
bloqueado. El rumble principal (motores izquierdo/derecho) debería
funcionar en juegos que usan XInput clásico; el de gatillos, con este
mando enumerado bajo ROOT, muy probablemente **no funciona nunca**, y no
es algo que se pueda arreglar iterando en `hidmaestro-bridge.exe` — es un
límite de la arquitectura de Windows/HIDMaestro, documentado por su
propio equipo tras una investigación exhaustiva.

**Confirmado por el usuario en un juego real (2026-09-20, mismo día)**:
el rumble principal **funciona correctamente**. Los gatillos, como se
predijo arriba, no se detectan — consistente con el límite de WGI, no un
fallo nuestro. Con esto, el objetivo funcional mínimo de esta feature
(rumble real donde antes no había ninguno con ViGEm) queda confirmado
end-to-end en hardware real; el de gatillos queda documentado como
limitación conocida y probablemente permanente mientras el mando siga
enumerado bajo ROOT sin driver en modo kernel.

`tools/gamepad-vibration-test/` se reescribió para usar `XInputSetState`
directamente en vez de `Windows.Gaming.Input`, como control positivo real
según su propia evidencia — ya no puede probar los motores de gatillo (el
XInput clásico ni siquiera tiene ese concepto).

## Actualización (2026-09-20): revertido el manejo de HidOutput, y contraste con libvirtualhid

Dos cosas más el mismo día:

**1. Se revirtió el manejo de `HidOutput` en `HandleOutputReceived()`.**
Ese código (añadido en la actualización anterior, basado en la captura de
Steam `00 00 00 00 FF 00 EB`) asumía que ese paquete era rumble real con
valores casualmente en cero. Pero la propia investigación de WGI de
HIDMaestro (arriba) describe paquetes de sonda/control con forma muy
similar (`00 0D 00 00 01`, `00 00 00 00 02`) que **nunca llevan datos de
motor reales** — y Steam casi seguro habla con este dispositivo vía WGI.
Tratar esa captura como rumble real arriesgaba mandar un evento
`rumble: 0,0` cada vez que WGI sondea el dispositivo, pudiendo pisar un
valor real que acabara de llegar por XInput. Revertido — solo queda el
camino `XInput` (5 bytes, documentado, confirmado con un juego real). El
logging `[diag]` se mantiene.

**2. Se contrastó la conclusión de "límite arquitectónico" con
`libvirtualhid`** (la librería de LizardByte, ver la sección de más abajo
"Alternativa investigada"), a petición del usuario, que recordaba su
soporte de vibración háptica como contraejemplo. Revisando su código
fuente real de Windows (`libvirtualhid_xbox360_umdf.cpp`): usa **VHF**
(Virtual HID Framework de Microsoft) además de su companion UMDF2 XUSB —
una diferencia arquitectónica real frente al driver a medida de
HIDMaestro. Pero su función de rumble (`queue_rumble_output`) solo tiene
dos parámetros (motor principal izquierdo/derecho, igual que XInput
clásico) — ninguna gestión de motores de gatillo en absoluto. Búsqueda en
todo su repo de "xbox_one", "xbox_series" o "impulse" en cualquier fichero
de Windows: **cero resultados**. Su único trabajo confirmado de gatillos
de impulso está titulado explícitamente "**Linux**: support Xbox Impulse
Triggers" (issue #109, cerrado) — nunca Windows.

**Conclusión revisada**: no hay contradicción real. Lo que `libvirtualhid`
confirma funcionando en Windows es la misma categoría de rumble que
HIDMaestro ya tiene ahora (motores principales) — no gatillos de impulso.
Ningún proyecto ha confirmado ni implementado gatillos de impulso en
Windows para un mando sin driver en modo kernel. La conclusión original se
mantiene, ahora con más respaldo cruzado en vez de menos.

## Corrección importante (2026-09-16): el rumble de gatillos no está confirmado NI descartado

Una versión anterior de este documento afirmaba, citando la investigación
propia de HIDMaestro (`docs/investigations/wgi-silent-sink-2026-04/`), que
el rumble de gatillos **no llega en absoluto** vía WGI/GameInput a los
mandos virtuales de HIDMaestro, y por eso recomendaba descartar la feature.
Al cuestionarlo, encontré dos problemas con esa conclusión:

1. **Esa investigación probó el perfil Xbox 360 Wired** (`VID 045E PID 028E`,
   confirmado en el propio documento), **no el perfil Xbox Series X|S**
   (`VID 045E PID 0B12`) que esta feature usa. El perfil `xbox-series-xs`
   declara `"driverMode":"xinputhid"` en su JSON — un modo de driver
   distinto al "XUSB companion" que la investigación probó.
2. **La investigación se hizo en una rama experimental**
   (`v1-dev-experiment-xusb-child-pdo`), contra un archivo de driver
   (`driver/xusbshim.c`) que el propio documento dice explícitamente que
   *"no existe en `master`"*. El código que de verdad se usaría
   (`driver/companion.c` en `master`) no fue el que se probó.

Busqué en los issues reales del repo cualquier test específico de
`xinputhid` + WGI/rumble de gatillos, y no encontré ninguno — ni a favor
ni en contra. **Esto significa que el estado real para nuestro caso de uso
concreto es: genuinamente sin verificar**, no "confirmado roto" como decía
antes. Dado eso, decidimos (con el usuario) cerrar este branch de todas
formas — dejando el código implementado y documentado, pero sin afirmar
que la funcionalidad de gatillos funcione hasta probarla en Windows real.

## Aviso importante, antes de nada

Esta es, con diferencia, **la feature menos verificada de las que hemos
hecho**. A diferencia de frame pacing (que compilamos y arrancamos de
verdad), **nada de este código se ha podido compilar ni ejecutar en este
entorno** — vive entero en `src/platform/windows/` (que nuestro Docker de
Linux ni siquiera incluye en el build) y en un proyecto .NET nuevo que
requiere Windows + .NET 10 SDK. Todo lo de abajo es "debería funcionar
según la documentación/código fuente que pude consultar", no "confirmado
funcionando".

## Qué se añadió y por qué

Apollo emula mandos virtuales con ViGEmBus, pero ViGEmBus solo emula un
Xbox 360 clásico — sin motores de vibración en los gatillos (rumble de
Xbox Series/Elite). El protocolo de red de Apollo hacia el cliente ya
soporta ese rumble de gatillos, así que el hueco real era del lado del
host. HIDMaestro (proyecto externo, MIT,
[github.com/hifihedgehog/HIDMaestro](https://github.com/hifihedgehog/HIDMaestro))
sí tiene un perfil de Xbox Series con ese soporte.

Se añadió un backend de input alternativo, seleccionable con
`input_backend` (`vigem` por defecto, `hidmaestro` opcional), que emula
**solo el perfil Xbox Series X|S** (alcance decidido contigo — no todos
los 234 perfiles de HIDMaestro, ver análisis previo).

## Por qué hay un proceso .NET separado (`tools/hidmaestro-bridge/`)

HIDMaestro solo tiene SDK en C#, sin API nativa en C/C++. Decisión
(confirmada contigo): en vez de mezclar código nativo/managed dentro de
Apollo (C++/CLI, complejo de mantener sin experiencia en C++), Apollo
lanza un `.exe` en .NET aparte y le habla por líneas JSON sobre su
stdin/stdout. Protocolo completo documentado como comentario al principio
de `src/platform/windows/input.cpp` (clase `hidmaestro_t`) y de
`tools/hidmaestro-bridge/Program.cs`.

## Diff conceptual: modificado vs. nuevo

**Modificado** (mínimo, todo detrás del flag):
- `src/config.h` / `src/config.cpp`: nuevo campo `input_backend`
  (default `"vigem"`, cero cambio de comportamiento).
- `src/platform/windows/input.cpp`: 4 puntos de despacho (`alloc_gamepad`,
  `free_gamepad`, `gamepad_update`, y el destructor/factory de
  `input_raw_t`) ahora comprueban primero si el backend HIDMaestro está
  activo; si no, siguen exactamente la ruta ViGEm de siempre. El resto
  del archivo (1782 líneas originales) no se tocó.
- `cmake/targets/windows.cmake` / `cmake/packaging/windows.cmake`: build e
  instalación del bridge, **detrás de un flag de CMake propio**
  (`SUNSHINE_ENABLE_HIDMAESTRO`, OFF por defecto) — así ningún build de
  Windows normal necesita tener .NET 10 instalado ni descargar el release
  de HIDMaestro (~115MB) a menos que se pida explícitamente.
- Web UI (`Inputs.vue`, `config.html`, `en.json`, `configuration.md`):
  nuevo selector, siguiendo el patrón exacto del selector de tipo de
  mando ya existente.

**Nuevo** (no existía antes):
- Clase `hidmaestro_t` completa en `input.cpp` (proceso hijo + pipes +
  hilo lector + parseo JSON).
- `tools/hidmaestro-bridge/` — proyecto .NET 10 nuevo entero
  (`hidmaestro-bridge.csproj` + `Program.cs`).
- Este documento y `hidmaestro-backend-analysis.md`.

## Impacto en compatibilidad con upstream

**Bajo.** Todo lo nuevo vive en archivos/directorios que no existen en
Apollo/Sunshine (el proyecto .NET, la clase `hidmaestro_t` autocontenida).
Los únicos puntos de fricción posibles en un futuro `git merge` son los 4
puntos de despacho en `input.cpp` (inserciones pequeñas al principio de
funciones ya existentes) y las listas de opciones en `config.html`/
`configuration.md`/`en.json` (mismo tipo de conflicto fácil que ya vimos
en frame pacing).

## Qué requiere validación en Windows

**Actualización:** la primera versión de este documento marcaba los
nombres de `HMButton` y el rango numérico de `Axes` como "no confirmados,
mejor suposición". Eso era un error de proceso mío — me había conformado
con resúmenes de páginas de documentación en vez de ir al código fuente
real. Cuando se cuestionó, descargué directamente
`sdk/HIDMaestro.Core/HMGamepadState.cs` (la definición real de los enums)
y `example/SdkDemo/Program.cs` (el demo oficial del SDK, que incluso
construye un mando Xbox Series X|S con botones reales) del propio repo de
HIDMaestro. Con eso corregí dos cosas que estaban mal:

- **`HMButton` no tiene miembros de D-pad** — el D-pad va por un campo
  `Hat` separado (`HMHat`, enum de 8 direcciones), no por el bitmask de
  botones. `Program.cs` ahora convierte los 4 bits de D-pad de Apollo a
  `HMHat` en una función dedicada (`DecodeHat`).
- **El rango de `Axes` no es -1..1** — es **0..1 uniforme**, con 0.5 =
  centro en los sticks y 0.0 = gatillo suelto. Confirmado explícitamente
  en los comentarios de `HMGamepadState.cs` y usado así en todo el demo
  oficial. `Program.cs` ahora normaliza con esa convención y usa el
  helper `HMGamepadStateHelpers.StandardAxes()` del propio SDK (resuelve
  los ejes correctos del perfil activo automáticamente, en vez de que
  nosotros los codifiquemos a mano).

Con esto, los nombres de botones/ejes y la convención numérica están
**confirmados contra el código fuente real**, no son ya una suposición.

Lo que sigue sin poder confirmarse en este entorno, por orden de
probabilidad de que algo esté mal:

1. **Formato exacto del report de rumble/gatillos que llega por
   `OutputReceived`** para el perfil `xbox-series-xs` — sigue siendo la
   mayor incertidumbre real. Se asumió el layout de 13 bytes que
   documenta el código de `PadForge` (mismo autor, mismo proyecto
   hermano) para controladores físicos Xbox One+/Series por GIP, pero el
   propio demo oficial de HIDMaestro **no llega a probar esto** — solo
   muestra `OutputReceived`/`OutputDecoded` disparando para un DualSense,
   no para un perfil Xbox. Es la pieza con menos evidencia directa de
   todo el diseño.
2. **Ruta del `.dll` dentro del ZIP de release** — el CMake busca
   `HIDMaestro.Core.dll` por nombre recursivamente en vez de asumir una
   ruta fija, precisamente porque no pude verificar la estructura interna
   del ZIP (118MB, no descargado en este entorno).
3. **Todo el comportamiento en tiempo real**: creación del mando virtual,
   que el gatillo LT/RT llegue correctamente, que el rumble de vuelta al
   cliente Moonlight funcione de extremo a extremo. El propio C++ de
   Apollo tampoco se ha podido compilar aquí (ver aviso al principio de
   este documento).

## Alternativa investigada: libvirtualhid (LizardByte)

Durante esta investigación surgió `libvirtualhid`
([github.com/LizardByte/libvirtualhid](https://github.com/LizardByte/libvirtualhid)),
una librería C++ nativa del propio mantenedor de Sunshine (LizardByte,
lanzada agosto 2026) pensada como reemplazo de ViGEmBus/inputtino, con
soporte de rumble de gatillos **confirmado y verificado en Linux** (issue
cerrado "Linux: support Xbox Impulse Triggers"). Ventajas sobre HIDMaestro:
C++ nativo (sin proceso .NET aparte), del mismo grupo que mantiene
Sunshine.

Pero: en Windows también usa UMDF2 en modo usuario (no kernel, contra lo
que asumí en un primer momento) — misma categoría arquitectónica que
HIDMaestro, así que podría o no compartir la misma limitación de WGI.
**No encontré ningún issue en su repo que confirme rumble de gatillos
funcionando en Windows** (0 resultados buscando "GameInput"), y el driver
de Windows requiere licencia de pago. No se investigó más a fondo — se
deja anotado aquí como vía a revisar en el futuro si el proyecto madura y
confirma soporte de Windows.

## Instalación del driver: no hace falta nada manual

**Actualización (2026-09-20):** no hay un instalador aparte de HIDMaestro
que ejecutar. Verificado directamente en `tools/hidmaestro-bridge/Program.cs:65-71`:
el propio bridge comprueba `s_context.IsDriverInstalled` al arrancar y, si
no lo está, llama a `s_context.InstallDriver()` automáticamente — mismo
patrón que documenta el propio SDK de HIDMaestro (README: *"Pure user
mode: no kernel driver, no EV cert, no reboots"*, certificado autofirmado
localmente, sin modo de pruebas de Windows).

Único requisito real: ese primer arranque necesita permisos de
administrador (instalar un driver, aunque sea en modo usuario, requiere
elevación). Si Apollo corre como servicio de Windows (la vía normal del
instalador), ya va elevado y no hace falta nada especial. Si se ejecuta
el `.exe` a mano sin "Ejecutar como administrador", `InstallDriver()`
fallaría — el bridge ya captura ese error con un try/catch y lo reporta
limpio vía el protocolo JSON (`EmitError`) en vez de colgarse, así que se
vería en `sunshine.log`.

## Cómo activar/probar una vez en Windows

1. Compilar vía CI (`.github/workflows/build-windows.yml`, ya incluye
   `-DSUNSHINE_ENABLE_HIDMAESTRO=ON` y la instalación de .NET 10 SDK
   automática — no hace falta instalar nada en local, ver
   `README-fork.md`) o en local si se prefiere (requiere .NET 10 SDK +
   Visual Studio 2022+).
2. Instalar el `.exe` resultante normalmente (como servicio, para que el
   primer arranque de HIDMaestro ya vaya elevado).
3. En la Web UI de Apollo → pestaña **Input** → **Gamepad Input Backend**
   → seleccionar "HIDMaestro". Guardar y reiniciar el stream.
4. Conectar un mando Xbox Series real en el cliente Moonlight, jugar algo
   que use rumble de gatillos (ej. un juego de carreras), y comprobar si
   se siente. Si no, revisar los logs de Apollo (busca "hidmaestro").
5. Si falla la compilación del bridge (`hidmaestro-bridge.csproj`), es
   probable que la versión del SDK instalada haya cambiado algo desde que
   se escribió este código — comparar `Program.cs` contra el SDK
   instalado (IntelliSense de Visual Studio) y corregir lo que no
   coincida.
