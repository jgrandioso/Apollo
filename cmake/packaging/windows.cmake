# windows specific packaging
install(TARGETS sunshine RUNTIME DESTINATION "." COMPONENT application)

# Hardening: include zlib1.dll (loaded via LoadLibrary() in openssl's libcrypto.a)
install(FILES "${ZLIB}" DESTINATION "." COMPONENT application)

# ViGEmBus installer
set(VIGEMBUS_INSTALLER "${CMAKE_BINARY_DIR}/vigembus_installer.exe")
file(DOWNLOAD
        "https://github.com/nefarius/ViGEmBus/releases/download/v1.21.442.0/ViGEmBus_1.21.442_x64_x86_arm64.exe"
        ${VIGEMBUS_INSTALLER}
        SHOW_PROGRESS
        EXPECTED_HASH SHA256=155c50f1eec07bdc28d2f61a3e3c2c6c132fee7328412de224695f89143316bc
        TIMEOUT 60
)
install(FILES ${VIGEMBUS_INSTALLER}
        DESTINATION "scripts"
        RENAME "vigembus_installer.exe"
        COMPONENT gamepad)

# Adding tools
install(TARGETS dxgi-info RUNTIME DESTINATION "tools" COMPONENT dxgi)
install(TARGETS audio-info RUNTIME DESTINATION "tools" COMPONENT audio)

# hidmaestro-bridge.exe (input_backend=hidmaestro) - only built/installed when
# SUNSHINE_ENABLE_HIDMAESTRO was passed to cmake; see cmake/targets/windows.cmake.
if(SUNSHINE_ENABLE_HIDMAESTRO)
    install(DIRECTORY "${HIDMAESTRO_BRIDGE_PUBLISH_DIR}/"
            DESTINATION "tools"
            COMPONENT hidmaestro)
endif()

# Mandatory tools
install(TARGETS sunshinesvc RUNTIME DESTINATION "tools" COMPONENT application)

# Drivers
#
# drivers/sudovda/ ships two binaries this fork added that were missing
# from both this repo's own history and upstream ClassicOldSong/Apollo:
# SudoVDA.dll (the actual UMDF2 driver install.bat's INF copies in) and
# nefconc.exe (the tool install.bat uses to create the device node and
# invoke the actual driver install). Without them, install.bat's
# device-node-creation step silently does nothing, and
# DiInstallDriverW fails with "file not found" - Apollo's Web UI then
# shows "Driver status: Uninitialized" forever. Widely reported upstream
# (ClassicOldSong/Apollo#1044, #1360, both titled exactly that), root
# cause never identified in either thread.
#
# Both files were extracted from the official Apollo v0.4.6 installer
# (published 2025-07-13, one day before this driver package's own
# DriverVer date) rather than rebuilt or fetched from a generic release,
# specifically because sudovda.cat cryptographically signs SudoVDA.dll
# by hash - any other build of it, even functionally identical, would
# fail signature verification. Confirmed sudovda.cat/sudovda.cer here
# are byte-identical to the ones inside that same installer before
# trusting its SudoVDA.dll/nefconc.exe as the right pairing.
install(DIRECTORY "${SUNSHINE_SOURCE_ASSETS_DIR}/windows/drivers/sudovda"
        DESTINATION "drivers"
        COMPONENT sudovda)

# Mandatory scripts
install(DIRECTORY "${SUNSHINE_SOURCE_ASSETS_DIR}/windows/misc/service/"
        DESTINATION "scripts"
        COMPONENT assets)
install(DIRECTORY "${SUNSHINE_SOURCE_ASSETS_DIR}/windows/misc/migration/"
        DESTINATION "scripts"
        COMPONENT assets)
install(DIRECTORY "${SUNSHINE_SOURCE_ASSETS_DIR}/windows/misc/path/"
        DESTINATION "scripts"
        COMPONENT assets)

# Configurable options for the service
install(DIRECTORY "${SUNSHINE_SOURCE_ASSETS_DIR}/windows/misc/autostart/"
        DESTINATION "scripts"
        COMPONENT autostart)

# scripts
install(DIRECTORY "${SUNSHINE_SOURCE_ASSETS_DIR}/windows/misc/firewall/"
        DESTINATION "scripts"
        COMPONENT firewall)
install(DIRECTORY "${SUNSHINE_SOURCE_ASSETS_DIR}/windows/misc/gamepad/"
        DESTINATION "scripts"
        COMPONENT gamepad)

# Sunshine assets
install(DIRECTORY "${SUNSHINE_SOURCE_ASSETS_DIR}/windows/assets/"
        DESTINATION "${SUNSHINE_ASSETS_DIR}"
        COMPONENT assets)

# copy assets (excluding shaders) to build directory, for running without install
file(COPY "${SUNSHINE_SOURCE_ASSETS_DIR}/windows/assets/"
        DESTINATION "${CMAKE_BINARY_DIR}/assets"
        PATTERN "shaders" EXCLUDE)
# use junction for shaders directory
cmake_path(CONVERT "${SUNSHINE_SOURCE_ASSETS_DIR}/windows/assets/shaders"
        TO_NATIVE_PATH_LIST shaders_in_build_src_native)
cmake_path(CONVERT "${CMAKE_BINARY_DIR}/assets/shaders" TO_NATIVE_PATH_LIST shaders_in_build_dest_native)
execute_process(COMMAND cmd.exe /c mklink /J "${shaders_in_build_dest_native}" "${shaders_in_build_src_native}")

set(CPACK_PACKAGE_ICON "${CMAKE_SOURCE_DIR}\\\\apollo.ico")

# The name of the directory that will be created in C:/Program files/
set(CPACK_PACKAGE_INSTALL_DIRECTORY "${CPACK_PACKAGE_NAME}")

# Setting components groups and dependencies
set(CPACK_COMPONENT_GROUP_CORE_EXPANDED true)

# sunshine binary
set(CPACK_COMPONENT_APPLICATION_DISPLAY_NAME "${CMAKE_PROJECT_NAME}")
set(CPACK_COMPONENT_APPLICATION_DESCRIPTION "${CMAKE_PROJECT_NAME} main application and required components.")
set(CPACK_COMPONENT_APPLICATION_GROUP "Core")
set(CPACK_COMPONENT_APPLICATION_REQUIRED true)
set(CPACK_COMPONENT_APPLICATION_DEPENDS assets)

# service auto-start script
set(CPACK_COMPONENT_AUTOSTART_DISPLAY_NAME "Launch on Startup")
set(CPACK_COMPONENT_AUTOSTART_DESCRIPTION "If enabled, launches Apollo automatically on system startup.")
set(CPACK_COMPONENT_AUTOSTART_GROUP "Core")

# assets
set(CPACK_COMPONENT_ASSETS_DISPLAY_NAME "Required Assets")
set(CPACK_COMPONENT_ASSETS_DESCRIPTION "Shaders, default box art, and web UI.")
set(CPACK_COMPONENT_ASSETS_GROUP "Core")
set(CPACK_COMPONENT_ASSETS_REQUIRED true)

# drivers
set(CPACK_COMPONENT_SUDOVDA_DISPLAY_NAME "SudoVDA")
set(CPACK_COMPONENT_SUDOVDA_DESCRIPTION "Driver required for Virtual Display to function.")
set(CPACK_COMPONENT_SUDOVDA_GROUP "Drivers")
set(CPACK_COMPONENT_SUDOVDA_REQUIRED true)

# audio tool
set(CPACK_COMPONENT_AUDIO_DISPLAY_NAME "audio-info")
set(CPACK_COMPONENT_AUDIO_DESCRIPTION "CLI tool providing information about sound devices.")
set(CPACK_COMPONENT_AUDIO_GROUP "Tools")

# display tool
set(CPACK_COMPONENT_DXGI_DISPLAY_NAME "dxgi-info")
set(CPACK_COMPONENT_DXGI_DESCRIPTION "CLI tool providing information about graphics cards and displays.")
set(CPACK_COMPONENT_DXGI_GROUP "Tools")

# firewall scripts
set(CPACK_COMPONENT_FIREWALL_DISPLAY_NAME "Add Firewall Exclusions")
set(CPACK_COMPONENT_FIREWALL_DESCRIPTION "Scripts to enable or disable firewall rules.")
set(CPACK_COMPONENT_FIREWALL_GROUP "Scripts")

# gamepad scripts
set(CPACK_COMPONENT_GAMEPAD_DISPLAY_NAME "Virtual Gamepad")
set(CPACK_COMPONENT_GAMEPAD_DESCRIPTION "Scripts to install and uninstall Virtual Gamepad.")
set(CPACK_COMPONENT_GAMEPAD_GROUP "Scripts")

# include specific packaging
include(${CMAKE_MODULE_PATH}/packaging/windows_nsis.cmake)
include(${CMAKE_MODULE_PATH}/packaging/windows_wix.cmake)
