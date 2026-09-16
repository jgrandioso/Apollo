# syntax=docker/dockerfile:1
# artifacts: true
# platforms: linux/amd64,linux/arm64/v8
# platforms_pr: linux/amd64
# no-cache-filters: sunshine-base,artifacts,sunshine
ARG BASE=debian
ARG TAG=trixie
FROM ${BASE}:${TAG} AS sunshine-base

ENV DEBIAN_FRONTEND=noninteractive

FROM sunshine-base AS sunshine-deps

SHELL ["/bin/bash", "-o", "pipefail", "-c"]

# Copy only the build script and necessary files first for better layer caching
WORKDIR /build/sunshine/
COPY --link scripts/linux_build.sh ./scripts/linux_build.sh
COPY --link packaging/linux/patches/ ./packaging/linux/patches/

# Install dependencies first - this layer will be cached
RUN <<_DEPS
#!/bin/bash
set -e
chmod +x ./scripts/linux_build.sh
./scripts/linux_build.sh \
  --step=deps \
  --cuda-patches \
  --sudo-off
apt-get clean
rm -rf /var/lib/apt/lists/*
_DEPS

FROM sunshine-deps AS sunshine-build

ARG BRANCH
ARG BUILD_VERSION
ARG COMMIT
# note: BUILD_VERSION may be blank

ENV BRANCH=${BRANCH}
ENV BUILD_VERSION=${BUILD_VERSION}
ENV COMMIT=${COMMIT}

# Now copy the full repository
COPY --link .. .

# WORKAROUND (local fork, not upstream Apollo): the second COPY above pulls
# scripts/linux_build.sh back in with its original 644 permissions from the
# host checkout, overwriting the chmod +x done in the sunshine-deps stage.
# Restore the execute bit here so the script can run.
RUN chmod +x ./scripts/linux_build.sh

# Configure, validate, build and package
RUN <<_BUILD
#!/bin/bash
set -e
./scripts/linux_build.sh \
  --step=cmake \
  --publisher-name='LizardByte' \
  --publisher-website='https://app.lizardbyte.dev' \
  --publisher-issue-url='https://app.lizardbyte.dev/support' \
  --sudo-off

./scripts/linux_build.sh \
  --step=validation \
  --sudo-off

./scripts/linux_build.sh \
  --step=build \
  --sudo-off

./scripts/linux_build.sh \
  --step=package \
  --sudo-off
_BUILD

# DISABLED (local fork, not upstream Apollo): this step tries to run
# ./test_sunshine, but BUILD_TESTS defaults to OFF in cmake/prep/options.cmake
# and scripts/linux_build.sh has no flag to turn it on, so the binary never
# gets built and this always fails with "No such file or directory".
# Tracked for later: enable -DBUILD_TESTS=ON explicitly when we add real
# tests for a feature (see docs/dev/*-analysis.md).
# WORKDIR /build/sunshine/build/tests
# RUN <<_TEST
# #!/bin/bash
# set -e
# export DISPLAY=:1
# Xvfb ${DISPLAY} -screen 0 1024x768x24 &
# ./test_sunshine --gtest_color=yes
# _TEST

FROM sunshine-base AS sunshine

ARG BASE
ARG TAG
ARG TARGETARCH

# WORKAROUND (local fork, not upstream Apollo): CPack now produces Apollo.deb
# (the fork renamed the package from Sunshine to Apollo), but this Dockerfile
# still referenced the old Sunshine.deb filename, so the COPY failed.

# artifacts to be extracted in CI
COPY --link --from=sunshine-build /build/sunshine/build/cpack_artifacts/Apollo.deb /artifacts/sunshine-${BASE}-${TAG}-${TARGETARCH}.deb

# copy deb from builder
COPY --link --from=sunshine-build /build/sunshine/build/cpack_artifacts/Apollo.deb /sunshine.deb

# install sunshine
RUN <<_INSTALL_SUNSHINE
#!/bin/bash
set -e
apt-get update -y
apt-get install -y --no-install-recommends /sunshine.deb
# WORKAROUND (local fork, not upstream Apollo): the binary dynamically links
# against libicuuc.so.76, but CPACK_DEBIAN_PACKAGE_SHLIBDEPS is explicitly
# OFF (cmake/packaging/linux.cmake:96-97, "doesn't work with the current
# config") and the hand-written CPACK_DEBIAN_PACKAGE_DEPENDS list never
# added libicu76, so apt never pulled it in as a dependency of the .deb.
# Confirmed with `ldd /usr/bin/sunshine` that this was the only missing lib.
apt-get install -y --no-install-recommends libicu76
apt-get clean
rm -rf /var/lib/apt/lists/*
_INSTALL_SUNSHINE

# network setup
EXPOSE 47984-47990/tcp
EXPOSE 48010
EXPOSE 47998-48000/udp

# setup user
ARG PGID=1000
ENV PGID=${PGID}
ARG PUID=1000
ENV PUID=${PUID}
ENV TZ="UTC"
ARG UNAME=lizard
ENV UNAME=${UNAME}

ENV HOME=/home/$UNAME

# setup user
RUN <<_SETUP_USER
#!/bin/bash
set -e
groupadd -f -g "${PGID}" "${UNAME}"
useradd -lm -d ${HOME} -s /bin/bash -g "${PGID}" -u "${PUID}" "${UNAME}"
mkdir -p ${HOME}/.config/sunshine
ln -s ${HOME}/.config/sunshine /config
chown -R ${UNAME} ${HOME}
_SETUP_USER

USER ${UNAME}
WORKDIR ${HOME}

# entrypoint
ENTRYPOINT ["/usr/bin/sunshine"]
