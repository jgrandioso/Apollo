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

## Qué NO se pudo validar aquí

Todo esto es código exclusivo de Windows
(`src/platform/windows/input.cpp`) y depende del layout de teclado
activo del host, así que nada de esto se pudo ejecutar en este entorno
de desarrollo Linux. Sin confirmar en Windows real:

1. Que `GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), ...))`
   realmente refleje el layout que ve el usuario en la práctica (debería,
   es la API estándar de Windows para esto, pero no se ha probado).
2. Que el fix realmente arregle `@`, `"`, `/`, `¿`, `¡` conectando desde
   el iPad reportado por el usuario - la causa raíz se dedujo leyendo el
   código y contrastando con los síntomas descritos, no se reprodujo el
   bug en un entorno real.
3. Que ningún juego probado por el usuario dependa de un scancode de
   símbolo/dígito para un bind - el riesgo se consideró bajo pero no se
   verificó activamente.

## Cómo probar una vez compilado en Windows

1. Host en Windows con layout español (u otro no-US) activo.
2. Conectar desde el cliente que reportó el problema (iPad/Moonlight
   iOS) y escribir en un campo de texto normal (no un juego) los
   símbolos que antes salían mal: `@`, `"`, `/`, `¿`, `¡`.
3. Confirmar que salen los caracteres correctos según el layout español.
4. Si tienes algún juego que usa una tecla de puntuación como bind,
   comprobar que sigue funcionando igual que antes.
