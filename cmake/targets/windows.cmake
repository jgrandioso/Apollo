# windows specific target definitions
set_target_properties(sunshine PROPERTIES LINK_SEARCH_START_STATIC 1)
set(CMAKE_FIND_LIBRARY_SUFFIXES ".dll")
find_library(ZLIB ZLIB1)
list(APPEND SUNSHINE_EXTERNAL_LIBRARIES
        $<TARGET_OBJECTS:sunshine_rc_object>
        Windowsapp.lib
        Wtsapi32.lib)

# HIDMaestro input backend (Apollo extension) - opt-in, OFF by default.
#
# input_backend defaults to "vigem" (see src/config.cpp), so most builds
# don't need this at all. Building it requires the .NET 10 SDK and downloads
# ~115MB from HIDMaestro's GitHub releases - both real enough costs that this
# shouldn't happen silently on every Windows build. Enable explicitly with:
#   cmake -B build -G Ninja -S . -DSUNSHINE_ENABLE_HIDMAESTRO=ON
option(SUNSHINE_ENABLE_HIDMAESTRO "Build tools/hidmaestro-bridge.exe (needed for input_backend=hidmaestro)" OFF)

if(SUNSHINE_ENABLE_HIDMAESTRO)
    find_program(DOTNET_EXECUTABLE dotnet)
    if(NOT DOTNET_EXECUTABLE)
        message(FATAL_ERROR "SUNSHINE_ENABLE_HIDMAESTRO is ON but the `dotnet` CLI (.NET 10 SDK) wasn't found on PATH.")
    endif()

    # HIDMaestro (https://github.com/hifihedgehog/HIDMaestro) has no NuGet
    # feed as of this writing - its docs say to reference HIDMaestro.Core.dll
    # directly from a release ZIP. The exact path of that DLL inside the ZIP
    # was NOT verified before writing this (see
    # docs/dev/hidmaestro-backend-analysis.md), so it's located by filename
    # after extraction rather than assumed.
    set(HIDMAESTRO_RELEASE_ZIP "${CMAKE_BINARY_DIR}/hidmaestro-release.zip")
    set(HIDMAESTRO_EXTRACT_DIR "${CMAKE_BINARY_DIR}/hidmaestro-release")

    if(NOT EXISTS "${HIDMAESTRO_RELEASE_ZIP}")
        # TODO: pin EXPECTED_HASH once a known-good download's SHA256 has
        # been recorded - left unpinned for now rather than guessed.
        file(DOWNLOAD
                "https://github.com/hifihedgehog/HIDMaestro/releases/download/v1.8.0/HIDMaestro-v1.8.0.zip"
                "${HIDMAESTRO_RELEASE_ZIP}"
                SHOW_PROGRESS
                TIMEOUT 300
        )
    endif()

    if(NOT EXISTS "${HIDMAESTRO_EXTRACT_DIR}")
        file(MAKE_DIRECTORY "${HIDMAESTRO_EXTRACT_DIR}")
        file(ARCHIVE_EXTRACT
                INPUT "${HIDMAESTRO_RELEASE_ZIP}"
                DESTINATION "${HIDMAESTRO_EXTRACT_DIR}"
        )
    endif()

    file(GLOB_RECURSE HIDMAESTRO_CORE_DLL_CANDIDATES "${HIDMAESTRO_EXTRACT_DIR}/*HIDMaestro.Core.dll")
    list(LENGTH HIDMAESTRO_CORE_DLL_CANDIDATES HIDMAESTRO_CORE_DLL_COUNT)
    if(HIDMAESTRO_CORE_DLL_COUNT EQUAL 0)
        message(FATAL_ERROR "Downloaded HIDMaestro release but couldn't find HIDMaestro.Core.dll anywhere inside it - the release layout may have changed since this was written.")
    endif()
    list(GET HIDMAESTRO_CORE_DLL_CANDIDATES 0 HIDMAESTRO_CORE_DLL)

    set(HIDMAESTRO_BRIDGE_SRC_DIR "${CMAKE_SOURCE_DIR}/tools/hidmaestro-bridge")
    set(HIDMAESTRO_BRIDGE_PUBLISH_DIR "${CMAKE_BINARY_DIR}/hidmaestro-bridge-publish")
    set(HIDMAESTRO_BRIDGE_EXE "${HIDMAESTRO_BRIDGE_PUBLISH_DIR}/hidmaestro-bridge.exe")

    add_custom_command(
            OUTPUT "${HIDMAESTRO_BRIDGE_EXE}"
            COMMAND "${DOTNET_EXECUTABLE}" publish
                    "${HIDMAESTRO_BRIDGE_SRC_DIR}/hidmaestro-bridge.csproj"
                    -c Release
                    -r win-x64
                    --self-contained true
                    -p:HIDMaestroCoreDllPath=${HIDMAESTRO_CORE_DLL}
                    -o "${HIDMAESTRO_BRIDGE_PUBLISH_DIR}"
            DEPENDS "${HIDMAESTRO_BRIDGE_SRC_DIR}/hidmaestro-bridge.csproj" "${HIDMAESTRO_BRIDGE_SRC_DIR}/Program.cs" "${HIDMAESTRO_CORE_DLL}"
            COMMENT "Publishing hidmaestro-bridge.exe (.NET)"
            VERBATIM
    )
    add_custom_target(hidmaestro_bridge ALL DEPENDS "${HIDMAESTRO_BRIDGE_EXE}")
    add_dependencies(sunshine hidmaestro_bridge)
endif()
