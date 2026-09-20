cmake_minimum_required(VERSION 3.20)

project(overlay_game LANGUAGES CXX)

add_executable(overlay_game WIN32
    native/overlay_game.cpp
)

# Keep the executable name used by the existing overlay-game launcher.
set_target_properties(overlay_game PROPERTIES OUTPUT_NAME dwrg)

target_compile_features(overlay_game PRIVATE cxx_std_17)
target_compile_definitions(overlay_game PRIVATE
    UNICODE
    _UNICODE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    _WIN32_WINNT=0x0A00
)

if(MSVC)
    # Keep the test executable self-contained instead of requiring the VC++ CRT installer.
    set_property(TARGET overlay_game PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

if(MSVC)
    target_compile_options(overlay_game PRIVATE /W4 /permissive- /EHsc /utf-8 /sdl)
else()
    target_compile_options(overlay_game PRIVATE -Wall -Wextra -Wpedantic)
endif()

target_link_libraries(overlay_game PRIVATE
    comdlg32
    gdi32
    ole32
    shell32
    user32
    windowscodecs
)

add_custom_command(TARGET overlay_game POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory
        "${CMAKE_SOURCE_DIR}/Assets"
        "$<TARGET_FILE_DIR:overlay_game>/Assets"
)
