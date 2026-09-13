cmake_minimum_required(VERSION 3.25)
project(LSOverlayNativeCore LANGUAGES CXX RC)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "LS Overlay Core requires Windows x64 and MSVC.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_options(/W4 /WX /utf-8 /permissive- /EHsc)
add_compile_definitions(UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00)

get_filename_component(CORE_ICON "../../src/GachaOverlay.App/Assets/Branding/LSOverlay-AppIcon.ico" ABSOLUTE)
set(CORE_MANIFEST "${CMAKE_CURRENT_SOURCE_DIR}/resources/app.manifest")
configure_file(resources/app.rc.in "${CMAKE_CURRENT_BINARY_DIR}/app.rc" @ONLY)

add_executable(LSOverlayCore WIN32 src/main.cpp src/renderer.cpp "${CMAKE_CURRENT_BINARY_DIR}/app.rc")
target_include_directories(LSOverlayCore PRIVATE src)
target_link_libraries(LSOverlayCore PRIVATE d2d1 dwrite windowscodecs ole32 user32 gdi32 shell32 psapi)
target_link_options(LSOverlayCore PRIVATE /MANIFEST:NO /DYNAMICBASE /NXCOMPAT)

enable_testing()
add_executable(LSOverlayCoreStateTests tests/shell_state_tests.cpp)
target_include_directories(LSOverlayCoreStateTests PRIVATE src)
add_test(NAME native_shell_state COMMAND LSOverlayCoreStateTests)
# UI smoke/measurement is explicit: do not open windows during ordinary ctest.
