# Duplicar el monitor virtual con la pantalla física (Fase 3 — cierre)

Branch: `feature/virtual-display-duplicate`. Lee primero
`docs/dev/virtual-display-duplicate-analysis.md` para la investigación y
el diseño completo, incluida la decisión de diseño (2026-09-20).

## Qué se añadió y por qué

Hasta ahora, cada vez que Apollo crea un monitor virtual para un
cliente, se añade siempre como pantalla **extendida** independiente —
nunca hay forma de que el propio host vea en su pantalla física lo mismo
que se está transmitiendo. Esta feature pone el monitor virtual en modo
**duplicado/clonado** con la pantalla primaria, a la resolución que pida
el cliente (no la de la física).

Nuevo flag: `virtual_display_duplicate_primary` (Web UI: Audio/Video).
Mutuamente excluyente con `isolated_virtual_display_option` (ya
existente): activar uno desactiva el otro, tanto en la Web UI (checkboxes
enlazados) como en tiempo de ejecución si se edita el config a mano (gana
`virtual_display_duplicate_primary`, con aviso en el log).

**Actualización (2026-09-21)**: empezó con `default activado` (única
feature de este fork que rompía el patrón opt-in), pero se revirtió a
**desactivado por defecto** tras confirmarse en hardware real un bug de
cursor duplicado/fantasma al reanudar la sesión desde un segundo cliente
distinto — ver la sección "Qué NO se pudo validar aquí" más abajo, ahora
con el hallazgo real documentado.

## Diff conceptual: modificado vs. nuevo

**Nuevo**:
- `VDISPLAY::duplicateWithPrimaryDisplay()` en
  `src/platform/windows/virtual_display.h`/`.cpp` — usa la misma API
  real de Windows CCD (`QueryDisplayConfig`/`SetDisplayConfig`) que ya
  usa `changeDisplaySettings2` en el mismo fichero, pero en vez de mover
  la posición del monitor virtual, reasigna su `sourceInfo` (adapter+id)
  al mismo que ya usa la pantalla primaria — así es como Windows
  representa un grupo de pantallas clonadas a nivel de
  `DISPLAYCONFIG_PATH_INFO`.

**Modificado**:
- `src/config.h`/`config.cpp`: nuevo flag `virtual_display_duplicate_primary`
  (bool, default `true`).
- `src/process.cpp` (~316-327): tras crear el monitor virtual y
  aplicarle resolución, se llama a `duplicateWithPrimaryDisplay()` si el
  flag está activo (con prioridad sobre `isolated_virtual_display_option`
  si ambos quedaran activos a la vez).
- Web UI (`AudioVideo.vue`): nuevo checkbox, con dos `computed` con
  getter/setter que fuerzan la exclusión mutua con el checkbox de
  "isolated" ya existente — no se pudo usar un simple `v-model` directo
  porque `Checkbox.vue` usa `defineModel()`, así que la lógica de
  "activar uno desactiva el otro" vive en el componente padre.
- `config.html`, `en.json`, `docs/configuration.md` — el flag expuesto y
  documentado, verificado contra el patrón ya existente de
  `isolated_virtual_display_option` en cada fichero.

## Impacto en compatibilidad con upstream

**Bajo.** Todo el cambio en `virtual_display.cpp` es una función nueva
añadida al final del fichero, sin tocar ninguna función existente salvo
por las dos líneas de invocación en `process.cpp` (una rama `if/else if`
nueva alrededor de la lógica de `isolated_virtual_display_option` ya
existente, sin eliminar nada). El campo de config es aditivo.

## Qué NO se pudo validar aquí (todo, honestamente)

Absolutamente nada de esto se ha podido ejecutar en este entorno de
desarrollo Linux — es código exclusivo de Windows
(`src/platform/windows/`). Solo se ha podido compilar la parte
multiplataforma (`config.h`/`config.cpp`) vía CI de Linux; la lógica
real de duplicado necesita CI/hardware Windows.

Sin poder probarlo en Windows real, quedan sin confirmar:

1. **CONFIRMADO en hardware real (2026-09-20): Windows NO acepta una
   resolución independiente en el target duplicado** — fuerza la misma
   resolución para todo el grupo clonado, como se sospechaba. El efecto
   observado: la pantalla física cambiaba a la resolución pedida por el
   cliente (en la prueba del usuario, 1360x720) en cuanto arrancaba un
   stream con este flag activo, y **se quedaba así indefinidamente**
   después de terminar la sesión — porque `duplicateWithPrimaryDisplay()`
   sobrescribe el `sourceMode` de la propia pantalla primaria (es el
   mecanismo real de clonado de Windows: todo el grupo comparte una sola
   superficie de origen), y el `terminate()` de `process.cpp` solo hacía
   `removeVirtualDisplay()` + `display_device::reset_persistence()` — eso
   destruye el display virtual, pero nunca tocaba de vuelta la resolución
   de la primaria, porque ese código se escribió pensando en el modo
   "isolated" (que nunca toca la primaria).

   **Arreglado**: se captura la resolución/refresco original de la
   primaria (`VDISPLAY::getDeviceSettings`) justo antes de llamar a
   `duplicateWithPrimaryDisplay()`, guardada en nuevos campos de
   `proc_t` (`duplicated_primary_display`,
   `original_primary_{width,height,refresh_rate}`). En `terminate()`,
   justo después de `removeVirtualDisplay()`, se restaura con la nueva
   función `VDISPLAY::restorePrimaryDisplayMode()` (API DEVMODE simple,
   sin volver a tocar la topología CCD — para cuando esto corre, el
   display virtual ya no existe y la primaria vuelve a ser un path
   normal). Sigue sin validar en Windows real que la restauración en sí
   funcione correctamente, aunque el mecanismo (`ChangeDisplaySettingsExW`)
   es el mismo que ya usa `changeDisplaySettings()` para el modo baseline.
2. **Si dejar la entrada de `sourceMode` que antes usaba el monitor
   virtual, ahora sin ningún path que la referencie, en el array que se
   pasa a `SetDisplayConfig` es realmente inofensivo** — se asumió que sí
   (comentado explícitamente en el código), pero no está confirmado.
   **Posible pista (2026-09-21)**: el bug de cursor duplicado/fantasma de
   abajo podría estar relacionado con esto — si Windows mantiene un
   overlay de cursor por hardware independiente para esa entrada
   "huérfana", eso explicaría un segundo cursor visible sin limpiar. No
   confirmado, solo una hipótesis razonada.
2b. **CONFIRMADO en hardware real (2026-09-21): cursor duplicado/fantasma
   al reanudar la sesión desde un segundo cliente distinto.** Escenario
   reproducido por el usuario: el iPad lanza una app (arranca el modo
   duplicado a la resolución que pide el iPad), luego el PC se conecta y
   "retoma" la misma sesión (sin que el iPad esté activamente resumed) —
   en el PC aparece un cursor duplicado/con rastro, específicamente
   ligado a la resolución que pidió el iPad (con otras resoluciones no se
   nota, o no se ha probado). Causa raíz sin confirmar — la teoría más
   plausible es que el cambio de resolución en crudo vía
   `SetDisplayConfig` no resetea correctamente el estado del overlay de
   cursor por hardware que estaba calibrado para la resolución anterior.
   **Por esto se revirtió el flag a desactivado por defecto** (ver
   arriba) — no se ha intentado arreglar el mecanismo en sí, requiere
   poder iterar en Windows real.
3. **El comportamiento real de principio a fin**: que el monitor virtual
   aparezca de verdad duplicado (no como una tercera pantalla más), que
   el toggle de la Web UI realmente alterne el comportamiento sin
   reiniciar el servicio, y que la exclusión mutua con
   `isolated_virtual_display_option` se sienta natural en la UI.

## Cómo activar/probar una vez en Windows

1. Compilar vía CI (`build-windows.yml`) o en local, sin flags extra —
   viene activado por defecto.
2. Conectar un cliente Moonlight forzando el uso de monitor virtual
   (`headless_mode`, o la app configurada con `virtual_display`, o sin
   pantalla activa que capturar).
3. En la pantalla física del host, debería verse el mismo contenido que
   recibe el cliente (a la resolución que pidió el cliente, no la nativa
   de la física) — si en vez de eso aparece como una tercera pantalla
   extendida más, el mecanismo de reasignación de `sourceInfo` no
   funcionó como se esperaba.
4. Para comprobar el toggle: Web UI → Audio/Video → desactivar
   "Duplicate Virtual Display with Primary Display" → reiniciar el
   stream → debería volver al comportamiento de siempre (pantalla
   extendida independiente).
5. Para comprobar la exclusión mutua: activar "Isolated Virtual
   Display" en la Web UI y confirmar que el checkbox de duplicado se
   desmarca solo (y viceversa).
