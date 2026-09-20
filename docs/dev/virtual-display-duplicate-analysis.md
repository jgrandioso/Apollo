# Análisis: Duplicar el monitor virtual con la pantalla física (Fase 1)

Análisis previo obligatorio antes de tocar código. Branch:
`feature/virtual-display-duplicate`.

## Qué pide la feature

Cuando un cliente se conecta forzado a usar el monitor virtual (SudoVDA),
que ese monitor virtual nuevo se ponga en modo "duplicado" con la
pantalla física del host, a la resolución que pida ese cliente concreto
— por defecto, con un toggle en la Web UI para desactivarlo si hace
falta.

## Qué hace Apollo hoy (código real, `src/process.cpp:236-334`)

Cuando se cumple alguna condición (`headless_mode`, el cliente pidió
virtual display, la app está configurada para ello, o no hay pantalla
activa que capturar):

1. `VDISPLAY::createVirtualDisplay(...)` crea el dispositivo con el
   ancho/alto/fps que pidió el cliente (`render_width`, `render_height`,
   `target_fps`).
2. `VDISPLAY::changeDisplaySettings(...)` aplica esa resolución/fps al
   monitor recién creado.
3. Si `isolated_virtual_display_option` está activo (config existente,
   default apagado), `VDISPLAY::changeDisplaySettings2(..., bApplyIsolated=true)`
   **reposiciona** el monitor virtual para que no se solape con las
   pantallas físicas en el escritorio extendido — esto es solo
   posicionamiento espacial, no tiene nada que ver con duplicar
   contenido. Importante no confundir los nombres en la UI.

Ninguno de los dos pasos existentes pone el monitor virtual en modo
"duplicado" (clonado) con ninguna otra pantalla — siempre se añade como
una pantalla **extendida** independiente.

## Cómo funciona "duplicado" a nivel de Windows (verificado en el propio código)

`changeDisplaySettings`/`changeDisplaySettings2` (mismo fichero) ya usan
la API real de Windows CCD (Connecting and Configuring Displays):
`QueryDisplayConfig()` para leer la topología activa
(`DISPLAYCONFIG_PATH_INFO[]`/`DISPLAYCONFIG_MODE_INFO[]`), modifican esos
arrays, y llaman a `SetDisplayConfig(..., SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE)`
para aplicar el cambio — patrón ya establecido en este mismo fichero, no
haría falta tocar la librería `libdisplaydevice` aparte (que también
tiene un concepto de topología con `setTopology()`, pero es una capa más
alta usada para otra cosa — configurar UNA pantalla concreta, no para
relaciones de clonado entre dos).

En el modelo de Windows, un monitor "duplicado" con otro es, a nivel de
`DISPLAYCONFIG_PATH_INFO`: dos **paths de destino** (`targetInfo`)
distintos que comparten el **mismo índice de fuente**
(`sourceInfo.id`/`sourceInfo.adapterId`) — es decir, dos salidas físicas
distintas mostrando el mismo framebuffer de origen. Para nuestro caso:
el path del monitor virtual nuevo debería apuntar al mismo `sourceInfo`
que ya usa la pantalla física principal, en vez de tener su propio
origen independiente (que es lo que pasa hoy, al crearse como pantalla
extendida).

## La incógnita real, sin poder verificarla aquí

El modo "duplicado" clásico de Windows históricamente **fuerza la misma
resolución** en todos los monitores del grupo clonado — no está
confirmado si Windows 10/11 modernos permiten que cada target de un
grupo clonado tenga su propia resolución de salida independiente (lo que
pide la feature: la pantalla física se queda como está, pero el
duplicado que ve el cliente remoto está a SU resolución). Esto
necesitaría probarse en hardware Windows real — si no es posible,
la alternativa sería escalar/recortar el contenido duplicado en el
propio pipeline de captura de Apollo en vez de a nivel de Windows CCD,
lo cual sería una feature bastante más grande.

## Diseño propuesto

- Nuevo config option: `virtual_display_duplicate_primary` (bool,
  **default `true`**, tal y como se pidió — comportamiento nuevo por
  defecto, no opt-in como el resto de features de este fork hasta
  ahora). Web UI: checkbox en la pestaña Audio/Video, junto a
  `isolated_virtual_display_option`, dejando claro en la descripción que
  son conceptos distintos (posicionar vs. duplicar contenido).
- Punto de enganche: `src/process.cpp`, justo después del bloque
  existente que ya crea el monitor virtual y le aplica resolución
  (después de la línea 316 actual, dentro del `if (!vdisplayName.empty())`).
  Nueva función `VDISPLAY::duplicateWithPrimaryDisplay(vdisplayName, ...)`
  en `virtual_display.cpp`, siguiendo el mismo patrón exacto que
  `changeDisplaySettings2` (leer topología actual, encontrar el
  `sourceInfo` de la pantalla marcada como primaria, reasignar el path
  del monitor virtual a ese mismo origen, aplicar con `SetDisplayConfig`).
- Si `virtual_display_duplicate_primary` es `false`, comportamiento
  idéntico al actual (pantalla extendida) — controlable por el usuario
  en cualquier momento sin reiniciar el servicio.

## Riesgos

- **Todo esto es Windows-only y no se puede compilar ni probar en este
  entorno de desarrollo Linux** — mismo límite que SudoVDA/HIDMaestro
  desde el principio de este proyecto.
- **La incógnita de la resolución independiente en modo clonado** (ver
  arriba) es el riesgo técnico principal — si Windows no lo permite tal
  cual, el diseño cambiaría.
- Al ser comportamiento **por defecto** (no opt-in), un fallo aquí
  afectaría a cualquiera que use el monitor virtual, no solo a quien
  active una opción nueva — justifica ser más cuidadoso que con el resto
  de features de este fork, y probablemente justifica un plan de
  rollback claro (el toggle) desde el primer commit, no añadido después.
- Interacción con `isolated_virtual_display_option`: si ambos están
  activos a la vez, ¿qué gana? Probablemente no tenga sentido activar
  los dos simultáneamente (duplicar Y reposicionar como si no se
  solapara son conceptualmente contradictorios) — a decidir antes de
  implementar.

## Decisión (2026-09-20)

1. **Si Windows fuerza una resolución compartida en el grupo duplicado,
   se mantiene la resolución del monitor virtual** (la que pidió el
   cliente) — se ajustaría la física a esa, no al revés. Se intenta
   primero con `SetDisplayConfig` normal (que en la práctica adopta la
   resolución que se le pase en el `sourceMode` compartido); si
   Windows lo rechaza igualmente, se loguea el fallo y no se activa el
   duplicado para esa sesión (no se fuerza nada más agresivo).
2. **Mutuamente excluyentes** con `isolated_virtual_display_option` en
   la Web UI — activar uno desactiva el otro automáticamente en el
   propio formulario (misma pareja de checkboxes, comportamiento tipo
   radio). A nivel de C++, si por editar el fichero de config a mano
   ambos quedan en `true`, `virtual_display_duplicate_primary` gana
   (es el nuevo comportamiento por defecto) y se loguea un aviso del
   conflicto.

Con esto, implementado en esta misma branch.
