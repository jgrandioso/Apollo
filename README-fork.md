# Apollo — fork personal de Jorge

Este es un fork personal de [Apollo](https://github.com/ClassicOldSong/Apollo)
(a su vez, fork de [Sunshine](https://github.com/LizardByte/Sunshine)/[Moonlight](https://moonlight-stream.org)),
mantenido para añadir funcionalidad que no existe en el proyecto oficial.
Lo desarrollo yo solo, sin experiencia previa en C++, así que todo el
código está pensado para ser legible y mantenible por mí a largo plazo,
no compacto ni "clever".

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
opción que ffmpeg ya soportaba pero Apollo no exponía.

## Estado de cada feature

| Feature | Branch | Estado |
|---|---|---|
| Frame pacing | `feature/frame-pacing` | ✅ Compilado y validado (arranque idéntico al baseline con defaults) |
| Presupuesto de datos | `feature/bandwidth-budget` | ❌ Descartado — limitación real de protocolo, ver el análisis |
| HIDMaestro | `feature/hidmaestro-backend` | ⚠️ Experimental — Windows-only, no compilable en este entorno de desarrollo, rumble de gatillos sin confirmar |
| Bitrate adaptativo | `feature/adaptive-bitrate` | ✅ Compilado (solo NVENC), algoritmo validado de forma aislada, reconfiguración real sin probar (necesita GPU) |
| Calidad en movimiento (AMD) | `feature/amd-high-motion-quality-boost` | ⚠️ Solo la parte multiplataforma compilada — el mapeo real está en código Windows-only sin compilar aquí |

Ninguna está fusionada en `master` todavía. Cada branch parte del mismo
commit base (`db2e4199`, que ya incluye las correcciones al pipeline de
build de Linux — ver más abajo).

**Pospuesto, sin branch**: bitrate adaptativo real para AMD (parcheando
ffmpeg) — confirmado técnicamente viable, pero es un proyecto aparte con
su propio repo (`LizardByte/build-deps`). Ver
`docs/dev/amd-amf-adaptive-bitrate-future-plan.md`.

Detalle completo de cada una, incluyendo qué se investigó, qué se
descartó y por qué, y qué queda pendiente de validar: `docs/dev/`.
Resumen de cambios: `CHANGELOG.md`.

## Restricciones de plataforma importantes

- **SudoVDA** (display virtual) es Windows-only. No se ha tocado ni se ha
  intentado portar a Linux — está fuera de alcance por diseño.
- **HIDMaestro** también es Windows-only (SDK en C#/.NET 10).
- El **bitrate adaptativo** solo funciona con GPU NVIDIA (NVENC). AMD
  necesitaría un proyecto aparte — ver
  `docs/dev/amd-amf-adaptive-bitrate-future-plan.md`.
- Todo el desarrollo se hizo en un entorno de **Linux** (Debian 13,
  servidor de Jorge). El host de producción final es **Windows** — el
  código Windows-only (HIDMaestro) nunca se ha compilado de verdad, solo
  contra documentación y código fuente real de sus dependencias.

## Cómo compilar

### Linux (validado)

Usa el Dockerfile que Apollo ya trae, con los fixes de este fork ya
aplicados en `master`:

```bash
docker build -f docker/debian-trixie.dockerfile -t apollo-fork:local .
```

Detalle completo de por qué hacía falta arreglar el Dockerfile original y
qué se tocó exactamente: `docs/dev/building-linux.md`.

### Windows (sin validar en este entorno)

Sigue `docs/building.md` (la guía oficial de Apollo, sin cambios). Para
`feature/hidmaestro-backend`, además: `.NET 10 SDK` + Visual Studio 2022+,
y compilar con `-DSUNSHINE_ENABLE_HIDMAESTRO=ON`.

## Estructura de branches

```
master                        <- fixes de build de Linux, base de todos los demás
├── feature/frame-pacing
├── feature/bandwidth-budget  <- solo documentación, sin código
├── feature/hidmaestro-backend
├── feature/adaptive-bitrate
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
  "repository is disabled" — hasta revertirlo). Aloja las 6 branches
  (`master` + las 5 de feature) únicamente para poder compilarlas vía
  GitHub Actions (`.github/workflows/build-windows.yml`) sin instalar
  ningún toolchain en local — ver la sección de CI más abajo. El merge
  local a un `master` limpio sigue pendiente hasta validar cada feature
  en Windows real.

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
