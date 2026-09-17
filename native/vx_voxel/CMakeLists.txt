cmake_minimum_required(VERSION 3.20)
project(vx_voxel LANGUAGES CXX VERSION 1.0.0)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_library(vx_voxel SHARED vx_voxel.cpp)
target_include_directories(vx_voxel PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

set_target_properties(vx_voxel PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
    POSITION_INDEPENDENT_CODE ON)

if(MSVC)
    target_compile_options(vx_voxel PRIVATE /W4 /EHsc)
else()
    target_compile_options(vx_voxel PRIVATE -Wall -Wextra)
endif()
