cmake_minimum_required(VERSION 3.16)
project(capstone_sql C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# Compiler warnings and options
if(MSVC)
    add_compile_options(/W3 /D_CRT_SECURE_NO_WARNINGS)
else()
    add_compile_options(-Wall -Wextra -O2)
endif()

# Unified output directory for all platforms (binaries directly placed in build/)
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR})
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_BINARY_DIR})
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_DEBUG ${CMAKE_BINARY_DIR})
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR})
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY_RELEASE ${CMAKE_BINARY_DIR})
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY_DEBUG ${CMAKE_BINARY_DIR})

# Math library on Unix / macOS
if(UNIX)
    set(MATH_LIB m)
else()
    set(MATH_LIB "")
endif()

# Optional SQLite3 support
find_package(SQLite3 QUIET)

# Common include directories
set(CORE_INCLUDE_DIRS
    ${CMAKE_CURRENT_SOURCE_DIR}/src/core/include
    ${CMAKE_CURRENT_SOURCE_DIR}/src/core/postprocessor
)

# Core sources
set(CORE_SOURCES
    src/core/engine/loader.c
    src/core/pipeline/pipeline.c
)

# Postprocessor sources
set(POSTPROCESSOR_SOURCES
    src/core/postprocessor/core.c
    src/core/postprocessor/parser.c
    src/core/postprocessor/replacement.c
    src/core/postprocessor/schema.c
)

# 1. Postprocessor Static Library
add_library(postprocessor STATIC ${POSTPROCESSOR_SOURCES})
target_include_directories(postprocessor PUBLIC ${CORE_INCLUDE_DIRS})
if(SQLite3_FOUND)
    target_compile_definitions(postprocessor PRIVATE HAVE_SQLITE3)
    target_link_libraries(postprocessor PUBLIC SQLite::SQLite3)
endif()
if(MATH_LIB)
    target_link_libraries(postprocessor PUBLIC ${MATH_LIB})
endif()

# 2. Main Executable: sql-cli
add_executable(sql-cli
    src/cli/main.c
    ${CORE_SOURCES}
)
target_include_directories(sql-cli PRIVATE ${CORE_INCLUDE_DIRS})
target_link_libraries(sql-cli PRIVATE postprocessor)
if(MATH_LIB)
    target_link_libraries(sql-cli PRIVATE ${MATH_LIB})
endif()

# 3. Postprocessor Unit Test Executable
add_executable(test_postprocessor
    src/core/postprocessor/test_postprocessor.c
)
target_include_directories(test_postprocessor PRIVATE ${CORE_INCLUDE_DIRS})
target_link_libraries(test_postprocessor PRIVATE postprocessor)
if(MATH_LIB)
    target_link_libraries(test_postprocessor PRIVATE ${MATH_LIB})
endif()

# 4. Error Rejection Test Executable
add_executable(error_test
    tests/error_test.c
)
target_include_directories(error_test PRIVATE ${CORE_INCLUDE_DIRS})
if(MATH_LIB)
    target_link_libraries(error_test PRIVATE ${MATH_LIB})
endif()

# 5. Contract Fixture Test Executable
add_executable(fixture_test
    tests/fixture_test.c
)
target_include_directories(fixture_test PRIVATE ${CORE_INCLUDE_DIRS})
if(NOT MSVC)
    target_compile_options(fixture_test PRIVATE -Wno-unused-function)
endif()
if(MATH_LIB)
    target_link_libraries(fixture_test PRIVATE ${MATH_LIB})
endif()

# Testing Integration (CTest)
include(CTest)
if(BUILD_TESTING)
    # Unit Test: Postprocessor
    add_test(NAME postprocessor_unit_test COMMAND test_postprocessor)

    # Error Rejection Test
    add_test(NAME error_rejection_test
        COMMAND error_test $<TARGET_FILE:sql-cli> ${CMAKE_CURRENT_SOURCE_DIR}/fixtures/v2/base/model.gguf ${CMAKE_CURRENT_BINARY_DIR}
    )

    # Fixture Tests: scan all fixtures/v*/*/cases.txt
    file(GLOB_RECURSE CASE_FILES CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/fixtures/v*/*/cases.txt")
    foreach(CASE_FILE ${CASE_FILES})
        get_filename_component(CASE_DIR ${CASE_FILE} DIRECTORY)
        file(RELATIVE_PATH REL_DIR ${CMAKE_CURRENT_SOURCE_DIR}/fixtures ${CASE_DIR})
        set(MODEL_FILE "${CASE_DIR}/model.gguf")
        if(EXISTS ${MODEL_FILE})
            add_test(NAME "fixture_${REL_DIR}"
                COMMAND fixture_test ${MODEL_FILE} ${CASE_FILE}
            )
        endif()
    endforeach()
endif()
