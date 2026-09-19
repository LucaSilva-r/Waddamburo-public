add_library(waddamburo_media SHARED src/media.c)
add_library(Waddamburo::Media ALIAS waddamburo_media)

waddamburo_configure_native_target(waddamburo_media)
target_compile_definitions(waddamburo_media PRIVATE WADDAMBURO_MEDIA_BUILDING_LIBRARY=1)
target_include_directories(waddamburo_media
  PUBLIC
    $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>)
set_target_properties(waddamburo_media PROPERTIES
  C_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN true)

if(BUILD_TESTING)
  add_executable(waddamburo_media_abi_test tests/media_abi_test.c)
  waddamburo_configure_native_target(waddamburo_media_abi_test)
  target_link_libraries(waddamburo_media_abi_test PRIVATE Waddamburo::Media)
  add_test(NAME media_abi COMMAND waddamburo_media_abi_test)
endif()
