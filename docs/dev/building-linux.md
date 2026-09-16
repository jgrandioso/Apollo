# Compilar el baseline de Apollo en Linux (entorno de desarrollo)

Este documento cubre la Fase 0 del fork: cómo se compiló el Apollo original
(sin modificar) en este entorno de desarrollo Linux (Debian 13 "trixie"), y
los problemas reales que aparecieron por el camino. El objetivo de esta fase
era únicamente confirmar que el proyecto base compila y arranca, **antes**
de tocar ninguna línea de código para las features nuevas.

> Nota de plataforma: el host de producción final de este fork es **Windows**.
> Todo lo de aquí es para poder desarrollar y validar cambios de lógica
> multiplataforma en Linux; SudoVDA (display virtual) es Windows-only y no
> se toca ni se intenta portar en este entorno — cualquier feature que
> dependa de él queda marcada como "requiere validación en host Windows".

## Entorno

- Host: Debian 13 (trixie), 4 núcleos, 7.7 GB RAM, sin GPU dedicada.
- Sin acceso a mando físico ni GPU en este entorno — el streaming real de
  Apollo **no se puede validar aquí**, solo que el binario arranca y no
  crashea.
- El host es un servidor de producción con otros servicios corriendo
  (Immich, Jellyfin, arr stack, etc.), así que **no se instaló ninguna
  dependencia de compilación en el sistema base**. Todo el build ocurre
  dentro de contenedores Docker aislados, usando el Dockerfile que el propio
  Apollo trae en `docker/debian-trixie.dockerfile`.

## Cómo se compiló

```bash
git clone https://github.com/ClassicOldSong/Apollo.git apollo-fork
cd apollo-fork
git submodule update --init --recursive

# build (compila, empaqueta en Apollo.deb) — target intermedio, no instala nada
docker build --target sunshine-build -f docker/debian-trixie.dockerfile -t apollo-fork:baseline-build .

# imagen final runnable (instala el .deb generado arriba)
docker build -f docker/debian-trixie.dockerfile -t apollo-fork:baseline .

# arrancar y comprobar que no crashea
docker run -d --name apollo-baseline-test apollo-fork:baseline
docker logs apollo-baseline-test
```

El build completo (con caché fría, incluyendo la descarga de ~5.5 GB del
CUDA Toolkit) tardó del orden de **30-45 minutos** en este hardware. Con
caché de Docker reutilizada, los reintentos tras cada fix tardaron segundos
o pocos minutos.

## Bugs encontrados en el pipeline de build/packaging oficial

Importante: **el código C++ de la aplicación compiló sin ni un solo error**.
Los cuatro problemas de abajo están todos en la capa de packaging/Docker
para Linux, no en la lógica de Apollo. Tiene sentido: Apollo es un fork
centrado en features de Windows, este fork no tiene `.github/workflows/`
(o sea, nadie corre este Dockerfile en CI automáticamente), así que esta
ruta de Linux acumuló bugs sin que nadie los notara.

Todos los fixes están aplicados **solo en nuestra copia local de
`docker/debian-trixie.dockerfile`**, marcados con comentarios `WORKAROUND`.
No se tocó ninguna línea de la aplicación (`src/`, `CMakeLists.txt`, etc.)
excepto donde se indica explícitamente.

### 1. `Permission denied` al ejecutar `scripts/linux_build.sh`

- **Causa:** el script está commiteado en git con permisos `644` (no
  ejecutable) — confirmado con `git ls-files -s scripts/linux_build.sh` →
  `100644`. La fase `sunshine-deps` del Dockerfile hace `chmod +x` sobre su
  copia del script, pero la fase `sunshine-build` vuelve a copiar **todo el
  repo desde el host** (`COPY --link .. .`), pisando ese `chmod +x` con los
  permisos originales del checkout.
- **Fix:** se añadió `RUN chmod +x ./scripts/linux_build.sh` justo después
  de ese segundo `COPY`, antes de intentar ejecutar el script.

### 2. El paso de tests (`_TEST`) falla: `./test_sunshine: No such file or directory`

- **Causa:** `option(BUILD_TESTS "Build tests" OFF)` en
  `cmake/prep/options.cmake` — los tests están desactivados por defecto, y
  `scripts/linux_build.sh` **no tiene ningún flag** para activarlos (se
  revisó el script completo, cero menciones a "tests"). El Dockerfile
  intenta ejecutar el binario de tests igualmente, y falla porque nunca se
  compiló.
- **Fix:** el bloque `WORKDIR .../tests` + `RUN <<_TEST` se dejó comentado
  en el Dockerfile local, con una nota explicando por qué. **Pendiente para
  más adelante:** cuando en la Fase 2 toque escribir tests reales para
  alguna feature, habrá que pasar `-DBUILD_TESTS=ON` explícitamente a mano
  (el script no lo hace por nosotros).

### 3. `COPY` busca `Sunshine.deb`, pero CPack genera `Apollo.deb`

- **Causa:** el fork renombró el paquete generado de "Sunshine" a "Apollo"
  (visible en el log de build: `CPack: - package: .../Apollo.deb
  generated`), pero `docker/debian-trixie.dockerfile` seguía referenciando
  el nombre antiguo `Sunshine.deb` en dos `COPY --link --from=sunshine-build`.
- **Fix:** ambas referencias corregidas a `Apollo.deb`.

### 4. Falta `libicuuc.so.76` en tiempo de ejecución

- **Causa:** el binario enlaza dinámicamente contra ICU (vía
  `libboost-locale`), pero `CPACK_DEBIAN_PACKAGE_SHLIBDEPS` está
  **desactivado a propósito** en `cmake/packaging/linux.cmake:97`, con un
  comentario del propio proyecto: *"This should automatically figure out
  dependencies, doesn't work with the current config"*. La lista manual de
  dependencias (`CPACK_DEBIAN_PACKAGE_DEPENDS`) nunca incluyó `libicu76`,
  así que `apt-get install /sunshine.deb` no lo traía.
- **Cómo se confirmó:** `ldd /usr/bin/sunshine | grep "not found"` dentro de
  un contenedor con el `.deb` ya instalado — solo salió esa librería, nada
  más. Se validó el fix en un contenedor temporal (`apt-get install
  libicu76` a mano + reejecución del binario) antes de tocar el Dockerfile,
  para no gastar otro build completo a ciegas.
- **Fix:** se añadió `apt-get install -y --no-install-recommends libicu76`
  en el paso de instalación del `.deb`, en el stage final `sunshine`.

### Aviso menor sin fix (no bloquea el arranque)

Durante el `postinst` del `.deb` aparecen estos dos avisos, no fatales:

```
error: setcap not found or not executable.
error: udevadm not found or not executable.
```

La imagen runtime (`debian:trixie` pelado) no trae `libcap2-bin` ni `udev`.
`setcap` es lo que le da al binario permiso para crear dispositivos de
input virtuales sin ser root; sin él, el binario arranca igual pero con
input limitado. No se ha arreglado porque no bloquea el objetivo de esta
fase (confirmar que arranca) y no es relevante para el host de producción
final (Windows). Si más adelante necesitamos probar el backend de input en
Linux de verdad, habrá que instalar `libcap2-bin`/`udev` y aplicar `setcap`
manualmente en la imagen de prueba.

## Resultado de la validación de arranque

Con los 4 fixes aplicados, `docker run` con el `ENTRYPOINT` real
(`/usr/bin/sunshine`, sin overrides) deja el contenedor en estado `Up`,
sin crashear, y expone la Web UI en `https://localhost:47990` dentro del
contenedor. Se verificó manualmente accediendo desde fuera vía
`https://10.7.0.1:47990` (puerto publicado solo en la interfaz WireGuard del
servidor, no en 0.0.0.0 ni en la LAN, siguiendo el patrón de este servidor
de exponer servicios solo por VPN).

Comportamiento esperado sin GPU/mando en este entorno (no son fallos):

```
Cannot load libcuda.so.1
Error: Couldn't load cuda: -1
Warning: Couldn't find /dev/dri, kmsgrab won't be enabled
Error: Unable to initialize capture method
[...]
Fatal: Unable to find display or encoder during startup.
```

Estos errores desaparecerán en un host real con GPU. Lo que sí confirma que
el baseline funciona es que, pese a esos fallos de captura, el proceso
**sigue vivo y levanta el main loop y la Web UI** — no aborta el programa.

## Qué queda sin validar aquí

- Streaming real (captura de vídeo, encoders por hardware, mandos físicos):
  requiere GPU + Moonlight cliente real, no disponible en este entorno.
- SudoVDA: Windows-only, fuera de alcance de este documento por completo.
- El backend de input real (crear dispositivos `/dev/uinput`,
  `setcap`/`udev`): no se validó, ver aviso menor arriba.
