cmake_minimum_required(VERSION 3.25)
project(LSOverlayNativeCore VERSION 1.0.0 LANGUAGES C CXX RC)

if(NOT WIN32 OR NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "LS Overlay Core requires Windows x64 and MSVC.")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
add_compile_options(/W4 /WX /utf-8 /permissive- /EHsc)
add_compile_definitions(UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00)

# Pinned, MIT-licensed C JSON codec; source remains in ignored build dependencies.
# No browser/managed runtime or client-side Discord parser is introduced.
include(FetchContent)
set(YYJSON_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(YYJSON_BUILD_MISC OFF CACHE BOOL "" FORCE)
set(YYJSON_BUILD_DOC OFF CACHE BOOL "" FORCE)
set(YYJSON_INSTALL OFF CACHE BOOL "" FORCE)
FetchContent_Declare(yyjson
    URL https://github.com/ibireme/yyjson/archive/refs/tags/0.12.0.tar.gz
    URL_HASH SHA256=b16246f617b2a136c78d73e5e2647c6f1de1313e46678062985bdcf1f40bb75d
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(yyjson)
# Upstream C warnings are managed upstream; our native C++ stays /W4 /WX.
target_compile_options(yyjson PRIVATE /W3 /WX-)

add_library(LSOverlayCoreTransport STATIC src/core_wire.cpp src/transport.cpp src/auth.cpp src/core_connection.cpp src/fixture_client.cpp src/dev_client.cpp src/sales_actions.cpp)
target_include_directories(LSOverlayCoreTransport PUBLIC src)
target_link_libraries(LSOverlayCoreTransport PUBLIC yyjson PRIVATE bcrypt crypt32 winhttp shell32 ole32)

add_library(LSOverlayCoreMedia STATIC src/media_package.cpp src/media_store.cpp src/media_download.cpp)
target_include_directories(LSOverlayCoreMedia PUBLIC src)
target_link_libraries(LSOverlayCoreMedia PUBLIC windowscodecs ole32 bcrypt LSOverlayCoreTransport)

get_filename_component(CORE_ICON "../../src/GachaOverlay.App/Assets/Branding/LSOverlay-AppIcon.ico" ABSOLUTE)
set(CORE_MANIFEST "${CMAKE_CURRENT_SOURCE_DIR}/resources/app.manifest")
configure_file(resources/app.rc.in "${CMAKE_CURRENT_BINARY_DIR}/app.rc" @ONLY)

add_executable(LSOverlayCore WIN32 src/main.cpp src/renderer.cpp src/chat_view.cpp src/text_outline.cpp src/sales_view.cpp src/user_settings.cpp src/settings_window.cpp "${CMAKE_CURRENT_BINARY_DIR}/app.rc")
target_include_directories(LSOverlayCore PRIVATE src)
target_link_libraries(LSOverlayCore PRIVATE d2d1 dwrite windowscodecs ole32 user32 gdi32 gdiplus dwmapi shell32 psapi comctl32 uxtheme winmm LSOverlayCoreTransport LSOverlayCoreMedia)
target_link_options(LSOverlayCore PRIVATE /MANIFEST:NO /DYNAMICBASE /NXCOMPAT)
add_custom_command(TARGET LSOverlayCore POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory
        "${CMAKE_CURRENT_SOURCE_DIR}/../../src/GachaOverlay.App/Assets/Fonts" "$<TARGET_FILE_DIR:LSOverlayCore>/fonts")

enable_testing()
add_executable(LSOverlayCoreProductionProbe tests/production_probe.cpp)
target_link_libraries(LSOverlayCoreProductionProbe PRIVATE LSOverlayCoreTransport)
add_executable(LSOverlayCoreSalesActionTests tests/sales_action_tests.cpp)
target_link_libraries(LSOverlayCoreSalesActionTests PRIVATE LSOverlayCoreTransport)
add_test(NAME native_sales_actions COMMAND LSOverlayCoreSalesActionTests)
add_executable(LSOverlayCoreStateTests tests/shell_state_tests.cpp)
target_include_directories(LSOverlayCoreStateTests PRIVATE src)
add_test(NAME native_shell_state COMMAND LSOverlayCoreStateTests)
add_executable(LSOverlayCoreWireTests tests/core_wire_tests.cpp)
target_link_libraries(LSOverlayCoreWireTests PRIVATE LSOverlayCoreTransport)
add_test(NAME native_core_wire COMMAND LSOverlayCoreWireTests)
add_executable(LSOverlayCoreAuthTests tests/auth_tests.cpp)
target_link_libraries(LSOverlayCoreAuthTests PRIVATE LSOverlayCoreTransport)
add_test(NAME native_core_auth COMMAND LSOverlayCoreAuthTests "${CMAKE_CURRENT_BINARY_DIR}/credential-tests")
add_executable(LSOverlayCoreChatScrollTests tests/chat_scroll_tests.cpp)
target_include_directories(LSOverlayCoreChatScrollTests PRIVATE src)
add_test(NAME native_chat_scroll COMMAND LSOverlayCoreChatScrollTests)
add_executable(LSOverlayCoreMediaTests tests/media_tests.cpp)
target_link_libraries(LSOverlayCoreMediaTests PRIVATE LSOverlayCoreMedia)
add_test(NAME native_media_timeline COMMAND LSOverlayCoreMediaTests)
add_executable(LSOverlayCoreSettingsTests tests/settings_tests.cpp src/user_settings.cpp)
target_link_libraries(LSOverlayCoreSettingsTests PRIVATE LSOverlayCoreMedia shell32)
add_test(NAME native_user_settings COMMAND LSOverlayCoreSettingsTests "${CMAKE_CURRENT_BINARY_DIR}/settings-tests/settings.json")
add_executable(LSOverlayCoreTransportProbe tests/transport_probe.cpp)
target_link_libraries(LSOverlayCoreTransportProbe PRIVATE LSOverlayCoreTransport)
add_executable(LSOverlayCoreMediaDeliveryProbe tests/media_delivery_probe.cpp)
target_link_libraries(LSOverlayCoreMediaDeliveryProbe PRIVATE LSOverlayCoreMedia)
# UI smoke/measurement is explicit: do not open windows during ordinary ctest.
