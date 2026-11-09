cmake_minimum_required (VERSION 3.16)

set(CMAKE_OSX_DEPLOYMENT_TARGET "10.15" CACHE STRING "Minimum OS X deployment version")

if(IOS)
    include(ios/ios.toolchain.cmake)
endif()

set(CMAKE_POSITION_INDEPENDENT_CODE ON)

execute_process(
    COMMAND git apply "${CMAKE_CURRENT_SOURCE_DIR}/ext/upgrade_cmake.patch"
    WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/ext"
    OUTPUT_QUIET
    ERROR_QUIET
)

add_subdirectory(ext/SPIRV-Cross)

set(SHADERC_SKIP_TESTS ON)
set(SHADERC_SKIP_INSTALL ON)
set(SHADERC_GOOGLE_TEST_DIR "${CMAKE_CURRENT_SOURCE_DIR}/ext/googletest" CACHE STRING "" FORCE)
set(SHADERC_SPIRV_TOOLS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/ext/spirv-tools" CACHE STRING "" FORCE)
set(SHADERC_SPIRV_HEADERS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/ext/spirv-headers" CACHE STRING "" FORCE)
set(SHADERC_GLSLANG_DIR "${CMAKE_CURRENT_SOURCE_DIR}/ext/glslang" CACHE STRING "" FORCE)
set(SHADERC_EFFCEE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/ext/effcee" CACHE STRING "" FORCE)
set(SHADERC_RE2_DIR "${CMAKE_CURRENT_SOURCE_DIR}/ext/re2" CACHE STRING "" FORCE)
add_subdirectory(ext/shaderc)

set(CMAKE_CXX_STANDARD 17)
include_directories(ext/SPIRV-Cross)
include_directories(ext/SPIRV-Cross/include)
include_directories(ext/shaderc/libshaderc/include/shaderc)

file(GLOB_RECURSE LIBVELDRID_SPIRV_SOURCES src/libveldrid-spirv/*.cpp src/libveldrid-spirv/*.hpp)

project(veldrid-spirv)

if(EMSCRIPTEN)
  set(LIBRARY_TYPE STATIC)
else()
  set(LIBRARY_TYPE SHARED)
endif()
message( "Configured to build a shared library." )

add_library(veldrid-spirv ${LIBRARY_TYPE} ${LIBVELDRID_SPIRV_SOURCES})
target_link_libraries(veldrid-spirv
    spirv-cross-core
    spirv-cross-glsl
    spirv-cross-reflect
    spirv-cross-msl
    spirv-cross-hlsl
    shaderc
)

# macOS builds are expected to be dynamic libraries rather than frameworks,
# only output frameworks for iOS builds.
if(IOS)
    set_target_properties(veldrid-spirv PROPERTIES FRAMEWORK true)
endif()

set_target_properties(veldrid-spirv PROPERTIES MACOSX_FRAMEWORK_IDENTIFIER "sh.ppy.veldrid-spirv")
set_target_properties(veldrid-spirv PROPERTIES MACOSX_FRAMEWORK_BUNDLE_VERSION "1.0.15")
set_target_properties(veldrid-spirv PROPERTIES MACOSX_FRAMEWORK_SHORT_VERSION_STRING "1.0.15")

set_target_properties(veldrid-spirv PROPERTIES PREFIX "lib")
set_target_properties(veldrid-spirv PROPERTIES DEBUG_POSTFIX "")

set_target_properties(veldrid-spirv PROPERTIES XCODE_ATTRIBUTE_ENABLE_BITCODE "NO")
