# libhrquickjs: vendored QuickJS (src/main/cpp/quickjs, unmodified) + the JNI shim.
# Built by the NDK for the APK (externalNativeBuild) and for the development machine, so the
# :host parity suite runs on the JVM against the same engine (specs/007-android-app research R2).
cmake_minimum_required(VERSION 3.22)
project(hrquickjs C)

set(CMAKE_C_STANDARD 11)
file(READ "${CMAKE_CURRENT_LIST_DIR}/src/main/cpp/quickjs/VERSION" QJS_VERSION)
string(STRIP "${QJS_VERSION}" QJS_VERSION)

set(QJS_DIR "${CMAKE_CURRENT_LIST_DIR}/src/main/cpp/quickjs")
add_library(hrquickjs SHARED
    ${QJS_DIR}/quickjs.c
    ${QJS_DIR}/libregexp.c
    ${QJS_DIR}/libunicode.c
    ${QJS_DIR}/cutils.c
    ${QJS_DIR}/dtoa.c
    ${CMAKE_CURRENT_LIST_DIR}/src/main/cpp/qjs_jni.c)

target_include_directories(hrquickjs PRIVATE ${QJS_DIR})
# Same defines and flags as upstream's Makefile (and quickjs-emscripten's build of it).
target_compile_definitions(hrquickjs PRIVATE _GNU_SOURCE CONFIG_VERSION="${QJS_VERSION}")
target_compile_options(hrquickjs PRIVATE -O2 -fwrapv -Wno-unused-parameter -Wno-sign-compare -Wno-implicit-fallthrough)

if(ANDROID)
    target_link_libraries(hrquickjs m)
    target_link_options(hrquickjs PRIVATE "-Wl,-z,max-page-size=16384")
else()
    find_package(JNI REQUIRED)
    target_include_directories(hrquickjs PRIVATE ${JNI_INCLUDE_DIRS})
    if(WIN32)
        target_compile_definitions(hrquickjs PRIVATE __USE_MINGW_ANSI_STDIO)
        # No runtime DLL dependencies for the JVM to find.
        target_link_options(hrquickjs PRIVATE -static -static-libgcc)
        set_target_properties(hrquickjs PROPERTIES PREFIX "")
    else()
        target_link_libraries(hrquickjs m pthread)
    endif()
endif()
