# Símbolos mal mapeados en hosts con layout no-US (cierre)

Branch: `feature/keyboard-symbol-layout`. Reportado por el usuario:
conectando desde un iPad (Moonlight iOS) a un host con Windows en
español, las letras se escriben bien pero los símbolos salen en la
tecla equivocada (`@`, `"`, `/`, `¿`, `¡`).

## Causa raíz

Moonlight normaliza la tecla que pulsas a "el código de tecla virtual
(VK) que en un teclado US produciría el mismo carácter" antes de
mandarlo por red. Apollo, para máxima compatibilidad con juegos que
leen scancodes en crudo, convierte ese VK a un scancode usando una
tabla estática pensada para layout US (`VK_TO_SCANCODE_MAP` en
`keylayout.h`, con su propio comentario: *"GameStream uses this as the
canonical key layout for scancode conversion"*).

Para letras esto funciona porque la posición física de una tecla es la
misma en prácticamente cualquier variante QWERTY. Para símbolos no: `@`
es Shift+2 en un teclado US, pero Shift+2 en un teclado español da
`"` (el `@` real está en AltGr+2). El scancode que se envía es
correcto posicionalmente (la tecla "2"), pero el carácter resultante
solo es correcto si el host también usa layout US.

Confirmado con el usuario que activar/desactivar `always_send_scancodes`
no cambiaba nada — eso es porque ese flag solo afecta a teclas que el
propio cliente marca como "no normalizadas" (`SS_KBE_FLAG_NON_NORMALIZED`),
y Moonlight para iOS aparentemente marca estas teclas como normalizadas
igualmente, así que Apollo nunca llegaba a esa rama del código.

## Qué se cambió

`src/platform/windows/input.cpp`, `keyboard_update()`: cuando la tecla
llega "normalizada" (que es el caso normal) y es una tecla de
símbolo/dígito (`is_symbol_or_digit_key()` — dígitos 0-9 y las teclas
`VK_OEM_*` dedicadas a puntuación), y el layout activo del host **no**
es US, se deja `ki.wScan` en 0 en vez de usar la tabla estática. Eso
hace que el código ya existente más abajo (`if (ki.wScan) ... else
ki.wVk = modcode;`) mande un evento de VK normal en vez de un scancode
- Windows entonces resuelve el carácter usando el layout real del host
en vez de asumir US.

**Deliberadamente excluido**: letras, teclas de navegación, teclas de
función y dígitos cuando el host SÍ está en layout US - esas siguen
exactamente igual que antes (tabla estática, scancode). El motivo de
incluir los dígitos en el conjunto afectado (aunque `1234567890` sin
Shift casi nunca produce símbolos raros) es que `@`, `"` etc. viven en
Shift+dígito, y separar "dígito solo" de "dígito con Shift" habría
necesitado rastrear el estado de Shift en tiempo real - la posición
física del dígito es igual en casi cualquier layout QWERTY, así que
tratarlo igual que las teclas OEM no debería cambiar qué scancode ve un
juego que lee dígitos en crudo (solo cambia qué CARÁCTER produce Shift
sobre esa misma tecla).

## Impacto en compatibilidad con upstream y con juegos

**Bajo.** El cambio es aditivo: dos funciones nuevas
(`is_symbol_or_digit_key()`, `host_layout_is_non_us()`) y una condición
extra dentro de la rama ya existente para teclas normalizadas, sin
tocar la rama de teclas no-normalizadas ni el resto de la función.

Riesgo real: un juego que lea el scancode en crudo de una tecla de
símbolo/puntuación (no de una tecla de movimiento/acción) para un bind,
en un host con layout no-US. Se consideró improbable — las teclas de
puntuación casi nunca se usan como bind de gameplay - y es la razón por
la que el fix no se aplicó a todas las teclas sin más.

## Actualización (2026-09-20): el primer fix no era suficiente

El fix de arriba solo tocaba la rama "normalizada" de `keyboard_update()`.
El usuario probó en hardware real (iPad + host Windows en español) y
`@` seguía saliendo como `"`. Con logging de diagnóstico añadido
(`min_log_level=debug`) se confirmó el paquete real que manda Moonlight
para iOS al pulsar `@`:

```
keyCode [0032]   (VK_2)
modifiers [01]   (Shift)
flags [01]       (SS_KBE_FLAG_NON_NORMALIZED)
```

Es decir: la tecla llega marcada **no normalizada**, no normalizada —
justo la rama que el primer fix no tocaba. Documentación real del
protocolo (`Limelight.h`, comentario sobre `SS_KBE_FLAG_NON_NORMALIZED`):
*"allows the client to inform the host that the keycode was not mapped
to a standard US English scancode and should be interpreted as-is"*.

En la práctica, para este cliente concreto, "interpretar tal cual"
significa: Moonlight para iOS, cuando no puede identificar limpiamente
una tecla de su propio teclado, manda el combo VK+modificador que
produciría el carácter deseado **en un teclado US** (aquí, Shift+2 para
`@`) con el flag puesto para avisar de que es una suposición de baja
confianza. Con `always_send_scancodes` desactivado (como lo tenía el
usuario), Apollo ya mandaba esto como evento VK simple — pero eso
**tampoco** arregla nada, porque Windows sigue resolviendo "VK_2 +
Shift" a través del layout activo del host (español), dando `"` en vez
de `@`. El problema no es scancode-vs-VK, es que el combo que llega ya
es intrínsecamente "US-shaped" y no hay forma de producir `@` con
Shift+2 en un teclado español (ahí `@` es AltGr+2).

### El segundo fix

Cuando la tecla no normalizada es de símbolo/dígito y el host no está
en layout US: se interpreta `modcode` como si fuera una pulsación en un
layout US real (cargado vía `LoadKeyboardLayoutW` sin cambiar el layout
visible del usuario), usando los modificadores que estén sujetos en
ese momento (`GetKeyState`, ya reflejan lo que el propio Apollo
sintetizó para cumplir lo que pedía el paquete del cliente). Eso
recupera el carácter que el cliente realmente quería (`@`). Ese
carácter se inyecta directamente como evento Unicode
(`KEYEVENTF_UNICODE`) en vez de reconstruir manualmente qué combo de
teclas produce `@` en el layout real del host — así no hace falta
pelear con los modificadores que el propio cliente/Apollo ya haya
pulsado (Shift, en este caso), que de otro modo podrían combinarse mal
con lo que hiciera falta inyectar (AltGr) si se intentase un enfoque de
"reescribir a la combinación correcta del host".

**Nivel de confianza**: alto en el diagnóstico (confirmado con el log
real del usuario), medio en la solución. El mecanismo de
"interpretar como si fuera US, luego reinyectar como Unicode" es sólido
en principio y encaja con la única evidencia real disponible, pero solo
se ha confirmado para **un cliente** (Moonlight iOS) y **un carácter**
(`@`/Shift+2). No hay garantía de que el cliente use consistentemente
"el combo US" como su representación de baja confianza para *todos*
los símbolos no identificables - es una inferencia a partir de un caso,
no un contrato documentado del protocolo.

## Qué NO se pudo validar aquí

Todo esto es código exclusivo de Windows
(`src/platform/windows/input.cpp`) y depende del layout de teclado
activo del host, así que nada de esto se pudo ejecutar en este entorno
de desarrollo Linux. Sin confirmar en Windows real:

1. Que `GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), ...))`
   realmente refleje el layout que ve el usuario en la práctica (debería,
   es la API estándar de Windows para esto, pero no se ha probado).
2. Que ningún juego probado por el usuario dependa de un scancode de
   símbolo/dígito para un bind - el riesgo se consideró bajo pero no se
   verificó activamente.
3. (Segundo fix) Que `resolve_char_via_us_layout()` produzca el
   carácter correcto para símbolos distintos de `@` (`"`, `/`, `¿`, `¡`)
   - solo se confirmó con logs reales el caso `@`/Shift+2. Si Moonlight
   iOS usa una lógica distinta para otros símbolos (p. ej. AltGr en vez
   de Shift para alguno), el fix podría no cubrir ese caso.
4. Que `ToUnicodeEx()` con un `HKL` distinto al activo no tenga efectos
   secundarios raros con el estado de "dead key" del hilo - no debería
   importar para teclas simples de dígito/OEM (ninguna es dead key en
   layout US), pero no se ha confirmado en Windows real.
5. Que la inyección Unicode (`KEYEVENTF_UNICODE`) funcione igual de bien
   que la inyección por VK/scancode dentro de un juego - debería, ya
   que solo se usa para teclas de símbolo/dígito no normalizadas en
   host no-US, un caso donde el VK/scancode ya estaba produciendo el
   carácter equivocado de todas formas.

## Cómo probar una vez compilado en Windows

1. Host en Windows con layout español (u otro no-US) activo, con
   `min_log_level` en Debug para poder ver las líneas
   `keyboard_update: ...` si algo no sale como se espera.
2. Conectar desde el cliente que reportó el problema (iPad/Moonlight
   iOS) y escribir en un campo de texto normal (no un juego) los
   símbolos que antes salían mal: `@`, `"`, `/`, `¿`, `¡`.
3. Confirmar que salen los caracteres correctos según el layout español.
4. Si tienes algún juego que usa una tecla de puntuación como bind,
   comprobar que sigue funcionando igual que antes.
