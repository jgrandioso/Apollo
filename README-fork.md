# Apollo — fork personal de Jorge

Este es un fork personal de [Apollo](https://github.com/ClassicOldSong/Apollo)
(a su vez, fork de [Sunshine](https://github.com/LizardByte/Sunshine)/[Moonlight](https://moonlight-stream.org)),
mantenido para añadir funcionalidad que no existe en el proyecto oficial.
Lo desarrollo yo solo, sin experiencia previa en C++, así que todo el
código está pensado para ser legible y mantenible por mí a largo plazo,
no compacto ni "clever".

**Nota para quien encuentre esto en el futuro**: todo el código, la
investigación y la documentación de este fork se hicieron con
[Claude Code](https://claude.com/claude-code) (Claude, de Anthropic),
bajo mi supervisión directa en cada paso — nada se comiteó ni se subió
sin que yo lo revisara antes. Si vas a reutilizar o adaptar algo de aquí,
ten en cuenta que el nivel de verificación varía por feature: algunas
están confirmadas en hardware Windows real, otras solo compiladas en CI
sin ejecutar — el estado real de cada una está en la tabla de abajo y en
`docs/dev/`, sin maquillar.

## Bugs reales de Apollo (no de este fork) encontrados en el camino

Investigando por qué algunas cosas no funcionaban, aparecieron varios
bugs genuinos del propio Apollo — no específicos de ninguna feature de
este fork, afectarían a cualquiera compilando desde el código fuente
público tal cual está hoy:

- **SudoVDA nunca funciona en una build compilada desde cero** —
  `install.bat` depende de dos binarios (`nefconc.exe`, `SudoVDA.dll`)
  que no están en el repo público ni se descargan en ningún sitio.
  Reportado sin resolver en
  [#1044](https://github.com/ClassicOldSong/Apollo/issues/1044) y
  [#1360](https://github.com/ClassicOldSong/Apollo/issues/1360)
  ("SudoVDA Driver status: Uninitialized").
- **AMD AMF se cuelga para siempre al arrancar** — desajuste de versión
  entre el FFmpeg realmente usado y código escrito para una versión más
  nueva. Ver [#1588](https://github.com/ClassicOldSong/Apollo/issues/1588).
- **Perfil H.264 de AMD AMF roto** — un campo que `video::config_t`
  nunca ha tenido.

Los tres están arreglados en `master` de este fork (ver `CHANGELOG.md`
para el detalle técnico de cada uno). Podrían proponerse como PR al
repo original — de momento se quedan aquí. El proyecto original lleva
más de un año sin publicar una release nueva (última: `v0.4.7-alpha.1`,
12/08/2025) y meses sin commits, lo que probablemente explica por qué
nadie los ha detectado/arreglado todavía.

## Por qué existe este fork

Cuatro cosas que quería y que Apollo/Sunshine no ofrecían:

1. **Frame pacing configurable** — afinar cómo se suaviza la cadencia de
   frames entre captura y codificación, y cómo se reparte su envío por
   red.
2. **Presupuesto de datos por sesión** — investigado y descartado (ver
   abajo, es responsabilidad del cliente, no del host).
3. **HIDMaestro como backend de mando alternativo** — para tener rumble
   de gatillos en Xbox Series, que ViGEmBus no puede ofrecer (solo emula
   un Xbox 360 clásico).
4. **Bitrate adaptativo real** — que reaccione a la pérdida de paquetes
   de la red durante el stream, no solo el "Warp Mode" que ya trae Apollo
   (que solo corrige una vez, al arrancar, por descuadre de framerate).

Más una quinta, añadida sobre la marcha al investigar la anterior para
AMD: **mejora de calidad en movimiento rápido para AMD (AMF)**, una
opción que ffmpeg ya soportaba pero Apollo no exponía. Y una sexta,
retomando algo que se había pospuesto por recursos: **bitrate adaptativo
también para AMD AMF**, vía un parche de ffmpeg mantenido en un fork
aparte.

## Estado de cada feature

| Feature | Branch | Estado |
|---|---|---|
| Frame pacing | `feature/frame-pacing` | ✅ Compilado y validado (arranque idéntico al baseline con defaults) |
| Presupuesto de datos | `feature/bandwidth-budget` | ❌ Descartado — limitación real de protocolo, ver el análisis |
| HIDMaestro | `feature/hidmaestro-backend` | ⚠️ Compila en CI real (con el flag `SUNSHINE_ENABLE_HIDMAESTRO=ON`), pero sin ejecutar todavía — rumble de gatillos sin confirmar |
| Bitrate adaptativo (NVENC) | `feature/adaptive-bitrate` | ✅ Compilado en CI real, algoritmo validado de forma aislada, reconfiguración real sin probar (necesita GPU NVIDIA) |
| Calidad en movimiento (AMD) | `feature/amd-high-motion-quality-boost` | ✅ Compila en CI real. Bug de arranque de AMD AMF (cuelgue al cerrar sesión de prueba) encontrado y arreglado en `master` en el camino |
| Bitrate adaptativo (AMD AMF) | `feature/amd-amf-adaptive-bitrate` | ✅ Compila en CI real, con parche de ffmpeg propio ([`jgrandioso/build-deps`](https://github.com/jgrandioso/build-deps)). Ejecución real sobre GPU AMD sin confirmar |

Ninguna está fusionada en `master` todavía. Cada branch parte del mismo
commit base (`db2e4199`, que ya incluye las correcciones al pipeline de
build de Linux — ver más abajo).

Detalle completo de cada una, incluyendo qué se investigó, qué se
descartó y por qué, y qué queda pendiente de validar: `docs/dev/`.
Resumen de cambios: `CHANGELOG.md`.

## Restricciones de plataforma importantes

- **SudoVDA** (display virtual) es Windows-only. No se ha tocado ni se ha
  intentado portar a Linux — está fuera de alcance por diseño.
- **HIDMaestro** también es Windows-only (SDK en C#/.NET 10).
- El **bitrate adaptativo** tiene dos implementaciones separadas: NVENC
  (`feature/adaptive-bitrate`) y AMD AMF (`feature/amd-amf-adaptive-bitrate`,
  necesita el fork parcheado de ffmpeg). No hay una versión unificada que
  cubra ambos a la vez en la misma branch.
- Todo el desarrollo se hizo en un entorno de **Linux** (Debian 13,
  servidor de Jorge). El host de producción final es **Windows** — gracias
  a la CI en GitHub Actions, varias branches ya se han compilado de
  verdad en Windows real (ver la tabla de arriba), pero **ninguna se ha
  ejecutado todavía** en una máquina Windows real fuera de esa CI.

## Cómo compilar

### Linux (validado)

Usa el Dockerfile que Apollo ya trae, con los fixes de este fork ya
aplicados en `master`:

```bash
docker build -f docker/debian-trixie.dockerfile -t apollo-fork:local .
```

Detalle completo de por qué hacía falta arreglar el Dockerfile original y
qué se tocó exactamente: `docs/dev/building-linux.md`.

### Windows (vía CI, sin instalar nada en local)

Cada branch tiene su propio workflow (`.github/workflows/build-windows.yml`,
disparo manual desde la pestaña Actions de GitHub) que compila en un
runner Windows real y deja el instalador como artifact descargable — así
se evita instalar MSYS2/toolchain en un PC de uso personal. Para
`feature/hidmaestro-backend` compila con `-DSUNSHINE_ENABLE_HIDMAESTRO=ON`
(.NET 10 SDK, instalado automáticamente en el workflow); para
`feature/amd-amf-adaptive-bitrate`, descarga el ffmpeg parcheado desde
`jgrandioso/build-deps` en vez de usar el submódulo normal — ver
`docs/dev/amd-amf-adaptive-bitrate.md`.

También sigue funcionando la vía manual estándar de Apollo
(`docs/building.md`, sin cambios) si prefieres compilar en local.

## Estructura de branches

```
master                        <- fixes de build de Linux, base de todos los demás
├── feature/frame-pacing
├── feature/bandwidth-budget           <- solo documentación, sin código
├── feature/hidmaestro-backend
├── feature/adaptive-bitrate           <- bitrate adaptativo, solo NVENC
│   └── feature/amd-amf-adaptive-bitrate  <- mismo mecanismo, para AMD AMF
└── feature/amd-high-motion-quality-boost
```

## Dónde vive el código

- **Remoto de trabajo** (`origin`): repo bare local en
  `/srv/apollo-fork/repo.git` (mismo patrón que el proyecto portfolio —
  sin depender de GitHub para nada). Clonar desde otro PC de la
  red/VPN: `git clone <usuario>@<host-vpn>:/srv/apollo-fork/repo.git`
  (usuario y host son los tuyos, no se publican aquí).
- **`upstream`**: `https://github.com/ClassicOldSong/Apollo.git`, para
  traer cambios del original.
- **`github.com/jgrandioso/Apollo`**: fork real, **público** (un intento
  de ponerlo en privado justo tras crearlo lo dejó bloqueado —
  "repository is disabled" — hasta revertirlo). Aloja las 7 branches
  (`master` + las 6 de feature) únicamente para poder compilarlas vía
  GitHub Actions sin instalar ningún toolchain en local. El merge local a
  un `master` limpio sigue pendiente hasta validar cada feature en
  Windows real.
- **`github.com/jgrandioso/build-deps`**: fork aparte de
  `LizardByte/build-deps` (de donde sale el ffmpeg precompilado), solo
  para `feature/amd-amf-adaptive-bitrate` — lleva el parche de bitrate
  dinámico de AMF y su propia CI (matriz recortada a Windows-AMD64). No
  hace falta clonarlo para nada normal del día a día, solo si hay que
  tocar ese parche.

## Documentación de desarrollo

Todo en `docs/dev/`, un par de archivos por feature:

- `<feature>-analysis.md` — investigación previa: qué hace Apollo hoy en
  esa área, qué se tocaría, riesgos, preguntas de alcance.
- `<feature>.md` — cierre: qué se añadió, diff conceptual
  modificado/nuevo, impacto en compatibilidad con futuros merges desde
  upstream, qué queda por validar, cómo activarlo.

Léelos antes de tocar cualquier feature — varias veces durante el
desarrollo una investigación superficial llevó a conclusiones
equivocadas que solo se corrigieron al volver a las fuentes reales
(código fuente de las dependencias, no resúmenes de documentación). Esa
historia queda registrada en los propios documentos, no oculta.
