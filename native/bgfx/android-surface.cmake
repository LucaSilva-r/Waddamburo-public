# Patches bgfx's Android EGL context to make its surface on the window a reset passes (bgfx keeps the one it was
# created with, which Android frees when the app goes to the background: unlocking the phone crashed).
file(READ "${SOURCE_DIR}/bgfx/src/glcontext_egl.cpp" _source)
set(_anchor "#	if BX_PLATFORM_ANDROID\n		if (m_ownsContext\n		&&  NULL != m_display\n		&&  NULL != m_nwh)")
set(_patched "#	if BX_PLATFORM_ANDROID\n		if (NULL != _swapChain.nwh)\n		{\n			m_nwh = _swapChain.nwh; // waddamburo: the window Android made again\n		}\n\n		if (m_ownsContext\n		&&  NULL != m_display\n		&&  NULL != m_nwh)")
if(_source MATCHES "waddamburo: the window Android made again")
  return()
endif()
string(FIND "${_source}" "${_anchor}" _at)
if(_at EQUAL -1)
  message(FATAL_ERROR "bgfx android-surface patch: anchor not found in glcontext_egl.cpp")
endif()
string(REPLACE "${_anchor}" "${_patched}" _source "${_source}")
file(WRITE "${SOURCE_DIR}/bgfx/src/glcontext_egl.cpp" "${_source}")
