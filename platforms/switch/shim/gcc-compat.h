// bx/platform.h uses clang's __is_target_os in #elif; GCC needs it defined.
#define __is_target_os(x) 0
#ifndef _GNU_SOURCE
#define _GNU_SOURCE 1 // newlib hides fseeko/ftello under strict -std=c++
#endif
