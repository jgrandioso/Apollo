# Sincronizar la Web UI con la de Sunshine (Fase 1 — análisis)

Branch: `feature/webui-sync`, creada desde `master` (commit base:
`1.0.0` con virtual-display-duplicate y las demás features ya
mergeadas). Este documento es solo análisis — no hay código todavía.

## Por qué no es un simple `git merge`/`cherry-pick`

Verificado en una sesión anterior: el historial de git de
`ClassicOldSong/Apollo` fue aplastado en algún punto (el commit más
antiguo alcanzable es literalmente "Removed Git history due to personal
info"), así que no comparte ningún ancestro común con
`LizardByte/Sunshine`. No hay manera de traer los commits de Sunshine
sobre nuestra base con las herramientas normales de git — cualquier
sincronización tiene que ser manual, comparando el código fuente
directamente.

## Estado actual: nuestra Web UI vs. la de Sunshine (verificado hoy, 2026-09-20)

**La nuestra** (heredada de Apollo, sin cambios de arquitectura):
multi-página clásica. `src_assets/common/assets/web/` tiene 9 ficheros
`.html` independientes (`index.html`, `apps.html`, `config.html`,
`login.html`, `password.html`, `pin.html`, `troubleshooting.html`,
`welcome.html`, `template_header.html`), cada uno arrancando su propia
instancia de Vue por separado. Componentes compartidos sueltos:
`Checkbox.vue`, `ClientCard.vue`, `Navbar.vue`, `PlatformLayout.vue`,
`ResourceCard.vue`, `ThemeToggle.vue`.

**La de Sunshine actual**: reescrita como SPA de verdad. Un único
`index.html`, con `App.vue` + `router.js` + `main.js` (el patrón
estándar de Vue Router) y páginas como componentes:
`Home.vue`, `Config.vue`, `Apps.vue`, `Featured.vue`, `Welcome.vue`,
`Troubleshooting.vue`, `Password.vue`, `Logout.vue`, `Pin.vue`,
`NavbarSimple.vue`, `Notification.vue`, `SimpleIcon.vue`.

**Dependencias** (`package.json`): las dos usan Vite + Vue 3, así que el
tooling base no ha cambiado tanto. Pero Sunshine añadió dependencias
que nosotros no tenemos: `@lizardbyte/shared-web` (un paquete propio de
LizardByte con infraestructura de UI/i18n compartida entre sus
proyectos — no sabemos todavía qué asume sobre branding/config),
`@lucide/vue`, `bootstrap` como dependencia explícita, `date-fns`,
`marked`, y test suite con `vitest`.

## Lo que complica adoptar la SPA nueva tal cual

Este fork tiene bastantes cosas propias en la Web UI actual que no
existen en Sunshine y habría que re-portar a mano sobre la nueva
arquitectura, no simplemente copiar sus ficheros encima:

- El checkbox nuevo `virtual_display_duplicate_primary` y su exclusión
  mutua con `isolated_virtual_display_option` (`AudioVideo.vue`).
- El indicador de estado del driver SudoVDA en la propia pestaña
  Audio/Video.
- Configuración de HIDMaestro.
- Toggles de bandwidth-budget, frame-pacing, y las ramas sin mergear de
  adaptive-bitrate (NVENC/AMD AMF) cuando se validen.
- `apollo_version.js` y el check de actualizaciones que ya reescribimos
  para apuntar a `jgrandioso/Apollo` en vez de a Apollo o Sunshine.
- Todo el branding "Apollo" (nombres, textos, quizás iconografía) frente
  al de "Sunshine" que trae la SPA nueva y el paquete
  `@lizardbyte/shared-web`.

Y, a la inversa, no hay forma de saber sin mirar más a fondo cuánta
lógica de negocio (validaciones, llamadas a la API REST del backend)
cambió de forma incompatible con nuestros endpoints C++ actuales
(`src/nvhttp.cpp`, `src/confighttp.cpp` etc.) — la SPA nueva de Sunshine
asume su propio backend actualizado, que también ha evolucionado desde
que Apollo se separó.

## Opciones

1. **Adoptar la SPA de Sunshine entera y re-portar todas nuestras
   features encima.** Moderniza toda la base y trae gratis las mejoras
   de UX que haya hecho Sunshine desde entonces (routing real,
   notificaciones, etc.). Es el proyecto más grande de los tres: tocar
   prácticamente cada página de la Web UI, verificar que el backend C++
   sigue respondiendo lo que la SPA nueva espera, y volver a
   implementar cada feature propia de este fork una por una.

2. **Dejar la Web UI como está** (multi-HTML) y no tocar nada salvo
   que aparezca una necesidad puntual. Riesgo mínimo, pero nos quedamos
   sin las mejoras de UX que Sunshine ya hizo y la brecha solo crece.

3. **Camino intermedio**: evaluar componente por componente qué aporta
   valor real (p. ej. `Notification.vue` si sustituye a algo peor que
   tengamos, o el sistema de routing si de verdad simplifica algo) y
   portar solo esas piezas puntuales a nuestra arquitectura actual, sin
   migrar todo de golpe.

## Recomendación

Empezar por la opción 3: antes de comprometernos a reescribir toda la
Web UI (opción 1, que es un proyecto grande con riesgo real de romper
features propias de este fork), vale la pena mirar más de cerca qué
mejoras concretas trae la SPA nueva de Sunshine que de verdad
mejorarían la experiencia aquí, y decidir con eso en la mano. No he
tocado ningún fichero de código todavía — falta tu decisión sobre por
dónde tirar antes de implementar nada.
